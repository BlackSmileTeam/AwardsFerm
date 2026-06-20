using AwardsFerm.Core.Models;
using AwardsFerm.Infrastructure.Storage;
using Microsoft.Playwright;

namespace AwardsFerm.Infrastructure.Playwright;

public sealed class BrowserLaunchResult : IAsyncDisposable
{
    public required IPlaywright Playwright { get; init; }
    public required IBrowserContext Context { get; init; }
    public required IPage Page { get; init; }

    public async ValueTask DisposeAsync()
    {
        try { await Page.CloseAsync(); } catch { /* ignore */ }
        try { await Context.CloseAsync(); } catch { /* ignore */ }
        Playwright.Dispose();
    }
}

public sealed class PlaywrightBrowserFactory
{
    public async Task<BrowserLaunchResult> LaunchPersistentAsync(
        DesktopProfile profile,
        YandexGamesSearchOptions options,
        string profilesRoot,
        CancellationToken cancellationToken = default)
    {
        var playwright = await Microsoft.Playwright.Playwright.CreateAsync();
        var sessionPart = string.IsNullOrWhiteSpace(profile.BrowserSessionId)
            ? "default"
            : profile.BrowserSessionId;
        var userDataDir = Path.Combine(profilesRoot, profile.Id, "browser-data", sessionPart);
        Directory.CreateDirectory(userDataDir);

        var windowPosition = ResolveWindowPosition(profile.Id);
        var isMobileLike = profile.FormFactor != DeviceFormFactor.Desktop;
        var viewport = new ViewportSize { Width = profile.ViewportWidth, Height = profile.ViewportHeight };

        var isLinux = OperatingSystem.IsLinux();
        var headless = ResolveHeadlessForLaunch(options.Headless, isLinux);

        var launchOptions = new BrowserTypeLaunchPersistentContextOptions
        {
            Headless = headless,
            UserAgent = profile.UserAgent,
            ViewportSize = viewport,
            Locale = profile.Locale,
            TimezoneId = profile.Timezone,
            Geolocation = new Geolocation
            {
                Latitude = (float)profile.Latitude,
                Longitude = (float)profile.Longitude
            },
            Permissions = ["geolocation"],
            ColorScheme = ColorScheme.Light,
            DeviceScaleFactor = (float)profile.DeviceScaleFactor,
            // CDP mobile=true на десктопном Chrome вызывает скачки масштаба; layout задаёт UA + viewport + touch.
            IsMobile = false,
            HasTouch = isMobileLike,
            SlowMo = 50,
            Args = BuildChromeArgs(profile, windowPosition, isLinux),
            Timeout = 120_000,
            ExtraHTTPHeaders = new Dictionary<string, string>
            {
                ["Accept-Language"] = BuildAcceptLanguage(profile.Locale)
            }
        };

        if (isMobileLike)
        {
            launchOptions.ScreenSize = new ScreenSize
            {
                Width = profile.ViewportWidth,
                Height = profile.ViewportHeight
            };
        }

        if (!string.IsNullOrWhiteSpace(profile.ProxyUrl))
        {
            if (ProxyUrlHelper.TryParseProxy(profile.ProxyUrl, out var creds))
            {
                launchOptions.Proxy = new Proxy
                {
                    Server = creds.Server,
                    Username = creds.Username,
                    Password = creds.Password
                };
            }
            else
            {
                launchOptions.Proxy = new Proxy { Server = profile.ProxyUrl };
            }
        }

        IBrowserContext context;
        if (!isLinux)
        {
            try
            {
                launchOptions.Channel = "chrome";
                context = await playwright.Chromium.LaunchPersistentContextAsync(userDataDir, launchOptions);
            }
            catch
            {
                launchOptions.Channel = null;
                context = await playwright.Chromium.LaunchPersistentContextAsync(userDataDir, launchOptions);
            }
        }
        else
        {
            launchOptions.Channel = null;
            context = await playwright.Chromium.LaunchPersistentContextAsync(userDataDir, launchOptions);
        }

        await context.AddInitScriptAsync(StealthScripts.BuildInitScript(profile));

        var page = context.Pages.Count > 0 ? context.Pages[0] : await context.NewPageAsync();
        await StabilizeViewportAsync(context, page, profile);

        return new BrowserLaunchResult
        {
            Playwright = playwright,
            Context = context,
            Page = page
        };
    }

    /// <summary>
    /// headless_shell в Docker часто зависает на GPU/X11; при DISPLAY используем полный Chromium на Xvfb.
    /// </summary>
    private static bool ResolveHeadlessForLaunch(bool requestedHeadless, bool isLinux)
    {
        if (!isLinux || !requestedHeadless)
            return requestedHeadless;

        var display = Environment.GetEnvironmentVariable("DISPLAY");
        return string.IsNullOrWhiteSpace(display);
    }

    private static string[] BuildChromeArgs(DesktopProfile profile, (int X, int Y) windowPosition, bool isLinux)
    {
        var common = new List<string>
        {
            "--disable-blink-features=AutomationControlled",
            "--disable-infobars",
            $"--lang={profile.Locale}",
            "--no-first-run",
            "--no-default-browser-check"
        };

        if (isLinux)
        {
            common.Add("--disable-gpu");
            common.Add("--disable-software-rasterizer");
        }

        if (profile.FormFactor == DeviceFormFactor.Desktop)
        {
            common.Add($"--window-size={profile.ViewportWidth},{profile.ViewportHeight}");
            common.Add($"--window-position={windowPosition.X},{windowPosition.Y}");
            return common.ToArray();
        }

        var chromeChrome = profile.FormFactor == DeviceFormFactor.Phone ? 110 : 90;
        common.Add($"--window-size={profile.ViewportWidth},{profile.ViewportHeight + chromeChrome}");
        common.Add($"--window-position={windowPosition.X},{windowPosition.Y}");
        common.Add("--disable-features=TranslateUI");
        return common.ToArray();
    }

    private static async Task StabilizeViewportAsync(IBrowserContext context, IPage page, DesktopProfile profile)
    {
        if (profile.FormFactor == DeviceFormFactor.Desktop)
            return;

        try
        {
            var cdp = await context.NewCDPSessionAsync(page);
            await cdp.SendAsync("Emulation.setDeviceMetricsOverride", new Dictionary<string, object>
            {
                ["width"] = profile.ViewportWidth,
                ["height"] = profile.ViewportHeight,
                ["deviceScaleFactor"] = profile.DeviceScaleFactor,
                ["mobile"] = false,
                ["screenWidth"] = profile.ViewportWidth,
                ["screenHeight"] = profile.ViewportHeight
            });
            await cdp.SendAsync("Emulation.setTouchEmulationEnabled", new Dictionary<string, object>
            {
                ["enabled"] = true,
                ["maxTouchPoints"] = profile.MaxTouchPoints
            });
        }
        catch
        {
            // best-effort
        }
    }

    private static (int X, int Y) ResolveWindowPosition(string profileId)
    {
        var hash = Math.Abs(profileId.GetHashCode(StringComparison.Ordinal));
        return (hash % 3 * 24, hash % 5 * 32);
    }

    private static string BuildAcceptLanguage(string locale)
    {
        if (string.IsNullOrWhiteSpace(locale))
            return "ru-RU,ru;q=0.9,en-US;q=0.8";

        var baseLang = locale.Contains('-', StringComparison.Ordinal)
            ? locale.Split('-')[0]
            : locale;
        return $"{locale},{baseLang};q=0.9,en-US;q=0.8";
    }
}
