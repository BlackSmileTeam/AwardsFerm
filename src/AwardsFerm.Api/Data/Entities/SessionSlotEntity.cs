namespace AwardsFerm.Api.Data.Entities;

public sealed class SessionSlotEntity
{
    public long Id { get; set; }
    public long AdAccountId { get; set; }
    public AdAccountEntity AdAccount { get; set; } = null!;

    public string ProfileId { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
    public bool ScheduleEnabled { get; set; }
    public string? ScheduledStartMsk { get; set; }
    public string? StopAtMsk { get; set; }
    public bool AutoRestart { get; set; } = true;
    public bool ProxyEnabled { get; set; } = true;
    public long? ProxyId { get; set; }
    public ProxyEntity? Proxy { get; set; }
    public string DevicePlatform { get; set; } = "Random";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public List<SessionRunEntity> Runs { get; set; } = [];
}
