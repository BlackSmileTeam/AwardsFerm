using AwardsFerm.Core.Interfaces;
using AwardsFerm.Core.Models;
using Microsoft.Playwright;

namespace AwardsFerm.Infrastructure.Playwright;

/// <summary>
/// Отслеживает вкладку Captcha Verification и ставит сессию на паузу, пока капча не решена.
/// </summary>
internal static class CaptchaTabWatcher
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(500);

    public static async Task RunAsync(
        string sessionId,
        string profileId,
        ActivePageHolder activePage,
        ISessionPauseCoordinator pauseCoordinator,
        ISessionEventReporter reporter,
        CancellationToken cancellationToken = default)
    {
        var captchaPauseActive = false;

        async Task<bool> TryPauseForCaptchaAsync()
        {
            if (!activePage.TryResolve(out _))
                return false;

            var captchaPage = await CaptchaHelper.FindManualCaptchaPageAsync(activePage.Context);
            if (captchaPage is null)
                return false;

            activePage.CaptchaFocusPage = captchaPage;

            try
            {
                await captchaPage.BringToFrontAsync();
            }
            catch
            {
                // ignore
            }

            if (!captchaPauseActive)
            {
                captchaPauseActive = true;
                pauseCoordinator.Pause(profileId);

                await reporter.ReportAsync(new SessionEvent
                {
                    SessionId = sessionId,
                    Type = SessionEventType.StatusChanged,
                    Status = SessionStatus.Paused,
                    Message = "Пауза — Captcha Verification"
                }, cancellationToken);

                await reporter.ReportAsync(new SessionEvent
                {
                    SessionId = sessionId,
                    Type = SessionEventType.Log,
                    Message = "⚠ Открыта вкладка Captcha Verification — все действия остановлены. " +
                              "Откройте «Просмотр» и пройдите капчу вручную."
                }, cancellationToken);
            }

            return true;
        }

        void OnPage(object? _, IPage page)
        {
            _ = HandleNewPageAsync(page);
        }

        async Task HandleNewPageAsync(IPage page)
        {
            if (page.IsClosed)
                return;

            try
            {
                await page.WaitForLoadStateAsync(LoadState.DOMContentLoaded,
                    new PageWaitForLoadStateOptions { Timeout = 12_000 });
            }
            catch
            {
                // title/url могут появиться позже
            }

            try
            {
                if (await CaptchaHelper.IsCaptchaVerificationPageAsync(page) ||
                    await CaptchaHelper.RequiresManualSolveAsync(page))
                {
                    await TryPauseForCaptchaAsync();
                }
            }
            catch
            {
                // ignore
            }
        }

        if (activePage.TryResolve(out _))
            activePage.Context.Page += OnPage;

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(PollInterval, cancellationToken);

                    if (!activePage.TryResolve(out _))
                        continue;

                    if (await TryPauseForCaptchaAsync())
                        continue;

                    if (captchaPauseActive)
                    {
                        captchaPauseActive = false;
                        activePage.CaptchaFocusPage = null;
                        pauseCoordinator.Resume(profileId);

                        await reporter.ReportAsync(new SessionEvent
                        {
                            SessionId = sessionId,
                            Type = SessionEventType.Log,
                            Message = "✓ Captcha Verification пройдена — продолжаем сессию"
                        }, cancellationToken);

                        await reporter.ReportAsync(new SessionEvent
                        {
                            SessionId = sessionId,
                            Type = SessionEventType.StatusChanged,
                            Status = SessionStatus.Running,
                            Message = "Продолжение"
                        }, cancellationToken);
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch
                {
                    // ignore transient errors
                }
            }
        }
        finally
        {
            if (activePage.TryResolve(out _))
                activePage.Context.Page -= OnPage;
        }
    }
}
