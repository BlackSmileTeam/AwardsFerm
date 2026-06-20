using Microsoft.Playwright;

namespace AwardsFerm.Infrastructure.Playwright;

internal sealed class ProxyConnectivityResult
{
    public string? PublicIp { get; init; }
    public int? HttpStatus { get; init; }
    public string? ErrorCode { get; init; }
    public string? ErrorMessage { get; init; }

    public bool IsSuccess => PublicIp is not null;

    public string DescribeFailure()
    {
        if (ErrorCode is not null || ErrorMessage is not null)
            return $"{ErrorCode ?? "ошибка прокси"}: {ErrorMessage ?? "нет выходного узла"}";

        if (HttpStatus is >= 400)
            return $"HTTP {HttpStatus} от прокси";

        return "не удалось получить IP";
    }
}

internal static class SessionNetworkHelper
{
    private static readonly string[] IpCheckUrls =
    [
        "http://api.ipify.org?format=json",
        "https://api.ipify.org?format=json",
        "http://ifconfig.me/ip",
        "https://ifconfig.me/ip"
    ];

    public static async Task<string?> GetPublicIpAsync(IPage page, CancellationToken cancellationToken = default)
    {
        var result = await CheckProxyConnectivityAsync(page, cancellationToken);
        return result.PublicIp;
    }

    /// <summary>Проверка IP без навигации — не сбивает страницу игры.</summary>
    public static async Task<string?> GetPublicIpInPlaceAsync(IPage page, CancellationToken cancellationToken = default)
    {
        foreach (var url in IpCheckUrls)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var ip = await page.EvaluateAsync<string?>(
                    """
                    async (url) => {
                        try {
                            const response = await fetch(url, { cache: 'no-store' });
                            const text = (await response.text()).trim();
                            if (!text) return null;
                            if (text.startsWith('{')) {
                                const json = JSON.parse(text);
                                return json.ip || null;
                            }
                            const line = text.split(/\s+/)[0];
                            return line || null;
                        } catch {
                            return null;
                        }
                    }
                    """,
                    url);

                if (!string.IsNullOrWhiteSpace(ip) && System.Net.IPAddress.TryParse(ip, out _))
                    return ip;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                // try next url
            }
        }

        return null;
    }

    public static async Task<ProxyConnectivityResult> CheckProxyConnectivityAsync(
        IPage page,
        CancellationToken cancellationToken = default)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(45));
        var linkedCt = timeoutCts.Token;

        ProxyConnectivityResult? lastFailure = null;

        foreach (var url in IpCheckUrls)
        {
            var result = await TryCheckUrlAsync(page, url, linkedCt);
            if (result.IsSuccess)
                return result;

            lastFailure = result;
        }

        return lastFailure ?? new ProxyConnectivityResult();
    }

    /// <summary>Проверяет, что целевой сайт открывается через прокси (не только IP-check).</summary>
    public static async Task<bool> ProbeTargetReachableAsync(
        IPage page,
        string url,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await page.GotoAsync(url, new PageGotoOptions
            {
                Timeout = 25_000,
                WaitUntil = WaitUntilState.Commit
            });

            if (SessionNavigationHelper.IsBrowserErrorPage(page.Url))
                return false;

            if (Uri.TryCreate(url, UriKind.Absolute, out var expected))
            {
                var host = expected.Host;
                var current = page.Url;
                if (current.Contains(host, StringComparison.OrdinalIgnoreCase))
                    return true;

                if (host.Contains("yandex.", StringComparison.OrdinalIgnoreCase) &&
                    (current.Contains("yandex.", StringComparison.OrdinalIgnoreCase) ||
                     current.Contains("dzen.", StringComparison.OrdinalIgnoreCase)))
                {
                    return true;
                }

                return false;
            }

            return true;
        }
        catch
        {
            return false;
        }
    }

    private static async Task<ProxyConnectivityResult> TryCheckUrlAsync(
        IPage page,
        string url,
        CancellationToken cancellationToken)
    {
        try
        {
            var response = await page.GotoAsync(url,
                new PageGotoOptions { Timeout = 15_000, WaitUntil = WaitUntilState.DOMContentLoaded });
            if (response is null)
                return new ProxyConnectivityResult();

            if (!response.Ok)
            {
                var proxyError = ReadProxyError(response);
                return new ProxyConnectivityResult
                {
                    HttpStatus = response.Status,
                    ErrorCode = proxyError.Code,
                    ErrorMessage = proxyError.Message
                };
            }

            var body = await page.Locator("body").InnerTextAsync();
            if (string.IsNullOrWhiteSpace(body))
                return new ProxyConnectivityResult { HttpStatus = response.Status };

            body = body.Trim();
            if (body.StartsWith('{'))
            {
                using var doc = System.Text.Json.JsonDocument.Parse(body);
                if (doc.RootElement.TryGetProperty("ip", out var ip))
                {
                    return new ProxyConnectivityResult
                    {
                        PublicIp = ip.GetString(),
                        HttpStatus = response.Status
                    };
                }
            }

            var line = body.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .FirstOrDefault();
            if (line is not null && System.Net.IPAddress.TryParse(line, out _))
            {
                return new ProxyConnectivityResult
                {
                    PublicIp = line,
                    HttpStatus = response.Status
                };
            }

            if (body.Length <= 45 && System.Net.IPAddress.TryParse(body, out _))
            {
                return new ProxyConnectivityResult
                {
                    PublicIp = body,
                    HttpStatus = response.Status
                };
            }

            return new ProxyConnectivityResult { HttpStatus = response.Status };
        }
        catch
        {
            return new ProxyConnectivityResult();
        }
    }

    private static (string? Code, string? Message) ReadProxyError(IResponse response)
    {
        var headers = response.Headers;
        headers.TryGetValue("err-code", out var code);
        headers.TryGetValue("err-msg", out var message);
        if (code is null && headers.TryGetValue("Err-Code", out var altCode))
            code = altCode;
        if (message is null && headers.TryGetValue("Err-Msg", out var altMessage))
            message = altMessage;

        return (code, message);
    }
}
