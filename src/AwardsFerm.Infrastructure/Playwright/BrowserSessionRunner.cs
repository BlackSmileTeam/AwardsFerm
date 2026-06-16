using AwardsFerm.Core.Interfaces;
using AwardsFerm.Core.Models;
using AwardsFerm.Infrastructure.Behavior;
using AwardsFerm.Infrastructure.Storage;
using Microsoft.Playwright;

namespace AwardsFerm.Infrastructure.Playwright;

public sealed class BrowserSessionRunner : IBrowserSessionRunner
{
    private const int TotalSteps = 12;

    private readonly PlaywrightBrowserFactory _browserFactory;
    private readonly ICookieStore _cookieStore;
    private readonly ISessionEventReporter _eventReporter;
    private readonly ISessionPauseCoordinator _pauseCoordinator;
    private readonly string _profilesRoot;

    public BrowserSessionRunner(
        PlaywrightBrowserFactory browserFactory,
        ICookieStore cookieStore,
        ISessionEventReporter eventReporter,
        ISessionPauseCoordinator pauseCoordinator,
        ProfileRepository profileRepository)
    {
        _browserFactory = browserFactory;
        _cookieStore = cookieStore;
        _eventReporter = eventReporter;
        _pauseCoordinator = pauseCoordinator;
        _profilesRoot = profileRepository.ProfilesRoot;
    }

    public async Task<SessionRunResult> RunYandexGamesSearchAsync(
        string sessionId,
        DesktopProfile profile,
        YandexGamesSearchOptions options,
        CancellationToken cancellationToken = default)
    {
        BrowserLaunchResult? launch = null;
        var screenshotCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        ActivePageHolder? activePage = null;
        var playGameOverCount = 0;
        DesktopProfile? sessionProfile = null;
        SessionTrafficMonitor? trafficMonitor = null;
        var accumulatedTrafficBytes = 0L;
        var trafficCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var geoCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        try
        {
            await ReportStatusAsync(sessionId, SessionStatus.Starting, cancellationToken);

            IBrowserContext context = null!;
            IPage page = null!;
            var useProxy = options.UseProxy;
            var maxProxyAttempts = useProxy ? 5 : 1;
            if (!useProxy)
            {
                await ReportLogAsync(sessionId, "Прокси отключён в настройках слота", cancellationToken);
            }

            for (var proxyTry = 0; proxyTry < maxProxyAttempts; proxyTry++)
            {
                sessionProfile = DeviceFingerprintRotator.RotateForSession(profile, _profilesRoot, useProxy);
                if (useProxy && sessionProfile.ProxyUrl is null)
                {
                    await ReportLogAsync(
                        sessionId,
                        "Прокси включён, но не найден в profiles/proxies.txt — проверьте файл и proxy.auth.json",
                        cancellationToken);
                }
                if (sessionProfile.ProxyUrl is not null)
                    sessionProfile.ProxyUrl = ProxyUrlHelper.WithRetryPortOffset(sessionProfile.ProxyUrl, proxyTry);

                DeviceFingerprintRotator.PruneOldBrowserInstances(_profilesRoot, profile.Id);
                await ReportLogAsync(
                    sessionId,
                    $"Новая сессия браузера ({sessionProfile.BrowserSessionId[..8]}…): {DeviceFingerprintRotator.Describe(sessionProfile)}" +
                    (ProxyUrlHelper.TryParseProxy(sessionProfile.ProxyUrl, out var proxyCreds) && proxyCreds.Username is not null
                        ? $" · auth: {proxyCreds.Username}"
                        : ""),
                    cancellationToken);

                launch = await _browserFactory.LaunchPersistentAsync(sessionProfile, options, _profilesRoot, cancellationToken);
                context = launch.Context;
                activePage = new ActivePageHolder { Context = context, Page = launch.Page, UrlPart = options.TargetGameUrlPart };
                page = activePage.Page;

                await SessionLocationHelper.ApplyAsync(context, page, sessionProfile, cancellationToken);

                var connectivity = await SessionNetworkHelper.CheckProxyConnectivityAsync(page, cancellationToken);
                var publicIp = connectivity.PublicIp;
                if (publicIp is not null)
                {
                    if (sessionProfile.ProxyUrl is not null)
                        ProxyRotator.ReportSuccess(sessionProfile.ProxyUrl);

                    await _eventReporter.ReportAsync(new SessionEvent
                    {
                        SessionId = sessionId,
                        Type = SessionEventType.IpDetected,
                        PublicIp = publicIp
                    }, cancellationToken);

                    await ReportLogAsync(
                        sessionId,
                        $"Внешний IP сессии: {publicIp}",
                        cancellationToken);

                    var ipGeo = await SessionLocationHelper.LookupByIpAsync(page, publicIp, cancellationToken);
                    if (ipGeo is not null && sessionProfile.ProxyUrl is not null)
                    {
                        SessionLocationHelper.ApplyLookupToProfile(sessionProfile, ipGeo);
                        await SessionLocationHelper.ApplyAsync(context, page, sessionProfile, cancellationToken);
                        await ReportLogAsync(
                            sessionId,
                            $"Локация по IP прокси: {SessionLocationHelper.Format(sessionProfile)}",
                            cancellationToken);
                    }
                    else
                    {
                        await ReportLogAsync(
                            sessionId,
                            $"Локация: {SessionLocationHelper.Format(sessionProfile)}",
                            cancellationToken);
                    }
                }
                else
                {
                    if (sessionProfile.ProxyUrl is not null)
                    {
                        ProxyRotator.ReportFailure(sessionProfile.ProxyUrl);
                        var detail = connectivity.DescribeFailure();
                        await ReportLogAsync(sessionId,
                            $"Прокси недоступна ({detail}) — попытка {proxyTry + 1}/{maxProxyAttempts}. " +
                            ProxyUrlHelper.DescribeProxyFailureHint(sessionProfile.ProxyUrl, connectivity.ErrorCode),
                            cancellationToken);

                        accumulatedTrafficBytes += trafficMonitor?.TotalBytes ?? 0;
                        if (trafficMonitor is not null)
                            await trafficMonitor.DisposeAsync();
                        trafficMonitor = null;
                        await launch.DisposeAsync();
                        launch = null;
                        continue;
                    }

                    await ReportLogAsync(
                        sessionId,
                        "Прокси отключён — продолжаем без внешнего IP",
                        cancellationToken);
                }

                await _cookieStore.LoadAsync(context, sessionProfile.CookiesPath, cancellationToken);
                trafficMonitor = await SessionTrafficMonitor.AttachAsync(context, cancellationToken);
                _ = StreamScreenshotsAsync(sessionId, activePage, screenshotCts.Token);
                _ = StreamTrafficAsync(sessionId, () => accumulatedTrafficBytes, () => trafficMonitor, trafficCts.Token);
                _ = StreamGeoDriftAsync(sessionId, activePage, sessionProfile, geoCts.Token);

                // Успешный старт: дальше выполняем сценарий.
                break;
            }

            if (launch is null)
                throw new PlaywrightException(useProxy
                    ? "All proxy attempts failed."
                    : "Browser launch failed.");

            await RunStepAsync(sessionId, profile.Id, 1, "Прогрев: открытие yandex.ru", cancellationToken, async () =>
            {
                page = activePage!.Resolve();
                await SessionNavigationHelper.GotoWithRetryAsync(page, "https://yandex.ru/", cancellationToken);
                await HumanBehavior.DelayAsync(3000, 5000, cancellationToken);
                await YandexUiHelper.DismissPopupsAsync(page, cancellationToken);
                await CaptchaHelper.WaitForManualSolveAsync(page, sessionId, _eventReporter, cancellationToken);
                await HumanBehavior.ScrollNaturallyAsync(page, cancellationToken);
                await HumanBehavior.DelayAsync(2000, 4000, cancellationToken);
            });

            await RunStepAsync(sessionId, profile.Id, 2, "Прогрев: новости и главная Яндекса", cancellationToken, async () =>
            {
                page = activePage!.Resolve();
                await YandexUiHelper.DismissPopupsAsync(page, cancellationToken);
                await YandexWarmupHelper.BrowseNewsBlocksAsync(page, sessionId, _eventReporter, cancellationToken);
                await HumanBehavior.MoveMouseRandomlyAsync(page, cancellationToken);
                await HumanBehavior.DelayAsync(2000, 4000, cancellationToken);
            });

            await RunStepAsync(sessionId, profile.Id, 3, "Переход на Яндекс Игры", cancellationToken, async () =>
            {
                page = activePage!.Resolve();
                await SessionNavigationHelper.GotoWithRetryAsync(page, "https://yandex.ru/games", cancellationToken);
                await HumanBehavior.DelayAsync(3000, 5000, cancellationToken);
                await YandexUiHelper.DismissPopupsAsync(page, cancellationToken);
                await CaptchaHelper.WaitForManualSolveAsync(page, sessionId, _eventReporter, cancellationToken);
            });

            await RunStepAsync(sessionId, profile.Id, 4, "Закрытие cookie-баннера и попапов", cancellationToken, async () =>
            {
                page = activePage!.Resolve();
                await YandexUiHelper.DismissPopupsAsync(page, cancellationToken);
                await TryClickAsync(page,
                [
                    "button:has-text('Принять')",
                    "button:has-text('OK')",
                    "button:has-text('Хорошо')",
                    "[data-testid='gdpr-accept']"
                ]);
                await HumanBehavior.DelayAsync(800, 1500, cancellationToken);
            });

            await RunStepAsync(sessionId, profile.Id, 5, "Открытие строки поиска", cancellationToken, async () =>
            {
                page = activePage!.Resolve();
                await CaptchaHelper.WaitForManualSolveAsync(page, sessionId, _eventReporter, cancellationToken);
                await YandexUiHelper.DismissPopupsAsync(page, cancellationToken);
                await YandexUiHelper.FocusSearchInputAsync(page, cancellationToken);
            });

            await RunStepAsync(sessionId, profile.Id, 6, $"Ввод запроса «{options.SearchQuery}»", cancellationToken, async () =>
            {
                page = activePage!.Resolve();
                var filled = await YandexUiHelper.FillSearchQueryAsync(page, options.SearchQuery, cancellationToken);
                if (!filled)
                {
                    var searchUrl =
                        $"https://yandex.ru/games/search?query={Uri.EscapeDataString(options.SearchQuery)}";
                    await ReportLogAsync(sessionId, "Поле поиска перекрыто — открываем поиск по URL", cancellationToken);
                    await SessionNavigationHelper.GotoWithRetryAsync(page, searchUrl, cancellationToken);
                    await HumanBehavior.DelayAsync(2000, 3500, cancellationToken);
                }
            });

            await RunStepAsync(sessionId, profile.Id, 7, "Запуск поиска", cancellationToken, async () =>
            {
                page = activePage!.Resolve();
                if (page.Url.Contains("/search", StringComparison.OrdinalIgnoreCase))
                {
                    await HumanBehavior.DelayAsync(1000, 2000, cancellationToken);
                    return;
                }

                await HumanBehavior.DelayAsync(500, 1200, cancellationToken);
                await page.Keyboard.PressAsync("Enter");
                await page.WaitForLoadStateAsync(LoadState.DOMContentLoaded, new PageWaitForLoadStateOptions { Timeout = 30_000 });
                await HumanBehavior.DelayAsync(2500, 4000, cancellationToken);
                await YandexUiHelper.DismissPopupsAsync(page, cancellationToken);
                await CaptchaHelper.WaitForManualSolveAsync(page, sessionId, _eventReporter, cancellationToken);
            });

            await RunStepAsync(sessionId, profile.Id, 8, "Ожидание результатов поиска", cancellationToken, async () =>
            {
                page = activePage!.Resolve();
                var found = await SessionNavigationHelper.TryWaitForSearchResultsAsync(page, cancellationToken);
                if (!found)
                {
                    await ReportLogAsync(sessionId,
                        "Выдача не появилась — открываем поиск по URL",
                        cancellationToken);
                    var searchUrl =
                        $"https://yandex.ru/games/search?query={Uri.EscapeDataString(options.SearchQuery)}";
                    await SessionNavigationHelper.GotoWithRetryAsync(page, searchUrl, cancellationToken);
                    await HumanBehavior.DelayAsync(2500, 4000, cancellationToken);
                    await YandexUiHelper.DismissPopupsAsync(page, cancellationToken);
                    await CaptchaHelper.WaitForManualSolveAsync(page, sessionId, _eventReporter, cancellationToken);
                    found = await SessionNavigationHelper.TryWaitForSearchResultsAsync(page, cancellationToken, 40_000);
                }

                if (!found)
                    throw new InvalidOperationException(
                        $"Результаты поиска «{options.SearchQuery}» не загрузились — проверьте капчу или селекторы.");

                await HumanBehavior.ScrollNaturallyAsync(page, cancellationToken);
            });

            ILocator? gameLink = null;
            await RunStepAsync(sessionId, profile.Id, 9, $"Поиск игры «{options.TargetGameTitle}»", cancellationToken, async () =>
            {
                page = activePage!.Resolve();
                gameLink = await FindGameLinkAsync(page, options);
                if (gameLink is null)
                    throw new InvalidOperationException(
                        $"Игра «{options.TargetGameTitle}» не найдена в выдаче по запросу «{options.SearchQuery}».");
            });

            await RunStepAsync(sessionId, profile.Id, 10, "Переход на страницу игры", cancellationToken, async () =>
            {
                page = activePage!.Resolve();
                gameLink ??= await FindGameLinkAsync(page, options)
                             ?? throw new InvalidOperationException("Ссылка на игру не найдена.");

                await ReportLogAsync(sessionId, "Прокрутка к карточке игры…", cancellationToken);
                try
                {
                    await gameLink.ScrollIntoViewIfNeededAsync(
                        new LocatorScrollIntoViewIfNeededOptions { Timeout = 10_000 });
                }
                catch
                {
                    await HumanBehavior.ScrollNaturallyAsync(page, cancellationToken);
                }

                var gamePage = await YandexUiHelper.OpenGamePageAsync(
                    context,
                    page,
                    gameLink,
                    options.TargetGameUrlPart,
                    options.TargetGameUrl,
                    sessionId,
                    _eventReporter,
                    cancellationToken);

                activePage!.Page = gamePage;
                page = gamePage;

                await YandexUiHelper.FocusGameTabAsync(context, gamePage, cancellationToken);
                await HumanBehavior.DelayAsync(2000, 4000, cancellationToken);
                await CaptchaHelper.WaitForManualSolveAsync(page, sessionId, _eventReporter, cancellationToken);
                await ReportLogAsync(sessionId, $"Открыта вкладка игры: {page.Url}", cancellationToken);
            });

            await RunStepAsync(sessionId, profile.Id, 11, "Нажатие кнопки «Играть» и загрузка игры", cancellationToken, async () =>
            {
                var gamePage = await YandexUiHelper.EnterGameAsync(
                    context,
                    activePage!.Resolve(),
                    options.TargetGameUrl,
                    options.TargetGameUrlPart,
                    sessionId,
                    _eventReporter,
                    cancellationToken);

                activePage!.Page = gamePage;
                await YandexUiHelper.WaitForGameFullyLoadedAsync(gamePage, cancellationToken);
                await ReportLogAsync(sessionId, "✓ Игра загружена (экран «Загрузка» завершён)", cancellationToken);
            });

            var playMinutes = options.PlayDurationMinSeconds / 60.0;
            var playMinutesMax = options.PlayDurationMaxSeconds / 60.0;
            _ = playMinutes;
            _ = playMinutesMax;
            var rotateSessionAfterPlay = false;
            await RunStepAsync(sessionId, profile.Id, 12, "Игра (непрерывный цикл)", cancellationToken, async () =>
            {
                page = activePage!.Resolve();

                await YandexUiHelper.WaitForGameFullyLoadedAsync(page, cancellationToken);

                playGameOverCount = 0;
                var playOutcome = await SlitherGamePlayHelper.PlaySessionAsync(
                    context,
                    page,
                    profile.Id,
                    options.PlayDurationMinSeconds,
                    options.PlayDurationMaxSeconds,
                    sessionId,
                    _eventReporter,
                    _pauseCoordinator,
                    cancellationToken);
                playGameOverCount = playOutcome.GamesPlayed;

                if (playOutcome.RotateSession)
                {
                    await ReportLogAsync(
                        sessionId,
                        $"Смена устройства после {playOutcome.GamesPlayed} игр — перезапуск с новым профилем",
                        cancellationToken);
                    await _cookieStore.SaveAsync(context, sessionProfile!.CookiesPath, cancellationToken);
                    rotateSessionAfterPlay = true;
                }
            });

            if (rotateSessionAfterPlay)
                return SessionRunResult.RestartAfterGameOvers(playGameOverCount);

            await _cookieStore.SaveAsync(context, sessionProfile!.CookiesPath, cancellationToken);
            return SessionRunResult.Completed(playGameOverCount);
        }
        catch (OperationCanceledException)
        {
            await ReportStatusAsync(sessionId, SessionStatus.Stopped, CancellationToken.None);
            await _eventReporter.ReportAsync(new SessionEvent
            {
                SessionId = sessionId,
                Type = SessionEventType.StatusChanged,
                Message = "Сессия остановлена",
                Status = SessionStatus.Stopped
            }, CancellationToken.None);
            return SessionRunResult.Completed(playGameOverCount);
        }
        catch (Exception ex)
        {
            await TryCaptureScreenshotAsync(sessionId, activePage);

            if (IsBrowserClosedException(ex))
            {
                await ReportLogAsync(sessionId, "Браузер закрыт — сессия будет перезапущена", CancellationToken.None);
                return SessionRunResult.BrowserClosed(playGameOverCount);
            }

            if (sessionProfile?.ProxyUrl is not null && SessionNavigationHelper.IsProxyNetworkError(ex))
            {
                ProxyRotator.ReportFailure(sessionProfile.ProxyUrl);
                await ReportLogAsync(sessionId,
                    "Сбой сети через прокси — исключаем из пула до cooldown",
                    CancellationToken.None);
            }

            await ReportLogAsync(sessionId, $"Ошибка: {ex.Message} — будет перезапуск", CancellationToken.None);
            return SessionRunResult.BrowserClosed(playGameOverCount);
        }
        finally
        {
            screenshotCts.Cancel();
            trafficCts.Cancel();
            geoCts.Cancel();
            var finalTraffic = accumulatedTrafficBytes + (trafficMonitor?.TotalBytes ?? 0);
            if (finalTraffic > 0)
                _ = ReportTrafficUpdateAsync(sessionId, finalTraffic, CancellationToken.None);

            await Task.Delay(200);
            if (trafficMonitor is not null)
                await trafficMonitor.DisposeAsync();
            if (launch is not null)
                await launch.DisposeAsync();
        }
    }

    private async Task StreamGeoDriftAsync(
        string sessionId,
        ActivePageHolder activePage,
        DesktopProfile profile,
        CancellationToken cancellationToken)
    {
        var random = new Random();
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var delayMinutes = random.Next(4, 9);
                await Task.Delay(TimeSpan.FromMinutes(delayMinutes), cancellationToken);

                if (!activePage.TryResolve(out var page) || page is null)
                    continue;

                SessionLocationHelper.ApplyRandomDrift(profile, random);
                await SessionLocationHelper.ApplyAsync(page.Context, page, profile, cancellationToken);
                await ReportLogAsync(
                    sessionId,
                    $"Геолокация смещена: {SessionLocationHelper.Format(profile)}",
                    cancellationToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch
            {
                await Task.Delay(60_000, cancellationToken);
            }
        }
    }

    private async Task StreamTrafficAsync(
        string sessionId,
        Func<long> accumulatedBytes,
        Func<SessionTrafficMonitor?> monitor,
        CancellationToken cancellationToken)
    {
        long lastReported = -1;
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var total = accumulatedBytes() + (monitor()?.TotalBytes ?? 0);
                if (total != lastReported)
                {
                    lastReported = total;
                    await ReportTrafficUpdateAsync(sessionId, total, cancellationToken);
                }

                await Task.Delay(8_000, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch
            {
                await Task.Delay(8_000, cancellationToken);
            }
        }
    }

    private Task ReportTrafficUpdateAsync(string sessionId, long bytes, CancellationToken cancellationToken) =>
        _eventReporter.ReportAsync(new SessionEvent
        {
            SessionId = sessionId,
            Type = SessionEventType.TrafficUpdated,
            TrafficBytes = bytes,
            Message = SessionTrafficMonitor.FormatBytes(bytes)
        }, cancellationToken);

    private async Task RunStepAsync(
        string sessionId,
        string profileId,
        int step,
        string stepName,
        CancellationToken cancellationToken,
        Func<Task> action)
    {
        await _pauseCoordinator.WaitIfPausedAsync(profileId, sessionId, _eventReporter, cancellationToken);

        await _eventReporter.ReportAsync(new SessionEvent
        {
            SessionId = sessionId,
            Type = SessionEventType.StepChanged,
            CurrentStep = step,
            TotalSteps = TotalSteps,
            StepName = stepName,
            Message = stepName,
            Status = SessionStatus.Running
        }, cancellationToken);

        await ReportLogAsync(sessionId, $"Шаг {step}/{TotalSteps}: {stepName}", cancellationToken);
        await ReportStatusAsync(sessionId, SessionStatus.Running, cancellationToken);

        await action();

        await _pauseCoordinator.WaitIfPausedAsync(profileId, sessionId, _eventReporter, cancellationToken);

        if (cancellationToken.IsCancellationRequested)
            throw new OperationCanceledException();
    }

    private async Task StreamScreenshotsAsync(string sessionId, ActivePageHolder activePage, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                if (activePage.TryResolve(out var page) && page is not null)
                    await CaptureScreenshotAsync(sessionId, page, cancellationToken);

                await Task.Delay(800, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (PlaywrightException ex) when (ex.Message.Contains("closed", StringComparison.OrdinalIgnoreCase))
            {
                await Task.Delay(1000, cancellationToken);
            }
            catch (PlaywrightException)
            {
                await Task.Delay(1000, cancellationToken);
            }
        }
    }

    private async Task TryCaptureScreenshotAsync(string sessionId, ActivePageHolder? activePage)
    {
        if (activePage is null || !activePage.TryResolve(out var page) || page is null)
            return;

        try
        {
            await CaptureScreenshotAsync(sessionId, page, CancellationToken.None);
        }
        catch
        {
            // Страница могла закрыться — не маскируем исходную ошибку.
        }
    }

    private async Task CaptureScreenshotAsync(string sessionId, IPage page, CancellationToken cancellationToken)
    {
        if (page.IsClosed)
            return;

        var bytes = await page.ScreenshotAsync(new PageScreenshotOptions
        {
            Type = ScreenshotType.Jpeg,
            Quality = 70,
            FullPage = false
        });

        await _eventReporter.ReportAsync(new SessionEvent
        {
            SessionId = sessionId,
            Type = SessionEventType.Screenshot,
            ScreenshotBase64 = Convert.ToBase64String(bytes)
        }, cancellationToken);
    }

    private static async Task<ILocator> FindSearchInputAsync(IPage page)
    {
        var selectors = new[]
        {
            "input[type='search']",
            "input[placeholder*='Поиск']",
            "input[placeholder*='поиск']",
            "input[placeholder*='Искать']",
            "input[aria-label*='Поиск']",
            "input[name='search']",
            ".search-input input",
            "[data-testid='search-input']"
        };

        foreach (var selector in selectors)
        {
            var locator = page.Locator(selector).First;
            if (await locator.CountAsync() > 0 && await locator.IsVisibleAsync())
                return locator;
        }

        throw new InvalidOperationException("Строка поиска на Яндекс Играх не найдена.");
    }

    private static async Task<ILocator?> FindGameLinkAsync(IPage page, YandexGamesSearchOptions options)
    {
        var byUrl = page.Locator($"a[href*='{options.TargetGameUrlPart}']").First;
        if (await byUrl.CountAsync() > 0)
            return byUrl;

        var byTitle = page.Locator($"a:has-text('{options.TargetGameTitle}')").First;
        if (await byTitle.CountAsync() > 0)
            return byTitle;

        var fallbackQuery = options.TargetGameTitle.Split(' ')[0];
        var fallback = page.Locator($"a:has-text('{fallbackQuery}')").First;
        if (await fallback.CountAsync() > 0)
            return fallback;

        return null;
    }

    private static async Task TryClickAsync(IPage page, string[] selectors)
    {
        foreach (var selector in selectors)
        {
            var locator = page.Locator(selector).First;
            if (await locator.CountAsync() > 0 && await locator.IsVisibleAsync())
            {
                await locator.ClickAsync(new LocatorClickOptions { Timeout = 3000 });
                return;
            }
        }
    }

    private async Task ReportLogAsync(string sessionId, string message, CancellationToken cancellationToken)
    {
        await _eventReporter.ReportAsync(new SessionEvent
        {
            SessionId = sessionId,
            Type = SessionEventType.Log,
            Message = message
        }, cancellationToken);
    }

    private async Task ReportStatusAsync(string sessionId, SessionStatus status, CancellationToken cancellationToken)
    {
        await _eventReporter.ReportAsync(new SessionEvent
        {
            SessionId = sessionId,
            Type = SessionEventType.StatusChanged,
            Status = status,
            Message = status.ToString()
        }, cancellationToken);
    }

    private static bool IsBrowserClosedException(Exception ex)
    {
        for (var current = ex; current is not null; current = current.InnerException)
        {
            if (current.GetType().Name.Contains("TargetClosed", StringComparison.OrdinalIgnoreCase))
                return true;

            if (current.Message.Contains("Target closed", StringComparison.OrdinalIgnoreCase)
                || current.Message.Contains("Browser has been closed", StringComparison.OrdinalIgnoreCase)
                || current.Message.Contains("has been closed", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
