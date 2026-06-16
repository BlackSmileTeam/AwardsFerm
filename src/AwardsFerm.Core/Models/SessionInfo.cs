namespace AwardsFerm.Core.Models;

public sealed class SessionInfo
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public long? AdAccountId { get; set; }
    public string ProfileId { get; set; } = "session-001";
    public string? StopAtMsk { get; set; }
    public bool AutoRestart { get; set; } = true;
    public SessionStatus Status { get; set; } = SessionStatus.Idle;
    public int CurrentStep { get; set; }
    public int TotalSteps { get; set; } = 12;
    public string CurrentStepName { get; set; } = string.Empty;
    public string? ErrorMessage { get; set; }
    public string? PublicIp { get; set; }
    public long TrafficBytes { get; set; }
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? FinishedAt { get; set; }
    public List<string> Logs { get; set; } = [];
}
