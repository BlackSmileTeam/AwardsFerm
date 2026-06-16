using AwardsFerm.Infrastructure.Playwright;

namespace AwardsFerm.Worker.Services;

public sealed class ProxyRefreshBackgroundService : BackgroundService
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IConfiguration _configuration;
    private readonly ILogger<ProxyRefreshBackgroundService> _logger;
    private readonly string _profilesRoot;

    public ProxyRefreshBackgroundService(
        IHttpClientFactory httpClientFactory,
        IConfiguration configuration,
        ILogger<ProxyRefreshBackgroundService> logger)
    {
        _httpClientFactory = httpClientFactory;
        _configuration = configuration;
        _logger = logger;
        _profilesRoot = FindProfilesRoot();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var hasMarketKey = ProxyMarketApiClient.IsApiKeyConfigured(_profilesRoot);
        var autoFetchSpys = _configuration.GetValue("Proxy:AutoFetch", false);

        if (!hasMarketKey && !autoFetchSpys)
        {
            _logger.LogInformation("Синхронизация прокси отключена (нет apiKey и Proxy:AutoFetch=false)");
            return;
        }

        var intervalMinutes = hasMarketKey
            ? _configuration.GetValue("Proxy:MarketSyncMinutes", 30)
            : _configuration.GetValue("Proxy:RefreshMinutes", 60);
        var interval = TimeSpan.FromMinutes(Math.Max(5, intervalMinutes));

        await RefreshAsync(stoppingToken);

        using var timer = new PeriodicTimer(interval);
        while (await timer.WaitForNextTickAsync(stoppingToken))
            await RefreshAsync(stoppingToken);
    }

    private async Task RefreshAsync(CancellationToken cancellationToken)
    {
        if (ProxyMarketApiClient.IsApiKeyConfigured(_profilesRoot))
        {
            try
            {
                var client = _httpClientFactory.CreateClient("proxymarket");
                var sync = new ProxyMarketApiClient(client);
                var result = await sync.SyncAsync(_profilesRoot, cancellationToken);
                if (result.Success)
                    _logger.LogInformation("Proxy.Market: {Message}", result.Message);
                else
                    _logger.LogWarning("Proxy.Market: {Message}", result.Message);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Не удалось синхронизировать Proxy.Market");
            }

            return;
        }

        if (!_configuration.GetValue("Proxy:AutoFetch", false))
            return;

        try
        {
            var client = _httpClientFactory.CreateClient("spysone");
            var lines = await SpysOneProxyFetcher.FetchRuProxyLinesAsync(client, cancellationToken);
            SpysOneProxyFetcher.WriteProxiesFile(_profilesRoot, lines);
            _logger.LogInformation("Обновлён список RU-прокси: {Count} шт. → {Path}", lines.Count, _profilesRoot);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Не удалось обновить RU-прокси с spys.one");
        }
    }

    private static string FindProfilesRoot()
    {
        var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "profiles");
            if (Directory.Exists(candidate))
                return candidate;
            dir = dir.Parent;
        }

        return Path.Combine(Directory.GetCurrentDirectory(), "profiles");
    }
}
