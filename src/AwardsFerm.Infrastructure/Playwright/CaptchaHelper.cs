using AwardsFerm.Core.Interfaces;
using AwardsFerm.Core.Models;
using AwardsFerm.Infrastructure.Behavior;
using Microsoft.Playwright;

namespace AwardsFerm.Infrastructure.Playwright;

internal static class CaptchaHelper
{
    private static readonly string[] CaptchaSelectors =
    [
        "text=Я не робот",
        "text=Подтвердите, что запросы отправляли вы",
        "text=Нажмите, чтобы продолжить",
        "text=Captcha Verification",
        "text=verify you're not a robot",
        ".CheckboxCaptcha",
        ".SmartCaptcha",
        ".smart-captcha",
        "[data-testid='smartCaptcha-container']",
        "[data-testid='checkbox-captcha']",
        "input[name='smart-token']",
        "iframe[src*='captcha']",
        "iframe[src*='smartcaptcha']"
    ];

    private static readonly string[] CheckboxSelectors =
    [
        "#js-button",
        ".CheckboxCaptcha-Button",
        ".CheckboxCaptcha-Checkbox",
        "#checkbox",
        "[data-testid='checkbox-captcha']",
        "[role='checkbox']",
        ".smart-captcha-checkbox",
        "input[type='checkbox']"
    ];

    private static readonly string[] ManualOnlyTextMarkers =
    [
        "Captcha Verification",
        "verify you're not a robot",
        "Please verify you're not a robot",
        "smart-captcha",
        "SmartCaptcha"
    ];

    public static async Task<bool> IsPresentAsync(IPage page)
    {
        if (page.IsClosed)
            return false;

        if (IsCaptchaUrl(page.Url))
            return true;

        if (await IsCaptchaVerificationPageAsync(page))
            return true;

        if (await HasSmartCaptchaDomAsync(page))
            return true;

        if (await HasCaptchaTextAsync(page))
            return true;

        foreach (var frame in page.Frames)
        {
            if (IsCaptchaUrl(frame.Url))
                return true;
        }

        foreach (var selector in CaptchaSelectors)
        {
            try
            {
                var locator = page.Locator(selector).First;
                if (await locator.CountAsync() > 0 && await locator.IsVisibleAsync())
                    return true;
            }
            catch
            {
                // Ignore selector errors on dynamic pages.
            }
        }

        return false;
    }

    public static async Task<bool> IsPresentInContextAsync(IBrowserContext context)
    {
        foreach (var page in context.Pages.ToList())
        {
            if (!page.IsClosed && await IsPresentAsync(page))
                return true;
        }

        return false;
    }

    /// <summary>
    /// Ищет вкладку с ручной капчей (приоритет — Captcha Verification).
    /// </summary>
    public static async Task<IPage?> FindManualCaptchaPageAsync(IBrowserContext context)
    {
        IPage? fallback = null;

        foreach (var page in context.Pages.ToList())
        {
            if (page.IsClosed)
                continue;

            if (await IsCaptchaVerificationPageAsync(page))
                return page;

            if (await RequiresManualSolveAsync(page))
                fallback ??= page;
            else if (fallback is null && await IsPresentAsync(page))
                fallback = page;
        }

        return fallback;
    }

    public static async Task<bool> IsCaptchaVerificationPageAsync(IPage page)
    {
        if (page.IsClosed)
            return false;

        try
        {
            var title = await page.TitleAsync();
            if (!string.IsNullOrWhiteSpace(title) &&
                title.Contains("Captcha Verification", StringComparison.OrdinalIgnoreCase))
                return true;
        }
        catch
        {
            // ignore
        }

        var url = page.Url;
        if (url.Contains("ads-captcha.yandex.", StringComparison.OrdinalIgnoreCase) ||
            url.Contains("showcaptcha", StringComparison.OrdinalIgnoreCase) ||
            url.Contains("checkcaptcha", StringComparison.OrdinalIgnoreCase))
            return true;

        try
        {
            var body = await page.Locator("body").InnerTextAsync();
            if (!string.IsNullOrWhiteSpace(body) &&
                body.Contains("Captcha Verification", StringComparison.OrdinalIgnoreCase))
                return true;
        }
        catch
        {
            // ignore
        }

        return false;
    }

    /// <summary>
    /// Yandex SmartCaptcha (ads-captcha) — только ручное решение через «Просмотр».
    /// </summary>
    public static async Task<bool> RequiresManualSolveAsync(IPage page)
    {
        if (page.IsClosed)
            return false;

        if (await IsCaptchaVerificationPageAsync(page))
            return true;

        var url = page.Url;
        if (url.Contains("ads-captcha.yandex.ru", StringComparison.OrdinalIgnoreCase) ||
            url.Contains("ads-captcha.yandex.", StringComparison.OrdinalIgnoreCase))
            return true;

        if (await HasSmartCaptchaDomAsync(page))
            return true;

        try
        {
            var body = await page.Locator("body").InnerTextAsync();
            foreach (var marker in ManualOnlyTextMarkers)
            {
                if (body.Contains(marker, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
        }
        catch
        {
            // ignore
        }

        foreach (var frame in page.Frames)
        {
            if (frame.Url.Contains("smartcaptcha.cloud.yandex.ru", StringComparison.OrdinalIgnoreCase) &&
                frame.Url.Contains("ads-captcha", StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    public static async Task<bool> TryAutoSolveAsync(IPage page, CancellationToken cancellationToken = default)
    {
        if (await RequiresManualSolveAsync(page))
            return false;

        foreach (var frame in page.Frames)
        {
            if (await TryClickCheckboxInFrameAsync(page, frame, cancellationToken))
                return true;
        }

        foreach (var iframeSelector in new[] { "iframe[src*='smartcaptcha']", "iframe[src*='captcha']" })
        {
            try
            {
                var frame = page.FrameLocator(iframeSelector);
                foreach (var selector in CheckboxSelectors)
                {
                    var checkbox = frame.Locator(selector).First;
                    if (await checkbox.CountAsync() == 0 || !await checkbox.IsVisibleAsync())
                        continue;

                    await HumanBehavior.MoveAndClickAsync(page, checkbox, cancellationToken);
                    await HumanBehavior.DelayAsync(2500, 4000, cancellationToken);
                    if (!await IsPresentAsync(page))
                        return true;
                }
            }
            catch
            {
                // try next iframe
            }
        }

        foreach (var selector in CheckboxSelectors)
        {
            try
            {
                var checkbox = page.Locator(selector).First;
                if (await checkbox.CountAsync() == 0 || !await checkbox.IsVisibleAsync())
                    continue;

                await HumanBehavior.MoveAndClickAsync(page, checkbox, cancellationToken);
                await HumanBehavior.DelayAsync(2500, 4000, cancellationToken);
                if (!await IsPresentAsync(page))
                    return true;
            }
            catch
            {
                // try next selector
            }
        }

        return !await IsPresentAsync(page);
    }

    public static async Task WaitForManualSolveAsync(
        IPage page,
        string sessionId,
        ISessionEventReporter reporter,
        CancellationToken cancellationToken,
        int maxWaitMinutes = 5,
        IBrowserContext? context = null,
        string? profileId = null,
        ISessionPauseCoordinator? pauseCoordinator = null,
        ActivePageHolder? activePage = null)
    {
        var captchaPage = context is not null
            ? await FindManualCaptchaPageAsync(context)
            : null;

        if (captchaPage is null && !await IsPresentAsync(page))
            return;

        captchaPage ??= page;

        if (activePage is not null)
            activePage.CaptchaFocusPage = captchaPage;

        try
        {
            try
            {
                await captchaPage.BringToFrontAsync();
            }
            catch
            {
                // ignore
            }

            var pageUrl = captchaPage.Url;
            var manualOnly = await RequiresManualSolveAsync(captchaPage);
            var headless = ResolveHeadlessMode();

            if (manualOnly)
            {
                if (pauseCoordinator is not null && !string.IsNullOrWhiteSpace(profileId) &&
                    !pauseCoordinator.IsPaused(profileId))
                {
                    pauseCoordinator.Pause(profileId);
                    await reporter.ReportAsync(new SessionEvent
                    {
                        SessionId = sessionId,
                        Type = SessionEventType.StatusChanged,
                        Status = SessionStatus.Paused,
                        Message = "Пауза — Captcha Verification"
                    }, cancellationToken);
                }

                await reporter.ReportAsync(new SessionEvent
                {
                    SessionId = sessionId,
                    Type = SessionEventType.Log,
                    Message = manualOnly && pageUrl.Contains("ads-captcha", StringComparison.OrdinalIgnoreCase)
                        ? $"⚠ Открыта Captcha Verification ({pageUrl}) — сессия на паузе"
                        : $"⚠ Обнаружена SmartCaptcha ({pageUrl}) — решается только вручную"
                }, cancellationToken);

                await reporter.ReportAsync(new SessionEvent
                {
                    SessionId = sessionId,
                    Type = SessionEventType.Log,
                    Message = headless
                        ? "⚠ Откройте «Просмотр» → «На весь экран» и пройдите капчу на экране"
                        : "⚠ Откройте «Просмотр» и пройдите капчу — автоматически она не решается"
                }, cancellationToken);

                await WaitUntilResolvedAsync(
                    captchaPage, sessionId, reporter, cancellationToken,
                    maxWaitMinutes: 15, manualOnly: true, context: context,
                    profileId: profileId, pauseCoordinator: pauseCoordinator);
                return;
            }

            await reporter.ReportAsync(new SessionEvent
            {
                SessionId = sessionId,
                Type = SessionEventType.Log,
                Message = $"⚠ ВНИМАНИЕ: обнаружена капча «Я не робот» ({pageUrl})"
            }, cancellationToken);

            if (headless)
            {
                await reporter.ReportAsync(new SessionEvent
                {
                    SessionId = sessionId,
                    Type = SessionEventType.Log,
                    Message = "⚠ Headless-режим: откройте «Просмотр» и кликните по капче на экране"
                }, cancellationToken);
            }
            else
            {
                await reporter.ReportAsync(new SessionEvent
                {
                    SessionId = sessionId,
                    Type = SessionEventType.Log,
                    Message = "Если автоклик не сработает — откройте «Просмотр» и кликните по капче на экране"
                }, cancellationToken);
            }

            await reporter.ReportAsync(new SessionEvent
            {
                SessionId = sessionId,
                Type = SessionEventType.Log,
                Message = "Капча — пробуем нажать галочку…"
            }, cancellationToken);

            for (var attempt = 1; attempt <= 4; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (pauseCoordinator is not null && !string.IsNullOrWhiteSpace(profileId))
                    await pauseCoordinator.WaitIfPausedAsync(profileId, sessionId, reporter, cancellationToken);

                if (await TryAutoSolveAsync(captchaPage, cancellationToken))
                {
                    await reporter.ReportAsync(new SessionEvent
                    {
                        SessionId = sessionId,
                        Type = SessionEventType.Log,
                        Message = "✓ Капча пройдена (галочка нажата)"
                    }, cancellationToken);
                    await HumanBehavior.DelayAsync(2000, 4000, cancellationToken);
                    return;
                }

                if (!await IsPresentInScopeAsync(captchaPage, context))
                {
                    await reporter.ReportAsync(new SessionEvent
                    {
                        SessionId = sessionId,
                        Type = SessionEventType.Log,
                        Message = "✓ Капча пройдена, продолжаем сценарий"
                    }, cancellationToken);
                    await HumanBehavior.DelayAsync(2000, 4000, cancellationToken);
                    return;
                }

                await Task.Delay(2000, cancellationToken);
            }

            await reporter.ReportAsync(new SessionEvent
            {
                SessionId = sessionId,
                Type = SessionEventType.Log,
                Message = "⚠ Автоклик не помог — откройте «Просмотр» и кликните по капче…"
            }, cancellationToken);

            if (pauseCoordinator is not null && !string.IsNullOrWhiteSpace(profileId) &&
                !pauseCoordinator.IsPaused(profileId))
            {
                pauseCoordinator.Pause(profileId);
                await reporter.ReportAsync(new SessionEvent
                {
                    SessionId = sessionId,
                    Type = SessionEventType.StatusChanged,
                    Status = SessionStatus.Paused,
                    Message = "Пауза — капча"
                }, cancellationToken);
            }

            await WaitUntilResolvedAsync(
                captchaPage, sessionId, reporter, cancellationToken, maxWaitMinutes, manualOnly: true,
                context: context, profileId: profileId, pauseCoordinator: pauseCoordinator);
        }
        finally
        {
            if (activePage is not null && ReferenceEquals(activePage.CaptchaFocusPage, captchaPage))
                activePage.CaptchaFocusPage = null;
        }
    }

    private static async Task<bool> IsPresentInScopeAsync(IPage page, IBrowserContext? context)
    {
        if (context is not null)
            return await IsPresentInContextAsync(context);

        return await IsPresentAsync(page);
    }

    private static async Task WaitUntilResolvedAsync(
        IPage page,
        string sessionId,
        ISessionEventReporter reporter,
        CancellationToken cancellationToken,
        int maxWaitMinutes,
        bool manualOnly = false,
        IBrowserContext? context = null,
        string? profileId = null,
        ISessionPauseCoordinator? pauseCoordinator = null)
    {
        var hasCoordinator = manualOnly && pauseCoordinator is not null && !string.IsNullOrWhiteSpace(profileId);
        var deadline = hasCoordinator
            ? DateTimeOffset.MaxValue
            : DateTimeOffset.UtcNow.AddMinutes(maxWaitMinutes);

        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (pauseCoordinator is not null && !string.IsNullOrWhiteSpace(profileId))
                await pauseCoordinator.WaitIfPausedAsync(profileId, sessionId, reporter, cancellationToken);

            if (!await IsPresentInScopeAsync(page, context))
            {
                if (pauseCoordinator is not null && !string.IsNullOrWhiteSpace(profileId) &&
                    pauseCoordinator.IsPaused(profileId))
                {
                    pauseCoordinator.Resume(profileId);
                }

                await reporter.ReportAsync(new SessionEvent
                {
                    SessionId = sessionId,
                    Type = SessionEventType.Log,
                    Message = "✓ Капча пройдена, продолжаем сценарий"
                }, cancellationToken);
                await HumanBehavior.DelayAsync(2000, 4000, cancellationToken);
                return;
            }

            if (!manualOnly && !await RequiresManualSolveAsync(page) && await TryAutoSolveAsync(page, cancellationToken))
            {
                await reporter.ReportAsync(new SessionEvent
                {
                    SessionId = sessionId,
                    Type = SessionEventType.Log,
                    Message = "✓ Капча пройдена, продолжаем сценарий"
                }, cancellationToken);
                await HumanBehavior.DelayAsync(2000, 4000, cancellationToken);
                return;
            }

            await Task.Delay(1000, cancellationToken);
        }

        if (hasCoordinator)
            return;

        throw new InvalidOperationException(
            $"Капча не решена за {maxWaitMinutes} мин. Откройте «Просмотр», пройдите капчу и запустите сессию снова.");
    }

    /// <summary>Останавливает текущий сценарий, пока в контексте открыта Captcha Verification.</summary>
    public static async Task BlockWhileCaptchaOpenAsync(
        IBrowserContext context,
        IPage page,
        string profileId,
        string sessionId,
        ISessionEventReporter reporter,
        ISessionPauseCoordinator pauseCoordinator,
        ActivePageHolder? activePage,
        CancellationToken cancellationToken)
    {
        await pauseCoordinator.WaitIfPausedAsync(profileId, sessionId, reporter, cancellationToken);

        if (!await IsPresentInContextAsync(context))
            return;

        await WaitForManualSolveAsync(
            page, sessionId, reporter, cancellationToken,
            maxWaitMinutes: 120,
            context: context,
            profileId: profileId,
            pauseCoordinator: pauseCoordinator,
            activePage: activePage);
    }

    private static bool IsCaptchaUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return false;

        return url.Contains("showcaptcha", StringComparison.OrdinalIgnoreCase) ||
               url.Contains("checkcaptcha", StringComparison.OrdinalIgnoreCase) ||
               url.Contains("ads-captcha.yandex.", StringComparison.OrdinalIgnoreCase) ||
               url.Contains("/captcha", StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<bool> HasSmartCaptchaDomAsync(IPage page)
    {
        var domSelectors = new[]
        {
            ".smart-captcha[data-sitekey]",
            "[data-testid='smartCaptcha-container']",
            "input[name='smart-token']",
            "iframe[data-testid='checkbox-iframe']",
            "iframe[data-testid='backend-iframe']"
        };

        foreach (var selector in domSelectors)
        {
            try
            {
                var locator = page.Locator(selector).First;
                if (await locator.CountAsync() > 0 && await locator.IsVisibleAsync())
                    return true;
            }
            catch
            {
                // ignore
            }
        }

        return false;
    }

    private static async Task<bool> HasCaptchaTextAsync(IPage page)
    {
        try
        {
            var body = await page.Locator("body").InnerTextAsync();
            if (string.IsNullOrWhiteSpace(body))
                return false;

            return body.Contains("Captcha Verification", StringComparison.OrdinalIgnoreCase) ||
                   body.Contains("verify you're not a robot", StringComparison.OrdinalIgnoreCase) ||
                   body.Contains("Я не робот", StringComparison.OrdinalIgnoreCase) ||
                   body.Contains("Подтвердите, что запросы отправляли вы", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static async Task<bool> TryClickCheckboxInFrameAsync(
        IPage page,
        IFrame frame,
        CancellationToken cancellationToken)
    {
        foreach (var selector in CheckboxSelectors)
        {
            try
            {
                var checkbox = frame.Locator(selector).First;
                if (await checkbox.CountAsync() == 0 || !await checkbox.IsVisibleAsync())
                    continue;

                await checkbox.ClickAsync(new LocatorClickOptions { Timeout = 5000 });
                await HumanBehavior.DelayAsync(2500, 4000, cancellationToken);
                return !await IsPresentAsync(page);
            }
            catch
            {
                // try next selector
            }
        }

        return false;
    }

    private static bool ResolveHeadlessMode()
    {
        var envHeadless = Environment.GetEnvironmentVariable("BROWSER_HEADLESS");
        if (string.IsNullOrEmpty(envHeadless))
            return true;

        if (!bool.TryParse(envHeadless, out var headless) || !headless)
            return false;

        if (Environment.GetEnvironmentVariable("BROWSER_VNC") == "true")
            return false;
        if (OperatingSystem.IsLinux() && !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("DISPLAY")))
            return false;

        return true;
    }
}
