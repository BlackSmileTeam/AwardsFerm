namespace AwardsFerm.Api.Data.Entities;

public sealed class ProxyEntity
{
    public long Id { get; set; }
    public long UserId { get; set; }
    public UserEntity User { get; set; } = null!;

    public string Name { get; set; } = string.Empty;
    public string Scheme { get; set; } = "http";
    public string Host { get; set; } = string.Empty;
    public int Port { get; set; }
    public string? Login { get; set; }
    public string? PasswordEncrypted { get; set; }

    public double? Latitude { get; set; }
    public double? Longitude { get; set; }
    public string? Timezone { get; set; }
    public string? Locale { get; set; }
    public string? LocationLabel { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
