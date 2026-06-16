namespace AwardsFerm.Api.Data.Entities;

public sealed class SessionIpAuditEntity
{
    public long Id { get; set; }
    public string RuntimeSessionId { get; set; } = string.Empty;
    public long? AdAccountId { get; set; }
    public string ProfileId { get; set; } = string.Empty;
    public string PublicIp { get; set; } = string.Empty;
    public DateTimeOffset CapturedAt { get; set; } = DateTimeOffset.UtcNow;
}
