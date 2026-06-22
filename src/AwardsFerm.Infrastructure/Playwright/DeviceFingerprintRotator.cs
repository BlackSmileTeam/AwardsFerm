using AwardsFerm.Core.Models;

namespace AwardsFerm.Infrastructure.Playwright;

internal static class DeviceFingerprintRotator
{
    private static readonly Random Random = new();

    private sealed record DeviceTemplate(
        SessionDevicePlatform DevicePlatform,
        DeviceFormFactor FormFactor,
        BrowserEngine BrowserEngine,
        int Width,
        int Height,
        double ScaleFactor,
        int Cores,
        int Memory,
        string OsPlatform,
        int MaxTouchPoints,
        string GpuVendor,
        string GpuRenderer,
        string UserAgent);

    private static readonly (string Vendor, string Renderer)[] LaptopGpus =
    [
        ("Intel Inc.", "Intel Iris OpenGL Engine"),
        ("Intel Inc.", "Intel(R) UHD Graphics 630"),
        ("NVIDIA Corporation", "NVIDIA GeForce GTX 1660 SUPER/PCIe/SSE2"),
        ("NVIDIA Corporation", "NVIDIA GeForce RTX 3060/PCIe/SSE2"),
        ("AMD", "AMD Radeon RX 580 Series"),
        ("Google Inc. (Intel)", "ANGLE (Intel, Intel(R) UHD Graphics 630 Direct3D11 vs_5_0 ps_5_0, D3D11)")
    ];

    private static readonly (string Vendor, string Renderer)[] DesktopGpus =
    [
        ("NVIDIA Corporation", "NVIDIA GeForce RTX 4070/PCIe/SSE2"),
        ("NVIDIA Corporation", "NVIDIA GeForce RTX 3070/PCIe/SSE2"),
        ("NVIDIA Corporation", "NVIDIA GeForce GTX 1660 Ti/PCIe/SSE2"),
        ("AMD", "AMD Radeon RX 6700 XT"),
        ("Intel Inc.", "Intel(R) UHD Graphics 770"),
        ("Google Inc. (NVIDIA)", "ANGLE (NVIDIA, NVIDIA GeForce RTX 3060 Direct3D11 vs_5_0 ps_5_0, D3D11)")
    ];

    private static readonly (string Model, string Android, int W, int H, double Dpr)[] TabletModels =
    [
        ("SM-X910", "14", 1366, 1024, 2),
        ("Lenovo TB-J606F", "13", 1280, 800, 1.5),
        ("SM-T970", "13", 1024, 768, 2),
        ("21051182G", "12", 1180, 820, 1.5)
    ];

    private static readonly (string Model, string Android, int W, int H, double Dpr)[] AndroidPhoneModels =
    [
        ("SM-S928B", "14", 412, 915, 3.5),
        ("Pixel 8 Pro", "14", 412, 892, 3.5),
        ("2201116SG", "13", 393, 873, 2.75),
        ("CPH2449", "13", 360, 800, 3)
    ];

    private static readonly (string Vendor, string Renderer)[] MobileGpus =
    [
        ("Qualcomm", "Adreno (TM) 740"),
        ("Qualcomm", "Adreno (TM) 730"),
        ("ARM", "Mali-G715"),
        ("Google Inc. (Qualcomm)", "ANGLE (Qualcomm, Adreno (TM) 740, OpenGL ES 3.2)")
    ];

    private static readonly (string Vendor, string Renderer)[] AppleGpus =
    [
        ("Apple Inc.", "Apple GPU"),
        ("Apple Inc.", "Apple A17 Pro GPU"),
        ("Apple Inc.", "Apple A16 GPU")
    ];

    private static readonly (string Model, int W, int H, double Dpr)[] MacBookModels =
    [
        ("MacBookAir10,1", 1280, 832, 2),
        ("Mac14,2", 1440, 900, 2),
        ("Mac15,3", 1512, 982, 2),
        ("Mac15,7", 1728, 1117, 2),
        ("Mac15,6", 1680, 1050, 2)
    ];

    private static readonly (string Model, int W, int H, double Dpr)[] IPhoneModels =
    [
        ("iPhone15,4", 393, 852, 3),
        ("iPhone16,2", 430, 932, 3),
        ("iPhone14,5", 390, 844, 3),
        ("iPhone13,2", 414, 896, 3)
    ];

    public static DesktopProfile RotateForSession(
        DesktopProfile baseProfile,
        string profilesRoot,
        bool useProxy = true,
        string? explicitProxyUrl = null,
        SessionDevicePlatform devicePlatform = SessionDevicePlatform.Random)
    {
        if (devicePlatform == SessionDevicePlatform.Native)
            return CreateNativeProfile(baseProfile, profilesRoot, useProxy, explicitProxyUrl);

        var chromeMajor = Random.Next(120, 132);
        var chromeBuild = Random.Next(6100, 6800);
        var resolvedPlatform = ResolvePlatform(devicePlatform);
        var template = PickDeviceTemplate(resolvedPlatform, chromeMajor, chromeBuild);

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

        return new DesktopProfile
        {
            Id = baseProfile.Id,
            Name = baseProfile.Name,
            BrowserSessionId = browserSessionId,
            DevicePlatform = template.DevicePlatform,
            BrowserEngine = template.BrowserEngine,
            FormFactor = template.FormFactor,
            UserAgent = template.UserAgent,
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
            Platform = template.OsPlatform,
            DeviceScaleFactor = template.ScaleFactor,
            MaxTouchPoints = template.MaxTouchPoints,
            SessionDeviceId = Guid.NewGuid().ToString("N"),
            SessionMac = GenerateMacAddress()
        };
    }

    private static DesktopProfile CreateNativeProfile(
        DesktopProfile baseProfile,
        string profilesRoot,
        bool useProxy,
        string? explicitProxyUrl)
    {
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

        return new DesktopProfile
        {
            Id = baseProfile.Id,
            Name = baseProfile.Name,
            BrowserSessionId = browserSessionId,
            DevicePlatform = SessionDevicePlatform.Native,
            UseNativeDevice = true,
            BrowserEngine = BrowserEngine.Chromium,
            FormFactor = DeviceFormFactor.Desktop,
            UserAgent = baseProfile.UserAgent,
            ViewportWidth = baseProfile.ViewportWidth,
            ViewportHeight = baseProfile.ViewportHeight,
            Locale = geo.Locale,
            Timezone = geo.Timezone,
            Latitude = geo.Latitude,
            Longitude = geo.Longitude,
            GeoAnchorLatitude = geo.Latitude,
            GeoAnchorLongitude = geo.Longitude,
            LocationLabel = geo.Label,
            ProxyUrl = baseProfile.ProxyUrl ?? proxy,
            CookiesPath = Path.Combine(profileDir, $"cookies-{browserSessionId}.json"),
            HardwareConcurrency = baseProfile.HardwareConcurrency,
            DeviceMemory = baseProfile.DeviceMemory,
            WebGlVendor = baseProfile.WebGlVendor,
            WebGlRenderer = baseProfile.WebGlRenderer,
            Platform = baseProfile.Platform,
            DeviceScaleFactor = 1,
            MaxTouchPoints = 0,
            SessionDeviceId = Guid.NewGuid().ToString("N"),
            SessionMac = GenerateMacAddress()
        };
    }

    private static SessionDevicePlatform ResolvePlatform(SessionDevicePlatform platform)
    {
        if (platform != SessionDevicePlatform.Random)
            return platform;

        var values = new[]
        {
            SessionDevicePlatform.Desktop,
            SessionDevicePlatform.Laptop,
            SessionDevicePlatform.MacBook,
            SessionDevicePlatform.Tablet,
            SessionDevicePlatform.AndroidPhone,
            SessionDevicePlatform.IPhone
        };
        return values[Random.Next(values.Length)];
    }

    private static DeviceTemplate PickDeviceTemplate(
        SessionDevicePlatform platform,
        int chromeMajor,
        int chromeBuild) =>
        platform switch
        {
            SessionDevicePlatform.Desktop => PickDesktop(chromeMajor, chromeBuild),
            SessionDevicePlatform.Laptop => PickLaptop(chromeMajor, chromeBuild),
            SessionDevicePlatform.MacBook => PickMacBook(),
            SessionDevicePlatform.Tablet => PickTablet(chromeMajor, chromeBuild),
            SessionDevicePlatform.AndroidPhone => PickAndroidPhone(chromeMajor, chromeBuild),
            SessionDevicePlatform.IPhone => PickIPhone(),
            _ => Random.Next(2) == 0
                ? PickDesktop(chromeMajor, chromeBuild)
                : PickLaptop(chromeMajor, chromeBuild)
        };

    private static DeviceTemplate PickDesktop(int chromeMajor, int chromeBuild)
    {
        var gpu = DesktopGpus[Random.Next(DesktopGpus.Length)];
        var cores = new[] { 6, 8, 12, 16, 24 }[Random.Next(5)];
        var memory = new[] { 8, 16, 32 }[Random.Next(3)];
        var (width, height) = new (int, int)[]
        {
            (1920, 1080),
            (2560, 1440),
            (1920, 1200),
            (1680, 1050),
            (1600, 900)
        }[Random.Next(5)];

        return new DeviceTemplate(
            SessionDevicePlatform.Desktop,
            DeviceFormFactor.Desktop,
            BrowserEngine.Chromium,
            width,
            height,
            1,
            cores,
            memory,
            "Win32",
            0,
            gpu.Vendor,
            gpu.Renderer,
            BuildChromeUserAgent(chromeMajor, chromeBuild));
    }

    private static DeviceTemplate PickLaptop(int chromeMajor, int chromeBuild)
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
            SessionDevicePlatform.Laptop,
            DeviceFormFactor.Desktop,
            BrowserEngine.Chromium,
            width,
            height,
            1,
            cores,
            memory,
            "Win32",
            0,
            gpu.Vendor,
            gpu.Renderer,
            BuildChromeUserAgent(chromeMajor, chromeBuild));
    }

    private static DeviceTemplate PickMacBook()
    {
        var mac = MacBookModels[Random.Next(MacBookModels.Length)];
        var gpu = AppleGpus[Random.Next(AppleGpus.Length)];
        var macOsMajor = Random.Next(13, 16);
        var macOsMinor = Random.Next(0, 6);
        var macOsPatch = Random.Next(0, 4);
        var safariMajor = macOsMajor >= 15 ? Random.Next(18, 20) : Random.Next(16, 18);
        var safariMinor = Random.Next(0, 4);
        var memory = new[] { 8, 16, 24 }[Random.Next(3)];

        return new DeviceTemplate(
            SessionDevicePlatform.MacBook,
            DeviceFormFactor.Desktop,
            BrowserEngine.WebKit,
            mac.W,
            mac.H,
            mac.Dpr,
            8,
            memory,
            "MacIntel",
            0,
            gpu.Vendor,
            gpu.Renderer,
            $"Mozilla/5.0 (Macintosh; Intel Mac OS X {macOsMajor}_{macOsMinor}_{macOsPatch}) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/{safariMajor}.{safariMinor} Safari/605.1.15");
    }

    private static DeviceTemplate PickTablet(int chromeMajor, int chromeBuild)
    {
        var tablet = TabletModels[Random.Next(TabletModels.Length)];
        var gpu = MobileGpus[Random.Next(MobileGpus.Length)];
        return new DeviceTemplate(
            SessionDevicePlatform.Tablet,
            DeviceFormFactor.Tablet,
            BrowserEngine.Chromium,
            tablet.W,
            tablet.H,
            tablet.Dpr,
            8,
            8,
            "Linux armv8l",
            10,
            gpu.Vendor,
            gpu.Renderer,
            $"Mozilla/5.0 (Linux; Android {tablet.Android}; {tablet.Model}) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/{chromeMajor}.0.{chromeBuild}.0 Safari/537.36");
    }

    private static DeviceTemplate PickAndroidPhone(int chromeMajor, int chromeBuild)
    {
        var phone = AndroidPhoneModels[Random.Next(AndroidPhoneModels.Length)];
        var gpu = MobileGpus[Random.Next(MobileGpus.Length)];
        return new DeviceTemplate(
            SessionDevicePlatform.AndroidPhone,
            DeviceFormFactor.Phone,
            BrowserEngine.Chromium,
            phone.W,
            phone.H,
            phone.Dpr,
            8,
            8,
            "Linux armv8l",
            5,
            gpu.Vendor,
            gpu.Renderer,
            $"Mozilla/5.0 (Linux; Android {phone.Android}; {phone.Model}) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/{chromeMajor}.0.{chromeBuild}.0 Mobile Safari/537.36");
    }

    private static DeviceTemplate PickIPhone()
    {
        var phone = IPhoneModels[Random.Next(IPhoneModels.Length)];
        var gpu = AppleGpus[Random.Next(AppleGpus.Length)];
        var iosMajor = Random.Next(16, 19);
        var iosMinor = Random.Next(0, 6);
        var safariMajor = iosMajor;
        var safariMinor = Random.Next(0, 4);

        return new DeviceTemplate(
            SessionDevicePlatform.IPhone,
            DeviceFormFactor.Phone,
            BrowserEngine.WebKit,
            phone.W,
            phone.H,
            phone.Dpr,
            6,
            4,
            "iPhone",
            5,
            gpu.Vendor,
            gpu.Renderer,
            $"Mozilla/5.0 (iPhone; CPU iPhone OS {iosMajor}_{iosMinor} like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/{safariMajor}.{safariMinor} Mobile/15E148 Safari/604.1");
    }

    private static string BuildChromeUserAgent(int chromeMajor, int chromeBuild) =>
        $"Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/{chromeMajor}.0.{chromeBuild}.0 Safari/537.36";

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

        var deviceLabel = profile.UseNativeDevice
            ? "без эмуляции (текущая машина)"
            : profile.DevicePlatform switch
        {
            SessionDevicePlatform.Native => "без эмуляции (текущая машина)",
            SessionDevicePlatform.Desktop => "ПК",
            SessionDevicePlatform.Laptop => "ноутбук",
            SessionDevicePlatform.MacBook => "MacBook (Safari)",
            SessionDevicePlatform.Tablet => "планшет",
            SessionDevicePlatform.AndroidPhone => "Android смартфон",
            SessionDevicePlatform.IPhone => "iPhone (Safari)",
            _ => profile.ViewportWidth >= 1600 ? "ПК" : "ноутбук"
        };

        var browserLabel = profile.UseNativeDevice
            ? "Chrome (нативный)"
            : profile.BrowserEngine == BrowserEngine.WebKit ? "Safari/WebKit" : "Chrome";

        var sizeLabel = profile.UseNativeDevice
            ? "размер окна хоста"
            : $"{profile.ViewportWidth}×{profile.ViewportHeight}";

        return $"ID: {profile.SessionDeviceId[..8]}…, {ipPart}, " +
               $"локация: {SessionLocationHelper.Format(profile)}, " +
               $"{deviceLabel}, {browserLabel}, {sizeLabel}, " +
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
