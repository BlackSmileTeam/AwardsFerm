using AwardsFerm.Core.Models;

namespace AwardsFerm.Core.Interfaces;

public interface ISessionPauseCoordinator
{
    void Pause(string profileId);
    void Resume(string profileId);
    void Clear(string profileId);
    bool IsPaused(string profileId);

    Task WaitIfPausedAsync(
        string profileId,
        string sessionId,
        ISessionEventReporter reporter,
        CancellationToken cancellationToken = default);
}
