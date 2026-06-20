using AwardsFerm.Core.Interfaces;
using AwardsFerm.Core.Models;
using AwardsFerm.Infrastructure.Behavior;
using Microsoft.Playwright;

namespace AwardsFerm.Infrastructure.Playwright;

internal static class OrientationHelper
{
    private enum RequiredOrientation
    {
        None,
        Portrait,
        Landscape
    }

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
        CancellationToken cancellationToken = default,
        LandscapeState? landscapeState = null,
        SessionStuckTracker? stuckTracker = null)
    {
        if (page.IsClosed)
            return false;

        var rotatePrompt = await IsRotatePromptVisibleAsync(page);
        if (!rotatePrompt && landscapeState?.Applied == true)
            return false;

        var required = await DetectRequiredOrientationAsync(page, rotatePrompt);
        if (!rotatePrompt && required == RequiredOrientation.None)
        {
            var vp = page.ViewportSize;
            if (vp is not null && vp.Width < vp.Height)
                required = RequiredOrientation.Landscape;
            else
                return false;
        }

        if (required == RequiredOrientation.None)
            return false;

        var (targetWidth, targetHeight) = ResolveViewport(profile, page, required);
        var current = page.ViewportSize;
        var alreadyCorrect = current is not null &&
            IsOrientationMatch(current.Width, current.Height, required);

        if (alreadyCorrect)
        {
            if (landscapeState is not null)
                landscapeState.Applied = true;
            if (!rotatePrompt)
                stuckTracker?.Reset("orientation_rotate");
            return false;
        }

        await ApplyViewportAsync(page, context, targetWidth, targetHeight, profile, cancellationToken);

        if (landscapeState is not null)
            landscapeState.Applied = true;

        if (stuckTracker is not null &&
            stuckTracker.Register("orientation_rotate") &&
            reporter is not null &&
            sessionId is not null)
        {
            await SessionScreenDiagnostic.TriggerRestartAsync(
                sessionId,
                page,
                "Повторяющийся поворот экрана без прогресса",
                reporter,
                cancellationToken);
        }

        if (reporter is not null && sessionId is not null && rotatePrompt)
        {
            var label = required == RequiredOrientation.Portrait ? "вертикальную" : "альбомную";
            await reporter.ReportAsync(new SessionEvent
            {
                SessionId = sessionId,
                Type = SessionEventType.Log,
                Message = $"Повёрнут экран в {label} ориентацию ({targetWidth}×{targetHeight})"
            }, cancellationToken);
        }

        if (!rotatePrompt)
            stuckTracker?.Reset("orientation_rotate");

        await HumanBehavior.DelayAsync(1500, 2500, cancellationToken);
        return true;
    }

    private static async Task<RequiredOrientation> DetectRequiredOrientationAsync(IPage page, bool rotatePromptVisible)
    {
        if (!rotatePromptVisible)
            return RequiredOrientation.None;

        var text = await GetOrientationHintTextAsync(page);
        var lower = text.ToLowerInvariant();

        if (lower.Contains("вертикаль", StringComparison.Ordinal) ||
            lower.Contains("vertical", StringComparison.Ordinal) ||
            lower.Contains("portrait", StringComparison.Ordinal))
        {
            return RequiredOrientation.Portrait;
        }

        if (lower.Contains("альбом", StringComparison.Ordinal) ||
            lower.Contains("landscape", StringComparison.Ordinal) ||
            lower.Contains("horizontal", StringComparison.Ordinal))
        {
            return RequiredOrientation.Landscape;
        }

        var vp = page.ViewportSize;
        if (vp is not null && vp.Width > vp.Height)
            return RequiredOrientation.Portrait;

        return RequiredOrientation.Landscape;
    }

    private static async Task<string> GetOrientationHintTextAsync(IPage page)
    {
        try
        {
            return await page.EvaluateAsync<string>(
                """
                () => {
                    const parts = [];
                    const body = document.body?.innerText?.trim();
                    if (body) parts.push(body);
                    for (const iframe of document.querySelectorAll('iframe')) {
                        try {
                            const t = iframe.contentDocument?.body?.innerText?.trim();
                            if (t) parts.push(t);
                        } catch { /* cross-origin */ }
                    }
                    return parts.join('\n').slice(0, 4000);
                }
                """) ?? string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    private static (int Width, int Height) ResolveViewport(
        DesktopProfile? profile,
        IPage page,
        RequiredOrientation required)
    {
        var baseW = profile?.ViewportWidth ?? page.ViewportSize?.Width ?? 1280;
        var baseH = profile?.ViewportHeight ?? page.ViewportSize?.Height ?? 800;
        var longSide = Math.Max(baseW, baseH);
        var shortSide = Math.Min(baseW, baseH);

        return required == RequiredOrientation.Portrait
            ? (shortSide, longSide)
            : (longSide, shortSide);
    }

    private static bool IsOrientationMatch(int width, int height, RequiredOrientation required) =>
        required == RequiredOrientation.Portrait
            ? width < height
            : width >= height;

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

    private static async Task ApplyViewportAsync(
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
