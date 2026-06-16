namespace AwardsFerm.Core.Models;

public sealed class SessionSlotDefinition
{
    public long Id { get; set; }
    public long AdAccountId { get; set; }
    public string ProfileId { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
    public bool ScheduleEnabled { get; set; }
    /// <summary>Автозапуск по Москве, формат HH:mm.</summary>
    public string? ScheduledStartMsk { get; set; }
    /// <summary>Остановить по Москве, формат HH:mm.</summary>
    public string? StopAtMsk { get; set; }
    public bool AutoRestart { get; set; } = true;
}

public sealed class SessionSlotsConfig
{
    public List<SessionSlotDefinition> Slots { get; set; } = [];
}

public sealed class UpdateSessionSlotRequest
{
    public string? Label { get; set; }
    public bool? ScheduleEnabled { get; set; }
    public string? ScheduledStartMsk { get; set; }
    public string? StopAtMsk { get; set; }
    public bool? AutoRestart { get; set; }
}

public sealed class CreateSessionSlotRequest
{
    public long? AdAccountId { get; set; }
    public string? Label { get; set; }
}
