namespace AwardsFerm.Api.Data.Entities;

public sealed class RsyaSnapshotEntity
{
    public long Id { get; set; }
    public long AdAccountId { get; set; }
    public AdAccountEntity AdAccount { get; set; } = null!;

    public decimal TodayReward { get; set; }
    public decimal MonthReward { get; set; }
    public long TodayShows { get; set; }
    public long TodayClicks { get; set; }
    public DateTimeOffset CapturedAt { get; set; } = DateTimeOffset.UtcNow;
}
