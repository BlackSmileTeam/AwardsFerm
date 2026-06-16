using System.Collections.Concurrent;
using AwardsFerm.Core.Interfaces;
using AwardsFerm.Core.Models;

namespace AwardsFerm.Infrastructure.Playwright;

public sealed class SessionPauseCoordinator : ISessionPauseCoordinator
{
    private readonly ConcurrentDictionary<string, byte> _paused = new(StringComparer.Ordinal);

    public void Pause(string profileId)
    {
        if (!string.IsNullOrWhiteSpace(profileId))
            _paused[profileId] = 0;
    }

    public void Resume(string profileId)
    {
        if (!string.IsNullOrWhiteSpace(profileId))
            _paused.TryRemove(profileId, out _);
    }

    public void Clear(string profileId)
    {
        if (!string.IsNullOrWhiteSpace(profileId))
            _paused.TryRemove(profileId, out _);
    }

    public bool IsPaused(string profileId) =>
        !string.IsNullOrWhiteSpace(profileId) && _paused.ContainsKey(profileId);

    public async Task WaitIfPausedAsync(
        string profileId,
        string sessionId,
        ISessionEventReporter reporter,
        CancellationToken cancellationToken = default)
    {
        if (!IsPaused(profileId))
            return;

        await reporter.ReportAsync(new SessionEvent
        {
            SessionId = sessionId,
            Type = SessionEventType.StatusChanged,
            Status = SessionStatus.Paused,
            Message = "Пауза"
        }, cancellationToken);

        while (IsPaused(profileId))
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Delay(400, cancellationToken);
        }

        await reporter.ReportAsync(new SessionEvent
        {
            SessionId = sessionId,
            Type = SessionEventType.StatusChanged,
            Status = SessionStatus.Running,
            Message = "Продолжение"
        }, cancellationToken);
    }
}
