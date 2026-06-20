using AwardsFerm.Core.Interfaces;
using AwardsFerm.Core.Models;
using AwardsFerm.Infrastructure.Behavior;
using Microsoft.Playwright;

namespace AwardsFerm.Infrastructure.Playwright;

internal static class OrientationHelper
{
    private static readonly string[] RotatePromptTexts =
    [
        "Поверните устройство",
        "Поверни устройство",
        "Поверните телефон",
        "Rotate your device",
        "Rotate the device"
    ];

    public static async Task<bool> EnsureLandscapeForGameAsync(
        IPage page,
        IBrowserContext context,
        DesktopProfile? profile = null,
        string? sessionId = null,
        ISessionEventReporter? reporter = null,
        CancellationToken cancellationToken = default)
    {
        if (page.IsClosed)
            return false;

        var needsLandscape = await IsRotatePromptVisibleAsync(page);
        if (!needsLandscape)
        {
            var vp = page.ViewportSize;
            needsLandscape = vp is not null && vp.Width < vp.Height;
        }

        if (!needsLandscape)
            return false;

        var targetWidth = profile?.ViewportWidth ?? page.ViewportSize?.Width ?? 1280;
        var targetHeight = profile?.ViewportHeight ?? page.ViewportSize?.Height ?? 800;
        if (targetWidth < targetHeight)
            (targetWidth, targetHeight) = (targetHeight, targetWidth);

        await ApplyLandscapeViewportAsync(page, context, targetWidth, targetHeight, profile, cancellationToken);

        if (reporter is not null && sessionId is not null)
        {
            await reporter.ReportAsync(new SessionEvent
            {
                SessionId = sessionId,
                Type = SessionEventType.Log,
                Message = $"Повёрнут экран в альбомную ориентацию ({targetWidth}×{targetHeight})"
            }, cancellationToken);
        }

        await HumanBehavior.DelayAsync(1500, 2500, cancellationToken);
        return true;
    }

    private static async Task<bool> IsRotatePromptVisibleAsync(IPage page)
    {
        foreach (var text in RotatePromptTexts)
        {
            try
            {
                var locator = page.Locator($"text={text}").First;
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

    private static async Task ApplyLandscapeViewportAsync(
        IPage page,
        IBrowserContext context,
        int width,
        int height,
        DesktopProfile? profile,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            await page.SetViewportSizeAsync(width, height);
        }
        catch
        {
            // best-effort
        }

        if (profile?.FormFactor == DeviceFormFactor.Desktop)
            return;

        try
        {
            var dpr = profile?.DeviceScaleFactor ?? 1.5;
            var touchPoints = profile?.MaxTouchPoints ?? 10;
            var cdp = await context.NewCDPSessionAsync(page);
            await cdp.SendAsync("Emulation.setDeviceMetricsOverride", new Dictionary<string, object>
            {
                ["width"] = width,
                ["height"] = height,
                ["deviceScaleFactor"] = dpr,
                ["mobile"] = false,
                ["screenWidth"] = width,
                ["screenHeight"] = height
            });
            await cdp.SendAsync("Emulation.setTouchEmulationEnabled", new Dictionary<string, object>
            {
                ["enabled"] = true,
                ["maxTouchPoints"] = touchPoints
            });
        }
        catch
        {
            // best-effort
        }
    }
}
