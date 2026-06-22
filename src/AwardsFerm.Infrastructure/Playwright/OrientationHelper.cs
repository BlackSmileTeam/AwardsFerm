using AwardsFerm.Core.Interfaces;
using AwardsFerm.Core.Models;
using AwardsFerm.Infrastructure.Behavior;
using Microsoft.Playwright;

namespace AwardsFerm.Infrastructure.Playwright;

/// <summary>
/// Ориентация экрана для Яндекс Игр: «Поверните устройство», вертикальная/горизонтальная.
/// </summary>
internal static class OrientationHelper
{
    private static readonly string[] RotatePromptMarkers =
    [
        "Поверните устройство",
        "Rotate your device",
        "Turn your device"
    ];

    private static readonly string[] PortraitMarkers =
    [
        "вертикальн",
        "только в вертикальной",
        "вертикальной ориентации",
        "portrait",
        "vertical orientation"
    ];

    private static readonly string[] LandscapeMarkers =
    [
        "альбомн",
        "горизонтальн",
        "landscape",
        "horizontal orientation"
    ];

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
        _ = landscapeState;
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

        var pageText = await CollectVisibleTextAsync(page);
        if (!ContainsRotatePrompt(pageText))
            return null;

        if (ContainsAny(pageText, PortraitMarkers))
            return ScreenOrientation.Portrait;

        if (ContainsAny(pageText, LandscapeMarkers))
            return ScreenOrientation.Landscape;

        // «Поверните устройство» без уточнения — по умолчанию портрет (типичный кейс мобильных игр).
        return ScreenOrientation.Portrait;
    }

    private static bool ContainsRotatePrompt(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return false;

        return ContainsAny(text, RotatePromptMarkers) ||
               ContainsAny(text, PortraitMarkers) ||
               ContainsAny(text, LandscapeMarkers);
    }

    private static async Task<string> CollectVisibleTextAsync(IPage page)
    {
        var parts = new List<string>();

        foreach (var frame in page.Frames)
        {
            try
            {
                var chunk = await frame.EvaluateAsync<string>(
                    """
                    () => {
                      const parts = [];
                      const visit = (root) => {
                        if (!root) return;
                        try {
                          const text = root.innerText || root.textContent || '';
                          if (text.trim()) parts.push(text);
                        } catch { /* ignore */ }
                        const nodes = root.querySelectorAll
                          ? root.querySelectorAll('[class*="rotate"], [class*="orientation"], [class*="portrait"], [class*="landscape"], p, h1, h2, h3, span, div')
                          : [];
                        for (const node of nodes) {
                          if (node.offsetParent === null && node !== document.body) continue;
                          const t = (node.innerText || node.textContent || '').trim();
                          if (t.length >= 8) parts.push(t);
                        }
                      };
                      visit(document.body);
                      return parts.join('\n');
                    }
                    """);
                if (!string.IsNullOrWhiteSpace(chunk))
                    parts.Add(chunk);
            }
            catch
            {
                // ignore frame errors
            }
        }

        try
        {
            var title = await page.TitleAsync();
            if (!string.IsNullOrWhiteSpace(title))
                parts.Add(title);
        }
        catch
        {
            // ignore
        }

        return string.Join('\n', parts);
    }

    private static bool ContainsAny(string text, IEnumerable<string> markers)
    {
        var lower = text.ToLowerInvariant();
        foreach (var marker in markers)
        {
            if (lower.Contains(marker, StringComparison.Ordinal))
                return true;
        }

        return false;
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

        var baseW = profile?.ViewportWidth > 0 ? profile.ViewportWidth : Math.Max(vp.Width, vp.Height);
        var baseH = profile?.ViewportHeight > 0 ? profile.ViewportHeight : Math.Min(vp.Width, vp.Height);

        var targetW = orientation == ScreenOrientation.Landscape
            ? Math.Max(baseW, baseH)
            : Math.Min(baseW, baseH);
        var targetH = orientation == ScreenOrientation.Landscape
            ? Math.Min(baseW, baseH)
            : Math.Max(baseW, baseH);

        if (vp.Width == targetW && vp.Height == targetH)
            return;

        var label = orientation == ScreenOrientation.Landscape ? "альбомную" : "портретную";
        await reporter.ReportAsync(new SessionEvent
        {
            SessionId = sessionId,
            Type = SessionEventType.Log,
            Message = $"Сообщение «Поверните устройство» — поворачиваем экран в {label} ориентацию ({targetW}×{targetH})"
        }, cancellationToken);

        await page.SetViewportSizeAsync(targetW, targetH);

        if (profile is not null)
        {
            profile.ViewportWidth = targetW;
            profile.ViewportHeight = targetH;
        }

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
            // CDP недоступен в WebKit — достаточно viewport.
        }

        await HumanBehavior.DelayAsync(800, 1200, cancellationToken);
    }

    private enum ScreenOrientation
    {
        Portrait,
        Landscape
    }
}
