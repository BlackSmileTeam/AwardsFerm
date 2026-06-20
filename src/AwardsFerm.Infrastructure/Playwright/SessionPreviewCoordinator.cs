using System.Collections.Concurrent;
using AwardsFerm.Core.Interfaces;

namespace AwardsFerm.Infrastructure.Playwright;

public sealed class SessionPreviewCoordinator : ISessionPreviewCoordinator
{
    private readonly ConcurrentDictionary<string, byte> _enabled = new(StringComparer.Ordinal);

    public void SetEnabled(string profileId, bool enabled)
    {
        if (string.IsNullOrWhiteSpace(profileId))
            return;

        if (enabled)
            _enabled[profileId] = 0;
        else
            _enabled.TryRemove(profileId, out _);
    }

    public bool IsEnabled(string profileId) =>
        !string.IsNullOrWhiteSpace(profileId) && _enabled.ContainsKey(profileId);

    public void Clear(string profileId)
    {
        if (!string.IsNullOrWhiteSpace(profileId))
            _enabled.TryRemove(profileId, out _);
    }
}
