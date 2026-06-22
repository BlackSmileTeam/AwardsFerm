using AwardsFerm.Core.Interfaces;
using AwardsFerm.Core.Models;
using Microsoft.Playwright;

namespace AwardsFerm.Infrastructure.Playwright;

/// <summary>
/// Отслеживает вкладку Captcha Verification и ставит сессию на паузу, пока капча не решена.
/// </summary>
internal static class CaptchaTabWatcher
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(1.5);

    public static async Task RunAsync(
        string sessionId,
        string profileId,
        ActivePageHolder activePage,
        ISessionPauseCoordinator pauseCoordinator,
        ISessionEventReporter reporter,
        CancellationToken cancellationToken = default)
    {
        var captchaPauseActive = false;

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(PollInterval, cancellationToken);

                if (!activePage.TryResolve(out _))
                    continue;

                var captchaPage = await CaptchaHelper.FindManualCaptchaPageAsync(activePage.Context);

                if (captchaPage is not null)
                {
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
                            Message = "⚠ Открыта вкладка Captcha Verification — сессия на паузе. " +
                                      "Откройте «Просмотр» и пройдите капчу вручную."
                        }, cancellationToken);
                    }

                    continue;
                }

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
}
