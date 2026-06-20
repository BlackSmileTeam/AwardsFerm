using AwardsFerm.Core.Models;

namespace AwardsFerm.Infrastructure.Playwright;

internal static class DeviceFingerprintRotator
{
    private static readonly Random Random = new();

    private sealed record DeviceTemplate(
        DeviceFormFactor FormFactor,
        int Width,
        int Height,
        double ScaleFactor,
        int Cores,
        int Memory,
        string Platform,
        int MaxTouchPoints,
        string GpuVendor,
        string GpuRenderer,
        string UserAgentFormat);

    private static readonly (string Vendor, string Renderer)[] LaptopGpus =
    [
        ("Intel Inc.", "Intel Iris OpenGL Engine"),
        ("Intel Inc.", "Intel(R) UHD Graphics 630"),
        ("NVIDIA Corporation", "NVIDIA GeForce GTX 1660 SUPER/PCIe/SSE2"),
        ("NVIDIA Corporation", "NVIDIA GeForce RTX 3060/PCIe/SSE2"),
        ("AMD", "AMD Radeon RX 580 Series"),
        ("Google Inc. (Intel)", "ANGLE (Intel, Intel(R) UHD Graphics 630 Direct3D11 vs_5_0 ps_5_0, D3D11)")
    ];

    private static readonly (string Model, string Android, int W, int H, double Dpr)[] TabletModels =
    [
        ("SM-X910", "14", 1366, 1024, 2),
        ("Lenovo TB-J606F", "13", 1280, 800, 1.5),
        ("SM-T970", "13", 1024, 768, 2),
        ("21051182G", "12", 1180, 820, 1.5)
    ];

    private static readonly (string Vendor, string Renderer)[] MobileGpus =
    [
        ("Qualcomm", "Adreno (TM) 740"),
        ("Qualcomm", "Adreno (TM) 730"),
        ("ARM", "Mali-G715"),
        ("Google Inc. (Qualcomm)", "ANGLE (Qualcomm, Adreno (TM) 740, OpenGL ES 3.2)")
    ];

    /// <summary>Случайный отпечаток устройства на каждый запуск сессии (базовый профиль не перезаписывается).</summary>
    public static DesktopProfile RotateForSession(
        DesktopProfile baseProfile,
        string profilesRoot,
        bool useProxy = true,
        string? explicitProxyUrl = null)
    {
        var chromeMajor = Random.Next(120, 132);
        var chromeBuild = Random.Next(6100, 6800);
        var template = PickDeviceTemplate(chromeMajor, chromeBuild);
        ProxyEntry? proxyEntry = null;
        if (!string.IsNullOrWhiteSpace(explicitProxyUrl))
        {
            proxyEntry = new ProxyEntry
            {
                Url = explicitProxyUrl,
                Geo = ProxyRotator.ResolveGeo(explicitProxyUrl, null)
            };
        }
        else if (useProxy)
        {
            proxyEntry = ProxyRotator.PickForProfile(profilesRoot, baseProfile.Id);
        }

        var proxy = proxyEntry?.Url;
        var browserSessionId = Guid.NewGuid().ToString("N");
        if (proxy is not null)
            proxy = ProxyUrlHelper.ConfigureForProfile(proxy, baseProfile.Id);
        var geo = proxy is not null
            ? ProxyRotator.ResolveGeo(proxyEntry!.Url, proxyEntry.Geo).WithJitter(Random)
            : RussiaGeo.PickForProfile(baseProfile.Id, baseProfile).WithJitter(Random);
        var profileDir = Path.Combine(profilesRoot, baseProfile.Id);

        var userAgent = string.Format(
            System.Globalization.CultureInfo.InvariantCulture,
            template.UserAgentFormat,
            chromeMajor,
            chromeBuild);

        return new DesktopProfile
        {
            Id = baseProfile.Id,
            Name = baseProfile.Name,
            BrowserSessionId = browserSessionId,
            FormFactor = template.FormFactor,
            UserAgent = userAgent,
            ViewportWidth = template.Width,
            ViewportHeight = template.Height,
            Locale = geo.Locale,
            Timezone = geo.Timezone,
            Latitude = geo.Latitude,
            Longitude = geo.Longitude,
            GeoAnchorLatitude = geo.Latitude,
            GeoAnchorLongitude = geo.Longitude,
            LocationLabel = geo.Label,
            ProxyUrl = baseProfile.ProxyUrl ?? proxy,
            CookiesPath = Path.Combine(profileDir, $"cookies-{browserSessionId}.json"),
            HardwareConcurrency = template.Cores,
            DeviceMemory = template.Memory,
            WebGlVendor = template.GpuVendor,
            WebGlRenderer = template.GpuRenderer,
            Platform = template.Platform,
            DeviceScaleFactor = template.ScaleFactor,
            MaxTouchPoints = template.MaxTouchPoints,
            SessionDeviceId = Guid.NewGuid().ToString("N"),
            SessionMac = GenerateMacAddress()
        };
    }

    private static DeviceTemplate PickDeviceTemplate(int chromeMajor, int chromeBuild)
    {
        _ = chromeMajor;
        _ = chromeBuild;
        return Random.Next(100) switch
        {
            < 55 => PickLaptop(),
            _ => PickTablet()
        };
    }

    private static DeviceTemplate PickLaptop()
    {
        var gpu = LaptopGpus[Random.Next(LaptopGpus.Length)];
        var cores = new[] { 4, 6, 8, 12, 16 }[Random.Next(5)];
        var memory = new[] { 4, 8, 16 }[Random.Next(3)];
        var widths = new[] { 1680, 1536, 1440, 1366, 1280 };
        var width = widths[Random.Next(widths.Length)];
        var height = width switch
        {
            1680 => 1050,
            1536 => 864,
            1440 => 900,
            1366 => 768,
            _ => 800
        };

        return new DeviceTemplate(
            DeviceFormFactor.Desktop,
            width,
            height,
            1,
            cores,
            memory,
            "Win32",
            0,
            gpu.Vendor,
            gpu.Renderer,
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/{0}.0.{1}.0 Safari/537.36");
    }

    private static DeviceTemplate PickTablet()
    {
        var tablet = TabletModels[Random.Next(TabletModels.Length)];
        var gpu = MobileGpus[Random.Next(MobileGpus.Length)];
        return new DeviceTemplate(
            DeviceFormFactor.Tablet,
            tablet.W,
            tablet.H,
            tablet.Dpr,
            8,
            8,
            "Linux armv8l",
            10,
            gpu.Vendor,
            gpu.Renderer,
            $"Mozilla/5.0 (Linux; Android {tablet.Android}; {tablet.Model}) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/{{0}}.0.{{1}}.0 Safari/537.36");
    }

    public static void PruneOldBrowserInstances(string profilesRoot, string profileId, int keep = 3)
    {
        var baseDir = Path.Combine(profilesRoot, profileId, "browser-data");
        if (!Directory.Exists(baseDir))
            return;

        foreach (var dir in Directory.GetDirectories(baseDir)
                     .OrderByDescending(Directory.GetCreationTimeUtc)
                     .Skip(keep))
        {
            try
            {
                Directory.Delete(dir, recursive: true);
            }
            catch
            {
                // ignore locked dirs
            }
        }

        var profileDir = Path.Combine(profilesRoot, profileId);
        foreach (var cookieFile in Directory.GetFiles(profileDir, "cookies-*.json")
                     .OrderByDescending(File.GetCreationTimeUtc)
                     .Skip(keep + 2))
        {
            try
            {
                File.Delete(cookieFile);
            }
            catch
            {
                // ignore
            }
        }
    }

    public static string Describe(DesktopProfile profile, string? publicIp = null)
    {
        var ipPart = publicIp is not null
            ? $"IP: {publicIp}"
            : profile.ProxyUrl is not null
                ? $"прокси: {MaskProxy(profile.ProxyUrl)} (IP уточняется после старта)"
                : "IP: ваш реальный (для смены — proxies.txt)";

        var deviceLabel = profile.FormFactor switch
        {
            DeviceFormFactor.Tablet => "планшет",
            _ => "ноутбук"
        };

        return $"ID: {profile.SessionDeviceId[..8]}…, {ipPart}, " +
               $"локация: {SessionLocationHelper.Format(profile)}, " +
               $"{deviceLabel}, {profile.ViewportWidth}×{profile.ViewportHeight}, " +
               $"{profile.HardwareConcurrency} CPU, {profile.DeviceMemory} GB RAM";
    }

    private static string GenerateMacAddress()
    {
        var bytes = new byte[6];
        Random.NextBytes(bytes);
        bytes[0] = (byte)((bytes[0] & 0xFE) | 0x02);
        return string.Join(':', bytes.Select(b => b.ToString("X2")));
    }

    private static string MaskProxy(string proxyUrl)
    {
        try
        {
            var uri = new Uri(proxyUrl);
            if (string.IsNullOrEmpty(uri.UserInfo))
                return $"{uri.Scheme}://{uri.Host}:{uri.Port}";

            return $"{uri.Scheme}://{MaskLogin(uri.UserInfo.Split(':')[0])}@{uri.Host}:{uri.Port}";
        }
        catch
        {
            return "настроен";
        }
    }

    private static string MaskLogin(string login) =>
        login.Length <= 4 ? "***" : $"{login[..4]}***";
}
