namespace AwardsFerm.Core.Models;

public enum SessionEventType
{
    Log,
    StepChanged,
    Screenshot,
    StatusChanged,
    IpDetected,
    TrafficUpdated,
    DiagnosticLog,
    Completed,
    Failed
}

public sealed class SessionEvent
{
    public string SessionId { get; set; } = string.Empty;
    public SessionEventType Type { get; set; }
    public string? Message { get; set; }
    public int? CurrentStep { get; set; }
    public int? TotalSteps { get; set; }
    public string? StepName { get; set; }
    public SessionStatus? Status { get; set; }
    public string? PublicIp { get; set; }
    public long? TrafficBytes { get; set; }
    public string? ScreenshotBase64 { get; set; }
    public DateTimeOffset Timestamp { get; set; } = DateTimeOffset.UtcNow;
}
