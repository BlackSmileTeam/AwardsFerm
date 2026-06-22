using AwardsFerm.Api.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace AwardsFerm.Api.Data;

public static class DatabaseBootstrap
{
    /// <summary>Хеш и соль из deploy/sql/init-admin.sql (пароль Admin123!).</summary>
    private const string DefaultAdminLogin = "admin";
    private const string DefaultAdminHash = "IfeI0vHx3ax6YDwK5m1nLPLGZQqkFTgmVN9LQ7B2/fk=";
    private const string DefaultAdminSalt = "427gUPHEgmWKK9DZuqqcJw==";

    public static async Task MigrateAndPatchAsync(AppDbContext db, CancellationToken cancellationToken = default)
    {
        await db.Database.MigrateAsync(cancellationToken);
        await EnsureSessionSlotDevicePlatformColumnAsync(db, cancellationToken);
    }

    /// <summary>
    /// Страховка для прод-серверов, где миграция DevicePlatform не попала в __EFMigrationsHistory.
    /// </summary>
    private static async Task EnsureSessionSlotDevicePlatformColumnAsync(
        AppDbContext db,
        CancellationToken cancellationToken)
    {
        try
        {
            _ = await db.SessionSlots
                .AsNoTracking()
                .Select(x => x.DevicePlatform)
                .FirstOrDefaultAsync(cancellationToken);
        }
        catch
        {
            try
            {
                await db.Database.ExecuteSqlRawAsync(
                    """
                    ALTER TABLE session_slots
                    ADD COLUMN DevicePlatform TEXT NOT NULL DEFAULT 'Random'
                    """,
                    cancellationToken);

                await db.Database.ExecuteSqlRawAsync(
                    """
                    INSERT OR IGNORE INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
                    VALUES ('20260617150000_SessionSlotDevicePlatform', '8.0.8')
                    """,
                    cancellationToken);
            }
            catch
            {
                // колонка уже есть или схема обновлена мигратором
            }
        }
    }

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
