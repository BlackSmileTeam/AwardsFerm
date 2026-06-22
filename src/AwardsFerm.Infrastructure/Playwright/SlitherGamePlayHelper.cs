using AwardsFerm.Core.Interfaces;
using AwardsFerm.Core.Models;
using AwardsFerm.Infrastructure.Behavior;
using Microsoft.Playwright;

namespace AwardsFerm.Infrastructure.Playwright;

/// <summary>
/// Сценарий игры Slither Worms Wars: меню «Играть», ползание, боковая реклама Яндекса, «Играть снова».
/// </summary>
internal static class SlitherGamePlayHelper
{
    internal sealed class PlaySessionOutcome
    {
        public int GamesPlayed { get; init; }
        public bool RotateSession { get; init; }
    }

    private sealed class GameOverAdState
    {
        public int SinceAd;
        public int UntilNextAd = Random.Shared.Next(1, 4);
    }

    private static readonly Random Random = new();

    private const int GamesPerSession = 20;

    private static readonly string[] GameOverSelectors =
    [
        "h2.font-display:has-text('Игра окончена')",
        "h2.bg-red-500:has-text('Игра окончена')",
        "h2:has-text('Игра окончена!')"
    ];

    private static readonly string[] InGamePlaySelectors =
    [
        "button.bg-red-500:has-text('Играть')",
        "main button.font-display:has-text('Играть')",
        "button.uppercase:has-text('Играть')"
    ];

    private static readonly string[] PlayAgainSelectors =
    [
        "button:has-text('Играть снова')",
        "button.bg-red-500:has-text('Играть снова')"
    ];

    private static readonly string[] StickyAdSelectors =
    [
        "#yandex-adv-sticky-banner-desktop",
        ".yandex-sticky-adv-banner__desktop-wrapper",
        ".yandex-sticky-adv-banner_desktop_right:not(.yandex-sticky-adv-banner_hidden)"
    ];

    public static async Task<PlaySessionOutcome> PlaySessionAsync(
        IBrowserContext context,
        IPage page,
        string profileId,
        int minSeconds,
        int maxSeconds,
        string sessionId,
        ISessionEventReporter reporter,
        ISessionPauseCoordinator pauseCoordinator,
        CancellationToken cancellationToken = default,
        DesktopProfile? deviceProfile = null,
        SessionStuckTracker? stuckTracker = null,
        LandscapeState? landscapeState = null,
        ActivePageHolder? activePage = null)
    {
        _ = minSeconds;
        _ = maxSeconds;

        var gameOverCount = 0;
        var gameOverAdState = new GameOverAdState();

        await page.BringToFrontAsync();
        await HumanBehavior.DelayAsync(1500, 2500, cancellationToken);

        if (await TryClickInGameMenuPlayAsync(page, cancellationToken))
        {
            await LogAsync(sessionId, reporter, "Нажата кнопка «Играть» в меню игры", cancellationToken);
            await HumanBehavior.DelayAsync(2000, 3500, cancellationToken);
        }

        landscapeState ??= new LandscapeState { Applied = true };

        while (!cancellationToken.IsCancellationRequested)
        {
            await pauseCoordinator.WaitIfPausedAsync(profileId, sessionId, reporter, cancellationToken);

            if (page.IsClosed)
                throw new PlaywrightException("Target closed");

            await page.BringToFrontAsync();
            await CaptchaHelper.WaitForManualSolveAsync(
                page, sessionId, reporter, cancellationToken,
                context: context, profileId: profileId, pauseCoordinator: pauseCoordinator, activePage: activePage);

            if (await YandexUiHelper.IsGameLoadErrorVisibleAsync(page))
            {
                if (await YandexUiHelper.TryRecoverLoadFailureAsync(
                        page, sessionId, reporter, stuckTracker, cancellationToken))
                {
                    continue;
                }

                await SessionScreenDiagnostic.TriggerRestartAsync(
                    sessionId, page, "Ошибка загрузки игры во время игрового цикла", reporter, cancellationToken);
            }

            await OrientationHelper.EnsureLandscapeForGameAsync(
                page, context, deviceProfile, sessionId, reporter, cancellationToken, landscapeState, stuckTracker);

            if (await IsGameOverVisibleAsync(page))
            {
                var (rotate, _, count) = await ProcessGameOverAsync(
                    context, page, profileId, sessionId, reporter, pauseCoordinator, activePage,
                    gameOverCount, gameOverAdState, cancellationToken);
                gameOverCount = count;
                if (rotate is not null)
                    return rotate;

                continue;
            }

            if (await IsInGameMenuVisibleAsync(page))
            {
                await YandexUiHelper.DismissFullscreenAdIfVisibleAsync(page, cancellationToken);

                if (await TryClickInGameMenuPlayAsync(page, cancellationToken))
                {
                    await LogAsync(sessionId, reporter, "Снова нажата «Играть» в меню", cancellationToken);
                    await HumanBehavior.DelayAsync(2000, 3500, cancellationToken);
                }

                continue;
            }

            if (!await YandexUiHelper.IsGameRunningAsync(page))
            {
                if (stuckTracker is not null && stuckTracker.Register("idle_no_game"))
                {
                    await SessionScreenDiagnostic.TriggerRestartAsync(
                        sessionId,
                        page,
                        "Игра не отвечает — нет canvas, меню и экрана «Игра окончена»",
                        reporter,
                        cancellationToken);
                }
            }
            else
            {
                stuckTracker?.Reset("idle_no_game");
            }

            // Игра идёт — рекламу не кликаем, только закрываем блокирующую полноэкранную
            await YandexUiHelper.DismissFullscreenAdIfVisibleAsync(page, cancellationToken);

            var chunkSeconds = Random.Next(20, 35);
            var stoppedByGameOver = await SlitherChunkAsync(
                context, page, profileId, chunkSeconds, sessionId, reporter, pauseCoordinator, activePage, cancellationToken);
            if (stoppedByGameOver)
            {
                var (rotate, _, count) = await ProcessGameOverAsync(
                    context, page, profileId, sessionId, reporter, pauseCoordinator, activePage,
                    gameOverCount, gameOverAdState, cancellationToken);
                gameOverCount = count;
                if (rotate is not null)
                    return rotate;
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        return new PlaySessionOutcome { GamesPlayed = gameOverCount, RotateSession = false };
    }

    private static async Task<(PlaySessionOutcome? Rotate, bool AdShown, int GamesPlayed)> ProcessGameOverAsync(
        IBrowserContext context,
        IPage page,
        string profileId,
        string sessionId,
        ISessionEventReporter reporter,
        ISessionPauseCoordinator pauseCoordinator,
        ActivePageHolder? activePage,
        int gameOverCount,
        GameOverAdState gameOverAdState,
        CancellationToken cancellationToken)
    {
        gameOverCount++;
        await LogAsync(sessionId, reporter, $"Игра окончена ({gameOverCount}/{GamesPerSession})", cancellationToken);

        var adShown = await HandleGameOverCycleAsync(
            context, page, profileId, sessionId, reporter, pauseCoordinator, activePage,
            gameOverAdState, cancellationToken);
        if (page.IsClosed)
            throw new PlaywrightException("Target closed");

        if (gameOverCount >= GamesPerSession)
        {
            await LogAsync(sessionId, reporter,
                $"{GamesPerSession} игр в сессии — закрываем браузер, следующий запуск с новым устройством",
                cancellationToken);
            return (new PlaySessionOutcome { GamesPlayed = gameOverCount, RotateSession = true }, adShown, gameOverCount);
        }

        return (null, adShown, gameOverCount);
    }

    private static async Task<bool> HandleGameOverCycleAsync(
        IBrowserContext context,
        IPage page,
        string profileId,
        string sessionId,
        ISessionEventReporter reporter,
        ISessionPauseCoordinator pauseCoordinator,
        ActivePageHolder? activePage,
        GameOverAdState adState,
        CancellationToken cancellationToken)
    {
        adState.SinceAd++;
        var adShown = false;

        if (adState.SinceAd >= adState.UntilNextAd)
        {
            adState.SinceAd = 0;
            adState.UntilNextAd = Random.Next(1, 4);
            await LogAsync(sessionId, reporter,
                $"После поражения — смотрим боковую рекламу (следующий раз через {adState.UntilNextAd} поражений)…",
                cancellationToken);

            if (await IsStickyAdVisibleAsync(page))
            {
                await HandleStickyAdAsync(context, page, sessionId, reporter, cancellationToken, force: true);
                adShown = true;
            }
            else if (await YandexUiHelper.IsFullscreenAdVisibleForInteractionAsync(page))
            {
                await LogAsync(sessionId, reporter,
                    "Боковой баннер не виден — взаимодействуем с полноэкранной рекламой (приоритет высокого CPC)…",
                    cancellationToken);
                await YandexUiHelper.InteractWithFullscreenAdIfVisibleAsync(
                    page, sessionId, reporter, cancellationToken);
                adShown = true;
            }
            else
            {
                await LogAsync(sessionId, reporter, "Боковая реклама не видна — пропускаем", cancellationToken);
                await HumanBehavior.DelayAsync(1500, 2500, cancellationToken);
            }
        }
        else
        {
            var remaining = adState.UntilNextAd - adState.SinceAd;
            await LogAsync(sessionId, reporter,
                $"Игра окончена — реклама через ещё {remaining} поражений", cancellationToken);
            await HumanBehavior.DelayAsync(800, 1500, cancellationToken);
        }

        await CaptchaHelper.WaitForManualSolveAsync(
            page, sessionId, reporter, cancellationToken,
            context: context, profileId: profileId, pauseCoordinator: pauseCoordinator, activePage: activePage);

        if (await TryClickPlayAgainAsync(page, cancellationToken))
        {
            await LogAsync(sessionId, reporter, "Нажато «Играть снова»", cancellationToken);
            await HumanBehavior.DelayAsync(2000, 3500, cancellationToken);
            return adShown;
        }

        if (await TryClickInGameMenuPlayAsync(page, cancellationToken))
        {
            await LogAsync(sessionId, reporter, "Нажато «Играть» в меню", cancellationToken);
            await HumanBehavior.DelayAsync(2000, 3500, cancellationToken);
        }

        return adShown;
    }

    private static async Task<bool> SlitherChunkAsync(
        IBrowserContext context,
        IPage page,
        string profileId,
        int seconds,
        string sessionId,
        ISessionEventReporter reporter,
        ISessionPauseCoordinator pauseCoordinator,
        ActivePageHolder? activePage,
        CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(seconds);
        var checks = 0;

        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await pauseCoordinator.WaitIfPausedAsync(profileId, sessionId, reporter, cancellationToken);
            if (page.IsClosed)
                return false;

            if (++checks % 8 == 0)
            {
                if (await CaptchaHelper.IsPresentInContextAsync(context))
                    await CaptchaHelper.WaitForManualSolveAsync(
                        page, sessionId, reporter, cancellationToken,
                        context: context, profileId: profileId, pauseCoordinator: pauseCoordinator, activePage: activePage);

                if (await IsGameOverVisibleAsync(page))
                    return true;
            }

            var box = await GetGamePlayAreaBoxAsync(page);
            if (box is null)
            {
                await HumanBehavior.DelayAsync(500, 1000, cancellationToken);
                continue;
            }

            var x = (float)(box.X + box.Width * (0.15 + Random.NextDouble() * 0.7));
            var y = (float)(box.Y + box.Height * (0.15 + Random.NextDouble() * 0.7));

            await page.Mouse.MoveAsync(x, y, new MouseMoveOptions { Steps = Random.Next(12, 28) });

            if (Random.Next(3) == 0)
                await page.Mouse.DownAsync();

            await HumanBehavior.DelayAsync(80, 200, cancellationToken);

            if (Random.Next(3) == 0)
                await page.Mouse.UpAsync();

            if (Random.Next(4) == 0)
                await page.Mouse.ClickAsync(x, y);

            await HumanBehavior.DelayAsync(200, 600, cancellationToken);
        }

        try
        {
            await page.Mouse.UpAsync();
        }
        catch
        {
            // ignore
        }

        return false;
    }

    private static async Task HandleStickyAdAsync(
        IBrowserContext context,
        IPage gamePage,
        string sessionId,
        ISessionEventReporter reporter,
        CancellationToken cancellationToken,
        bool force = false)
    {
        if (!await IsStickyAdVisibleAsync(gamePage))
            return;

        var stickyProbe = await HighCpcAdClassifier.ProbeStickyAdTextAsync(gamePage);
        var stickyCategory = HighCpcAdClassifier.Classify(stickyProbe);
        await LogAsync(sessionId, reporter,
            stickyCategory == HighCpcAdClassifier.HighCpcCategory.Unknown
                ? "Боковая реклама — категория не определена, кликаем доступный баннер…"
                : $"Боковая реклама — {HighCpcAdClassifier.Describe(stickyCategory)}, клик и просмотр (не менее 30 сек)…",
            cancellationToken);

        var adLocator = await FindStickyAdLocatorAsync(gamePage);
        if (adLocator is null)
            return;

        var popupTask = context.WaitForPageAsync(new BrowserContextWaitForPageOptions { Timeout = 8000 });
        var urlBefore = gamePage.Url;

        try
        {
            await HumanBehavior.MoveAndClickAsync(gamePage, adLocator, cancellationToken);
        }
        catch
        {
            try
            {
                await adLocator.ClickAsync(new LocatorClickOptions { Timeout = 5000, Force = true });
            }
            catch
            {
                return;
            }
        }

        await HumanBehavior.DelayAsync(1500, 3000, cancellationToken);

        IPage? adPage = null;
        if (popupTask.IsCompletedSuccessfully)
        {
            try
            {
                adPage = await popupTask;
            }
            catch
            {
                // ignore
            }
        }

        if (adPage is not null && !adPage.IsClosed && adPage != gamePage)
        {
            try
            {
                await LogAdPageCategoryAsync(adPage, sessionId, reporter, cancellationToken);
                await InteractWithAdPageAsync(adPage, cancellationToken);
            }
            finally
            {
                await CloseAdPageSafelyAsync(adPage, cancellationToken);
            }
        }
        else if (!gamePage.Url.Equals(urlBefore, StringComparison.OrdinalIgnoreCase))
        {
            await LogAdPageCategoryAsync(gamePage, sessionId, reporter, cancellationToken);
            await InteractWithAdPageAsync(gamePage, cancellationToken);
            try
            {
                await gamePage.GoBackAsync(new PageGoBackOptions { Timeout = 15_000 });
            }
            catch
            {
                // ignore
            }
        }

        await gamePage.BringToFrontAsync();
        await HumanBehavior.DelayAsync(1000, 2000, cancellationToken);

        if (gamePage.IsClosed)
            throw new PlaywrightException("Target closed");

        await YandexUiHelper.DismissFullscreenAdIfVisibleAsync(gamePage, cancellationToken);

        if (await IsGameOverVisibleAsync(gamePage))
        {
            await LogAsync(sessionId, reporter, "После рекламы — «Играть снова»", cancellationToken);
            await TryClickPlayAgainAsync(gamePage, cancellationToken);
            await HumanBehavior.DelayAsync(1500, 2500, cancellationToken);
        }
        else if (await IsInGameMenuVisibleAsync(gamePage))
        {
            await TryClickInGameMenuPlayAsync(gamePage, cancellationToken);
            await HumanBehavior.DelayAsync(1500, 2500, cancellationToken);
        }
    }

    private static async Task LogAdPageCategoryAsync(
        IPage adPage,
        string sessionId,
        ISessionEventReporter reporter,
        CancellationToken cancellationToken)
    {
        var category = await HighCpcAdClassifier.ClassifyPageAsync(adPage);
        await LogAsync(sessionId, reporter,
            $"Страница рекламодателя: {HighCpcAdClassifier.Describe(category)} · {adPage.Url}",
            cancellationToken);
    }

    private const int MinAdPageSeconds = 30;

    private static async Task InteractWithAdPageAsync(IPage adPage, CancellationToken cancellationToken)
    {
        if (adPage.IsClosed)
            return;

        var alertCount = 0;
        void OnDialog(object? _, IDialog dialog)
        {
            alertCount++;
            _ = dialog.DismissAsync();
        }

        adPage.Dialog += OnDialog;

        try
        {
            try
            {
                await adPage.WaitForLoadStateAsync(LoadState.DOMContentLoaded,
                    new PageWaitForLoadStateOptions { Timeout = 15_000 });
            }
            catch
            {
                // ignore
            }

            var interactUntil = DateTimeOffset.UtcNow.AddSeconds(MinAdPageSeconds + Random.Next(0, 20));
            var linkClicks = 0;
            var ticks = 0;

            while (DateTimeOffset.UtcNow < interactUntil)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (adPage.IsClosed)
                    return;

                if (alertCount >= 3)
                {
                    await TryDismissAlertsAsync(adPage, cancellationToken);
                    return;
                }

                try
                {
                    ticks++;
                    if (ticks % 3 == 0)
                        await TryDismissAlertsAsync(adPage, cancellationToken);

                    if (linkClicks < 6 && (ticks % 2 == 0 || Random.Next(3) == 0))
                    {
                        if (await TryClickBestCpcLinkAsync(adPage, cancellationToken))
                        {
                            linkClicks++;
                            await adPage.WaitForLoadStateAsync(LoadState.DOMContentLoaded,
                                new PageWaitForLoadStateOptions { Timeout = 15_000 });
                            await HumanBehavior.DelayAsync(2500, 5000, cancellationToken);
                            await HumanBehavior.ScrollNaturallyAsync(adPage, cancellationToken);
                            continue;
                        }
                    }

                    var action = Random.Next(4);
                    switch (action)
                    {
                        case 0:
                            await HumanBehavior.ScrollNaturallyAsync(adPage, cancellationToken);
                            break;
                        case 1:
                            await TryClickRandomButtonAsync(adPage, cancellationToken);
                            break;
                        default:
                        {
                            var vp = adPage.ViewportSize;
                            var w = vp?.Width ?? 1280;
                            var h = vp?.Height ?? 720;
                            var x = Random.Next(80, Math.Max(100, w - 80));
                            var y = Random.Next(80, Math.Max(100, h - 80));
                            await adPage.Mouse.MoveAsync(x, y, new MouseMoveOptions { Steps = Random.Next(8, 20) });
                            break;
                        }
                    }
                }
                catch
                {
                    // ignore transient ad page errors
                }

                await HumanBehavior.DelayAsync(1000, 2500, cancellationToken);
            }
        }
        finally
        {
            adPage.Dialog -= OnDialog;
        }
    }

    private static async Task TryDismissAlertsAsync(IPage page, CancellationToken cancellationToken)
    {
        if (page.IsClosed)
            return;

        try
        {
            await page.Keyboard.PressAsync("Escape");
            await HumanBehavior.DelayAsync(200, 500, cancellationToken);
            await page.Keyboard.PressAsync("Escape");
        }
        catch
        {
            // ignore
        }

        try
        {
            var closed = await page.EvaluateAsync<bool>(
                """
                () => {
                  const closeSelectors = [
                    '[role="alertdialog"] button',
                    '.modal button.close',
                    '.popup-close',
                    '[aria-label="Close"]',
                    '[aria-label="Закрыть"]',
                    'button:has-text("OK")',
                    'button:has-text("Ок")',
                    'button:has-text("Закрыть")'
                  ];
                  for (const sel of closeSelectors) {
                    const el = document.querySelector(sel);
                    if (el && el.offsetParent !== null) { el.click(); return true; }
                  }
                  const dlg = document.querySelector('dialog[open]');
                  if (dlg && typeof dlg.close === 'function') { dlg.close(); return true; }
                  return false;
                }
                """);
            if (closed)
                await HumanBehavior.DelayAsync(300, 700, cancellationToken);
        }
        catch
        {
            // ignore
        }
    }

    private static async Task CloseAdPageSafelyAsync(IPage adPage, CancellationToken cancellationToken)
    {
        if (adPage.IsClosed)
            return;

        try
        {
            await adPage.CloseAsync();
        }
        catch
        {
            try
            {
                await TryDismissAlertsAsync(adPage, cancellationToken);
                await adPage.CloseAsync();
            }
            catch
            {
                // ignore
            }
        }
    }

    private static async Task<bool> TryClickBestCpcLinkAsync(IPage page, CancellationToken cancellationToken)
    {
        try
        {
            var href = await HighCpcAdClassifier.PickBestLinkHrefAsync(page);

            if (string.IsNullOrWhiteSpace(href))
                return false;

            await page.GotoAsync(href, new PageGotoOptions
            {
                WaitUntil = WaitUntilState.DOMContentLoaded,
                Timeout = 20_000
            });
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static async Task TryClickRandomButtonAsync(IPage page, CancellationToken cancellationToken)
    {
        var buttons = page.Locator("button:visible, a[role='button']:visible, [role='button']:visible");
        var count = await buttons.CountAsync();
        if (count == 0)
            return;

        var index = Random.Next(Math.Min(count, 8));
        var btn = buttons.Nth(index);
        if (!await btn.IsVisibleAsync())
            return;

        try
        {
            await HumanBehavior.MoveAndClickAsync(page, btn, cancellationToken);
        }
        catch
        {
            // ignore
        }
    }

    private static async Task<bool> TryClickInGameMenuPlayAsync(IPage page, CancellationToken cancellationToken)
    {
        await YandexUiHelper.DismissFullscreenAdIfVisibleAsync(page, cancellationToken);

        var frame = GetGameFrame(page);
        if (frame is not null && await TryClickInFrameAsync(page, frame, InGamePlaySelectors, "Играть", cancellationToken))
            return true;

        return await TryClickInScopeAsync(page, page.Locator("body"), InGamePlaySelectors, "Играть", cancellationToken);
    }

    private static async Task<bool> TryClickPlayAgainAsync(IPage page, CancellationToken cancellationToken)
    {
        var frame = GetGameFrame(page);
        if (frame is not null && await TryClickInFrameAsync(page, frame, PlayAgainSelectors, "Играть снова", cancellationToken))
            return true;

        return await TryClickInScopeAsync(page, page.Locator("body"), PlayAgainSelectors, "Играть снова", cancellationToken);
    }

    private static async Task<bool> TryClickInScopeAsync(
        IPage page,
        ILocator scope,
        string[] selectors,
        string exactText,
        CancellationToken cancellationToken)
    {
        foreach (var selector in selectors)
        {
            try
            {
                var button = scope.Locator(selector).First;
                if (await button.CountAsync() == 0 || !await button.IsVisibleAsync())
                    continue;

                var text = (await button.InnerTextAsync()).Trim();
                if (!text.Equals(exactText, StringComparison.OrdinalIgnoreCase) &&
                    !text.StartsWith(exactText, StringComparison.OrdinalIgnoreCase))
                    continue;

                await HumanBehavior.MoveAndClickAsync(page, button, cancellationToken);
                return true;
            }
            catch
            {
                // try next
            }
        }

        try
        {
            var byRole = scope.GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = exactText }).First;
            if (await byRole.CountAsync() > 0 && await byRole.IsVisibleAsync())
            {
                await HumanBehavior.MoveAndClickAsync(page, byRole, cancellationToken);
                return true;
            }
        }
        catch
        {
            // ignore
        }

        return false;
    }

    private static async Task<bool> TryClickInFrameAsync(
        IPage page,
        IFrameLocator frame,
        string[] selectors,
        string exactText,
        CancellationToken cancellationToken)
    {
        foreach (var selector in selectors)
        {
            try
            {
                var button = frame.Locator(selector).First;
                if (await button.CountAsync() == 0 || !await button.IsVisibleAsync())
                    continue;

                var text = (await button.InnerTextAsync()).Trim();
                if (!text.Equals(exactText, StringComparison.OrdinalIgnoreCase) &&
                    !text.StartsWith(exactText, StringComparison.OrdinalIgnoreCase))
                    continue;

                await HumanBehavior.MoveAndClickAsync(page, button, cancellationToken);
                return true;
            }
            catch
            {
                // try next
            }
        }

        try
        {
            var byRole = frame.GetByRole(AriaRole.Button, new FrameLocatorGetByRoleOptions { Name = exactText }).First;
            if (await byRole.CountAsync() > 0 && await byRole.IsVisibleAsync())
            {
                await HumanBehavior.MoveAndClickAsync(page, byRole, cancellationToken);
                return true;
            }
        }
        catch
        {
            // ignore
        }

        return false;
    }

    private static async Task<bool> IsInGameMenuVisibleAsync(IPage page)
    {
        var frame = GetGameFrame(page);
        if (frame is not null)
        {
            try
            {
                var title = frame.Locator("h1:has-text('Slither Worms Wars')").First;
                if (await title.CountAsync() > 0 && await title.IsVisibleAsync())
                    return true;

                foreach (var selector in InGamePlaySelectors)
                {
                    var button = frame.Locator(selector).First;
                    if (await button.CountAsync() > 0 && await button.IsVisibleAsync())
                    {
                        var text = (await button.InnerTextAsync()).Trim();
                        if (text.Equals("Играть", StringComparison.OrdinalIgnoreCase))
                            return true;
                    }
                }
            }
            catch
            {
                // ignore
            }
        }

        foreach (var selector in InGamePlaySelectors)
        {
            try
            {
                var button = page.Locator(selector).First;
                if (await button.CountAsync() > 0 && await button.IsVisibleAsync())
                {
                    var text = (await button.InnerTextAsync()).Trim();
                    if (text.Equals("Играть", StringComparison.OrdinalIgnoreCase))
                        return true;
                }
            }
            catch
            {
                // ignore
            }
        }

        return false;
    }

    private static async Task<bool> IsGameOverVisibleAsync(IPage page)
    {
        var frame = GetGameFrame(page);

        foreach (var selector in GameOverSelectors)
        {
            if (frame is not null)
            {
                try
                {
                    var heading = frame.Locator(selector).First;
                    if (await heading.CountAsync() > 0 && await heading.IsVisibleAsync())
                        return true;
                }
                catch
                {
                    // ignore
                }
            }

            try
            {
                var heading = page.Locator(selector).First;
                if (await heading.CountAsync() > 0 && await heading.IsVisibleAsync())
                    return true;
            }
            catch
            {
                // ignore
            }
        }

        return false;
    }

    private static async Task<bool> IsStickyAdVisibleAsync(IPage page)
    {
        foreach (var selector in StickyAdSelectors)
        {
            try
            {
                var ad = page.Locator(selector).First;
                if (await ad.CountAsync() == 0 || !await ad.IsVisibleAsync())
                    continue;

                var box = await ad.BoundingBoxAsync();
                if (box is { Width: >= 80, Height: >= 80 })
                    return true;
            }
            catch
            {
                // ignore
            }
        }

        return false;
    }

    private static async Task<ILocator?> FindStickyAdLocatorAsync(IPage page)
    {
        foreach (var selector in StickyAdSelectors)
        {
            try
            {
                var ad = page.Locator(selector).First;
                if (await ad.CountAsync() > 0 && await ad.IsVisibleAsync())
                {
                    var box = await ad.BoundingBoxAsync();
                    if (box is { Width: >= 80, Height: >= 80 })
                        return ad;
                }
            }
            catch
            {
                // ignore
            }
        }

        return null;
    }

    private static async Task<LocatorBoundingBoxResult?> GetGamePlayAreaBoxAsync(IPage page)
    {
        var frame = GetGameFrame(page);
        if (frame is not null)
        {
            try
            {
                var canvas = frame.Locator("canvas").First;
                if (await canvas.CountAsync() > 0 && await canvas.IsVisibleAsync())
                {
                    var box = await canvas.BoundingBoxAsync();
                    if (box is { Width: >= 200, Height: >= 150 })
                        return box;
                }

                var main = frame.Locator("main").First;
                if (await main.CountAsync() > 0 && await main.IsVisibleAsync())
                {
                    var box = await main.BoundingBoxAsync();
                    if (box is { Width: >= 300, Height: >= 200 })
                        return box;
                }
            }
            catch
            {
                // ignore
            }
        }

        var gameFrame = page.Locator("#game-frame").First;
        if (await gameFrame.CountAsync() > 0 && await gameFrame.IsVisibleAsync())
            return await gameFrame.BoundingBoxAsync();

        return null;
    }

    private static IFrameLocator? GetGameFrame(IPage page)
    {
        try
        {
            return page.FrameLocator("#game-frame");
        }
        catch
        {
            return null;
        }
    }

    private static async Task LogAsync(
        string sessionId,
        ISessionEventReporter reporter,
        string message,
        CancellationToken cancellationToken)
    {
        await reporter.ReportAsync(new SessionEvent
        {
            SessionId = sessionId,
            Type = SessionEventType.Log,
            Message = message
        }, cancellationToken);
    }
}
