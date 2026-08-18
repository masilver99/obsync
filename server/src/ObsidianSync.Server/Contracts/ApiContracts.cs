using ObsidianSync.Server.Data;

namespace ObsidianSync.Server.Contracts;

public sealed record RegisterRequest(string UserName, string Password);
public sealed record LoginRequest(string UserName, string Password);
public sealed record UserSummary(Guid Id, string UserName);
public sealed record AuthResponse(string Token, DateTime ExpiresUtc, UserSummary User);

public sealed record CreateVaultRequest(string Name);
public sealed record VaultSummary(Guid Id, string Name, long CurrentRevision, VaultRole Role);
public sealed record CreateDeviceRequest(string Name);
public sealed record DeviceSummary(Guid Id, string Name, DateTime LastSeenUtc);

public sealed record ChangeDto(
    Guid FileId,
    long Revision,
    string Operation,
    string Path,
    string? OldPath,
    string? ContentHash,
    long Size,
    bool IsConflict,
    long BaseFileRevision,
    DateTime CreatedUtc);

public sealed record ChangesResponse(Guid VaultId, long CurrentRevision, IReadOnlyList<ChangeDto> Changes);

public sealed record SyncMutationResponse(
    string Status,
    Guid FileId,
    long Revision,
    string Path,
    string? OldPath,
    string? ContentHash,
    long Size,
    long CurrentFileRevision,
    long CurrentVaultRevision,
    bool Replay,
    string? ConflictPath = null);

public sealed record DeleteRequest(Guid DeviceId, string OperationId, string Path, long BaseFileRevision);
public sealed record RenameRequest(Guid DeviceId, string OperationId, string OldPath, string Path, long BaseFileRevision);

public sealed record HistoryDto(
    Guid Id,
    long Revision,
    string Operation,
    string Path,
    string? OldPath,
    string? ContentHash,
    long Size,
    bool IsConflict,
    DateTime CreatedUtc,
    Guid? CreatedByDeviceId);

public sealed record MemberSummary(Guid UserId, string UserName, VaultRole Role);
public sealed record AddMemberRequest(string UserName, VaultRole Role);

public sealed record SyncHeartbeatRequest(Guid DeviceId, string Status, long LastKnownRevision, string? ErrorMessage = null);
public sealed record SyncHeartbeatResponse(Guid SessionId, string Status, DateTime RecordedUtc);

public sealed record AdminDashboardResponse(
    AdminOverviewDto Overview,
    IReadOnlyList<AdminUserDto> Users,
    IReadOnlyList<AdminVaultDto> Vaults);

public sealed record AdminOverviewDto(
    int UserCount,
    int VaultCount,
    int DeviceCount,
    int ActiveFileCount,
    long LogicalBytes,
    long ObjectCount,
    long ObjectBytes,
    DateTime? LastSuccessfulSyncUtc);

public sealed record AdminUserDto(
    Guid Id,
    string UserName,
    bool IsAdmin,
    DateTime CreatedUtc,
    int DeviceCount,
    int VaultCount,
    IReadOnlyList<string> VaultNames,
    DateTime? LastSeenUtc);

public sealed record AdminVaultDto(
    Guid Id,
    string Name,
    string OwnerUserName,
    long CurrentRevision,
    int MemberCount,
    int FileCount,
    long LogicalBytes,
    int RevisionCount,
    DateTime? LastSuccessfulSyncUtc);

public sealed record AdminCreateUserRequest(string UserName, string Password, bool IsAdmin = false);
public sealed record AdminSetPasswordRequest(string Password);
public sealed record AdminMemberRequest(string UserName, VaultRole Role);

public sealed record ErrorResponse(string Error, string? Detail = null);
