using System.Collections.Concurrent;
using AwardsFerm.Core.Interfaces;

namespace AwardsFerm.Infrastructure.Playwright;

public sealed class SessionPreviewCoordinator : ISessionPreviewCoordinator
{
    private readonly ConcurrentDictionary<string, byte> _enabled = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, string> _lastFrames = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, byte> _captureNow = new(StringComparer.Ordinal);

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
        if (string.IsNullOrWhiteSpace(profileId))
            return;

        _enabled.TryRemove(profileId, out _);
        _lastFrames.TryRemove(profileId, out _);
        _captureNow.TryRemove(profileId, out _);
    }

    public void SetLastFrame(string profileId, string base64)
    {
        if (string.IsNullOrWhiteSpace(profileId) || string.IsNullOrWhiteSpace(base64))
            return;

        _lastFrames[profileId] = base64;
    }

    public string? GetLastFrame(string profileId) =>
        !string.IsNullOrWhiteSpace(profileId) && _lastFrames.TryGetValue(profileId, out var frame)
            ? frame
            : null;

    public void RequestImmediateCapture(string profileId)
    {
        if (!string.IsNullOrWhiteSpace(profileId))
            _captureNow[profileId] = 0;
    }

    public bool TakeImmediateCaptureRequest(string profileId) =>
        !string.IsNullOrWhiteSpace(profileId) && _captureNow.TryRemove(profileId, out _);
}
