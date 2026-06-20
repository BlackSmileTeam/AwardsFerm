using AwardsFerm.Core.Interfaces;
using Microsoft.Playwright;

namespace AwardsFerm.Infrastructure.Playwright;

internal sealed class SessionActivityTracker
{
    private long _lastTrafficBytes;

    public DateTimeOffset LastActivityUtc { get; private set; } = DateTimeOffset.UtcNow;
    public int CurrentStep { get; private set; }
    public DateTimeOffset StepStartedUtc { get; private set; } = DateTimeOffset.UtcNow;

    public void MarkStep(int step)
    {
        CurrentStep = step;
        StepStartedUtc = DateTimeOffset.UtcNow;
        LastActivityUtc = DateTimeOffset.UtcNow;
    }

    public void MarkActivity() => LastActivityUtc = DateTimeOffset.UtcNow;

    public void MarkTraffic(long bytes)
    {
        if (bytes <= _lastTrafficBytes)
            return;

        _lastTrafficBytes = bytes;
        LastActivityUtc = DateTimeOffset.UtcNow;
    }
}

internal sealed class SessionStallState
{
    private readonly CancellationTokenSource _cts = new();

    public string? Reason { get; private set; }

    public bool IsTriggered => Reason is not null;

    public CancellationToken Token => _cts.Token;

    public void Trigger(string reason)
    {
        if (Reason is not null)
            return;

        Reason = reason;
        _cts.Cancel();
    }
}

internal static class SessionActivityWatchdog
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan StepStallThreshold = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan GameStallThreshold = TimeSpan.FromMinutes(25);
    private static readonly TimeSpan GameActivityGrace = TimeSpan.FromMinutes(15);

    public static async Task RunAsync(
        string sessionId,
        string profileId,
        ActivePageHolder activePage,
        SessionActivityTracker activity,
        SessionStallState stallState,
        ISessionPauseCoordinator pauseCoordinator,
        ISessionEventReporter reporter,
        CancellationToken cancellationToken = default)
    {
        var unresponsiveStreak = 0;

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(PollInterval, cancellationToken);

                if (pauseCoordinator.IsPaused(profileId))
                {
                    activity.MarkActivity();
                    continue;
                }

                if (!activePage.TryResolve(out var page) || page is null || page.IsClosed)
                {
                    await TriggerStallAsync(
                        sessionId,
                        null,
                        stallState,
                        reporter,
                        "Браузер или вкладка закрыты — перезапуск",
                        cancellationToken);
                    return;
                }

                var step = activity.CurrentStep;

                if (step >= 12 &&
                    (await YandexUiHelper.IsGameRunningAsync(page) || await CaptchaHelper.IsPresentAsync(page)))
                {
                    unresponsiveStreak = 0;
                    activity.MarkActivity();
                }
                else if (await IsPageUnresponsiveAsync(page))
                {
                    unresponsiveStreak++;

                    if (unresponsiveStreak == 2 && step < 12 && await TryRecoverUnresponsivePageAsync(page))
                    {
                        unresponsiveStreak = 0;
                        activity.MarkActivity();
                        await reporter.ReportAsync(new Core.Models.SessionEvent
                        {
                            SessionId = sessionId,
                            Type = Core.Models.SessionEventType.Log,
                            Message = "Страница временно не отвечала — обновили без перезапуска браузера"
                        }, cancellationToken);
                        continue;
                    }

                    if (unresponsiveStreak >= 3)
                    {
                        await TriggerStallAsync(
                            sessionId,
                            page,
                            stallState,
                            reporter,
                            "Страница не отвечает — перезапуск браузера",
                            cancellationToken);
                        return;
                    }

                    continue;
                }
                else
                {
                    unresponsiveStreak = 0;
                }

                var stepIdle = DateTimeOffset.UtcNow - activity.StepStartedUtc;
                var activityIdle = DateTimeOffset.UtcNow - activity.LastActivityUtc;
                var threshold = step >= 12 ? GameStallThreshold : StepStallThreshold;

                if (stepIdle < threshold)
                    continue;

                if (step >= 12 && activityIdle < GameActivityGrace)
                    continue;

                if (step >= 12 && await YandexUiHelper.IsGameRunningAsync(page))
                {
                    activity.MarkActivity();
                    continue;
                }

                if (step >= 12 && await CaptchaHelper.IsPresentAsync(page))
                    continue;

                var url = page.Url;
                if (string.IsNullOrWhiteSpace(url) ||
                    url.Equals("about:blank", StringComparison.OrdinalIgnoreCase))
                {
                    await TriggerStallAsync(
                        sessionId,
                        page,
                        stallState,
                        reporter,
                        "Вкладка пуста — страница не загрузилась",
                        cancellationToken);
                    return;
                }

                await TriggerStallAsync(
                    sessionId,
                    page,
                    stallState,
                    reporter,
                    $"Нет прогресса {stepIdle.TotalMinutes:F0} мин (шаг {step}) — перезапуск браузера",
                    cancellationToken);
                return;
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch
            {
                await Task.Delay(PollInterval, cancellationToken);
            }
        }
    }

    private static async Task<bool> IsPageUnresponsiveAsync(IPage page)
    {
        if (page.IsClosed)
            return true;

        var evaluateTask = page.EvaluateAsync<string>("() => document.readyState");
        if (await Task.WhenAny(evaluateTask, Task.Delay(12_000)) == evaluateTask)
        {
            try
            {
                _ = await evaluateTask;
                return false;
            }
            catch
            {
                return !await HasRecoverablePageContentAsync(page);
            }
        }

        return !await HasRecoverablePageContentAsync(page);
    }

    private static async Task<bool> HasRecoverablePageContentAsync(IPage page)
    {
        if (page.IsClosed || SessionNavigationHelper.IsBrowserErrorPage(page.Url))
            return false;

        try
        {
            var html = await page.ContentAsync();
            return !string.IsNullOrWhiteSpace(html) && html.Length >= 200;
        }
        catch
        {
            return false;
        }
    }

    private static async Task<bool> TryRecoverUnresponsivePageAsync(IPage page)
    {
        if (page.IsClosed || SessionNavigationHelper.IsBrowserErrorPage(page.Url))
            return false;

        try
        {
            await page.ReloadAsync(new PageReloadOptions
            {
                WaitUntil = WaitUntilState.DOMContentLoaded,
                Timeout = 45_000
            });
            await Task.Delay(2500);
            return !await IsPageUnresponsiveAsync(page);
        }
        catch
        {
            return false;
        }
    }

    private static async Task TriggerStallAsync(
        string sessionId,
        IPage? page,
        SessionStallState stallState,
        ISessionEventReporter reporter,
        string reason,
        CancellationToken cancellationToken)
    {
        if (stallState.IsTriggered)
            return;

        if (page is not null && !page.IsClosed)
            await SessionScreenDiagnostic.ReportDiagnosticAsync(sessionId, page, reason, reporter, cancellationToken);

        await reporter.ReportAsync(new Core.Models.SessionEvent
        {
            SessionId = sessionId,
            Type = Core.Models.SessionEventType.Log,
            Message = $"⚠ {reason}"
        }, cancellationToken);

        stallState.Trigger(reason);
    }
}
