using System.Text.Json.Serialization;

namespace AwardsFerm.Infrastructure.Playwright;

internal sealed class ProxyAuthConfig
{
    public string Host { get; set; } = "pool.proxy.market";
    public int Port { get; set; } = 10000;
    public string Scheme { get; set; } = "http";
    public string Login { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;

    /// <summary>API-ключ из ЛК Proxy.Market → API.</summary>
    public string? ApiKey { get; set; }

    /// <summary>ID пакета трафика (если не задан — берётся активный из API).</summary>
    public int? PackageId { get; set; }

    /// <summary>Страна при автосоздании списка (ru).</summary>
    public string Country { get; set; } = "ru";

    /// <summary>Ротация в минутах: -1 липкая, 0 на запрос, 1–60 интервал.</summary>
    public int RotationMinutes { get; set; } = 10;

    public int? RegionId { get; set; }
    public int? CityId { get; set; }

    [JsonIgnore]
    public bool IsComplete =>
        !string.IsNullOrWhiteSpace(Login) &&
        !string.IsNullOrWhiteSpace(Password) &&
        !string.IsNullOrWhiteSpace(Host);

    [JsonIgnore]
    public bool HasApiKey => !string.IsNullOrWhiteSpace(ApiKey);
}

internal static class ProxyAuthStore
{
    private static readonly object Lock = new();
    private static ProxyAuthConfig? _cached;
    private static string? _cachedPath;
    private static DateTime _cachedMtime = DateTime.MinValue;

    public static ProxyAuthConfig? Load(string profilesRoot)
    {
        var path = Path.Combine(profilesRoot, "proxy.auth.json");
        lock (Lock)
        {
            if (!File.Exists(path))
            {
                _cached = null;
                _cachedPath = path;
                _cachedMtime = DateTime.MinValue;
                return null;
            }

            var mtime = File.GetLastWriteTimeUtc(path);
            if (_cachedPath == path && mtime == _cachedMtime && _cached is not null)
                return _cached;

            try
            {
                var json = File.ReadAllText(path);
                _cached = System.Text.Json.JsonSerializer.Deserialize<ProxyAuthConfig>(json,
                    new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                _cachedPath = path;
                _cachedMtime = mtime;
            }
            catch
            {
                _cached = null;
            }

            return _cached;
        }
    }

    public static void Save(string profilesRoot, ProxyAuthConfig config)
    {
        var path = Path.Combine(profilesRoot, "proxy.auth.json");
        lock (Lock)
        {
            var json = System.Text.Json.JsonSerializer.Serialize(config, new System.Text.Json.JsonSerializerOptions
            {
                WriteIndented = true,
                PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase,
                DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
            });
            File.WriteAllText(path, json);
            _cached = config;
            _cachedPath = path;
            _cachedMtime = File.GetLastWriteTimeUtc(path);
        }
    }

    public static void InvalidateCache()
    {
        lock (Lock)
        {
            _cachedMtime = DateTime.MinValue;
        }
    }

    public static string Apply(string profilesRoot, string proxyUrl)
    {
        var auth = Load(profilesRoot);
        if (auth is not { IsComplete: true })
            return proxyUrl;

        try
        {
            var uri = string.IsNullOrWhiteSpace(proxyUrl)
                ? new Uri($"{auth.Scheme}://{auth.Host}:{auth.Port}")
                : new Uri(proxyUrl);

            if (!uri.Host.Equals(auth.Host, StringComparison.OrdinalIgnoreCase))
                return proxyUrl;

            var scheme = uri.Scheme is "socks5" or "socks5h" ? "socks5" : auth.Scheme;
            var port = uri.Port > 0 ? uri.Port : auth.Port;
            var builder = new UriBuilder
            {
                Scheme = scheme,
                Host = uri.Host,
                Port = port,
                UserName = auth.Login,
                Password = auth.Password
            };
            return builder.Uri.ToString();
        }
        catch
        {
            return proxyUrl;
        }
    }
}
