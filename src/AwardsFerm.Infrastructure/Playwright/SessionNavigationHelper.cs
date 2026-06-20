using AwardsFerm.Core.Interfaces;
using AwardsFerm.Infrastructure.Behavior;
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
        Func<string, Task>? onProgress = null,
        string? captchaSessionId = null,
        ISessionEventReporter? captchaReporter = null)
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
                EnsureNavigationSucceeded(page, url);
                if (onProgress is not null)
                    await onProgress($"Страница загружена: {page.Url}");
                if (captchaSessionId is not null && captchaReporter is not null)
                    await CaptchaHelper.WaitForManualSolveAsync(page, captchaSessionId, captchaReporter, cancellationToken);
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

    public static bool IsBrowserErrorPage(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return true;

        return url.StartsWith("chrome-error://", StringComparison.OrdinalIgnoreCase) ||
               url.Contains("chromewebdata", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsPageUnavailableText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return false;

        var lower = text.ToLowerInvariant();
        return lower.Contains("временно недоступ", StringComparison.Ordinal) ||
               lower.Contains("перемещена по новому", StringComparison.Ordinal) ||
               lower.Contains("перемещён", StringComparison.Ordinal) ||
               lower.Contains("перемещен", StringComparison.Ordinal) ||
               lower.Contains("temporarily unavailable", StringComparison.Ordinal) ||
               lower.Contains("moved permanently", StringComparison.Ordinal);
    }

    public static async Task<bool> IsPageUnavailableAsync(IPage page)
    {
        if (page.IsClosed)
            return false;

        if (IsBrowserErrorPage(page.Url))
            return true;

        try
        {
            var body = await page.Locator("body").InnerTextAsync();
            if (IsPageUnavailableText(body))
                return true;
        }
        catch
        {
            // ignore
        }

        foreach (var frame in page.Frames)
        {
            try
            {
                if (IsBrowserErrorPage(frame.Url))
                    return true;

                var body = await frame.Locator("body").InnerTextAsync();
                if (IsPageUnavailableText(body))
                    return true;
            }
            catch
            {
                // cross-origin or detached
            }
        }

        return false;
    }

    public static async Task<bool> TryReloadIfUnavailableAsync(
        IPage page,
        CancellationToken cancellationToken = default,
        Func<string, Task>? onProgress = null)
    {
        if (page.IsClosed || !await IsPageUnavailableAsync(page))
            return false;

        if (onProgress is not null)
            await onProgress("Страница недоступна — обновляем…");

        try
        {
            await page.ReloadAsync(new PageReloadOptions
            {
                WaitUntil = WaitUntilState.Commit,
                Timeout = 30_000
            });
            await HumanBehavior.DelayAsync(1500, 2500, cancellationToken);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static void EnsureNavigationSucceeded(IPage page, string targetUrl)
    {
        var current = page.Url;
        if (IsBrowserErrorPage(current))
        {
            throw new PlaywrightException(
                $"net::ERR_FAILED at {targetUrl} (страница ошибки браузера: {current})");
        }

        if (!Uri.TryCreate(targetUrl, UriKind.Absolute, out var expected))
            return;

        var expectedHost = expected.Host;
        if (current.Contains(expectedHost, StringComparison.OrdinalIgnoreCase))
            return;

        if (expectedHost.Contains("yandex.", StringComparison.OrdinalIgnoreCase) &&
            (current.Contains("yandex.", StringComparison.OrdinalIgnoreCase) ||
             current.Contains("dzen.", StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        throw new PlaywrightException(
            $"net::ERR_FAILED at {targetUrl} (открыта другая страница: {current})");
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

    private static bool IsRetryableNavigationError(Exception ex)
    {
        if (IsProxyNetworkError(ex))
            return true;

        for (var current = ex; current is not null; current = current.InnerException)
        {
            var msg = current.Message;
            if (msg.Contains("Timeout", StringComparison.OrdinalIgnoreCase) ||
                msg.Contains("net::ERR_", StringComparison.OrdinalIgnoreCase) ||
                msg.Contains("Execution context was destroyed", StringComparison.OrdinalIgnoreCase) ||
                msg.Contains("страница ошибки браузера", StringComparison.OrdinalIgnoreCase) ||
                msg.Contains("открыта другая страница", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
