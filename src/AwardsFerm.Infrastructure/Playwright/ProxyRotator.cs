namespace AwardsFerm.Infrastructure.Playwright;

internal sealed class ProxyGeoLocation
{
    public double Latitude { get; init; }
    public double Longitude { get; init; }
    public string Timezone { get; init; } = "Europe/Moscow";
    public string Locale { get; init; } = "ru-RU";
    public string Label { get; init; } = string.Empty;

    public ProxyGeoLocation WithJitter(Random random, double latDelta = 0.018, double lonDelta = 0.025)
    {
        return new ProxyGeoLocation
        {
            Latitude = Latitude + (random.NextDouble() - 0.5) * latDelta,
            Longitude = Longitude + (random.NextDouble() - 0.5) * lonDelta,
            Timezone = Timezone,
            Locale = Locale,
            Label = Label
        };
    }
}

internal sealed class ProxyEntry
{
    public required string Url { get; init; }
    public ProxyGeoLocation? Geo { get; init; }
}

internal static class ProxyRotator
{
    private static readonly Random Random = new();
    private static readonly object Lock = new();
    private static ProxyEntry[] _cache = [];
    private static string? _cachePath;
    private static DateTime _cacheMtime = DateTime.MinValue;
    private static readonly Dictionary<string, int> RotationIndex = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, ProxyFailureInfo> ProxyFailures = new(StringComparer.OrdinalIgnoreCase);

    private const int MaxFailuresBeforeBan = 2;
    private static readonly TimeSpan FailureCooldown = TimeSpan.FromMinutes(20);

    private static readonly Dictionary<string, ProxyGeoLocation> KnownHosts = new(StringComparer.OrdinalIgnoreCase)
    {
        ["120.26.123.95:8010"] = new() { Latitude = 30.25, Longitude = 120.17, Timezone = "Asia/Shanghai", Locale = "zh-CN", Label = "Ханчжоу, Китай" },
        ["203.146.80.98:8080"] = new() { Latitude = 13.75, Longitude = 100.50, Timezone = "Asia/Bangkok", Locale = "th-TH", Label = "Таиланд" },
        ["217.60.63.215:1080"] = new() { Latitude = 48.8566, Longitude = 2.3522, Timezone = "Europe/Paris", Locale = "fr-FR", Label = "Париж, Франция" },
        ["193.39.168.88:1080"] = new() { Latitude = 55.7558, Longitude = 37.6173, Timezone = "Europe/Moscow", Locale = "ru-RU", Label = "Москва, Россия" },
        ["12.89.176.82:3128"] = new() { Latitude = 41.4993, Longitude = -81.6944, Timezone = "America/New_York", Locale = "en-US", Label = "Кливленд, США" },
        ["154.160.53.2:8888"] = new() { Latitude = 5.6037, Longitude = -0.1870, Timezone = "Africa/Accra", Locale = "en-GH", Label = "Аккра, Гана" },
        ["157.245.100.190:442"] = new() { Latitude = 12.9716, Longitude = 77.5946, Timezone = "Asia/Kolkata", Locale = "en-IN", Label = "Бангалор, Индия" },
        ["109.71.246.44:1080"] = new() { Latitude = 55.7558, Longitude = 37.6173, Timezone = "Europe/Moscow", Locale = "ru-RU", Label = "Россия" },
        ["186.96.16.117:1080"] = new() { Latitude = 40.4168, Longitude = -3.7038, Timezone = "Europe/Madrid", Locale = "es-ES", Label = "Испания" },
        ["pool.proxy.market:10000"] = new() { Latitude = 59.9343, Longitude = 30.3351, Timezone = "Europe/Moscow", Locale = "ru-RU", Label = "Санкт-Петербург, Beeline" },
        ["tproxy.pro:39834"] = new() { Latitude = 55.7558, Longitude = 37.6173, Timezone = "Europe/Moscow", Locale = "ru-RU", Label = "Россия, Beeline (tproxy)" },
    };

    public static string? PickNext(string profilesRoot) => PickForProfile(profilesRoot, null)?.Url;

    public static ProxyEntry? PickForProfile(string profilesRoot, string? profileId)
    {
        var proxies = LoadProxies(profilesRoot);
        if (proxies.Length == 0)
            return null;

        var healthy = FilterBannedProxies(proxies);
        if (healthy.Length == 0)
            healthy = proxies;

        if (string.IsNullOrWhiteSpace(profileId))
            return healthy[Random.Next(healthy.Length)];

        var index = GetAndIncrementRotationIndex(profileId);
        return healthy[index % healthy.Length];
    }

    public static void ReportFailure(string? proxyUrl)
    {
        if (string.IsNullOrWhiteSpace(proxyUrl))
            return;

        var key = ExtractHostKey(proxyUrl);
        if (key is null)
            return;

        lock (Lock)
        {
            if (!ProxyFailures.TryGetValue(key, out var info))
                info = new ProxyFailureInfo();

            info.Count++;
            info.LastFailureUtc = DateTime.UtcNow;
            ProxyFailures[key] = info;
        }
    }

    public static void ReportSuccess(string? proxyUrl)
    {
        if (string.IsNullOrWhiteSpace(proxyUrl))
            return;

        var key = ExtractHostKey(proxyUrl);
        if (key is null)
            return;

        lock (Lock)
        {
            ProxyFailures.Remove(key);
        }
    }

    public static void InvalidateCache()
    {
        lock (Lock)
        {
            _cacheMtime = DateTime.MinValue;
            _cache = [];
        }
    }

    private static ProxyEntry[] FilterBannedProxies(ProxyEntry[] proxies)
    {
        lock (Lock)
        {
            if (ProxyFailures.Count == 0)
                return proxies;

            var now = DateTime.UtcNow;
            var healthy = new List<ProxyEntry>(proxies.Length);
            foreach (var p in proxies)
            {
                var key = ExtractHostKey(p.Url);
                if (key is null)
                {
                    healthy.Add(p);
                    continue;
                }

                if (!ProxyFailures.TryGetValue(key, out var info))
                {
                    healthy.Add(p);
                    continue;
                }

                var cooldownExpired = (now - info.LastFailureUtc) > FailureCooldown;
                var banned = info.Count >= MaxFailuresBeforeBan && !cooldownExpired;
                if (!banned)
                    healthy.Add(p);
            }

            return [..healthy];
        }
    }

    public static ProxyGeoLocation ResolveGeo(string? proxyUrl, ProxyGeoLocation? inlineGeo = null)
    {
        if (inlineGeo is not null && !string.IsNullOrWhiteSpace(inlineGeo.Label))
            return inlineGeo;

        if (!string.IsNullOrWhiteSpace(proxyUrl))
        {
            var hostKey = ExtractHostKey(proxyUrl);
            if (hostKey is not null && KnownHosts.TryGetValue(hostKey, out var known))
                return known;
        }

        return new ProxyGeoLocation
        {
            Latitude = 55.7558,
            Longitude = 37.6173,
            Timezone = "Europe/Moscow",
            Locale = "ru-RU",
            Label = "Россия"
        };
    }

    private static int GetAndIncrementRotationIndex(string profileId)
    {
        lock (Lock)
        {
            if (!RotationIndex.TryGetValue(profileId, out var index))
                index = profileId switch
                {
                    "session-001" => 0,
                    "session-002" => 1,
                    "session-003" => 2,
                    _ => Math.Abs(profileId.GetHashCode(StringComparison.Ordinal))
                };

            RotationIndex[profileId] = index + 1;
            return index;
        }
    }

    private static ProxyEntry[] LoadProxies(string profilesRoot)
    {
        var path = Path.Combine(profilesRoot, "proxies.txt");
        lock (Lock)
        {
            var mtime = File.Exists(path) ? File.GetLastWriteTimeUtc(path) : DateTime.MinValue;
            var authMtime = File.Exists(Path.Combine(profilesRoot, "proxy.auth.json"))
                ? File.GetLastWriteTimeUtc(Path.Combine(profilesRoot, "proxy.auth.json"))
                : DateTime.MinValue;
            var cacheKey = $"{path}|{mtime.Ticks}|{authMtime.Ticks}";

            if (_cachePath == cacheKey)
                return _cache;

            _cachePath = cacheKey;
                _cacheMtime = mtime;

                var entries = File.Exists(path)
                    ? File.ReadAllLines(path)
                        .Select(line => ParseLine(profilesRoot, line))
                        .Where(e => e is not null)
                        .Cast<ProxyEntry>()
                        .ToList()
                    : [];

                if (entries.Count == 0)
                {
                    var authOnly = BuildFromAuthOnly(profilesRoot);
                    if (authOnly is not null)
                        entries.Add(authOnly);
                }

                _cache = entries.ToArray();
        }

        return _cache;
    }

    private static ProxyEntry? BuildFromAuthOnly(string profilesRoot)
    {
        var auth = ProxyAuthStore.Load(profilesRoot);
        if (auth is null)
            return null;

        var url = ProxyAuthStore.Apply(profilesRoot, $"{auth.Scheme}://{auth.Host}:{auth.Port}");
        return new ProxyEntry
        {
            Url = url,
            Geo = ResolveGeo(url, null)
        };
    }

    private static ProxyEntry? ParseLine(string profilesRoot, string line)
    {
        var trimmed = line.Trim();
        if (trimmed.Length == 0 || trimmed.StartsWith('#'))
            return null;

        var parts = trimmed.Split('|', StringSplitOptions.TrimEntries);
        var url = ProxyAuthStore.Apply(profilesRoot, parts[0]);
        if (string.IsNullOrWhiteSpace(url) || !url.Contains("://", StringComparison.Ordinal))
            return null;

        ProxyGeoLocation? geo = null;
        if (parts.Length >= 4
            && double.TryParse(parts[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var lat)
            && double.TryParse(parts[2], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var lon))
        {
            geo = new ProxyGeoLocation
            {
                Latitude = lat,
                Longitude = lon,
                Timezone = parts[3],
                Locale = parts.Length > 4 ? parts[4] : "ru-RU",
                Label = parts.Length > 5 ? parts[5] : string.Empty
            };
        }

        geo = ResolveGeo(url, geo);
        return new ProxyEntry { Url = url, Geo = geo };
    }

    private static string? ExtractHostKey(string proxyUrl)
    {
        try
        {
            var uri = new Uri(proxyUrl);
            return $"{uri.Host}:{uri.Port}";
        }
        catch
        {
            return null;
        }
    }

    private sealed class ProxyFailureInfo
    {
        public int Count { get; set; }
        public DateTime LastFailureUtc { get; set; } = DateTime.MinValue;
    }
}
