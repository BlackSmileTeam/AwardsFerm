using AwardsFerm.Api.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace AwardsFerm.Api.Data;

public static class DatabaseBootstrap
{
    /// <summary>Хеш и соль из deploy/sql/init-admin.sql (пароль Admin123!).</summary>
    private const string DefaultAdminLogin = "admin";
    private const string DefaultAdminHash = "IfeI0vHx3ax6YDwK5m1nLPLGZQqkFTgmVN9LQ7B2/fk=";
    private const string DefaultAdminSalt = "427gUPHEgmWKK9DZuqqcJw==";

    public static async Task EnsureDefaultAdminAsync(AppDbContext db, CancellationToken cancellationToken = default)
    {
        if (await db.Users.AnyAsync(cancellationToken))
            return;

        db.Users.Add(new UserEntity
        {
            Login = DefaultAdminLogin,
            PasswordHash = DefaultAdminHash,
            PasswordSalt = DefaultAdminSalt,
            CreatedAt = DateTimeOffset.UtcNow
        });

        await db.SaveChangesAsync(cancellationToken);
    }
}
