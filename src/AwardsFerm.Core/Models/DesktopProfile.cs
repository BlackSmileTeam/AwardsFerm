namespace AwardsFerm.Core.Models;

public sealed class DesktopProfile
{
    public string Id { get; set; } = "session-001";
    public string Name { get; set; } = "Desktop Chrome Win10";
    public string UserAgent { get; set; } =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/121.0.0.0 Safari/537.36";
    public int ViewportWidth { get; set; } = 1920;
    public int ViewportHeight { get; set; } = 1080;
    public string Locale { get; set; } = "ru-RU";
    public string Timezone { get; set; } = "Europe/Moscow";
    public double Latitude { get; set; } = 60.053085;
    public double Longitude { get; set; } = 30.311729;
    public string? ProxyUrl { get; set; }
    public string LocationLabel { get; set; } = string.Empty;
    public string CookiesPath { get; set; } = "profiles/session-001/cookies.json";
    public int HardwareConcurrency { get; set; } = 8;
    public int DeviceMemory { get; set; } = 8;
    public string WebGlVendor { get; set; } = "Intel Inc.";
    public string WebGlRenderer { get; set; } = "Intel Iris OpenGL Engine";
    public string Platform { get; set; } = "Win32";
    public double DeviceScaleFactor { get; set; } = 1;
    /// <summary>Локальный идентификатор сессии (не передаётся сайтам).</summary>
    public string SessionDeviceId { get; set; } = string.Empty;
    /// <summary>Случайный MAC для лога; сайты в браузере MAC не получают.</summary>
    public string SessionMac { get; set; } = string.Empty;
    /// <summary>Уникальный идентификатор экземпляра браузера (отдельный user-data и cookies).</summary>
    public string BrowserSessionId { get; set; } = string.Empty;
    public DeviceFormFactor FormFactor { get; set; } = DeviceFormFactor.Desktop;
    public SessionDevicePlatform DevicePlatform { get; set; } = SessionDevicePlatform.Random;
    /// <summary>Не подменять fingerprint — использовать реальный браузер на хосте.</summary>
    public bool UseNativeDevice { get; set; }
    public BrowserEngine BrowserEngine { get; set; } = BrowserEngine.Chromium;
    public int MaxTouchPoints { get; set; }
    /// <summary>Базовая точка для периодического смещения координат.</summary>
    public double GeoAnchorLatitude { get; set; }
    public double GeoAnchorLongitude { get; set; }
}
