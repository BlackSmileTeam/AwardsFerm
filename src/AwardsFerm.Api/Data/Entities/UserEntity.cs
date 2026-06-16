namespace AwardsFerm.Api.Data.Entities;

public sealed class UserEntity
{
    public long Id { get; set; }
    public string Login { get; set; } = string.Empty;
    public string PasswordHash { get; set; } = string.Empty;
    public string PasswordSalt { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public List<AdAccountEntity> AdAccounts { get; set; } = [];
}
