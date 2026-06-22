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
    private readonly ISessionPreviewCoordinator _previewCoordinator;
    private readonly SessionRemoteInputCoordinator _remoteInput;
    private readonly IProxyIpChangeCoordinator _proxyIpCoordinator;
    private readonly string _profilesRoot;

    public BrowserSessionRunner(
        PlaywrightBrowserFactory browserFactory,
        ICookieStore cookieStore,
        ISessionEventReporter eventReporter,
        ISessionPauseCoordinator pauseCoordinator,
        ISessionPreviewCoordinator previewCoordinator,
        SessionRemoteInputCoordinator remoteInput,
        IProxyIpChangeCoordinator proxyIpCoordinator,
        ProfileRepository profileRepository)
    {
        _browserFactory = browserFactory;
        _cookieStore = cookieStore;
        _eventReporter = eventReporter;
        _pauseCoordinator = pauseCoordinator;
        _previewCoordinator = previewCoordinator;
        _remoteInput = remoteInput;
        _proxyIpCoordinator = proxyIpCoordinator;
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
        var ipChangeCts = new CancellationTokenSource();
        var stallState = new SessionStallState();
        var activity = new SessionActivityTracker();
        using var linkedSessionCts = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken, ipChangeCts.Token, stallState.Token);
        var sessionCt = linkedSessionCts.Token;
        var ipChangeState = new IpChangeDetectorState();
        string? sessionBaselineIp = null;
        string? sessionProxyHostKey = null;
        IDisposable? proxyIpRegistration = null;

        try
        {
            await ReportStatusAsync(sessionId, SessionStatus.Starting, sessionCt);

            IBrowserContext context = null!;
            IPage page = null!;
            var useProxy = options.UseProxy;
            var maxProxyAttempts = useProxy ? 5 : 1;
            if (!useProxy)
            {
                await ReportLogAsync(sessionId, "Прокси отключён в настройках слота", sessionCt);
            }

            for (var proxyTry = 0; proxyTry < maxProxyAttempts; proxyTry++)
            {
                sessionProfile = DeviceFingerprintRotator.RotateForSession(
                    profile,
                    _profilesRoot,
                    useProxy,
                    options.ProxyUrl,
                    options.DevicePlatform);
                if (useProxy && sessionProfile.ProxyUrl is null)
                {
                    await ReportLogAsync(
                        sessionId,
                        options.ProxyUrl is null
                            ? "Прокси включён, но не выбран в слоте и не найден в profiles/proxies.txt — добавьте прокси в UI или proxies.txt"
                            : "Прокси включён, но URL не удалось применить",
                        sessionCt);
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
                    sessionCt);

                launch = await _browserFactory.LaunchPersistentAsync(sessionProfile, options, _profilesRoot, sessionCt);
                context = launch.Context;
                activePage = new ActivePageHolder { Context = context, Page = launch.Page, UrlPart = options.TargetGameUrlPart };
                page = activePage.Page;

                await SessionLocationHelper.ApplyAsync(context, page, sessionProfile, sessionCt);

                var connectivity = await SessionNetworkHelper.CheckProxyConnectivityAsync(page, sessionCt);
                var publicIp = connectivity.PublicIp;
                if (publicIp is not null)
                {
                    if (sessionProfile.ProxyUrl is not null)
                        ProxyRotator.ReportSuccess(sessionProfile.ProxyUrl);

                    sessionBaselineIp = publicIp;

                    await _eventReporter.ReportAsync(new SessionEvent
                    {
                        SessionId = sessionId,
                        Type = SessionEventType.IpDetected,
                        PublicIp = publicIp
                    }, sessionCt);

                    await ReportLogAsync(
                        sessionId,
                        $"Внешний IP сессии: {publicIp}",
                        sessionCt);

                    var ipGeo = await SessionLocationHelper.LookupByIpAsync(page, publicIp, sessionCt);
                    if (ipGeo is not null && sessionProfile.ProxyUrl is not null)
                    {
                        SessionLocationHelper.ApplyLookupToProfile(sessionProfile, ipGeo);
                        await SessionLocationHelper.ApplyAsync(context, page, sessionProfile, sessionCt);
                        await ReportLogAsync(
                            sessionId,
                            $"Локация по IP прокси: {SessionLocationHelper.Format(sessionProfile)}",
                            sessionCt);
                    }
                    else
                    {
                        await ReportLogAsync(
                            sessionId,
                            $"Локация: {SessionLocationHelper.Format(sessionProfile)}",
                            sessionCt);
                    }

                    await ReportLogAsync(sessionId, "Проверка доступа к yandex.ru через прокси…", sessionCt);
                    if (!await SessionNetworkHelper.ProbeTargetReachableAsync(page, "https://yandex.ru/", sessionCt))
                    {
                        ProxyRotator.ReportFailure(sessionProfile.ProxyUrl);
                        await ReportLogAsync(
                            sessionId,
                            $"Прокси не открывает yandex.ru — попытка {proxyTry + 1}/{maxProxyAttempts}. " +
                            ProxyUrlHelper.DescribeProxyFailureHint(sessionProfile.ProxyUrl, "ERR_TUNNEL_CONNECTION_FAILED"),
                            sessionCt);

                        accumulatedTrafficBytes += trafficMonitor?.TotalBytes ?? 0;
                        if (trafficMonitor is not null)
                            await trafficMonitor.DisposeAsync();
                        trafficMonitor = null;
                        await launch.DisposeAsync();
                        launch = null;
                        continue;
                    }

                    await ReportLogAsync(sessionId, "yandex.ru доступен через прокси", sessionCt);
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
                            sessionCt);

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
                        sessionCt);
                }

                await _cookieStore.LoadAsync(context, sessionProfile.CookiesPath, sessionCt);
                trafficMonitor = await SessionTrafficMonitor.AttachAsync(context, sessionCt);
                _remoteInput.Register(profile.Id, () =>
                {
                    if (activePage is null)
                        return null;
                    try
                    {
                        return activePage.ResolveForPreview();
                    }
                    catch
                    {
                        return null;
                    }
                });
                _ = StreamScreenshotsAsync(sessionId, profile.Id, activePage, screenshotCts.Token);
                _ = StreamTrafficAsync(sessionId, () => accumulatedTrafficBytes, () => trafficMonitor, activity, trafficCts.Token);
                _ = StreamGeoDriftAsync(activePage, sessionProfile, geoCts.Token);
                _ = CaptchaTabWatcher.RunAsync(
                    sessionId,
                    profile.Id,
                    activePage,
                    _pauseCoordinator,
                    _eventReporter,
                    sessionCt);
                _ = SessionActivityWatchdog.RunAsync(
                    sessionId,
                    profile.Id,
                    activePage,
                    activity,
                    stallState,
                    _pauseCoordinator,
                    _eventReporter,
                    sessionCt);

                // Успешный старт: дальше выполняем сценарий.
                activity.MarkActivity();
                break;
            }

            if (launch is null)
                throw new PlaywrightException(useProxy
                    ? "All proxy attempts failed."
                    : "Browser launch failed.");

            if (useProxy && sessionBaselineIp is not null && activePage is not null)
            {
                sessionProxyHostKey = ProxyUrlHelper.ExtractHostKey(sessionProfile!.ProxyUrl);
                if (sessionProxyHostKey is not null)
                {
                    proxyIpRegistration = _proxyIpCoordinator.RegisterSession(new ProxyIpSessionRegistration
                    {
                        ProfileId = profile.Id,
                        SessionId = sessionId,
                        ProxyHostKey = sessionProxyHostKey,
                        BaselineIp = sessionBaselineIp,
                        Reporter = _eventReporter,
                        OnRemoteIpChange = (oldIp, newIp) =>
                        {
                            ipChangeState.Changed = true;
                            ipChangeState.OldIp = oldIp;
                            ipChangeState.NewIp = newIp;
                            ipChangeCts.Cancel();
                        }
                    });
                }

                _ = SessionIpWatchdog.RunAsync(
                    sessionId,
                    profile.Id,
                    activePage,
                    sessionBaselineIp,
                    sessionProxyHostKey,
                    ipChangeState,
                    ipChangeCts,
                    _eventReporter,
                    _proxyIpCoordinator,
                    sessionCt);
                await ReportLogAsync(sessionId, "Мониторинг смены IP прокси включён (проверка каждые 2 мин)", sessionCt);
            }

            await RunStepAsync(sessionId, profile.Id, 1, "Прогрев: открытие yandex.ru", activity, sessionCt, async () =>
            {
                page = activePage!.Resolve();
                await ReportLogAsync(sessionId, "Открываем yandex.ru…", sessionCt);
                await SessionNavigationHelper.GotoWithRetryAsync(
                    page,
                    "https://yandex.ru/",
                    sessionCt,
                    attempts: 2,
                    timeoutMs: 35_000,
                    onProgress: msg => ReportLogAsync(sessionId, msg, sessionCt),
                    captchaSessionId: sessionId,
                    captchaReporter: _eventReporter);
                await ReportLogAsync(sessionId, "yandex.ru открыт", sessionCt);
                await HumanBehavior.DelayAsync(3000, 5000, sessionCt);
                await ReportLogAsync(sessionId, "Закрываем всплывающие окна…", sessionCt);
                await YandexUiHelper.DismissPopupsAsync(page, sessionCt);
                await WaitCaptchaAsync(page, context, profile.Id, sessionId, activePage, sessionCt);
                await ReportLogAsync(sessionId, "Прокрутка страницы…", sessionCt);
                await HumanBehavior.ScrollNaturallyAsync(page, sessionCt);
                await HumanBehavior.DelayAsync(2000, 4000, sessionCt);
            });

            await RunStepAsync(sessionId, profile.Id, 2, "Прогрев: новости и главная Яндекса", activity, sessionCt, async () =>
            {
                page = activePage!.Resolve();
                await YandexUiHelper.DismissPopupsAsync(page, sessionCt);
                await YandexWarmupHelper.BrowseNewsBlocksAsync(page, sessionId, _eventReporter, sessionCt);
                await HumanBehavior.MoveMouseRandomlyAsync(page, sessionCt);
                await HumanBehavior.DelayAsync(2000, 4000, sessionCt);
            });

            await RunStepAsync(sessionId, profile.Id, 3, "Переход на Яндекс Игры", activity, sessionCt, async () =>
            {
                page = activePage!.Resolve();
                await SessionNavigationHelper.GotoWithRetryAsync(
                    page,
                    "https://yandex.ru/games",
                    sessionCt,
                    captchaSessionId: sessionId,
                    captchaReporter: _eventReporter);
                await HumanBehavior.DelayAsync(3000, 5000, sessionCt);
                await YandexUiHelper.DismissPopupsAsync(page, sessionCt);
                await WaitCaptchaAsync(page, context, profile.Id, sessionId, activePage, sessionCt);
            });

            await RunStepAsync(sessionId, profile.Id, 4, "Закрытие cookie-баннера и попапов", activity, sessionCt, async () =>
            {
                page = activePage!.Resolve();
                await YandexUiHelper.DismissPopupsAsync(page, sessionCt);
                await TryClickAsync(page,
                [
                    "button:has-text('Принять')",
                    "button:has-text('OK')",
                    "button:has-text('Хорошо')",
                    "[data-testid='gdpr-accept']"
                ]);
                await HumanBehavior.DelayAsync(800, 1500, sessionCt);
            });

            await RunStepAsync(sessionId, profile.Id, 5, "Открытие строки поиска", activity, sessionCt, async () =>
            {
                page = activePage!.Resolve();
                await WaitCaptchaAsync(page, context, profile.Id, sessionId, activePage, sessionCt);
                await YandexUiHelper.DismissPopupsAsync(page, sessionCt);

                try
                {
                    await YandexUiHelper.FocusSearchInputAsync(page, sessionCt);
                }
                catch (InvalidOperationException)
                {
                    var searchUrl =
                        $"https://yandex.ru/games/search?query={Uri.EscapeDataString(options.SearchQuery)}";
                    await ReportLogAsync(sessionId, "Строка поиска не найдена — открываем поиск по URL", sessionCt);
                    await SessionNavigationHelper.GotoWithRetryAsync(
                        page,
                        searchUrl,
                        sessionCt,
                        captchaSessionId: sessionId,
                        captchaReporter: _eventReporter);
                    await HumanBehavior.DelayAsync(2000, 3500, sessionCt);
                }
            });

            await RunStepAsync(sessionId, profile.Id, 6, $"Ввод запроса «{options.SearchQuery}»", activity, sessionCt, async () =>
            {
                page = activePage!.Resolve();
                var filled = await YandexUiHelper.FillSearchQueryAsync(page, options.SearchQuery, sessionCt);
                if (!filled)
                {
                    var searchUrl =
                        $"https://yandex.ru/games/search?query={Uri.EscapeDataString(options.SearchQuery)}";
                    await ReportLogAsync(sessionId, "Поле поиска перекрыто — открываем поиск по URL", sessionCt);
                    await SessionNavigationHelper.GotoWithRetryAsync(
                        page,
                        searchUrl,
                        sessionCt,
                        captchaSessionId: sessionId,
                        captchaReporter: _eventReporter);
                    await HumanBehavior.DelayAsync(2000, 3500, sessionCt);
                }
            });

            await RunStepAsync(sessionId, profile.Id, 7, "Запуск поиска", activity, sessionCt, async () =>
            {
                page = activePage!.Resolve();
                if (page.Url.Contains("/search", StringComparison.OrdinalIgnoreCase))
                {
                    await HumanBehavior.DelayAsync(1000, 2000, sessionCt);
                    return;
                }

                await HumanBehavior.DelayAsync(500, 1200, sessionCt);
                await page.Keyboard.PressAsync("Enter");
                await page.WaitForLoadStateAsync(LoadState.DOMContentLoaded, new PageWaitForLoadStateOptions { Timeout = 30_000 });
                await HumanBehavior.DelayAsync(2500, 4000, sessionCt);
                await YandexUiHelper.DismissPopupsAsync(page, sessionCt);
                await WaitCaptchaAsync(page, context, profile.Id, sessionId, activePage, sessionCt);
            });

            await RunStepAsync(sessionId, profile.Id, 8, "Ожидание результатов поиска", activity, sessionCt, async () =>
            {
                page = activePage!.Resolve();
                var found = await SessionNavigationHelper.TryWaitForSearchResultsAsync(page, sessionCt);
                if (!found)
                {
                    await ReportLogAsync(sessionId,
                        "Выдача не появилась — открываем поиск по URL",
                        sessionCt);
                    var searchUrl =
                        $"https://yandex.ru/games/search?query={Uri.EscapeDataString(options.SearchQuery)}";
                    await SessionNavigationHelper.GotoWithRetryAsync(
                        page,
                        searchUrl,
                        sessionCt,
                        captchaSessionId: sessionId,
                        captchaReporter: _eventReporter);
                    await HumanBehavior.DelayAsync(2500, 4000, sessionCt);
                    await YandexUiHelper.DismissPopupsAsync(page, sessionCt);
                    await WaitCaptchaAsync(page, context, profile.Id, sessionId, activePage, sessionCt);
                    found = await SessionNavigationHelper.TryWaitForSearchResultsAsync(page, sessionCt, 40_000);
                }

                if (!found)
                    throw new InvalidOperationException(
                        $"Результаты поиска «{options.SearchQuery}» не загрузились — проверьте капчу или селекторы.");

                await HumanBehavior.ScrollNaturallyAsync(page, sessionCt);
            });

            ILocator? gameLink = null;
            await RunStepAsync(sessionId, profile.Id, 9, $"Поиск игры «{options.TargetGameTitle}»", activity, sessionCt, async () =>
            {
                page = activePage!.Resolve();
                gameLink = await FindGameLinkAsync(page, options);
                if (gameLink is null)
                    throw new InvalidOperationException(
                        $"Игра «{options.TargetGameTitle}» не найдена в выдаче по запросу «{options.SearchQuery}».");
            });

            await RunStepAsync(sessionId, profile.Id, 10, "Переход на страницу игры", activity, sessionCt, async () =>
            {
                page = activePage!.Resolve();
                gameLink ??= await FindGameLinkAsync(page, options)
                             ?? throw new InvalidOperationException("Ссылка на игру не найдена.");

                await ReportLogAsync(sessionId, "Прокрутка к карточке игры…", sessionCt);
                try
                {
                    await gameLink.ScrollIntoViewIfNeededAsync(
                        new LocatorScrollIntoViewIfNeededOptions { Timeout = 10_000 });
                }
                catch
                {
                    await HumanBehavior.ScrollNaturallyAsync(page, sessionCt);
                }

                var gamePage = await YandexUiHelper.OpenGamePageAsync(
                    context,
                    page,
                    gameLink,
                    options.TargetGameUrlPart,
                    options.TargetGameUrl,
                    sessionId,
                    _eventReporter,
                    sessionCt);

                activePage!.Page = gamePage;
                page = gamePage;

                await YandexUiHelper.FocusGameTabAsync(context, gamePage, sessionCt);
                await HumanBehavior.DelayAsync(2000, 4000, sessionCt);
                await WaitCaptchaAsync(page, context, profile.Id, sessionId, activePage, sessionCt);
                await ReportLogAsync(sessionId, $"Открыта вкладка игры: {page.Url}", sessionCt);
            });

            await RunStepAsync(sessionId, profile.Id, 11, "Нажатие кнопки «Играть» и загрузка игры", activity, sessionCt, async () =>
            {
                var landscapeState = new LandscapeState();
                var stuckTracker = new SessionStuckTracker();

                var gamePage = await YandexUiHelper.EnterGameAsync(
                    context,
                    activePage!.Resolve(),
                    options.TargetGameUrl,
                    options.TargetGameUrlPart,
                    sessionId,
                    _eventReporter,
                    sessionCt,
                    sessionProfile,
                    landscapeState,
                    stuckTracker,
                    profile.Id,
                    _pauseCoordinator,
                    activePage);

                activePage!.Page = gamePage;
                await YandexUiHelper.WaitForGameFullyLoadedAsync(
                    gamePage,
                    sessionCt,
                    context: context,
                    profile: sessionProfile,
                    sessionId: sessionId,
                    reporter: _eventReporter,
                    landscapeState: landscapeState,
                    stuckTracker: stuckTracker,
                    profileId: profile.Id,
                    pauseCoordinator: _pauseCoordinator,
                    activePage: activePage);

                for (var recoverAttempt = 0; recoverAttempt < 4; recoverAttempt++)
                {
                    if (await YandexUiHelper.IsGameRunningAsync(gamePage) &&
                        !await YandexUiHelper.IsGameLoadErrorVisibleAsync(gamePage))
                    {
                        break;
                    }

                    if (!await YandexUiHelper.TryRecoverLoadFailureAsync(
                            gamePage, sessionId, _eventReporter, stuckTracker, sessionCt))
                    {
                        break;
                    }

                    await YandexUiHelper.DismissPopupsAsync(gamePage, sessionCt);
                    await WaitCaptchaAsync(gamePage, context, profile.Id, sessionId, activePage, sessionCt);

                    gamePage = await YandexUiHelper.EnterGameAsync(
                        context,
                        gamePage,
                        options.TargetGameUrl,
                        options.TargetGameUrlPart,
                        sessionId,
                        _eventReporter,
                        sessionCt,
                        sessionProfile,
                        landscapeState,
                        stuckTracker,
                        profile.Id,
                        _pauseCoordinator,
                        activePage);

                    activePage!.Page = gamePage;

                    await YandexUiHelper.WaitForGameFullyLoadedAsync(
                        gamePage,
                        sessionCt,
                        context: context,
                        profile: sessionProfile,
                        sessionId: sessionId,
                        reporter: _eventReporter,
                        landscapeState: landscapeState,
                        stuckTracker: stuckTracker,
                        profileId: profile.Id,
                        pauseCoordinator: _pauseCoordinator,
                        activePage: activePage);
                }

                if (await YandexUiHelper.IsGameLoadErrorVisibleAsync(gamePage) ||
                    !await YandexUiHelper.IsGameRunningAsync(gamePage))
                {
                    await SessionScreenDiagnostic.TriggerRestartAsync(
                        sessionId,
                        gamePage,
                        "Игра не запустилась — на экране нет игрового canvas",
                        _eventReporter,
                        sessionCt);
                }

                await ReportLogAsync(sessionId, "✓ Игра загружена (экран «Загрузка» завершён)", sessionCt);
            });

            var playMinutes = options.PlayDurationMinSeconds / 60.0;
            var playMinutesMax = options.PlayDurationMaxSeconds / 60.0;
            _ = playMinutes;
            _ = playMinutesMax;
            var rotateSessionAfterPlay = false;
            await RunStepAsync(sessionId, profile.Id, 12, "Игра (непрерывный цикл)", activity, sessionCt, async () =>
            {
                page = activePage!.Resolve();
                var landscapeState = new LandscapeState { Applied = true };
                var stuckTracker = new SessionStuckTracker();

                await YandexUiHelper.WaitForGameFullyLoadedAsync(
                    page,
                    sessionCt,
                    context: context,
                    profile: sessionProfile,
                    sessionId: sessionId,
                    reporter: _eventReporter,
                    landscapeState: landscapeState,
                    stuckTracker: stuckTracker,
                    profileId: profile.Id,
                    pauseCoordinator: _pauseCoordinator,
                    activePage: activePage);

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
                    sessionCt,
                    sessionProfile,
                    stuckTracker,
                    landscapeState,
                    activePage);
                playGameOverCount = playOutcome.GamesPlayed;

                if (playOutcome.RotateSession)
                {
                    await ReportLogAsync(
                        sessionId,
                        $"Смена устройства после {playOutcome.GamesPlayed} игр — перезапуск с новым профилем",
                        sessionCt);
                    await _cookieStore.SaveAsync(context, sessionProfile!.CookiesPath, sessionCt);
                    rotateSessionAfterPlay = true;
                }
            });

            if (rotateSessionAfterPlay)
                return SessionRunResult.RestartAfterGameOvers(playGameOverCount);

            await _cookieStore.SaveAsync(context, sessionProfile!.CookiesPath, sessionCt);
            return SessionRunResult.Completed(playGameOverCount);
        }
        catch (SessionStuckException ex)
        {
            return SessionRunResult.RestartAfterDiagnostic(playGameOverCount, ex.Message);
        }
        catch (OperationCanceledException)
        {
            if (stallState.IsTriggered && !cancellationToken.IsCancellationRequested)
            {
                await ReportLogAsync(
                    sessionId,
                    $"Перезапуск после зависания: {stallState.Reason}",
                    CancellationToken.None);
                return SessionRunResult.RestartAfterDiagnostic(playGameOverCount, stallState.Reason);
            }

            if (ipChangeState.Changed && !cancellationToken.IsCancellationRequested)
            {
                if (sessionProxyHostKey is not null)
                {
                    await ReportLogAsync(
                        sessionId,
                        $"IP сменился: {ipChangeState.OldIp} → {ipChangeState.NewIp} — перезапуск сессии (остальные сессии на том же прокси получат сигнал)",
                        CancellationToken.None);
                }
                else
                {
                    await ReportLogAsync(
                        sessionId,
                        $"IP сменился: {ipChangeState.OldIp} → {ipChangeState.NewIp} — перезапуск сессии",
                        CancellationToken.None);
                }

                return SessionRunResult.RestartAfterIpChange(
                    playGameOverCount,
                    ipChangeState.OldIp,
                    ipChangeState.NewIp);
            }

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
            await TryCaptureScreenshotAsync(sessionId, profile.Id, activePage);

            if (IsBrowserClosedException(ex))
            {
                await ReportLogAsync(sessionId, "Браузер закрыт — сессия будет перезапущена", CancellationToken.None);
                await ReportLogAsync(sessionId, "Перезапуск…", CancellationToken.None);
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
            await ReportLogAsync(sessionId, "Перезапуск…", CancellationToken.None);
            return SessionRunResult.BrowserClosed(playGameOverCount);
        }
        finally
        {
            proxyIpRegistration?.Dispose();
            _remoteInput.Clear(profile.Id);
            ipChangeCts.Cancel();
            ipChangeCts.Dispose();
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
        ActivePageHolder activePage,
        DesktopProfile profile,
        CancellationToken sessionCt)
    {
        var random = new Random();
        while (!sessionCt.IsCancellationRequested)
        {
            try
            {
                var delayMinutes = random.Next(4, 9);
                await Task.Delay(TimeSpan.FromMinutes(delayMinutes), sessionCt);

                if (!activePage.TryResolve(out var page) || page is null)
                    continue;

                SessionLocationHelper.ApplyRandomDrift(profile, random);
                await SessionLocationHelper.ApplyAsync(page.Context, page, profile, sessionCt);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch
            {
                await Task.Delay(60_000, sessionCt);
            }
        }
    }

    private async Task StreamTrafficAsync(
        string sessionId,
        Func<long> accumulatedBytes,
        Func<SessionTrafficMonitor?> monitor,
        SessionActivityTracker activity,
        CancellationToken sessionCt)
    {
        long lastReported = -1;
        while (!sessionCt.IsCancellationRequested)
        {
            try
            {
                var total = accumulatedBytes() + (monitor()?.TotalBytes ?? 0);
                activity.MarkTraffic(total);
                if (total != lastReported)
                {
                    lastReported = total;
                    await ReportTrafficUpdateAsync(sessionId, total, sessionCt);
                }

                await Task.Delay(8_000, sessionCt);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch
            {
                await Task.Delay(8_000, sessionCt);
            }
        }
    }

    private Task WaitCaptchaAsync(
        IPage page,
        IBrowserContext context,
        string profileId,
        string sessionId,
        ActivePageHolder? activePage,
        CancellationToken cancellationToken) =>
        CaptchaHelper.WaitForManualSolveAsync(
            page,
            sessionId,
            _eventReporter,
            cancellationToken,
            context: context,
            profileId: profileId,
            pauseCoordinator: _pauseCoordinator,
            activePage: activePage);

    private Task ReportTrafficUpdateAsync(string sessionId, long bytes, CancellationToken sessionCt) =>
        _eventReporter.ReportAsync(new SessionEvent
        {
            SessionId = sessionId,
            Type = SessionEventType.TrafficUpdated,
            TrafficBytes = bytes,
            Message = SessionTrafficMonitor.FormatBytes(bytes)
        }, sessionCt);

    private async Task RunStepAsync(
        string sessionId,
        string profileId,
        int step,
        string stepName,
        SessionActivityTracker activity,
        CancellationToken sessionCt,
        Func<Task> action)
    {
        await _pauseCoordinator.WaitIfPausedAsync(profileId, sessionId, _eventReporter, sessionCt);

        activity.MarkStep(step);

        await _eventReporter.ReportAsync(new SessionEvent
        {
            SessionId = sessionId,
            Type = SessionEventType.StepChanged,
            CurrentStep = step,
            TotalSteps = TotalSteps,
            StepName = stepName,
            Message = stepName,
            Status = SessionStatus.Running
        }, sessionCt);

        await ReportLogAsync(sessionId, $"Шаг {step}/{TotalSteps}: {stepName}", sessionCt);
        await ReportStatusAsync(sessionId, SessionStatus.Running, sessionCt);

        await action();

        await _pauseCoordinator.WaitIfPausedAsync(profileId, sessionId, _eventReporter, sessionCt);

        if (sessionCt.IsCancellationRequested)
            throw new OperationCanceledException();
    }

    private async Task StreamScreenshotsAsync(
        string sessionId,
        string profileId,
        ActivePageHolder activePage,
        CancellationToken sessionCt)
    {
        while (!sessionCt.IsCancellationRequested)
        {
            try
            {
                if (!_previewCoordinator.IsEnabled(profileId))
                {
                    await Task.Delay(300, sessionCt);
                    continue;
                }

                if (activePage.TryResolve(out var page) && page is not null)
                {
                    var frame = await _remoteInput.TryCaptureFrameAsync(profileId, sessionCt);
                    if (!string.IsNullOrWhiteSpace(frame))
                        _previewCoordinator.SetLastFrame(profileId, frame);
                }

                var delayMs = _previewCoordinator.TakeImmediateCaptureRequest(profileId) ? 250 : 800;
                await Task.Delay(delayMs, sessionCt);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (PlaywrightException ex) when (ex.Message.Contains("closed", StringComparison.OrdinalIgnoreCase))
            {
                await Task.Delay(1000, sessionCt);
            }
            catch (PlaywrightException)
            {
                await Task.Delay(1000, sessionCt);
            }
        }
    }

    private async Task TryCaptureScreenshotAsync(string sessionId, string profileId, ActivePageHolder? activePage)
    {
        if (activePage is null || !activePage.TryResolve(out var page) || page is null)
            return;

        try
        {
            var frame = await SessionScreenshotHelper.CapturePageAsync(page, CancellationToken.None);
            if (!string.IsNullOrWhiteSpace(frame))
                _previewCoordinator.SetLastFrame(profileId, frame);
        }
        catch
        {
            // Страница могла закрыться — не маскируем исходную ошибку.
        }
    }

    private async Task CaptureScreenshotAsync(
        string sessionId,
        string profileId,
        IPage page,
        CancellationToken sessionCt)
    {
        var frame = await SessionScreenshotHelper.CapturePageAsync(page, sessionCt);
        if (!string.IsNullOrWhiteSpace(frame))
            _previewCoordinator.SetLastFrame(profileId, frame);
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

    private async Task ReportLogAsync(string sessionId, string message, CancellationToken sessionCt)
    {
        await _eventReporter.ReportAsync(new SessionEvent
        {
            SessionId = sessionId,
            Type = SessionEventType.Log,
            Message = message
        }, sessionCt);
    }

    private async Task ReportStatusAsync(string sessionId, SessionStatus status, CancellationToken sessionCt)
    {
        await _eventReporter.ReportAsync(new SessionEvent
        {
            SessionId = sessionId,
            Type = SessionEventType.StatusChanged,
            Status = status,
            Message = status.ToString()
        }, sessionCt);
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
