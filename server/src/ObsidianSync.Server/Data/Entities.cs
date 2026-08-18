namespace ObsidianSync.Server.Data;

public enum VaultRole
{
    Owner,
    Editor,
    ReadOnly
}

public enum FileOperation
{
    Upsert,
    Delete,
    Rename,
    ConflictCopy
}

public sealed class User
{
    public Guid Id { get; set; }
    public required string UserName { get; set; }
    public required string NormalizedUserName { get; set; }
    public required string PasswordHash { get; set; }
    public bool IsAdmin { get; set; }
    public DateTime CreatedUtc { get; set; }
}

public sealed class Vault
{
    public Guid Id { get; set; }
    public required string Name { get; set; }
    public Guid OwnerId { get; set; }
    public DateTime CreatedUtc { get; set; }
    public long CurrentRevision { get; set; }
}

public sealed class VaultMember
{
    public Guid VaultId { get; set; }
    public Guid UserId { get; set; }
    public VaultRole Role { get; set; }
    public DateTime CreatedUtc { get; set; }
}

public sealed class Device
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public required string Name { get; set; }
    public DateTime CreatedUtc { get; set; }
    public DateTime LastSeenUtc { get; set; }
}

public sealed class FileEntry
{
    public Guid Id { get; set; }
    public Guid VaultId { get; set; }
    public required string Path { get; set; }
    public required string PathKey { get; set; }
    public bool IsDeleted { get; set; }
    public long LastChangedRevision { get; set; }
    public string? CurrentContentHash { get; set; }
    public long CurrentSize { get; set; }
}

public sealed class FileRevision
{
    public Guid Id { get; set; }
    public Guid VaultId { get; set; }
    public Guid FileId { get; set; }
    public long Revision { get; set; }
    public FileOperation Operation { get; set; }
    public required string Path { get; set; }
    public string? OldPath { get; set; }
    public string? ContentHash { get; set; }
    public long Size { get; set; }
    public long BaseFileRevision { get; set; }
    public Guid? CreatedByDeviceId { get; set; }
    public DateTime CreatedUtc { get; set; }
    public bool IsConflict { get; set; }
    public Guid? ConflictOfFileId { get; set; }
    public string? OperationId { get; set; }
}

public sealed class AppliedOperation
{
    public Guid Id { get; set; }
    public Guid VaultId { get; set; }
    public Guid DeviceId { get; set; }
    public required string OperationId { get; set; }
    public Guid? FileRevisionId { get; set; }
    public required string ResultStatus { get; set; }
    public DateTime CreatedUtc { get; set; }
}

public sealed class Invitation
{
    public Guid Id { get; set; }
    public Guid VaultId { get; set; }
    public required string InvitedUserName { get; set; }
    public VaultRole Role { get; set; }
    public required string TokenHash { get; set; }
    public DateTime CreatedUtc { get; set; }
    public DateTime ExpiresUtc { get; set; }
    public DateTime? AcceptedUtc { get; set; }
}

public sealed class SyncSession
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public Guid DeviceId { get; set; }
    public Guid VaultId { get; set; }
    public DateTime StartedUtc { get; set; }
    public DateTime? CompletedUtc { get; set; }
    public required string Status { get; set; }
    public long LastKnownRevision { get; set; }
    public string? ErrorMessage { get; set; }
}
