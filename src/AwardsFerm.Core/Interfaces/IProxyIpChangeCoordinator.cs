namespace AwardsFerm.Core.Interfaces;

/// <summary>
/// Координация смены IP на общем прокси между параллельными сессиями Worker.
/// </summary>
public interface IProxyIpChangeCoordinator
{
    IDisposable RegisterSession(ProxyIpSessionRegistration registration);

    void NotifyIpChanged(string proxyHostKey, string sourceProfileId, string oldIp, string newIp);
}

public sealed class ProxyIpSessionRegistration
{
    public required string ProfileId { get; init; }
    public required string SessionId { get; init; }
    public required string ProxyHostKey { get; init; }
    public required string BaselineIp { get; init; }
    public required ISessionEventReporter Reporter { get; init; }
    public required Action<string, string> OnRemoteIpChange { get; init; }
}
