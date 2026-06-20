using AwardsFerm.Core.Interfaces;
using AwardsFerm.Core.Models;

namespace AwardsFerm.Infrastructure.Playwright;

public sealed class ProxyIpChangeCoordinator : IProxyIpChangeCoordinator
{
    private readonly object _lock = new();
    private readonly Dictionary<string, Registration> _byProfile = new(StringComparer.Ordinal);

    public IDisposable RegisterSession(ProxyIpSessionRegistration registration)
    {
        ArgumentNullException.ThrowIfNull(registration);

        lock (_lock)
        {
            _byProfile[registration.ProfileId] = new Registration(registration);
        }

        return new RegistrationHandle(this, registration.ProfileId);
    }

    public void NotifyIpChanged(string proxyHostKey, string sourceProfileId, string oldIp, string newIp)
    {
        if (string.IsNullOrWhiteSpace(proxyHostKey))
            return;

        List<(Registration Reg, string OldIp)> targets;
        lock (_lock)
        {
            targets = _byProfile.Values
                .Where(r => r.ProxyHostKey.Equals(proxyHostKey, StringComparison.OrdinalIgnoreCase)
                            && !r.ProfileId.Equals(sourceProfileId, StringComparison.Ordinal))
                .Select(r => (r, r.BaselineIp))
                .ToList();
        }

        foreach (var (reg, baselineIp) in targets)
        {
            var fromIp = string.IsNullOrWhiteSpace(baselineIp) ? oldIp : baselineIp;
            _ = ReportRemoteSignalAsync(reg, sourceProfileId, fromIp, newIp);
            try
            {
                reg.OnRemoteIpChange(fromIp, newIp);
            }
            catch
            {
                // ignore cancel errors
            }
        }
    }

    private static async Task ReportRemoteSignalAsync(
        Registration reg,
        string sourceProfileId,
        string oldIp,
        string newIp)
    {
        try
        {
            await reg.Reporter.ReportAsync(new SessionEvent
            {
                SessionId = reg.SessionId,
                Type = SessionEventType.Log,
                Message =
                    $"Смена IP прокси (сигнал от {sourceProfileId}): {oldIp} → {newIp} — перезапуск сессии"
            }, CancellationToken.None);

            await reg.Reporter.ReportAsync(new SessionEvent
            {
                SessionId = reg.SessionId,
                Type = SessionEventType.IpDetected,
                PublicIp = newIp,
                Message = $"IP сменился: {oldIp} → {newIp}"
            }, CancellationToken.None);
        }
        catch
        {
            // ignore reporting errors
        }
    }

    private void Unregister(string profileId)
    {
        lock (_lock)
        {
            _byProfile.Remove(profileId);
        }
    }

    private sealed class Registration
    {
        public Registration(ProxyIpSessionRegistration source)
        {
            ProfileId = source.ProfileId;
            SessionId = source.SessionId;
            ProxyHostKey = source.ProxyHostKey;
            BaselineIp = source.BaselineIp;
            Reporter = source.Reporter;
            OnRemoteIpChange = source.OnRemoteIpChange;
        }

        public string ProfileId { get; }
        public string SessionId { get; }
        public string ProxyHostKey { get; }
        public string BaselineIp { get; }
        public ISessionEventReporter Reporter { get; }
        public Action<string, string> OnRemoteIpChange { get; }
    }

    private sealed class RegistrationHandle : IDisposable
    {
        private readonly ProxyIpChangeCoordinator _owner;
        private readonly string _profileId;
        private int _disposed;

        public RegistrationHandle(ProxyIpChangeCoordinator owner, string profileId)
        {
            _owner = owner;
            _profileId = profileId;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;

            _owner.Unregister(_profileId);
        }
    }
}
