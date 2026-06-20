using AwardsFerm.Core.Interfaces;
using AwardsFerm.Core.Models;
using AwardsFerm.Infrastructure.Behavior;
using Microsoft.Playwright;

namespace AwardsFerm.Infrastructure.Playwright;

/// <summary>
/// Ориентация экрана для Яндекс Игр: игра может требовать портрет или альбом.
/// </summary>
internal static class OrientationHelper
{
    public static async Task EnsureLandscapeForGameAsync(
        IPage page,
        IBrowserContext context,
        DesktopProfile? profile,
        string sessionId,
        ISessionEventReporter reporter,
        CancellationToken cancellationToken = default,
        LandscapeState? landscapeState = null,
        SessionStuckTracker? stuckTracker = null)
    {
        if (page.IsClosed)
            return;

        var required = await DetectRequiredOrientationAsync(page, cancellationToken);
        if (required is null)
            return;

        var vp = page.ViewportSize;
        if (vp is not null && IsViewportMatchingOrientation(vp.Width, vp.Height, required.Value))
        {
            stuckTracker?.Reset("orientation");
            return;
        }

        var beforeW = vp?.Width ?? 0;
        var beforeH = vp?.Height ?? 0;

        await ApplyOrientationAsync(page, context, profile, required.Value, sessionId, reporter, cancellationToken);

        var after = page.ViewportSize;
        var dimensionsChanged = after is not null &&
            (after.Width != beforeW || after.Height != beforeH);

        if (dimensionsChanged && stuckTracker is not null)
        {
            var stillWrong = after is null || !IsViewportMatchingOrientation(after.Width, after.Height, required.Value);
            if (stillWrong)
            {
                if (stuckTracker.Register("orientation"))
                    throw new SessionStuckException("Повторяющийся поворот экрана без прогресса");
            }
            else
            {
                stuckTracker.Reset("orientation");
            }
        }
    }

    private static bool IsViewportMatchingOrientation(int width, int height, ScreenOrientation orientation)
    {
        var isLandscape = width > height;
        return orientation == ScreenOrientation.Landscape ? isLandscape : !isLandscape;
    }

    private static async Task<ScreenOrientation?> DetectRequiredOrientationAsync(
        IPage page,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var rotatePromptVisible = await IsRotateDevicePromptVisibleAsync(page);
        if (!rotatePromptVisible)
            return null;

        var portraitHint = await HasPortraitOrientationHintAsync(page);
        if (portraitHint)
            return ScreenOrientation.Portrait;

        var landscapeHint = await HasLandscapeOrientationHintAsync(page);
        if (landscapeHint)
            return ScreenOrientation.Landscape;

        // «Поверните устройство» без явной подсказки — чаще требуется портрет (мобильные игры на десктопе).
        return ScreenOrientation.Portrait;
    }

    private static async Task<bool> IsRotateDevicePromptVisibleAsync(IPage page)
    {
        foreach (var frame in page.Frames)
        {
            try
            {
                var rotate = frame.GetByText("Поверните устройство", new() { Exact = false });
                if (await rotate.First.IsVisibleAsync())
                    return true;
            }
            catch
            {
                // ignore
            }
        }

        try
        {
            var rotate = page.GetByText("Поверните устройство", new() { Exact = false });
            return await rotate.First.IsVisibleAsync();
        }
        catch
        {
            return false;
        }
    }

    private static async Task<bool> HasPortraitOrientationHintAsync(IPage page)
    {
        foreach (var frame in page.Frames)
        {
            try
            {
                var body = await frame.Locator("body").InnerTextAsync();
                if (ContainsPortraitHint(body))
                    return true;
            }
            catch
            {
                // ignore
            }
        }

        try
        {
            var body = await page.Locator("body").InnerTextAsync();
            return ContainsPortraitHint(body);
        }
        catch
        {
            return false;
        }
    }

    private static bool ContainsPortraitHint(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return false;

        var lower = text.ToLowerInvariant();
        return lower.Contains("вертикаль", StringComparison.Ordinal) ||
               lower.Contains("portrait", StringComparison.Ordinal);
    }

    private static async Task<bool> HasLandscapeOrientationHintAsync(IPage page)
    {
        foreach (var frame in page.Frames)
        {
            try
            {
                var body = await frame.Locator("body").InnerTextAsync();
                if (ContainsLandscapeHint(body))
                    return true;
            }
            catch
            {
                // ignore
            }
        }

        try
        {
            var body = await page.Locator("body").InnerTextAsync();
            return ContainsLandscapeHint(body);
        }
        catch
        {
            return false;
        }
    }

    private static bool ContainsLandscapeHint(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return false;

        var lower = text.ToLowerInvariant();
        return lower.Contains("альбом", StringComparison.Ordinal) ||
               lower.Contains("landscape", StringComparison.Ordinal) ||
               lower.Contains("горизонталь", StringComparison.Ordinal);
    }

    private static async Task ApplyOrientationAsync(
        IPage page,
        IBrowserContext context,
        DesktopProfile? profile,
        ScreenOrientation orientation,
        string sessionId,
        ISessionEventReporter reporter,
        CancellationToken cancellationToken)
    {
        var vp = page.ViewportSize;
        if (vp is null)
            return;

        var targetW = orientation == ScreenOrientation.Landscape
            ? Math.Max(vp.Width, vp.Height)
            : Math.Min(vp.Width, vp.Height);
        var targetH = orientation == ScreenOrientation.Landscape
            ? Math.Min(vp.Width, vp.Height)
            : Math.Max(vp.Width, vp.Height);

        if (vp.Width == targetW && vp.Height == targetH)
            return;

        var label = orientation == ScreenOrientation.Landscape ? "альбомную" : "портретную";
        await reporter.ReportAsync(new SessionEvent
        {
            SessionId = sessionId,
            Type = SessionEventType.Log,
            Message = $"Повёрнут экран в {label} ориентацию ({targetW}×{targetH})"
        }, cancellationToken);

        await page.SetViewportSizeAsync(targetW, targetH);

        try
        {
            var cdpSession = await context.NewCDPSessionAsync(page);
            await cdpSession.SendAsync("Emulation.setDeviceMetricsOverride", new Dictionary<string, object>
            {
                ["width"] = targetW,
                ["height"] = targetH,
                ["deviceScaleFactor"] = profile?.DeviceScaleFactor ?? 1,
                ["mobile"] = profile?.FormFactor != DeviceFormFactor.Desktop,
                ["screenOrientation"] = new Dictionary<string, object>
                {
                    ["type"] = orientation == ScreenOrientation.Landscape ? "landscapePrimary" : "portraitPrimary",
                    ["angle"] = 0
                }
            });
        }
        catch
        {
            // CDP может быть недоступен — viewport достаточно
        }

        await HumanBehavior.DelayAsync(800, 1200, cancellationToken);
    }

    private enum ScreenOrientation
    {
        Portrait,
        Landscape
    }
}