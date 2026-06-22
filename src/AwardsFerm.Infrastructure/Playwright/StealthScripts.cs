using AwardsFerm.Core.Models;

namespace AwardsFerm.Infrastructure.Playwright;

internal static class StealthScripts
{
    public static string BuildInitScript(DesktopProfile profile)
    {
        if (profile.UseNativeDevice)
            return "/* native device — no fingerprint spoofing */";

        var vendor = EscapeJs(profile.WebGlVendor);
        var renderer = EscapeJs(profile.WebGlRenderer);
        var platform = EscapeJs(profile.Platform);
        var locale = EscapeJs(profile.Locale);

        var isChromium = profile.BrowserEngine == BrowserEngine.Chromium;

        return $$"""
            Object.defineProperty(navigator, 'webdriver', { get: () => false });

            {{(isChromium ? """
            if (!window.chrome) {
                window.chrome = { runtime: {} };
            }
            """ : "")}}

            Object.defineProperty(navigator, 'languages', {
                get: () => ['{{locale}}', 'ru', 'en-US', 'en'],
            });

            Object.defineProperty(navigator, 'platform', {
                get: () => '{{platform}}',
            });

            Object.defineProperty(navigator, 'hardwareConcurrency', {
                get: () => {{profile.HardwareConcurrency}},
            });

            Object.defineProperty(navigator, 'deviceMemory', {
                get: () => {{profile.DeviceMemory}},
            });

            Object.defineProperty(navigator, 'maxTouchPoints', {
                get: () => {{profile.MaxTouchPoints}},
            });

            const originalQuery = window.navigator.permissions.query;
            window.navigator.permissions.query = (parameters) =>
                parameters.name === 'notifications'
                    ? Promise.resolve({ state: Notification.permission })
                    : originalQuery(parameters);

            const getParameter = WebGLRenderingContext.prototype.getParameter;
            WebGLRenderingContext.prototype.getParameter = function(parameter) {
                if (parameter === 37445) return '{{vendor}}';
                if (parameter === 37446) return '{{renderer}}';
                return getParameter.call(this, parameter);
            };
            """;
    }

    private static string EscapeJs(string value) =>
        value.Replace("\\", "\\\\").Replace("'", "\\'");
}
