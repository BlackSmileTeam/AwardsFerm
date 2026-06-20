using AwardsFerm.Core.Interfaces;
using AwardsFerm.Core.Models;
using AwardsFerm.Infrastructure.Behavior;
using Microsoft.Playwright;

namespace AwardsFerm.Infrastructure.Playwright;

internal static class YandexUiHelper
{
    private static readonly string[] SafeDismissSelectors =
    [
        "button:has-text('Нет')",
        "button:has-text('Не сейчас')",
        "button:has-text('Позже')",
        "button:has-text('Отмена')",
        "button:has-text('Принять')",
        "button:has-text('OK')",
        "button:has-text('Хорошо')",
        "[data-testid='gdpr-accept']"
    ];

    private static readonly string[] FullDismissSelectors =
    [
        "button:has-text('Закрыть')",
        "[aria-label='Закрыть']",
        "[aria-label='Close']",
        ".modal__close",
        ".popup2__close"
    ];

  /// <summary>Кнопка «Играть» в play-guard-dialog (страж перед запуском игры).</summary>
    private static readonly string[] PlayGuardPlaySelectors =
    [
        "button[data-guard-accept='play_button']",
        ".play-guard-dialog__apply-button",
        ".play-guard-dialog__apply button",
        ".play-guard-dialog button[data-guard-accept]"
    ];

    private static readonly string[] ModalPlaySelectors =
    [
        "[class*='game'] button:has-text('Играть')",
        "[class*='Game'] button:has-text('Играть')",
        "[class*='modal'] button:has-text('Играть')",
        "[class*='overlay'] button:has-text('Играть')",
        "[class*='popup'] button:has-text('Играть')",
        "[class*='play'] button:has-text('Играть')",
        "button:has-text('Играть')",
        "a:has-text('Играть')",
        "[role='button']:has-text('Играть')"
    ];

    /// <summary>Кнопка закрытия полноэкранной рекламы Yandex после play-guard.</summary>
    private static readonly string[] FullscreenAdCloseSelectors =
    [
        "[data-testid='yandex-fullscreen-render-button']",
        "button.close-button_type_adv-fullscreen",
        ".play-modal_visible button.close-button",
        ".play-yandex-modal_visible button.close-button",
        "button[aria-label='Закрыть'].close-button_type_adv-fullscreen"
    ];

    public static async Task DismissPopupsAsync(IPage page, CancellationToken cancellationToken = default)
    {
        if (page.IsClosed)
            return;

        // Не трогаем попапы, пока виден play-guard или полноэкранная реклама.
        if (await IsPlayGuardDialogVisibleAsync(page) || await IsFullscreenAdVisibleAsync(page))
            return;

        var isGamePage = page.Url.Contains("/games/app/", StringComparison.OrdinalIgnoreCase);
        var dismissSelectors = isGamePage
            ? SafeDismissSelectors.Where(s =>
                !s.Contains("Отмена", StringComparison.Ordinal) &&
                !s.Contains("guard-close", StringComparison.OrdinalIgnoreCase)).ToArray()
            : SafeDismissSelectors;
        await DismissSelectorsAsync(page, dismissSelectors, cancellationToken, isGamePage);

        if (!isGamePage)
            await DismissSelectorsAsync(page, FullDismissSelectors, cancellationToken, isGamePage: false);

        try
        {
            var defaultSearchDecline = page.Locator("text=Яндекс станет основным поиском").Locator("..")
                .Locator("button:has-text('Нет'), button:has-text('Не сейчас')").First;
            if (await defaultSearchDecline.CountAsync() > 0 && await defaultSearchDecline.IsVisibleAsync())
                await defaultSearchDecline.ClickAsync(new LocatorClickOptions { Timeout = 2000 });
        }
        catch
        {
            // ignore
        }
    }

    public static async Task FocusGameTabAsync(IBrowserContext context, IPage gamePage, CancellationToken cancellationToken = default)
    {
        foreach (var p in context.Pages.ToList())
        {
            if (p == gamePage || p.IsClosed)
                continue;

            try
            {
                await p.CloseAsync();
            }
            catch
            {
                // ignore
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        await gamePage.BringToFrontAsync();
    }

    public static async Task<IPage> EnterGameAsync(
        IBrowserContext context,
        IPage? page,
        string gameUrl,
        string targetUrlPart,
        string sessionId,
        ISessionEventReporter reporter,
        CancellationToken cancellationToken = default,
        DesktopProfile? profile = null,
        LandscapeState? landscapeState = null,
        SessionStuckTracker? stuckTracker = null)
    {
        page = await ReacquireGamePageAsync(context, page, gameUrl, targetUrlPart, cancellationToken);
        await FocusGameTabAsync(context, page, cancellationToken);
        await page.BringToFrontAsync();
        await OrientationHelper.EnsureLandscapeForGameAsync(
            page, context, profile, sessionId, reporter, cancellationToken, landscapeState, stuckTracker);

        if (await IsGameRunningAsync(page))
            return page;

        await WaitForEntryBarrierAsync(page, cancellationToken);

        for (var attempt = 1; attempt <= 5; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            page = await ReacquireGamePageAsync(context, page, gameUrl, targetUrlPart, cancellationToken);

            if (await TryRecoverLoadFailureAsync(page, sessionId, reporter, stuckTracker, cancellationToken))
                continue;

            if (!page.Url.Contains(targetUrlPart, StringComparison.OrdinalIgnoreCase))
            {
                await page.GotoAsync(gameUrl, new PageGotoOptions
                {
                    WaitUntil = WaitUntilState.DOMContentLoaded,
                    Timeout = 60_000
                });
                await HumanBehavior.DelayAsync(2500, 4000, cancellationToken);
                await CaptchaHelper.WaitForManualSolveAsync(page, sessionId, reporter, cancellationToken);
            }

            await FocusGameTabAsync(context, page, cancellationToken);
            await DismissPopupsAsync(page, cancellationToken);
            await CaptchaHelper.WaitForManualSolveAsync(page, sessionId, reporter, cancellationToken);
            await OrientationHelper.EnsureLandscapeForGameAsync(
                page, context, profile, sessionId, reporter, cancellationToken, landscapeState, stuckTracker);

            if (await IsGameRunningAsync(page))
                return page;

            await WaitForEntryBarrierAsync(page, cancellationToken);

            // Реклама появляется всегда; play-guard с кнопкой «Играть» — не всегда.
            if (await IsFullscreenAdVisibleAsync(page))
            {
                await reporter.ReportAsync(new SessionEvent
                {
                    SessionId = sessionId,
                    Type = SessionEventType.Log,
                    Message = "Обнаружена полноэкранная реклама, закрываем…"
                }, cancellationToken);
                await DismissFullscreenAdAsync(page, cancellationToken);

                if (await IsGameRunningAsync(page))
                    return page;

                await HumanBehavior.DelayAsync(1000, 2000, cancellationToken);
                continue;
            }

            var guardVisible = await IsPlayGuardDialogVisibleAsync(page);
            if (guardVisible)
            {
                await reporter.ReportAsync(new SessionEvent
                {
                    SessionId = sessionId,
                    Type = SessionEventType.Log,
                    Message = "Обнаружен play-guard, нажимаем «Играть»…"
                }, cancellationToken);
            }
            else if (await IsLoadingScreenAsync(page))
            {
                await WaitForGameFullyLoadedAsync(
                    page, cancellationToken, context: context, profile: profile,
                    sessionId: sessionId, reporter: reporter,
                    landscapeState: landscapeState, stuckTracker: stuckTracker);
                if (await IsGameRunningAsync(page))
                    return page;
                continue;
            }
            else if (!await IsEntryBarrierVisibleAsync(page))
            {
                // Ни рекламы, ни play-guard — возможно guard уже принят, ждём загрузку.
                try
                {
                    await WaitForGameFullyLoadedAsync(
                        page, cancellationToken, context: context, profile: profile,
                        sessionId: sessionId, reporter: reporter,
                        landscapeState: landscapeState, stuckTracker: stuckTracker);
                    if (await IsGameRunningAsync(page))
                        return page;
                }
                catch (TimeoutException) when (attempt < 5)
                {
                    // повторим цикл
                }

                continue;
            }

            if (await IsGameRunningAsync(page))
                return page;

            await reporter.ReportAsync(new SessionEvent
            {
                SessionId = sessionId,
                Type = SessionEventType.Log,
                Message = guardVisible
                    ? $"Клик по «Играть» в play-guard-dialog (попытка {attempt}/5)…"
                    : $"Клик по «Играть» (попытка {attempt}/5)…"
            }, cancellationToken);

            var clicked = await ClickPlayGuardButtonAsync(page, cancellationToken)
                || await ClickModalPlayButtonAsync(page, cancellationToken);
            if (!clicked)
            {
                clicked = await ClickPlayButtonViaJavaScriptAsync(page, cancellationToken);
            }

            if (!clicked)
            {
                await HumanBehavior.DelayAsync(1500, 2500, cancellationToken);
                continue;
            }

            page = await ReacquireGamePageAsync(context, page, gameUrl, targetUrlPart, cancellationToken);
            await WaitForPlayGuardDismissedAsync(page, cancellationToken);

            if (await IsFullscreenAdVisibleAsync(page))
            {
                await reporter.ReportAsync(new SessionEvent
                {
                    SessionId = sessionId,
                    Type = SessionEventType.Log,
                    Message = "После «Играть» появилась реклама, закрываем…"
                }, cancellationToken);
                await DismissFullscreenAdAsync(page, cancellationToken);
            }

            try
            {
                await WaitForGameFullyLoadedAsync(
                    page, cancellationToken, context: context, profile: profile,
                    sessionId: sessionId, reporter: reporter,
                    landscapeState: landscapeState, stuckTracker: stuckTracker);
                if (await IsGameRunningAsync(page))
                    return page;
            }
            catch (TimeoutException) when (attempt < 5)
            {
                await reporter.ReportAsync(new SessionEvent
                {
                    SessionId = sessionId,
                    Type = SessionEventType.Log,
                    Message = "Загрузка затянулась, повторная попытка…"
                }, cancellationToken);
            }
        }

        throw new InvalidOperationException(
            "Игра не запустилась после нажатия «Играть». Canvas/iframe игры не появился.");
    }

    public static async Task<bool> IsGameRunningAsync(IPage page)
    {
        if (page.IsClosed)
            return false;

        if (await IsPlayGuardDialogVisibleAsync(page))
            return false;

        if (await IsFullscreenAdVisibleAsync(page))
            return false;

        if (!await IsGuardAcceptedAsync(page))
            return false;

        if (await IsLoadingScreenAsync(page))
            return false;

        if (await IsGameLoaderVisibleAsync(page))
            return false;

        if (await HasLargeCanvasAsync(page.Locator("canvas")))
            return true;

        if (await HasLargeGameIframeAsync(page))
            return true;

        foreach (var frame in page.Frames)
        {
            try
            {
                if (await HasLargeCanvasAsync(frame.Locator("canvas")))
                    return true;
            }
            catch
            {
                // frame detached
            }
        }

        return false;
    }

    /// <summary>
    /// Ждёт исчезновения «Загрузка» и появления игрового canvas/iframe.
    /// </summary>
    public static async Task WaitForGameFullyLoadedAsync(
        IPage page,
        CancellationToken cancellationToken = default,
        int maxWaitSeconds = 120,
        IBrowserContext? context = null,
        DesktopProfile? profile = null,
        string? sessionId = null,
        ISessionEventReporter? reporter = null,
        LandscapeState? landscapeState = null,
        SessionStuckTracker? stuckTracker = null)
    {
        if (page.IsClosed)
            throw new InvalidOperationException("Страница игры закрыта.");

        var deadline = DateTimeOffset.UtcNow.AddSeconds(maxWaitSeconds);
        var sawLoading = false;
        DateTimeOffset? loadingSince = null;

        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (reporter is not null && sessionId is not null &&
                await TryRecoverLoadFailureAsync(page, sessionId, reporter, stuckTracker, cancellationToken))
            {
                continue;
            }

            if (context is not null)
            {
                await OrientationHelper.EnsureLandscapeForGameAsync(
                    page, context, profile, sessionId, reporter, cancellationToken,
                    landscapeState, stuckTracker);
            }

            if (await IsFullscreenAdVisibleAsync(page))
            {
                if (stuckTracker is not null &&
                    stuckTracker.Register("fullscreen_ad") &&
                    reporter is not null && sessionId is not null)
                {
                    await SessionScreenDiagnostic.TriggerRestartAsync(
                        sessionId,
                        page,
                        "Повторяющееся закрытие полноэкранной рекламы без прогресса",
                        reporter,
                        cancellationToken);
                }

                await DismissFullscreenAdAsync(page, cancellationToken);
                continue;
            }

            stuckTracker?.Reset("fullscreen_ad");

            if (await IsLoadingScreenAsync(page))
            {
                sawLoading = true;
                loadingSince ??= DateTimeOffset.UtcNow;
                if (loadingSince is not null &&
                    DateTimeOffset.UtcNow - loadingSince.Value > TimeSpan.FromSeconds(90) &&
                    reporter is not null && sessionId is not null)
                {
                    await SessionScreenDiagnostic.TriggerRestartAsync(
                        sessionId,
                        page,
                        "Загрузка игры длится более 90 секунд",
                        reporter,
                        cancellationToken);
                }

                await Task.Delay(1000, cancellationToken);
                continue;
            }

            loadingSince = null;

            if (await IsGameRunningAsync(page) && !await IsGameLoadErrorVisibleAsync(page))
            {
                await HumanBehavior.DelayAsync(2000, 3000, cancellationToken);
                return;
            }

            if (sawLoading || DateTimeOffset.UtcNow > deadline.AddSeconds(-maxWaitSeconds + 15))
            {
                await Task.Delay(1000, cancellationToken);
                continue;
            }

            await Task.Delay(800, cancellationToken);
        }

        if (await IsGameRunningAsync(page) && !await IsGameLoadErrorVisibleAsync(page))
            return;

        if (reporter is not null && sessionId is not null)
        {
            await SessionScreenDiagnostic.TriggerRestartAsync(
                sessionId,
                page,
                $"Игра не загрузилась за {maxWaitSeconds} сек",
                reporter,
                cancellationToken);
        }

        throw new TimeoutException(
            $"Игра не загрузилась за {maxWaitSeconds} сек (экран «Загрузка» или canvas не появился).");
    }

    public static async Task<bool> IsGameLoadErrorVisibleAsync(IPage page)
    {
        if (page.IsClosed)
            return false;

        if (await SessionNavigationHelper.IsPageUnavailableAsync(page))
            return true;

        if (await SessionNavigationHelper.IsCdnGameFrameBrokenAsync(page))
            return true;

        var errorTexts =
            new[]
            {
                "ошибка загрузки",
                "не удалось загрузить",
                "ошибка при загрузке",
                "что-то пошло не так",
                "failed to load",
                "loading error",
                "something went wrong",
                "try again",
                "повторить",
                "временно недоступ",
                "перемещена по новому",
                "веб-страница по адресу"
            };

        try
        {
            foreach (var text in errorTexts)
            {
                var locator = page.GetByText(text, new PageGetByTextOptions { Exact = false });
                if (await locator.CountAsync() > 0 && await locator.First.IsVisibleAsync())
                    return true;
            }

            foreach (var frame in page.Frames)
            {
                foreach (var text in errorTexts)
                {
                    var locator = frame.GetByText(text, new FrameGetByTextOptions { Exact = false });
                    if (await locator.CountAsync() > 0 && await locator.First.IsVisibleAsync())
                        return true;
                }
            }
        }
        catch
        {
            // ignore
        }

        return false;
    }

    public static async Task<bool> TryRecoverLoadFailureAsync(
        IPage page,
        string sessionId,
        ISessionEventReporter reporter,
        SessionStuckTracker? stuckTracker,
        CancellationToken cancellationToken)
    {
        if (page.IsClosed)
            return false;

        if (!await IsGameLoadErrorVisibleAsync(page))
            return false;

        var cdnSrc = await SessionNavigationHelper.GetCdnGameIframeSrcAsync(page);
        if (cdnSrc is not null &&
            (stuckTracker is null || !stuckTracker.Register("cdn_reload", maxRepeats: 6)))
        {
            await reporter.ReportAsync(new SessionEvent
            {
                SessionId = sessionId,
                Type = SessionEventType.Log,
                Message = "CDN игры недоступен — перезагружаем iframe…"
            }, cancellationToken);

            if (await SessionNavigationHelper.TryReloadCdnGameFrameAsync(page, cancellationToken))
            {
                await HumanBehavior.DelayAsync(2000, 3500, cancellationToken);
                await DismissPopupsAsync(page, cancellationToken);
                if (!await IsGameLoadErrorVisibleAsync(page))
                    return true;
            }
        }

        if (stuckTracker is not null && stuckTracker.Register("page_reload", maxRepeats: 6))
            return false;

        var message = cdnSrc is not null
            ? "CDN игры не восстановился — обновляем страницу…"
            : "Ошибка загрузки — обновляем страницу…";

        await reporter.ReportAsync(new SessionEvent
        {
            SessionId = sessionId,
            Type = SessionEventType.Log,
            Message = message
        }, cancellationToken);

        try
        {
            await SessionNavigationHelper.ReloadResilientAsync(page);
            await HumanBehavior.DelayAsync(2000, 3500, cancellationToken);
            await DismissPopupsAsync(page, cancellationToken);
            await CaptchaHelper.WaitForManualSolveAsync(page, sessionId, reporter, cancellationToken);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static async Task<bool> IsPlayGuardDialogVisibleAsync(IPage page)
    {
        try
        {
            var guard = page.Locator(".play-guard-dialog:not(.play-guard-dialog_hidden)");
            if (await guard.CountAsync() > 0 && await guard.First.IsVisibleAsync())
                return true;

            var applyBlock = page.Locator(".play-guard-dialog__apply");
            if (await applyBlock.CountAsync() > 0 && await applyBlock.First.IsVisibleAsync())
                return true;

            var playBtn = page.Locator("button[data-guard-accept='play_button']");
            if (await playBtn.CountAsync() > 0 && await playBtn.First.IsVisibleAsync())
                return true;
        }
        catch
        {
            // ignore
        }

        return false;
    }

    private static async Task<bool> IsGuardAcceptedAsync(IPage page)
    {
        try
        {
            var hasClass = await page.EvaluateAsync<bool>(
                "() => document.documentElement.classList.contains('without-guard') || document.body.classList.contains('without-guard')");
            if (hasClass)
                return true;
        }
        catch
        {
            // ignore
        }

        return !await IsPlayGuardDialogVisibleAsync(page);
    }

    private static async Task WaitForPlayGuardDismissedAsync(IPage page, CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(45);
        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (await IsGuardAcceptedAsync(page))
                return;

            await Task.Delay(400, cancellationToken);
        }
    }

    private static async Task<bool> IsFullscreenAdVisibleAsync(IPage page)
    {
        try
        {
            var modal = page.Locator(".play-modal.play-modal_visible");
            if (await modal.CountAsync() > 0 && await modal.First.IsVisibleAsync())
                return true;

            var yandexModal = page.Locator(".play-yandex-modal_visible");
            if (await yandexModal.CountAsync() > 0 && await yandexModal.First.IsVisibleAsync())
                return true;

            var advClose = page.Locator("button.close-button_type_adv-fullscreen");
            if (await advClose.CountAsync() > 0 && await advClose.First.IsVisibleAsync())
                return true;

            var advTestId = page.Locator("[data-testid='yandex-fullscreen-render-button']");
            if (await advTestId.CountAsync() > 0 && await advTestId.First.IsVisibleAsync())
                return true;
        }
        catch
        {
            // ignore
        }

        return false;
    }

    public static async Task DismissFullscreenAdIfVisibleAsync(
        IPage page,
        CancellationToken cancellationToken = default)
    {
        if (await IsFullscreenAdVisibleAsync(page))
            await DismissFullscreenAdAsync(page, cancellationToken);
    }

    /// <summary>Открывает полноэкранную рекламу (клик по контенту), проводит на странице ≥30 сек, затем закрывает.</summary>
    public static async Task InteractWithFullscreenAdIfVisibleAsync(
        IPage page,
        CancellationToken cancellationToken = default)
    {
        if (!await IsFullscreenAdVisibleAsync(page))
            return;

        await TryOpenFullscreenAdContentAsync(page, cancellationToken);
        await HumanBehavior.DelayAsync(2000, 4000, cancellationToken);
        await BrowsePageBrieflyAsync(page, 30, cancellationToken);
        await DismissFullscreenAdAsync(page, cancellationToken);
    }

    private static async Task TryOpenFullscreenAdContentAsync(IPage page, CancellationToken cancellationToken)
    {
        var contentSelectors = new[]
        {
            "[data-testid='yandex-fullscreen-render-button']",
            ".play-modal_visible a",
            ".play-modal_visible iframe",
            ".play-yandex-modal_visible a",
            ".play-modal_visible",
            ".play-yandex-modal_visible"
        };

        foreach (var selector in contentSelectors)
        {
            try
            {
                var target = page.Locator(selector).First;
                if (await target.CountAsync() == 0 || !await target.IsVisibleAsync())
                    continue;

                await HumanBehavior.MoveAndClickAsync(page, target, cancellationToken);
                await HumanBehavior.DelayAsync(1500, 3000, cancellationToken);
                return;
            }
            catch
            {
                // try next
            }
        }

        try
        {
            var viewport = page.ViewportSize;
            if (viewport is not null)
            {
                await page.Mouse.ClickAsync(viewport.Width / 2, viewport.Height / 2);
            }
        }
        catch
        {
            // ignore
        }
    }

    private static async Task BrowsePageBrieflyAsync(IPage page, int minSeconds, CancellationToken cancellationToken)
    {
        if (page.IsClosed)
            return;

        var interactUntil = DateTimeOffset.UtcNow.AddSeconds(minSeconds + Random.Shared.Next(0, 15));

        while (DateTimeOffset.UtcNow < interactUntil)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (page.IsClosed)
                return;

            try
            {
                await HumanBehavior.ScrollNaturallyAsync(page, cancellationToken);
                await HumanBehavior.DelayAsync(2000, 5000, cancellationToken);
                await HumanBehavior.MoveMouseRandomlyAsync(page, cancellationToken);
            }
            catch
            {
                return;
            }
        }
    }

    private static async Task DismissFullscreenAdAsync(
        IPage page,
        CancellationToken cancellationToken,
        int maxWaitSeconds = 90)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(maxWaitSeconds);

        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!await IsFullscreenAdVisibleAsync(page))
                return;

            if (await TryClickFullscreenAdCloseAsync(page, cancellationToken))
            {
                await HumanBehavior.DelayAsync(600, 1200, cancellationToken);
                if (!await IsFullscreenAdVisibleAsync(page))
                    return;
            }

            // Реклама может показывать таймер до появления активной кнопки «Закрыть»
            await Task.Delay(2000, cancellationToken);
        }
    }

    private static async Task<bool> TryClickFullscreenAdCloseAsync(IPage page, CancellationToken cancellationToken)
    {
        foreach (var selector in FullscreenAdCloseSelectors)
        {
            try
            {
                var button = page.Locator(selector).First;
                if (await button.CountAsync() == 0 || !await button.IsVisibleAsync())
                    continue;

                if (!await button.IsEnabledAsync())
                    continue;

                await HumanBehavior.MoveAndClickAsync(page, button, cancellationToken);
                return true;
            }
            catch
            {
                // try next selector
            }
        }

        try
        {
            var clicked = await page.EvaluateAsync<bool>(
                """
                () => {
                    const selectors = [
                        '[data-testid="yandex-fullscreen-render-button"]',
                        'button.close-button_type_adv-fullscreen',
                        '.play-modal_visible button.close-button'
                    ];
                    for (const sel of selectors) {
                        const el = document.querySelector(sel);
                        if (!el || el.offsetParent === null) continue;
                        el.dispatchEvent(new PointerEvent('pointerdown', { bubbles: true, cancelable: true }));
                        el.dispatchEvent(new PointerEvent('pointerup', { bubbles: true, cancelable: true }));
                        el.click();
                        return true;
                    }
                    return false;
                }
                """);
            return clicked;
        }
        catch
        {
            return false;
        }
    }

    private static async Task<bool> IsGameLoaderVisibleAsync(IPage page)
    {
        try
        {
            var loader = page.Locator(".game-loader:not(.game-loader_hidden)");
            if (await loader.CountAsync() > 0 && await loader.First.IsVisibleAsync())
                return true;
        }
        catch
        {
            // ignore
        }

        return false;
    }

    private static async Task<bool> IsLoadingScreenAsync(IPage page)
    {
        if (await IsGameLoaderVisibleAsync(page))
            return true;

        try
        {
            var loadingTexts = new[] { "Загрузка", "Loading", "Загружается", "Загружаем игру" };
            foreach (var text in loadingTexts)
            {
                var locator = page.GetByText(text, new PageGetByTextOptions { Exact = false });
                if (await locator.CountAsync() > 0 && await locator.First.IsVisibleAsync())
                    return true;
            }

            foreach (var frame in page.Frames)
            {
                foreach (var text in loadingTexts)
                {
                    var locator = frame.GetByText(text, new FrameGetByTextOptions { Exact = false });
                    if (await locator.CountAsync() > 0 && await locator.First.IsVisibleAsync())
                        return true;
                }
            }
        }
        catch
        {
            // ignore
        }

        return false;
    }

    public static async Task<IPage> OpenGamePageAsync(
        IBrowserContext context,
        IPage currentPage,
        ILocator gameLink,
        string targetUrlPart,
        string gameUrl,
        string? sessionId = null,
        ISessionEventReporter? reporter = null,
        CancellationToken cancellationToken = default)
    {
        var existing = FindGamePage(context, currentPage, targetUrlPart);
        if (existing is not null)
        {
            await ReportOpenProgressAsync(sessionId, reporter, "Страница игры уже открыта", cancellationToken);
            return await FinalizeGamePageAsync(existing, cancellationToken);
        }

        await ReportOpenProgressAsync(sessionId, reporter, "Клик по ссылке на игру в выдаче…", cancellationToken);

        gameLink = await RefreshGameLinkAsync(currentPage, gameLink, targetUrlPart, cancellationToken);
        var clicked = await TryOpenGameViaLinkClickAsync(context, currentPage, gameLink, targetUrlPart, cancellationToken);

        if (clicked is not null)
        {
            await ReportOpenProgressAsync(sessionId, reporter, "Вкладка игры открыта после клика", cancellationToken);
            return await FinalizeGamePageAsync(clicked, cancellationToken);
        }

        await ReportOpenProgressAsync(sessionId, reporter, "Клик не открыл игру, прямой переход по URL…", cancellationToken);
        return await NavigateToGameUrlDirectAsync(context, currentPage, gameUrl, targetUrlPart, cancellationToken);
    }

    private static async Task<ILocator> RefreshGameLinkAsync(
        IPage page,
        ILocator gameLink,
        string targetUrlPart,
        CancellationToken cancellationToken)
    {
        try
        {
            if (await gameLink.CountAsync() > 0 && await gameLink.IsVisibleAsync())
            {
                await gameLink.ScrollIntoViewIfNeededAsync(new LocatorScrollIntoViewIfNeededOptions { Timeout = 10_000 });
                return gameLink;
            }
        }
        catch
        {
            // stale locator — ищем заново
        }

        var refreshed = page.Locator($"a[href*='{targetUrlPart}']").First;
        if (await refreshed.CountAsync() > 0)
        {
            await refreshed.ScrollIntoViewIfNeededAsync(new LocatorScrollIntoViewIfNeededOptions { Timeout = 10_000 });
            return refreshed;
        }

        return gameLink;
    }

    private static async Task<IPage?> TryOpenGameViaLinkClickAsync(
        IBrowserContext context,
        IPage currentPage,
        ILocator gameLink,
        string targetUrlPart,
        CancellationToken cancellationToken)
    {
        var popupTask = context.WaitForPageAsync(new BrowserContextWaitForPageOptions { Timeout = 20_000 });

        try
        {
            await HumanBehavior.MoveAndClickAsync(currentPage, gameLink, cancellationToken);
        }
        catch
        {
            try
            {
                await gameLink.ClickAsync(new LocatorClickOptions { Timeout = 8000 });
            }
            catch
            {
                return null;
            }
        }

        var deadline = DateTimeOffset.UtcNow.AddSeconds(25);

        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var found = FindGamePage(context, currentPage, targetUrlPart);
            if (found is not null)
                return found;

            if (popupTask.IsCompletedSuccessfully)
            {
                try
                {
                    var popup = await popupTask;
                    if (!popup.IsClosed)
                    {
                        if (popup.Url.Contains(targetUrlPart, StringComparison.OrdinalIgnoreCase))
                            return popup;

                        try
                        {
                            await popup.WaitForURLAsync($"**{targetUrlPart}**",
                                new PageWaitForURLOptions { Timeout = 8000 });
                            return popup;
                        }
                        catch
                        {
                            // popup мог быть рекламой или пустой вкладкой
                        }
                    }
                }
                catch
                {
                    // ignore popup errors
                }
            }

            await Task.Delay(500, cancellationToken);
        }

        return FindGamePage(context, currentPage, targetUrlPart);
    }

    private static async Task<IPage> NavigateToGameUrlDirectAsync(
        IBrowserContext context,
        IPage currentPage,
        string gameUrl,
        string targetUrlPart,
        CancellationToken cancellationToken)
    {
        var targetPage = currentPage.IsClosed
            ? context.Pages.LastOrDefault(p => !p.IsClosed) ?? await context.NewPageAsync()
            : currentPage;

        await targetPage.BringToFrontAsync();
        await targetPage.GotoAsync(gameUrl, new PageGotoOptions
        {
            WaitUntil = WaitUntilState.DOMContentLoaded,
            Timeout = 60_000
        });
        await HumanBehavior.DelayAsync(2000, 3500, cancellationToken);

        if (!targetPage.Url.Contains(targetUrlPart, StringComparison.OrdinalIgnoreCase))
        {
            var existing = FindGamePage(context, targetPage, targetUrlPart);
            if (existing is not null)
                return await FinalizeGamePageAsync(existing, cancellationToken);
        }

        return await FinalizeGamePageAsync(targetPage, cancellationToken);
    }

    private static IPage? FindGamePage(IBrowserContext context, IPage currentPage, string targetUrlPart)
    {
        foreach (var page in context.Pages)
        {
            if (!page.IsClosed && page.Url.Contains(targetUrlPart, StringComparison.OrdinalIgnoreCase))
                return page;
        }

        if (!currentPage.IsClosed &&
            currentPage.Url.Contains(targetUrlPart, StringComparison.OrdinalIgnoreCase))
            return currentPage;

        return null;
    }

    private static async Task<IPage> FinalizeGamePageAsync(IPage gamePage, CancellationToken cancellationToken)
    {
        if (gamePage.IsClosed)
            throw new InvalidOperationException("Вкладка игры закрылась до завершения загрузки.");

        await gamePage.BringToFrontAsync();
        await gamePage.WaitForLoadStateAsync(LoadState.DOMContentLoaded,
            new PageWaitForLoadStateOptions { Timeout = 45_000 });
        await DismissPopupsAsync(gamePage, cancellationToken);
        return gamePage;
    }

    private static async Task ReportOpenProgressAsync(
        string? sessionId,
        ISessionEventReporter? reporter,
        string message,
        CancellationToken cancellationToken)
    {
        if (reporter is null || string.IsNullOrEmpty(sessionId))
            return;

        await reporter.ReportAsync(new SessionEvent
        {
            SessionId = sessionId,
            Type = SessionEventType.Log,
            Message = message
        }, cancellationToken);
    }

    private static async Task DismissSelectorsAsync(
        IPage page,
        string[] selectors,
        CancellationToken cancellationToken,
        bool isGamePage = false)
    {
        foreach (var selector in selectors)
        {
            try
            {
                var locator = page.Locator(selector).First;
                if (await locator.CountAsync() > 0 && await locator.IsVisibleAsync())
                {
                    if (isGamePage)
                    {
                        var guardClose = await locator.GetAttributeAsync("data-guard-close");
                        if (!string.IsNullOrEmpty(guardClose))
                            continue;
                    }

                    await locator.ClickAsync(new LocatorClickOptions { Timeout = 2000 });
                    await HumanBehavior.DelayAsync(300, 600, cancellationToken);
                }
            }
            catch
            {
                // ignore
            }
        }
    }

    private static async Task<bool> IsEntryBarrierVisibleAsync(IPage page) =>
        await IsPlayGuardDialogVisibleAsync(page) || await IsFullscreenAdVisibleAsync(page);

    private static async Task WaitForEntryBarrierAsync(IPage page, CancellationToken cancellationToken)
    {
        if (page.IsClosed)
            return;

        var deadline = DateTimeOffset.UtcNow.AddSeconds(30);
        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (await IsGameRunningAsync(page))
                return;

            if (await IsFullscreenAdVisibleAsync(page) || await IsPlayGuardDialogVisibleAsync(page))
                break;

            await Task.Delay(300, cancellationToken);
        }

        try
        {
            await page.WaitForSelectorAsync(
                ".play-modal.play-modal_visible, .play-yandex-modal_visible, " +
                "button[data-guard-accept='play_button'], .play-guard-dialog__apply-button, " +
                "button:has-text('Играть')",
                new PageWaitForSelectorOptions
                {
                    Timeout = 15_000,
                    State = WaitForSelectorState.Visible
                });
        }
        catch
        {
            // Барьер мог уже исчезнуть или игра загружается без play-guard.
        }

        await HumanBehavior.DelayAsync(1000, 2000, cancellationToken);
    }

    /// <summary>
    /// Кликает по кнопке «Играть» в play-guard-dialog (data-guard-accept="play_button").
    /// </summary>
    private static async Task<bool> ClickPlayGuardButtonAsync(IPage page, CancellationToken cancellationToken)
    {
        if (page.IsClosed)
            return false;

        try
        {
            var roleButton = page.Locator(".play-guard-dialog")
                .GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = "Играть" })
                .First;
            if (await roleButton.CountAsync() > 0 && await roleButton.IsVisibleAsync())
            {
                if (await TryClickPlayButtonAsync(page, roleButton, cancellationToken))
                    return true;
            }
        }
        catch
        {
            // try selectors below
        }

        foreach (var selector in PlayGuardPlaySelectors)
        {
            try
            {
                var button = page.Locator(selector).First;
                if (await button.CountAsync() == 0 || !await button.IsVisibleAsync())
                    continue;

                if (await TryClickPlayButtonAsync(page, button, cancellationToken))
                    return true;
            }
            catch
            {
                // try next selector
            }
        }

        return false;
    }

    private static async Task<bool> TryClickPlayButtonAsync(IPage page, ILocator button, CancellationToken cancellationToken)
    {
        await button.ScrollIntoViewIfNeededAsync();
        await HumanBehavior.DelayAsync(400, 800, cancellationToken);

        try
        {
            await button.FocusAsync();
        }
        catch
        {
            // ignore
        }

        try
        {
            await HumanBehavior.MoveAndClickAsync(page, button, cancellationToken);
            return true;
        }
        catch
        {
            try
            {
                await button.ClickAsync(new LocatorClickOptions { Timeout = 10_000, Delay = 100 });
                return true;
            }
            catch
            {
                try
                {
                    await button.ClickAsync(new LocatorClickOptions { Timeout = 10_000, Force = true });
                    return true;
                }
                catch
                {
                    var box = await button.BoundingBoxAsync();
                    if (box is null)
                        return false;

                    var x = (float)(box.X + box.Width / 2);
                    var y = (float)(box.Y + box.Height / 2);
                    await page.Mouse.MoveAsync(x, y, new MouseMoveOptions { Steps = 12 });
                    await page.Mouse.ClickAsync(x, y);
                    return true;
                }
            }
        }
    }

    /// <summary>
    /// Кликает по кнопке «Играть» в центральном модальном окне (fallback).
    /// </summary>
    private static async Task<bool> ClickModalPlayButtonAsync(IPage page, CancellationToken cancellationToken)
    {
        if (page.IsClosed)
            return false;

        var viewport = page.ViewportSize;
        var centerX = (viewport?.Width ?? 1920) / 2.0;
        var centerY = (viewport?.Height ?? 1080) / 2.0;

        ILocator? best = null;
        var bestScore = double.MinValue;

        foreach (var selector in ModalPlaySelectors)
        {
            var locator = page.Locator(selector);
            var count = await locator.CountAsync();
            for (var i = 0; i < count; i++)
            {
                var item = locator.Nth(i);
                if (!await item.IsVisibleAsync())
                    continue;

                var box = await item.BoundingBoxAsync();
                if (box is null || box.Width < 40 || box.Height < 20)
                    continue;

                var elCenterX = box.X + box.Width / 2;
                var elCenterY = box.Y + box.Height / 2;
                var dist = Math.Sqrt(Math.Pow(elCenterX - centerX, 2) + Math.Pow(elCenterY - centerY, 2));
                var area = box.Width * box.Height;

                // Приоритет: крупная кнопка в центре экрана (модальное окно)
                var score = area - dist * 3;
                if (score > bestScore)
                {
                    bestScore = score;
                    best = item;
                }
            }
        }

        // Поиск во фреймах
        foreach (var frame in page.Frames)
        {
            try
            {
                foreach (var selector in ModalPlaySelectors)
                {
                    var locator = frame.Locator(selector);
                    var count = await locator.CountAsync();
                    for (var i = 0; i < count; i++)
                    {
                        var item = locator.Nth(i);
                        if (!await item.IsVisibleAsync())
                            continue;

                        var box = await item.BoundingBoxAsync();
                        if (box is null || box.Width < 40)
                            continue;

                        var elCenterX = box.X + box.Width / 2;
                        var elCenterY = box.Y + box.Height / 2;
                        var dist = Math.Sqrt(Math.Pow(elCenterX - centerX, 2) + Math.Pow(elCenterY - centerY, 2));
                        var score = box.Width * box.Height - dist * 3;
                        if (score > bestScore)
                        {
                            bestScore = score;
                            best = item;
                        }
                    }
                }
            }
            catch
            {
                // ignore detached frames
            }
        }

        if (best is null)
            return false;

        await best.ScrollIntoViewIfNeededAsync();
        await HumanBehavior.DelayAsync(400, 800, cancellationToken);

        try
        {
            await best.ClickAsync(new LocatorClickOptions { Timeout = 10_000, Delay = 100 });
            return true;
        }
        catch
        {
            try
            {
                await best.ClickAsync(new LocatorClickOptions { Timeout = 10_000, Force = true });
                return true;
            }
            catch
            {
                return false;
            }
        }
    }

    private static async Task<bool> ClickPlayButtonViaJavaScriptAsync(IPage page, CancellationToken cancellationToken)
    {
        if (page.IsClosed)
            return false;

        cancellationToken.ThrowIfCancellationRequested();

        var clicked = await page.EvaluateAsync<bool>(
            """
            () => {
              const fireClick = (el) => {
                el.dispatchEvent(new PointerEvent('pointerdown', { bubbles: true, cancelable: true }));
                el.dispatchEvent(new MouseEvent('mousedown', { bubbles: true, cancelable: true }));
                el.dispatchEvent(new PointerEvent('pointerup', { bubbles: true, cancelable: true }));
                el.dispatchEvent(new MouseEvent('mouseup', { bubbles: true, cancelable: true }));
                el.dispatchEvent(new MouseEvent('click', { bubbles: true, cancelable: true }));
                if (typeof el.click === 'function') el.click();
              };

              const guardBtn = document.querySelector("button[data-guard-accept='play_button']");
              if (guardBtn && guardBtn.offsetParent !== null) {
                fireClick(guardBtn);
                return true;
              }

              const vw = window.innerWidth;
              const vh = window.innerHeight;
              const cx = vw / 2;
              const cy = vh / 2;

              const nodes = document.querySelectorAll('button, a, [role="button"], div, span');
              let best = null;
              let bestScore = -Infinity;

              for (const el of nodes) {
                const text = (el.textContent || '').trim();
                if (text !== 'Играть' && text !== 'Play') continue;
                if (!el.offsetParent && el.style.display !== 'contents') continue;

                const rect = el.getBoundingClientRect();
                if (rect.width < 40 || rect.height < 20) continue;

                const ex = rect.left + rect.width / 2;
                const ey = rect.top + rect.height / 2;
                const dist = Math.hypot(ex - cx, ey - cy);
                const area = rect.width * rect.height;
                const score = area - dist * 3;

                if (score > bestScore) {
                  bestScore = score;
                  best = el;
                }
              }

              if (!best) return false;
              fireClick(best);
              return true;
            }
            """);

        return clicked;
    }

    private static async Task<bool> HasLargeCanvasAsync(ILocator canvasLocator)
    {
        var count = await canvasLocator.CountAsync();
        for (var i = 0; i < count; i++)
        {
            var canvas = canvasLocator.Nth(i);
            if (!await canvas.IsVisibleAsync())
                continue;

            var box = await canvas.BoundingBoxAsync();
            if (box is { Width: >= 200, Height: >= 150 })
                return true;
        }

        return false;
    }

    private static async Task<bool> HasLargeGameIframeAsync(IPage page)
    {
        var gameFrame = page.Locator("#game-frame");
        if (await gameFrame.CountAsync() > 0 && await gameFrame.First.IsVisibleAsync())
        {
            var box = await gameFrame.First.BoundingBoxAsync();
            if (box is { Width: >= 400, Height: >= 300 })
                return true;
        }

        var iframes = page.Locator("iframe");
        var count = await iframes.CountAsync();
        for (var i = 0; i < count; i++)
        {
            var iframe = iframes.Nth(i);
            if (!await iframe.IsVisibleAsync())
                continue;

            var box = await iframe.BoundingBoxAsync();
            if (box is { Width: >= 400, Height: >= 300 })
                return true;
        }

        return false;
    }

    private static async Task<IPage> ReacquireGamePageAsync(
        IBrowserContext context,
        IPage? page,
        string gameUrl,
        string targetUrlPart,
        CancellationToken cancellationToken)
    {
        if (page is { IsClosed: false } &&
            page.Url.Contains(targetUrlPart, StringComparison.OrdinalIgnoreCase))
            return page;

        var existing = context.Pages.LastOrDefault(p =>
            !p.IsClosed && p.Url.Contains(targetUrlPart, StringComparison.OrdinalIgnoreCase));

        if (existing is not null)
            return existing;

        var anyOpen = context.Pages.LastOrDefault(p => !p.IsClosed);
        if (anyOpen is not null)
        {
            await anyOpen.GotoAsync(gameUrl, new PageGotoOptions
            {
                WaitUntil = WaitUntilState.DOMContentLoaded,
                Timeout = 60_000
            });
            await HumanBehavior.DelayAsync(2500, 4000, cancellationToken);
            return anyOpen;
        }

        var created = await context.NewPageAsync();
        await created.GotoAsync(gameUrl, new PageGotoOptions
        {
            WaitUntil = WaitUntilState.DOMContentLoaded,
            Timeout = 60_000
        });
        return created;
    }

    private static async Task<IPage> WaitForGameRunningAsync(
        IBrowserContext context,
        IPage page,
        string gameUrl,
        string targetUrlPart,
        CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(90);

        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();

            page = await ReacquireGamePageAsync(context, page, gameUrl, targetUrlPart, cancellationToken);

            if (await IsGameRunningAsync(page))
                return page;

            await Task.Delay(1000, cancellationToken);
        }

        return page;
    }

    private static async Task<IPage> WaitForPageWithUrlAsync(
        IBrowserContext context,
        string urlPart,
        CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(60);
        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();

            foreach (var p in context.Pages)
            {
                if (!p.IsClosed && p.Url.Contains(urlPart, StringComparison.OrdinalIgnoreCase))
                    return p;
            }

            await Task.Delay(500, cancellationToken);
        }

        var urls = string.Join(", ", context.Pages.Where(p => !p.IsClosed).Select(p => p.Url));
        throw new TimeoutException(
            $"Страница игры не открылась за 60 сек. Открытые вкладки: {urls}");
    }

    private static readonly string[] SearchInputSelectors =
    [
        "#search-input",
        "input[data-testid='search-text-input']",
        "input[type='search']",
        "input[placeholder*='Найти игру']",
        "input[placeholder*='Поиск']",
        "input[aria-label*='Поиск']",
        ".search-input input"
    ];

    public static async Task FocusSearchInputAsync(IPage page, CancellationToken cancellationToken)
    {
        var input = await FindSearchInputLocatorAsync(page);
        try
        {
            await input.FocusAsync(new LocatorFocusOptions { Timeout = 10_000 });
        }
        catch (PlaywrightException)
        {
            await input.ClickAsync(new LocatorClickOptions { Force = true, Timeout = 5000 });
        }

        await HumanBehavior.DelayAsync(200, 500, cancellationToken);
    }

    public static async Task<bool> FillSearchQueryAsync(IPage page, string query, CancellationToken cancellationToken)
    {
        var input = await FindSearchInputLocatorAsync(page);
        try
        {
            await input.ClickAsync(new LocatorClickOptions { Force = true, Timeout = 5000 });
            await input.FillAsync(string.Empty, new LocatorFillOptions { Force = true, Timeout = 5000 });
            await input.FillAsync(query, new LocatorFillOptions { Force = true, Timeout = 10_000 });
            await HumanBehavior.DelayAsync(300, 700, cancellationToken);
            return true;
        }
        catch (PlaywrightException)
        {
            var ok = await page.EvaluateAsync<bool>(
                @"([query]) => {
                    const el = document.querySelector('#search-input')
                        || document.querySelector('input[data-testid=search-text-input]')
                        || document.querySelector('input[type=search]');
                    if (!el) return false;
                    el.focus();
                    el.value = query;
                    el.dispatchEvent(new Event('input', { bubbles: true }));
                    el.dispatchEvent(new Event('change', { bubbles: true }));
                    return true;
                }",
                new object[] { query });
            return ok;
        }
    }

    private static async Task<ILocator> FindSearchInputLocatorAsync(IPage page)
    {
        foreach (var selector in SearchInputSelectors)
        {
            var locator = page.Locator(selector).First;
            if (await locator.CountAsync() > 0 && await locator.IsVisibleAsync())
                return locator;
        }

        throw new InvalidOperationException("Строка поиска на Яндекс Играх не найдена.");
    }
}
