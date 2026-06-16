namespace AwardsFerm.Api.Data.Entities;

public sealed class SessionRunEntity
{
    public long Id { get; set; }
    public long SessionSlotId { get; set; }
    public SessionSlotEntity SessionSlot { get; set; } = null!;

    public string RuntimeSessionId { get; set; } = string.Empty;
    public string Status { get; set; } = "Starting";
    public DateTimeOffset StartedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? FinishedAt { get; set; }
    public string? ErrorMessage { get; set; }
    public int GameOverCount { get; set; }
}
