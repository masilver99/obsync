using Microsoft.EntityFrameworkCore;

namespace ObsidianSync.Server.Data;

public sealed class SyncDbContext(DbContextOptions<SyncDbContext> options) : DbContext(options)
{
    public DbSet<User> Users => Set<User>();
    public DbSet<Vault> Vaults => Set<Vault>();
    public DbSet<VaultMember> VaultMembers => Set<VaultMember>();
    public DbSet<Device> Devices => Set<Device>();
    public DbSet<FileEntry> Files => Set<FileEntry>();
    public DbSet<FileRevision> FileRevisions => Set<FileRevision>();
    public DbSet<AppliedOperation> AppliedOperations => Set<AppliedOperation>();
    public DbSet<Invitation> Invitations => Set<Invitation>();
    public DbSet<SyncSession> SyncSessions => Set<SyncSession>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<User>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => x.NormalizedUserName).IsUnique();
            entity.Property(x => x.UserName).HasMaxLength(200).IsRequired();
            entity.Property(x => x.NormalizedUserName).HasMaxLength(200).IsRequired();
            entity.Property(x => x.PasswordHash).IsRequired();
            entity.HasIndex(x => x.IsAdmin);
        });

        modelBuilder.Entity<Vault>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Name).HasMaxLength(200).IsRequired();
            entity.HasIndex(x => x.OwnerId);
        });

        modelBuilder.Entity<VaultMember>(entity =>
        {
            entity.HasKey(x => new { x.VaultId, x.UserId });
            entity.Property(x => x.Role).HasConversion<string>().HasMaxLength(20);
            entity.HasIndex(x => x.UserId);
        });

        modelBuilder.Entity<Device>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Name).HasMaxLength(200).IsRequired();
            entity.HasIndex(x => x.UserId);
        });

        modelBuilder.Entity<FileEntry>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Path).HasMaxLength(4000).IsRequired();
            entity.Property(x => x.PathKey).HasMaxLength(4000).IsRequired();
            entity.HasIndex(x => new { x.VaultId, x.PathKey }).IsUnique();
        });

        modelBuilder.Entity<FileRevision>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Operation).HasConversion<string>().HasMaxLength(30);
            entity.Property(x => x.Path).HasMaxLength(4000).IsRequired();
            entity.Property(x => x.OldPath).HasMaxLength(4000);
            entity.Property(x => x.ContentHash).HasMaxLength(64);
            entity.Property(x => x.OperationId).HasMaxLength(200);
            entity.HasIndex(x => new { x.VaultId, x.Revision }).IsUnique();
            entity.HasIndex(x => new { x.VaultId, x.FileId, x.Revision });
        });

        modelBuilder.Entity<AppliedOperation>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.Property(x => x.OperationId).HasMaxLength(200).IsRequired();
            entity.Property(x => x.ResultStatus).HasMaxLength(30).IsRequired();
            entity.HasIndex(x => new { x.VaultId, x.DeviceId, x.OperationId }).IsUnique();
        });

        modelBuilder.Entity<Invitation>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.Property(x => x.InvitedUserName).HasMaxLength(200).IsRequired();
            entity.Property(x => x.TokenHash).HasMaxLength(128).IsRequired();
            entity.Property(x => x.Role).HasConversion<string>().HasMaxLength(20);
            entity.HasIndex(x => new { x.VaultId, x.InvitedUserName });
        });

        modelBuilder.Entity<SyncSession>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Status).HasMaxLength(20).IsRequired();
            entity.Property(x => x.ErrorMessage).HasMaxLength(1000);
            entity.HasIndex(x => new { x.VaultId, x.CompletedUtc });
            entity.HasIndex(x => new { x.UserId, x.StartedUtc });
        });
    }
}
