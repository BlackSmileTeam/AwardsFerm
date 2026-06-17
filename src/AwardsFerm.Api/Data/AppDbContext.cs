using AwardsFerm.Api.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace AwardsFerm.Api.Data;

public sealed class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

    public DbSet<UserEntity> Users => Set<UserEntity>();
    public DbSet<AdAccountEntity> AdAccounts => Set<AdAccountEntity>();
    public DbSet<SessionSlotEntity> SessionSlots => Set<SessionSlotEntity>();
    public DbSet<ProxyEntity> Proxies => Set<ProxyEntity>();
    public DbSet<SessionRunEntity> SessionRuns => Set<SessionRunEntity>();
    public DbSet<RsyaSnapshotEntity> RsyaSnapshots => Set<RsyaSnapshotEntity>();
    public DbSet<SessionIpAuditEntity> SessionIpAudits => Set<SessionIpAuditEntity>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<UserEntity>(b =>
        {
            b.ToTable("users");
            b.HasKey(x => x.Id);
            b.Property(x => x.Login).HasMaxLength(64).IsRequired();
            b.HasIndex(x => x.Login).IsUnique();
            b.Property(x => x.PasswordHash).IsRequired();
            b.Property(x => x.PasswordSalt).IsRequired();
        });

        modelBuilder.Entity<AdAccountEntity>(b =>
        {
            b.ToTable("ad_accounts");
            b.HasKey(x => x.Id);
            b.Property(x => x.Name).HasMaxLength(128).IsRequired();
            b.Property(x => x.GameTitle).HasMaxLength(256).IsRequired();
            b.Property(x => x.GameUrl).HasMaxLength(2048).IsRequired();
            b.Property(x => x.TokenEncrypted).IsRequired();
            b.HasOne(x => x.User).WithMany(x => x.AdAccounts).HasForeignKey(x => x.UserId);
        });

        modelBuilder.Entity<SessionSlotEntity>(b =>
        {
            b.ToTable("session_slots");
            b.HasKey(x => x.Id);
            b.Property(x => x.ProfileId).HasMaxLength(64).IsRequired();
            b.Property(x => x.Label).HasMaxLength(128).IsRequired();
            b.Property(x => x.ScheduledStartMsk).HasMaxLength(5);
            b.Property(x => x.StopAtMsk).HasMaxLength(5);
            b.HasIndex(x => new { x.AdAccountId, x.ProfileId }).IsUnique();
            b.HasOne(x => x.AdAccount).WithMany(x => x.SessionSlots).HasForeignKey(x => x.AdAccountId);
            b.HasOne(x => x.Proxy).WithMany().HasForeignKey(x => x.ProxyId).OnDelete(DeleteBehavior.SetNull);
        });

        modelBuilder.Entity<ProxyEntity>(b =>
        {
            b.ToTable("proxies");
            b.HasKey(x => x.Id);
            b.Property(x => x.Name).HasMaxLength(128).IsRequired();
            b.Property(x => x.Scheme).HasMaxLength(16).IsRequired();
            b.Property(x => x.Host).HasMaxLength(256).IsRequired();
            b.Property(x => x.Login).HasMaxLength(256);
            b.Property(x => x.PasswordEncrypted).HasMaxLength(2048);
            b.Property(x => x.Timezone).HasMaxLength(64);
            b.Property(x => x.Locale).HasMaxLength(16);
            b.Property(x => x.LocationLabel).HasMaxLength(256);
            b.HasIndex(x => new { x.UserId, x.Name });
            b.HasOne(x => x.User).WithMany().HasForeignKey(x => x.UserId);
        });

        modelBuilder.Entity<SessionRunEntity>(b =>
        {
            b.ToTable("session_runs");
            b.HasKey(x => x.Id);
            b.Property(x => x.RuntimeSessionId).HasMaxLength(64).IsRequired();
            b.Property(x => x.Status).HasMaxLength(32).IsRequired();
            b.Property(x => x.ErrorMessage).HasMaxLength(2048);
            b.HasIndex(x => x.RuntimeSessionId);
            b.HasOne(x => x.SessionSlot).WithMany(x => x.Runs).HasForeignKey(x => x.SessionSlotId);
        });

        modelBuilder.Entity<RsyaSnapshotEntity>(b =>
        {
            b.ToTable("rsya_snapshots");
            b.HasKey(x => x.Id);
            b.HasIndex(x => new { x.AdAccountId, x.CapturedAt });
            b.HasOne(x => x.AdAccount).WithMany(x => x.RsyaSnapshots).HasForeignKey(x => x.AdAccountId);
        });

        modelBuilder.Entity<SessionIpAuditEntity>(b =>
        {
            b.ToTable("session_ip_audits");
            b.HasKey(x => x.Id);
            b.Property(x => x.RuntimeSessionId).HasMaxLength(64).IsRequired();
            b.Property(x => x.ProfileId).HasMaxLength(64).IsRequired();
            b.Property(x => x.PublicIp).HasMaxLength(64).IsRequired();
            b.HasIndex(x => new { x.RuntimeSessionId, x.CapturedAt });
            b.HasIndex(x => new { x.AdAccountId, x.CapturedAt });
        });
    }
}
