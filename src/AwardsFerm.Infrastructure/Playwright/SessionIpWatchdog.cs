using AwardsFerm.Core.Interfaces;
using AwardsFerm.Core.Models;

namespace AwardsFerm.Infrastructure.Playwright;

internal sealed class IpChangeDetectorState
{
    public bool Changed { get; set; }
    public string? OldIp { get; set; }
    public string? NewIp { get; set; }
}

internal static class SessionIpWatchdog
{
    private static readonly TimeSpan DefaultPollInterval = TimeSpan.FromMinutes(2);

    public static Task RunAsync(
        string sessionId,
        string profileId,
        ActivePageHolder activePage,
        string baselineIp,
        string? proxyHostKey,
        IpChangeDetectorState state,
        CancellationTokenSource ipChangeCts,
        ISessionEventReporter reporter,
        IProxyIpChangeCoordinator? proxyIpCoordinator,
        CancellationToken cancellationToken)
    {
        return Task.Run(async () =>
        {
            var pollInterval = ResolvePollInterval();
            var currentIp = baselineIp;

            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(pollInterval, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }

                if (!activePage.TryResolve(out var page) || page is null || page.IsClosed)
                    continue;

                string? nextIp;
                try
                {
                    nextIp = await SessionNetworkHelper.GetPublicIpInPlaceAsync(page, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch
                {
                    continue;
                }

                if (string.IsNullOrWhiteSpace(nextIp) ||
                    string.Equals(nextIp, currentIp, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                state.Changed = true;
                state.OldIp = currentIp;
                state.NewIp = nextIp;

                try
                {
                    await reporter.ReportAsync(new SessionEvent
                    {
                        SessionId = sessionId,
                        Type = SessionEventType.IpDetected,
                        PublicIp = nextIp,
                        Message = $"IP сменился: {currentIp} → {nextIp}"
                    }, CancellationToken.None);
                }
                catch
                {
                    // ignore reporting errors
                }

                if (proxyHostKey is not null && proxyIpCoordinator is not null)
                {
                    proxyIpCoordinator.NotifyIpChanged(proxyHostKey, profileId, currentIp, nextIp);
                }

                ipChangeCts.Cancel();
                break;
            }
        }, cancellationToken);
    }

    private static TimeSpan ResolvePollInterval()
    {
        var env = Environment.GetEnvironmentVariable("PROXY_IP_CHECK_INTERVAL_SEC");
        if (int.TryParse(env, out var seconds) && seconds is >= 30 and <= 600)
            return TimeSpan.FromSeconds(seconds);

        return DefaultPollInterval;
    }
}
