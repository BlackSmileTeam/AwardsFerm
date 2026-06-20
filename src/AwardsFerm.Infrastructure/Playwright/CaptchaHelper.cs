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
        ".CheckboxCaptcha",
        ".SmartCaptcha",
        "iframe[src*='captcha']",
        "iframe[src*='smartcaptcha']",
        "[data-testid='checkbox-captcha']"
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

    public static async Task<bool> IsPresentAsync(IPage page)
    {
        var url = page.Url;
        if (url.Contains("showcaptcha", StringComparison.OrdinalIgnoreCase) ||
            url.Contains("checkcaptcha", StringComparison.OrdinalIgnoreCase) ||
            url.Contains("/captcha", StringComparison.OrdinalIgnoreCase))
            return true;

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

    public static async Task<bool> TryAutoSolveAsync(IPage page, CancellationToken cancellationToken = default)
    {
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

    public static async Task WaitForManualSolveAsync(
        IPage page,
        string sessionId,
        ISessionEventReporter reporter,
        CancellationToken cancellationToken,
        int maxWaitMinutes = 5)
    {
        if (!await IsPresentAsync(page))
            return;

        var pageUrl = page.Url;
        var headless = ResolveHeadlessMode();

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
                Message = "⚠ Headless-режим: капчу вручную решить нельзя — пробуем автоклик"
            }, cancellationToken);
        }
        else
        {
            await reporter.ReportAsync(new SessionEvent
            {
                SessionId = sessionId,
                Type = SessionEventType.Log,
                Message = "Если автоклик не сработает — решите капчу в окне браузера"
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

            if (await TryAutoSolveAsync(page, cancellationToken))
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

            if (!await IsPresentAsync(page))
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
            Message = "⚠ Автоклик не помог — решите капчу вручную в окне браузера…"
        }, cancellationToken);

        var deadline = DateTimeOffset.UtcNow.AddMinutes(maxWaitMinutes);
        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (await TryAutoSolveAsync(page, cancellationToken) || !await IsPresentAsync(page))
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

        throw new InvalidOperationException(
            $"Капча не решена за {maxWaitMinutes} мин. Решите её в окне браузера и запустите сессию снова.");
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
