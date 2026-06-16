using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Playwright;

namespace AwardsFerm.Infrastructure.Playwright;

/// <summary>Считает объём данных через CDP Network (приближение к расходу прокси).</summary>
internal sealed class SessionTrafficMonitor : IAsyncDisposable
{
    private readonly ConcurrentDictionary<IPage, ICDPSession> _sessions = new();
    private long _bytes;

    public long TotalBytes => Interlocked.Read(ref _bytes);

    public static async Task<SessionTrafficMonitor> AttachAsync(IBrowserContext context, CancellationToken cancellationToken = default)
    {
        var monitor = new SessionTrafficMonitor();
        context.Page += (_, page) => _ = monitor.AttachPageAsync(page);
        foreach (var page in context.Pages)
            await monitor.AttachPageAsync(page, cancellationToken);

        return monitor;
    }

    private async Task AttachPageAsync(IPage page, CancellationToken cancellationToken = default)
    {
        if (_sessions.ContainsKey(page))
            return;

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var cdp = await page.Context.NewCDPSessionAsync(page);
            if (!_sessions.TryAdd(page, cdp))
            {
                await cdp.DetachAsync();
                return;
            }

            await cdp.SendAsync("Network.enable");
            cdp.Event("Network.loadingFinished").OnEvent += (_, payload) => AddFromLoadingFinished(payload);
            page.Close += (_, _) => _ = DetachPageAsync(page);
        }
        catch
        {
            _sessions.TryRemove(page, out _);
        }
    }

    private void AddFromLoadingFinished(object? payload)
    {
        if (payload is not JsonElement json)
            return;

        if (json.TryGetProperty("encodedDataLength", out var encoded))
            AddBytes((long)encoded.GetDouble());
    }

    private void AddBytes(long value)
    {
        if (value > 0)
            Interlocked.Add(ref _bytes, value);
    }

    private async Task DetachPageAsync(IPage page)
    {
        if (_sessions.TryRemove(page, out var cdp))
        {
            try { await cdp.DetachAsync(); } catch { /* ignore */ }
        }
    }

    public static string FormatBytes(long bytes)
    {
        if (bytes < 1024)
            return $"{bytes} Б";
        if (bytes < 1024 * 1024)
            return $"{bytes / 1024.0:0.1} КБ";
        if (bytes < 1024L * 1024 * 1024)
            return $"{bytes / 1024.0 / 1024.0:0.1} МБ";
        return $"{bytes / 1024.0 / 1024.0 / 1024.0:0.2} ГБ";
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var page in _sessions.Keys.ToArray())
            await DetachPageAsync(page);
    }
}
