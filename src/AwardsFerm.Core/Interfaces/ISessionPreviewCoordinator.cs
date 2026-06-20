namespace AwardsFerm.Core.Interfaces;

public interface ISessionPreviewCoordinator
{
    void SetEnabled(string profileId, bool enabled);
    bool IsEnabled(string profileId);
    void Clear(string profileId);
    void SetLastFrame(string profileId, string base64);
    string? GetLastFrame(string profileId);
    void RequestImmediateCapture(string profileId);
    bool TakeImmediateCaptureRequest(string profileId);
}
