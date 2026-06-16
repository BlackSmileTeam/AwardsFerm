namespace AwardsFerm.Api.Data.Entities;

public sealed class AdAccountEntity
{
    public long Id { get; set; }
    public long UserId { get; set; }
    public UserEntity User { get; set; } = null!;

    public string Name { get; set; } = string.Empty;
    public string GameTitle { get; set; } = string.Empty;
    public string GameUrl { get; set; } = string.Empty;
    public string TokenEncrypted { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public List<SessionSlotEntity> SessionSlots { get; set; } = [];
    public List<RsyaSnapshotEntity> RsyaSnapshots { get; set; } = [];
}
