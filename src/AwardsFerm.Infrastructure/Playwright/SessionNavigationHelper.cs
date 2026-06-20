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
        int timeoutMs = 60_000,
        Func<string, Task>? onProgress = null)
    {
        Exception? last = null;
        for (var i = 0; i < attempts; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (onProgress is not null)
                await onProgress($"Переход: {url} (попытка {i + 1}/{attempts})");

            try
            {
                await GotoResilientAsync(page, url, timeoutMs);
                if (onProgress is not null)
                    await onProgress($"Страница загружена: {page.Url}");
                return;
            }
            catch (Exception ex) when (IsRetryableNavigationError(ex) && i < attempts - 1)
            {
                last = ex;
                if (onProgress is not null)
                    await onProgress($"Ошибка перехода, повтор: {TrimNavigationError(ex)}");
                await Task.Delay(2000 + i * 1500, cancellationToken);
            }
        }

        if (last is not null)
            throw last;

        await GotoResilientAsync(page, url, timeoutMs);
    }

    /// <summary>
    /// Commit не ждёт бесконечных скриптов Яндекса; DOMContentLoaded — best-effort.
    /// </summary>
    private static async Task GotoResilientAsync(IPage page, string url, int timeoutMs)
    {
        await page.GotoAsync(url, new PageGotoOptions
        {
            WaitUntil = WaitUntilState.Commit,
            Timeout = timeoutMs
        });

        try
        {
            await page.WaitForLoadStateAsync(LoadState.DOMContentLoaded, new PageWaitForLoadStateOptions
            {
                Timeout = Math.Min(20_000, timeoutMs / 2)
            });
        }
        catch (TimeoutException)
        {
            // Тяжёлые страницы (yandex.ru) часто не отдают DOMContentLoaded — продолжаем после Commit.
        }
    }

    private static string TrimNavigationError(Exception ex)
    {
        var msg = ex.Message;
        var idx = msg.IndexOf('\n', StringComparison.Ordinal);
        return idx > 0 ? msg[..idx] : msg;
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
