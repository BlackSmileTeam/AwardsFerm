namespace AwardsFerm.Core.Interfaces;

public interface ISessionPreviewCoordinator
{
    void SetEnabled(string profileId, bool enabled);
    bool IsEnabled(string profileId);
    void Clear(string profileId);
}
