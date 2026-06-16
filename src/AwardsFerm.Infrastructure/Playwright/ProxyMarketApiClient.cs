using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AwardsFerm.Infrastructure.Playwright;

public sealed class ProxyMarketSyncResult
{
    public bool Success { get; init; }
    public string Message { get; init; } = string.Empty;
    public string? Login { get; init; }
    public int ProxyCount { get; init; }
}

/// <summary>Синхронизация логина/пароля pool.proxy.market через API Proxy.Market.</summary>
public sealed class ProxyMarketApiClient
{
    private const string ApiBase = "https://api.dashboard.proxy.market";
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    private readonly HttpClient _http;

    public ProxyMarketApiClient(HttpClient http) => _http = http;

    public static bool IsApiKeyConfigured(string profilesRoot)
    {
        var auth = ProxyAuthStore.Load(profilesRoot);
        return auth?.HasApiKey == true;
    }

    public async Task<ProxyMarketSyncResult> SyncAsync(string profilesRoot, CancellationToken cancellationToken = default)
    {
        var auth = ProxyAuthStore.Load(profilesRoot);
        if (auth is null || string.IsNullOrWhiteSpace(auth.ApiKey))
            return new ProxyMarketSyncResult { Success = false, Message = "apiKey не задан в proxy.auth.json" };

        var packageId = auth.PackageId;
        if (packageId is null or <= 0)
            packageId = await ResolvePackageIdAsync(auth.ApiKey, cancellationToken);

        if (packageId is null or <= 0)
            return new ProxyMarketSyncResult { Success = false, Message = "Пакет трафика не найден в ЛК Proxy.Market" };

        var proxies = await ListPackageProxiesAsync(auth.ApiKey, packageId.Value, cancellationToken);
        if (proxies.Count == 0)
        {
            await CreateProxyAsync(auth, packageId.Value, cancellationToken);
            proxies = await ListPackageProxiesAsync(auth.ApiKey, packageId.Value, cancellationToken);
        }

        if (proxies.Count == 0)
            return new ProxyMarketSyncResult { Success = false, Message = "Список прокси пуст — создайте в ЛК или через API" };

        var picked = proxies
            .OrderByDescending(p => p.BoughtAt)
            .First();

        auth.Host = picked.Ip;
        auth.Port = picked.HttpPort;
        auth.Scheme = "http";
        auth.Login = picked.Login;
        auth.Password = picked.Password;
        auth.PackageId = packageId;
        ProxyAuthStore.Save(profilesRoot, auth);
        ProxyRotator.InvalidateCache();

        return new ProxyMarketSyncResult
        {
            Success = true,
            Message = $"Синхронизирован {picked.Login}@{picked.Ip}:{picked.HttpPort} (списков: {proxies.Count})",
            Login = picked.Login,
            ProxyCount = proxies.Count
        };
    }

    private async Task<int?> ResolvePackageIdAsync(string apiKey, CancellationToken cancellationToken)
    {
        var url = $"{ApiBase}/dev-api/v2/packages/{apiKey}?page=1&perPage=10";
        var response = await _http.GetFromJsonAsync<PackagesResponse>(url, JsonOptions, cancellationToken);
        return response?.Data?
            .Where(p => p.IsActive)
            .OrderByDescending(p => p.Total - p.Used)
            .Select(p => (int?)p.Id)
            .FirstOrDefault();
    }

    private async Task<List<ProxyMarketProxy>> ListPackageProxiesAsync(
        string apiKey,
        int packageId,
        CancellationToken cancellationToken)
    {
        var url = $"{ApiBase}/dev-api/list/{apiKey}";
        var body = new { type = "all", package_id = packageId, page = 1, page_size = 50, sort = 0 };
        using var response = await _http.PostAsJsonAsync(url, body, cancellationToken);
        response.EnsureSuccessStatusCode();

        var payload = await response.Content.ReadFromJsonAsync<ProxyListResponse>(JsonOptions, cancellationToken);
        if (payload?.List?.Data is null || payload.List.Error)
            return [];

        return payload.List.Data
            .Where(p => !string.IsNullOrWhiteSpace(p.Login) && !string.IsNullOrWhiteSpace(p.Password))
            .Select(p => new ProxyMarketProxy
            {
                Id = p.Id,
                Login = p.Login!,
                Password = p.Password!,
                Ip = string.IsNullOrWhiteSpace(p.Ip) ? "pool.proxy.market" : p.Ip,
                HttpPort = ParsePort(p.HttpPort, 10000),
                BoughtAt = ParseDate(p.BoughtAt)
            })
            .ToList();
    }

    private async Task CreateProxyAsync(ProxyAuthConfig auth, int packageId, CancellationToken cancellationToken)
    {
        var url = $"{ApiBase}/dev-api/v2/package/create-proxy/{auth.ApiKey}";
        var rotation = auth.RotationMinutes is >= -1 and <= 60 ? auth.RotationMinutes : 10;
        var body = new Dictionary<string, object>
        {
            ["packageId"] = packageId,
            ["country"] = string.IsNullOrWhiteSpace(auth.Country) ? "ru" : auth.Country,
            ["rotation"] = rotation
        };

        if (auth.RegionId is > 0)
            body["regionId"] = auth.RegionId.Value;
        if (auth.CityId is > 0)
            body["cityId"] = auth.CityId.Value;

        using var response = await _http.PostAsJsonAsync(url, body, cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    private static int ParsePort(string? value, int fallback) =>
        int.TryParse(value, out var port) && port > 0 ? port : fallback;

    private static DateTime ParseDate(string? value) =>
        DateTime.TryParse(value, out var dt) ? dt : DateTime.MinValue;

    private sealed class PackagesResponse
    {
        public List<PackageItem>? Data { get; set; }
    }

    private sealed class PackageItem
    {
        public int Id { get; set; }
        public long Total { get; set; }
        public long Used { get; set; }

        [JsonPropertyName("is_active")]
        public bool IsActive { get; set; }
    }

    private sealed class ProxyListResponse
    {
        public ProxyListBlock? List { get; set; }
    }

    private sealed class ProxyListBlock
    {
        public bool Error { get; set; }
        public List<ProxyListItem>? Data { get; set; }
    }

    private sealed class ProxyListItem
    {
        public int Id { get; set; }
        public string? Login { get; set; }
        public string? Password { get; set; }
        public string? Ip { get; set; }

        [JsonPropertyName("http_port")]
        public string? HttpPort { get; set; }

        [JsonPropertyName("bought_at")]
        public string? BoughtAt { get; set; }
    }

    private sealed class ProxyMarketProxy
    {
        public int Id { get; set; }
        public required string Login { get; set; }
        public required string Password { get; set; }
        public required string Ip { get; set; }
        public int HttpPort { get; set; }
        public DateTime BoughtAt { get; set; }
    }
}
