using Microsoft.Playwright;

namespace AwardsFerm.Infrastructure.Playwright;

internal static class SessionNavigationHelper
{
    private static readonly string[] SearchResultSelectors =
    [
        "a[href*='/games/app/']",
        "a[href*='/games/']",
        "[data-testid='search-results'] a",
        "[class*='SearchResults'] a",
        "[class*='search-result'] a"
    ];

    public static async Task GotoWithRetryAsync(
        IPage page,
        string url,
        CancellationToken cancellationToken = default,
        int attempts = 3,
        int timeoutMs = 90_000)
    {
        Exception? last = null;
        for (var i = 0; i < attempts; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await page.GotoAsync(url, new PageGotoOptions
                {
                    WaitUntil = WaitUntilState.DOMContentLoaded,
                    Timeout = timeoutMs
                });
                return;
            }
            catch (Exception ex) when (IsRetryableNavigationError(ex) && i < attempts - 1)
            {
                last = ex;
                await Task.Delay(2000 + i * 1500, cancellationToken);
            }
        }

        if (last is not null)
            throw last;

        await page.GotoAsync(url, new PageGotoOptions
        {
            WaitUntil = WaitUntilState.DOMContentLoaded,
            Timeout = timeoutMs
        });
    }

    public static async Task<bool> TryWaitForSearchResultsAsync(
        IPage page,
        CancellationToken cancellationToken = default,
        int timeoutMs = 35_000)
    {
        foreach (var selector in SearchResultSelectors)
        {
            try
            {
                await page.WaitForSelectorAsync(selector, new PageWaitForSelectorOptions
                {
                    Timeout = timeoutMs,
                    State = WaitForSelectorState.Visible
                });
                return true;
            }
            catch (TimeoutException)
            {
                // try next selector
            }
            catch (PlaywrightException ex) when (ex.Message.Contains("Timeout", StringComparison.OrdinalIgnoreCase))
            {
                // try next selector
            }
        }

        return false;
    }

    public static bool IsProxyNetworkError(Exception ex)
    {
        for (var current = ex; current is not null; current = current.InnerException)
        {
            var msg = current.Message;
            if (msg.Contains("ERR_TUNNEL_CONNECTION_FAILED", StringComparison.OrdinalIgnoreCase) ||
                msg.Contains("ERR_TIMED_OUT", StringComparison.OrdinalIgnoreCase) ||
                msg.Contains("ERR_PROXY_CONNECTION_FAILED", StringComparison.OrdinalIgnoreCase) ||
                msg.Contains("ERR_CONNECTION_RESET", StringComparison.OrdinalIgnoreCase) ||
                msg.Contains("ERR_CONNECTION_CLOSED", StringComparison.OrdinalIgnoreCase) ||
                msg.Contains("ERR_SSL", StringComparison.OrdinalIgnoreCase) ||
                msg.Contains("ERR_NETWORK_CHANGED", StringComparison.OrdinalIgnoreCase) ||
                msg.Contains("NS_ERROR_PROXY", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsRetryableNavigationError(Exception ex) =>
        IsProxyNetworkError(ex) ||
        ex.Message.Contains("Timeout", StringComparison.OrdinalIgnoreCase) ||
        ex.Message.Contains("net::ERR_", StringComparison.OrdinalIgnoreCase);
}
