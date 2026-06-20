namespace AwardsFerm.Infrastructure.Playwright;

internal static class ProxyUrlHelper
{
    private const string PoolMarketHost = "pool.proxy.market";

    /// <summary>
    /// Настройка URL прокси под слот. Proxy.Market: разные порты 10000+ = разные IP.
    /// Логин/пароль всегда как в ЛК провайдера, без изменений.
    /// </summary>
    public static string ConfigureForProfile(string proxyUrl, string profileId)
    {
        if (string.IsNullOrWhiteSpace(proxyUrl))
            return proxyUrl;

        try
        {
            var uri = new Uri(proxyUrl);
            if (uri.Host.Contains(PoolMarketHost, StringComparison.OrdinalIgnoreCase))
                return ConfigurePoolMarket(uri, profileId);

            return proxyUrl;
        }
        catch
        {
            return proxyUrl;
        }
    }

    private static string ConfigurePoolMarket(Uri uri, string profileId)
    {
        var builder = new UriBuilder(uri)
        {
            Port = 10000 + ResolveProfilePortOffset(profileId)
        };

        return builder.Uri.ToString();
    }

    private static int ResolveProfilePortOffset(string profileId)
    {
        var digits = new string(profileId.Where(char.IsDigit).ToArray());
        if (int.TryParse(digits, out var n) && n > 0)
            return (n - 1) % 100;

        return Math.Abs(profileId.GetHashCode(StringComparison.Ordinal)) % 100;
    }

    /// <summary>Смещение порта при повторной попытке (10000 → 10001 → 10002…).</summary>
    public static string WithRetryPortOffset(string proxyUrl, int retryIndex)
    {
        if (retryIndex <= 0 || string.IsNullOrWhiteSpace(proxyUrl))
            return proxyUrl;

        try
        {
            var uri = new Uri(proxyUrl);
            if (!uri.Host.Contains(PoolMarketHost, StringComparison.OrdinalIgnoreCase))
                return proxyUrl;

            var builder = new UriBuilder(uri)
            {
                Port = uri.Port + retryIndex
            };
            return builder.Uri.ToString();
        }
        catch
        {
            return proxyUrl;
        }
    }

    public static string? ExtractHostKey(string? proxyUrl)
    {
        if (string.IsNullOrWhiteSpace(proxyUrl))
            return null;

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

    public static bool TryParseProxy(string? proxyUrl, out ProxyCredentials credentials)
    {
        credentials = default!;
        if (string.IsNullOrWhiteSpace(proxyUrl))
            return false;

        try
        {
            var uri = new Uri(proxyUrl);
            var scheme = uri.Scheme is "socks5" or "socks5h" ? "socks5" : "http";
            var server = $"{scheme}://{uri.Host}:{uri.Port}";

            if (string.IsNullOrEmpty(uri.UserInfo))
            {
                credentials = new ProxyCredentials(server, null, null);
                return true;
            }

            var parts = uri.UserInfo.Split(':', 2);
            if (parts.Length != 2)
                return false;

            credentials = new ProxyCredentials(server, Uri.UnescapeDataString(parts[0]), Uri.UnescapeDataString(parts[1]));
            return true;
        }
        catch
        {
            return false;
        }
    }

    internal readonly record struct ProxyCredentials(string Server, string? Username, string? Password);

    public static string DescribeProxyFailureHint(string? proxyUrl, string? errorCode)
    {
        if (errorCode is "503.9.12" or "503.9.1" or "503.9.6")
            return "Нет свободных узлов в пуле. В ЛК Proxy.Market: проверьте трафик, смените город/оператора или напишите в поддержку с кодом ошибки.";

        try
        {
            if (!string.IsNullOrWhiteSpace(proxyUrl))
            {
                var host = new Uri(proxyUrl).Host;
                if (host.Contains(PoolMarketHost, StringComparison.OrdinalIgnoreCase))
                    return "Проверьте баланс и пул в ЛК Proxy.Market; порты 10000+ на слот, логин/пароль без изменений.";
                if (host.Equals("tproxy.pro", StringComparison.OrdinalIgnoreCase))
                    return "ProxyCola: IP-check прошёл, но сайт недоступен — смените IP в ЛК или дождитесь ротации (~20 мин).";
            }
        }
        catch
        {
            /* ignore */
        }

        return "Проверьте логин/пароль, срок действия прокси и доступность хоста в личном кабинете провайдера.";
    }
}
