using Microsoft.EntityFrameworkCore;
using ObsidianSync.Server.Contracts;
using ObsidianSync.Server.Data;
using ObsidianSync.Server.Domain;
using ObsidianSync.Server.Storage;

namespace ObsidianSync.Server.Services;

public sealed class SyncService(
    SyncDbContext db,
    IObjectStore objectStore,
    ILogger<SyncService> logger)
{
    public async Task<IReadOnlyList<VaultSummary>> GetVaultsAsync(Guid userId, CancellationToken cancellationToken)
    {
        return await (
            from member in db.VaultMembers.AsNoTracking()
            join vault in db.Vaults.AsNoTracking() on member.VaultId equals vault.Id
            where member.UserId == userId
            orderby vault.Name
            select new VaultSummary(vault.Id, vault.Name, vault.CurrentRevision, member.Role))
            .ToListAsync(cancellationToken);
    }

    public async Task<VaultSummary> CreateVaultAsync(Guid userId, string name, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Trim().Length > 200)
        {
            throw new ArgumentException("A vault name between 1 and 200 characters is required.", nameof(name));
        }

        var now = DateTime.UtcNow;
        var vault = new Vault
        {
            Id = Guid.NewGuid(),
            Name = name.Trim(),
            OwnerId = userId,
            CreatedUtc = now,
            CurrentRevision = 0
        };

        db.Vaults.Add(vault);
        db.VaultMembers.Add(new VaultMember
        {
            VaultId = vault.Id,
            UserId = userId,
            Role = VaultRole.Owner,
            CreatedUtc = now
        });
        await db.SaveChangesAsync(cancellationToken);
        return new VaultSummary(vault.Id, vault.Name, vault.CurrentRevision, VaultRole.Owner);
    }

    public async Task<IReadOnlyList<DeviceSummary>> GetDevicesAsync(Guid userId, CancellationToken cancellationToken)
    {
        return await db.Devices.AsNoTracking()
            .Where(device => device.UserId == userId)
            .OrderBy(device => device.Name)
            .Select(device => new DeviceSummary(device.Id, device.Name, device.LastSeenUtc))
            .ToListAsync(cancellationToken);
    }

    public async Task<DeviceSummary> CreateDeviceAsync(Guid userId, string name, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Trim().Length > 200)
        {
            throw new ArgumentException("A device name between 1 and 200 characters is required.", nameof(name));
        }

        var now = DateTime.UtcNow;
        var device = new Device
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            Name = name.Trim(),
            CreatedUtc = now,
            LastSeenUtc = now
        };
        db.Devices.Add(device);
        await db.SaveChangesAsync(cancellationToken);
        return new DeviceSummary(device.Id, device.Name, device.LastSeenUtc);
    }

    public async Task<SyncHeartbeatResponse> RecordHeartbeatAsync(
        Guid userId,
        Guid vaultId,
        SyncHeartbeatRequest request,
        CancellationToken cancellationToken)
    {
        await RequireMemberAsync(userId, vaultId, write: false, cancellationToken);
        await RequireDeviceAsync(userId, request.DeviceId, cancellationToken);

        var status = request.Status?.Trim().ToLowerInvariant();
        if (status is not ("started" or "completed" or "failed"))
        {
            throw new ArgumentException("Sync status must be started, completed, or failed.", nameof(request));
        }

        if (request.LastKnownRevision < 0)
        {
            throw new ArgumentException("LastKnownRevision cannot be negative.", nameof(request));
        }

        if (request.ErrorMessage is { Length: > 1000 })
        {
            throw new ArgumentException("Sync error messages cannot exceed 1000 characters.", nameof(request));
        }

        var now = DateTime.UtcNow;
        var session = new SyncSession
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            DeviceId = request.DeviceId,
            VaultId = vaultId,
            StartedUtc = now,
            CompletedUtc = status == "completed" ? now : null,
            Status = status,
            LastKnownRevision = request.LastKnownRevision,
            ErrorMessage = status == "failed" ? request.ErrorMessage : null
        };
        db.SyncSessions.Add(session);
        await db.SaveChangesAsync(cancellationToken);
        return new SyncHeartbeatResponse(session.Id, session.Status, now);
    }

    public async Task<ChangesResponse> GetChangesAsync(Guid userId, Guid vaultId, long after, int limit, CancellationToken cancellationToken)
    {
        await RequireMemberAsync(userId, vaultId, write: false, cancellationToken);
        limit = Math.Clamp(limit, 1, 2000);
        var vault = await db.Vaults.AsNoTracking().SingleAsync(item => item.Id == vaultId, cancellationToken);
        var changes = await db.FileRevisions.AsNoTracking()
            .Where(revision => revision.VaultId == vaultId && revision.Revision > after)
            .OrderBy(revision => revision.Revision)
            .Take(limit)
            .Select(revision => new ChangeDto(
                revision.FileId,
                revision.Revision,
                revision.Operation.ToString().ToLowerInvariant(),
                revision.Path,
                revision.OldPath,
                revision.ContentHash,
                revision.Size,
                revision.IsConflict,
                revision.BaseFileRevision,
                revision.CreatedUtc))
            .ToListAsync(cancellationToken);

        return new ChangesResponse(vaultId, vault.CurrentRevision, changes);
    }

    public async Task<SyncMutationResponse> UploadAsync(
        Guid userId,
        Guid vaultId,
        Guid deviceId,
        string operationId,
        string path,
        long baseFileRevision,
        Stream content,
        CancellationToken cancellationToken)
    {
        path = PathRules.Normalize(path);
        ValidateOperationId(operationId);
        await RequireMemberAsync(userId, vaultId, write: true, cancellationToken);
        var device = await RequireDeviceAsync(userId, deviceId, cancellationToken);

        var existing = await FindAppliedOperationAsync(vaultId, deviceId, operationId, cancellationToken);
        if (existing is not null)
        {
            return await ReplayAsync(existing, cancellationToken);
        }

        var objectResult = await objectStore.PutAsync(content, cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        existing = await FindAppliedOperationAsync(vaultId, deviceId, operationId, cancellationToken);
        if (existing is not null)
        {
            await transaction.CommitAsync(cancellationToken);
            return await ReplayAsync(existing, cancellationToken);
        }

        var vault = await db.Vaults.SingleAsync(item => item.Id == vaultId, cancellationToken);
        var pathKey = PathRules.Key(path);
        var entry = await db.Files.SingleOrDefaultAsync(item => item.VaultId == vaultId && item.PathKey == pathKey, cancellationToken);
        var isConflict = entry is not null && entry.LastChangedRevision != baseFileRevision;

        if (isConflict)
        {
            var revision = NextRevision(vault);
            var conflictPath = await CreateConflictPathAsync(vaultId, path, device.Name, cancellationToken);
            var conflictEntry = new FileEntry
            {
                Id = Guid.NewGuid(),
                VaultId = vaultId,
                Path = conflictPath,
                PathKey = PathRules.Key(conflictPath),
                IsDeleted = false,
                LastChangedRevision = revision,
                CurrentContentHash = objectResult.Hash,
                CurrentSize = objectResult.Size
            };
            var conflictRevision = new FileRevision
            {
                Id = Guid.NewGuid(),
                VaultId = vaultId,
                FileId = conflictEntry.Id,
                Revision = revision,
                Operation = FileOperation.ConflictCopy,
                Path = conflictPath,
                ContentHash = objectResult.Hash,
                Size = objectResult.Size,
                BaseFileRevision = baseFileRevision,
                CreatedByDeviceId = deviceId,
                CreatedUtc = DateTime.UtcNow,
                IsConflict = true,
                ConflictOfFileId = entry?.Id,
                OperationId = operationId
            };
            db.Files.Add(conflictEntry);
            db.FileRevisions.Add(conflictRevision);
            db.AppliedOperations.Add(new AppliedOperation
            {
                Id = Guid.NewGuid(),
                VaultId = vaultId,
                DeviceId = deviceId,
                OperationId = operationId,
                FileRevisionId = conflictRevision.Id,
                ResultStatus = "conflict",
                CreatedUtc = DateTime.UtcNow
            });
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);

            logger.LogInformation("Preserved stale upload as conflict copy for vault {VaultId}, path {Path}, revision {Revision}", vaultId, path, revision);
            return new SyncMutationResponse("conflict", conflictEntry.Id, revision, conflictPath, null, objectResult.Hash, objectResult.Size, entry?.LastChangedRevision ?? 0, vault.CurrentRevision, false, conflictPath);
        }

        var isNewEntry = entry is null;
        entry ??= new FileEntry
        {
            Id = Guid.NewGuid(),
            VaultId = vaultId,
            Path = path,
            PathKey = pathKey
        };
        entry.Path = path;
        entry.PathKey = pathKey;
        entry.IsDeleted = false;
        entry.CurrentContentHash = objectResult.Hash;
        entry.CurrentSize = objectResult.Size;
        var acceptedRevision = NextRevision(vault);
        entry.LastChangedRevision = acceptedRevision;

        var fileRevision = new FileRevision
        {
            Id = Guid.NewGuid(),
            VaultId = vaultId,
            FileId = entry.Id,
            Revision = acceptedRevision,
            Operation = FileOperation.Upsert,
            Path = path,
            ContentHash = objectResult.Hash,
            Size = objectResult.Size,
            BaseFileRevision = baseFileRevision,
            CreatedByDeviceId = deviceId,
            CreatedUtc = DateTime.UtcNow,
            OperationId = operationId
        };
        if (isNewEntry)
        {
            db.Files.Add(entry);
        }

        db.FileRevisions.Add(fileRevision);
        db.AppliedOperations.Add(new AppliedOperation
        {
            Id = Guid.NewGuid(),
            VaultId = vaultId,
            DeviceId = deviceId,
            OperationId = operationId,
            FileRevisionId = fileRevision.Id,
            ResultStatus = "accepted",
            CreatedUtc = DateTime.UtcNow
        });
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        logger.LogInformation("Accepted upload for vault {VaultId}, path {Path}, revision {Revision}", vaultId, path, acceptedRevision);
        return new SyncMutationResponse("accepted", entry.Id, acceptedRevision, path, null, objectResult.Hash, objectResult.Size, acceptedRevision, vault.CurrentRevision, false);
    }

    public async Task<SyncMutationResponse> DeleteAsync(Guid userId, Guid vaultId, DeleteRequest request, CancellationToken cancellationToken)
    {
        var path = PathRules.Normalize(request.Path);
        ValidateOperationId(request.OperationId);
        await RequireMemberAsync(userId, vaultId, write: true, cancellationToken);
        await RequireDeviceAsync(userId, request.DeviceId, cancellationToken);

        var existing = await FindAppliedOperationAsync(vaultId, request.DeviceId, request.OperationId, cancellationToken);
        if (existing is not null)
        {
            return await ReplayAsync(existing, cancellationToken);
        }

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var vault = await db.Vaults.SingleAsync(item => item.Id == vaultId, cancellationToken);
        var entry = await db.Files.SingleOrDefaultAsync(item => item.VaultId == vaultId && item.PathKey == PathRules.Key(path), cancellationToken);
        if (entry is null || entry.IsDeleted)
        {
            throw new KeyNotFoundException("The file does not exist.");
        }

        if (entry.LastChangedRevision != request.BaseFileRevision)
        {
            throw new SyncConflictException("The file changed on the server after the client base revision.");
        }

        var revision = NextRevision(vault);
        entry.IsDeleted = true;
        entry.LastChangedRevision = revision;
        var fileRevision = new FileRevision
        {
            Id = Guid.NewGuid(),
            VaultId = vaultId,
            FileId = entry.Id,
            Revision = revision,
            Operation = FileOperation.Delete,
            Path = entry.Path,
            ContentHash = null,
            Size = 0,
            BaseFileRevision = request.BaseFileRevision,
            CreatedByDeviceId = request.DeviceId,
            CreatedUtc = DateTime.UtcNow,
            OperationId = request.OperationId
        };
        db.FileRevisions.Add(fileRevision);
        db.AppliedOperations.Add(CreateAppliedOperation(vaultId, request.DeviceId, request.OperationId, fileRevision, "accepted"));
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        logger.LogInformation("Accepted delete for vault {VaultId}, path {Path}, revision {Revision}", vaultId, path, revision);
        return new SyncMutationResponse("accepted", entry.Id, revision, entry.Path, null, null, 0, revision, vault.CurrentRevision, false);
    }

    public async Task<SyncMutationResponse> RenameAsync(Guid userId, Guid vaultId, RenameRequest request, CancellationToken cancellationToken)
    {
        var oldPath = PathRules.Normalize(request.OldPath);
        var newPath = PathRules.Normalize(request.Path);
        ValidateOperationId(request.OperationId);
        await RequireMemberAsync(userId, vaultId, write: true, cancellationToken);
        await RequireDeviceAsync(userId, request.DeviceId, cancellationToken);

        var existing = await FindAppliedOperationAsync(vaultId, request.DeviceId, request.OperationId, cancellationToken);
        if (existing is not null)
        {
            return await ReplayAsync(existing, cancellationToken);
        }

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var vault = await db.Vaults.SingleAsync(item => item.Id == vaultId, cancellationToken);
        var source = await db.Files.SingleOrDefaultAsync(item => item.VaultId == vaultId && item.PathKey == PathRules.Key(oldPath), cancellationToken);
        if (source is null || source.IsDeleted)
        {
            throw new KeyNotFoundException("The source file does not exist.");
        }

        var destination = await db.Files.SingleOrDefaultAsync(item => item.VaultId == vaultId && item.PathKey == PathRules.Key(newPath), cancellationToken);
        if (destination is not null && destination.Id != source.Id)
        {
            throw new SyncConflictException("The destination path is already used by another logical file.");
        }

        if (source.LastChangedRevision != request.BaseFileRevision)
        {
            throw new SyncConflictException("The file changed on the server after the client base revision.");
        }

        var revision = NextRevision(vault);
        source.Path = newPath;
        source.PathKey = PathRules.Key(newPath);
        source.LastChangedRevision = revision;
        var fileRevision = new FileRevision
        {
            Id = Guid.NewGuid(),
            VaultId = vaultId,
            FileId = source.Id,
            Revision = revision,
            Operation = FileOperation.Rename,
            Path = newPath,
            OldPath = oldPath,
            ContentHash = source.CurrentContentHash,
            Size = source.CurrentSize,
            BaseFileRevision = request.BaseFileRevision,
            CreatedByDeviceId = request.DeviceId,
            CreatedUtc = DateTime.UtcNow,
            OperationId = request.OperationId
        };
        db.FileRevisions.Add(fileRevision);
        db.AppliedOperations.Add(CreateAppliedOperation(vaultId, request.DeviceId, request.OperationId, fileRevision, "accepted"));
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        logger.LogInformation("Accepted rename for vault {VaultId}, {OldPath} to {NewPath}, revision {Revision}", vaultId, oldPath, newPath, revision);
        return new SyncMutationResponse("accepted", source.Id, revision, newPath, oldPath, source.CurrentContentHash, source.CurrentSize, revision, vault.CurrentRevision, false);
    }

    public async Task<IReadOnlyList<HistoryDto>> GetHistoryAsync(Guid userId, Guid vaultId, Guid fileId, CancellationToken cancellationToken)
    {
        await RequireMemberAsync(userId, vaultId, write: false, cancellationToken);
        var exists = await db.Files.AsNoTracking().AnyAsync(file => file.VaultId == vaultId && file.Id == fileId, cancellationToken);
        if (!exists)
        {
            throw new KeyNotFoundException("The file does not exist.");
        }

        return await db.FileRevisions.AsNoTracking()
            .Where(revision => revision.VaultId == vaultId && revision.FileId == fileId)
            .OrderByDescending(revision => revision.Revision)
            .Select(revision => new HistoryDto(
                revision.Id,
                revision.Revision,
                revision.Operation.ToString().ToLowerInvariant(),
                revision.Path,
                revision.OldPath,
                revision.ContentHash,
                revision.Size,
                revision.IsConflict,
                revision.CreatedUtc,
                revision.CreatedByDeviceId))
            .ToListAsync(cancellationToken);
    }

    public async Task<(FileRevision Revision, Stream Content)> OpenContentAsync(Guid userId, Guid vaultId, Guid fileId, long? revisionNumber, CancellationToken cancellationToken)
    {
        await RequireMemberAsync(userId, vaultId, write: false, cancellationToken);
        var currentFile = await db.Files.AsNoTracking().SingleOrDefaultAsync(file => file.VaultId == vaultId && file.Id == fileId, cancellationToken);
        if (currentFile is null)
        {
            throw new KeyNotFoundException("The file does not exist.");
        }
        if (revisionNumber is null && currentFile.IsDeleted)
        {
            throw new KeyNotFoundException("The file is deleted; request a specific historical revision for its previous content.");
        }

        var query = db.FileRevisions.AsNoTracking().Where(revision => revision.VaultId == vaultId && revision.FileId == fileId);
        var revision = revisionNumber is null
            ? await query.Where(item => item.ContentHash != null).OrderByDescending(item => item.Revision).FirstOrDefaultAsync(cancellationToken)
            : await query.SingleOrDefaultAsync(item => item.Revision == revisionNumber.Value, cancellationToken);
        if (revision?.ContentHash is null)
        {
            throw new KeyNotFoundException("The requested file revision has no content.");
        }

        if (!await objectStore.ExistsAsync(revision.ContentHash, cancellationToken))
        {
            throw new InvalidOperationException("The database references a missing content object.");
        }

        return (revision, await objectStore.OpenReadAsync(revision.ContentHash, cancellationToken));
    }

    public async Task<IReadOnlyList<MemberSummary>> GetMembersAsync(Guid userId, Guid vaultId, CancellationToken cancellationToken)
    {
        await RequireMemberAsync(userId, vaultId, write: false, cancellationToken);
        return await (
            from member in db.VaultMembers.AsNoTracking()
            join user in db.Users.AsNoTracking() on member.UserId equals user.Id
            where member.VaultId == vaultId
            orderby user.UserName
            select new MemberSummary(user.Id, user.UserName, member.Role))
            .ToListAsync(cancellationToken);
    }

    public async Task AddMemberAsync(Guid userId, Guid vaultId, AddMemberRequest request, CancellationToken cancellationToken)
    {
        var owner = await RequireMemberAsync(userId, vaultId, write: true, cancellationToken);
        if (owner.Role != VaultRole.Owner)
        {
            throw new UnauthorizedAccessException("Only the vault owner can manage members.");
        }

        var normalized = NormalizeUserName(request.UserName);
        var target = await db.Users.SingleOrDefaultAsync(user => user.NormalizedUserName == normalized, cancellationToken)
            ?? throw new KeyNotFoundException("The invited user does not exist yet.");
        var current = await db.VaultMembers.FindAsync(new object[] { vaultId, target.Id }, cancellationToken);
        if (current is null)
        {
            db.VaultMembers.Add(new VaultMember { VaultId = vaultId, UserId = target.Id, Role = request.Role, CreatedUtc = DateTime.UtcNow });
        }
        else
        {
            current.Role = request.Role;
        }

        await db.SaveChangesAsync(cancellationToken);
    }

    public static string NormalizeUserName(string userName) => userName.Trim().ToUpperInvariant();

    private async Task<VaultMember> RequireMemberAsync(Guid userId, Guid vaultId, bool write, CancellationToken cancellationToken)
    {
        var member = await db.VaultMembers.AsNoTracking().SingleOrDefaultAsync(item => item.VaultId == vaultId && item.UserId == userId, cancellationToken);
        if (member is null)
        {
            throw new KeyNotFoundException("The vault does not exist or is not shared with this user.");
        }

        if (write && member.Role == VaultRole.ReadOnly)
        {
            throw new UnauthorizedAccessException("The user has read-only access to this vault.");
        }

        return member;
    }

    private async Task<Device> RequireDeviceAsync(Guid userId, Guid deviceId, CancellationToken cancellationToken)
    {
        var device = await db.Devices.SingleOrDefaultAsync(item => item.Id == deviceId && item.UserId == userId, cancellationToken);
        if (device is null)
        {
            throw new UnauthorizedAccessException("The device does not belong to the authenticated user.");
        }

        device.LastSeenUtc = DateTime.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        return device;
    }

    private async Task<AppliedOperation?> FindAppliedOperationAsync(Guid vaultId, Guid deviceId, string operationId, CancellationToken cancellationToken)
    {
        return await db.AppliedOperations.AsNoTracking()
            .SingleOrDefaultAsync(item => item.VaultId == vaultId && item.DeviceId == deviceId && item.OperationId == operationId, cancellationToken);
    }

    private async Task<SyncMutationResponse> ReplayAsync(AppliedOperation operation, CancellationToken cancellationToken)
    {
        if (operation.FileRevisionId is null)
        {
            throw new InvalidOperationException("The idempotency record is incomplete.");
        }

        var revision = await db.FileRevisions.AsNoTracking().SingleAsync(item => item.Id == operation.FileRevisionId, cancellationToken);
        var vaultRevision = await db.Vaults.AsNoTracking().Where(item => item.Id == operation.VaultId).Select(item => item.CurrentRevision).SingleAsync(cancellationToken);
        var currentFileRevision = revision.IsConflict && revision.ConflictOfFileId is not null
            ? await db.Files.AsNoTracking().Where(item => item.Id == revision.ConflictOfFileId.Value).Select(item => item.LastChangedRevision).SingleAsync(cancellationToken)
            : revision.Revision;
        return new SyncMutationResponse(
            operation.ResultStatus,
            revision.FileId,
            revision.Revision,
            revision.Path,
            revision.OldPath,
            revision.ContentHash,
            revision.Size,
            currentFileRevision,
            vaultRevision,
            true,
            revision.IsConflict ? revision.Path : null);
    }

    private static AppliedOperation CreateAppliedOperation(Guid vaultId, Guid deviceId, string operationId, FileRevision revision, string status) => new()
    {
        Id = Guid.NewGuid(),
        VaultId = vaultId,
        DeviceId = deviceId,
        OperationId = operationId,
        FileRevisionId = revision.Id,
        ResultStatus = status,
        CreatedUtc = DateTime.UtcNow
    };

    private static long NextRevision(Vault vault) => ++vault.CurrentRevision;

    private static void ValidateOperationId(string operationId)
    {
        if (string.IsNullOrWhiteSpace(operationId) || operationId.Trim().Length > 200)
        {
            throw new ArgumentException("A stable operationId between 1 and 200 characters is required.", nameof(operationId));
        }
    }

    private async Task<string> CreateConflictPathAsync(Guid vaultId, string path, string deviceName, CancellationToken cancellationToken)
    {
        var slash = path.LastIndexOf('/');
        var directory = slash >= 0 ? path[..(slash + 1)] : string.Empty;
        var fileName = slash >= 0 ? path[(slash + 1)..] : path;
        var extension = Path.GetExtension(fileName);
        var stem = extension.Length == 0 ? fileName : fileName[..^extension.Length];
        var safeDevice = new string(deviceName.Where(character => char.IsLetterOrDigit(character) || character is '-' or '_').ToArray());
        if (string.IsNullOrWhiteSpace(safeDevice))
        {
            safeDevice = "device";
        }

        var timestamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmssfff");
        var baseName = $"{stem} (conflict {safeDevice} {timestamp}){extension}";
        var candidate = PathRules.Normalize(directory + baseName);
        var suffix = 2;
        while (await db.Files.AnyAsync(file => file.VaultId == vaultId && file.PathKey == PathRules.Key(candidate), cancellationToken))
        {
            candidate = PathRules.Normalize(directory + $"{stem} (conflict {safeDevice} {timestamp} {suffix++}){extension}");
        }

        return candidate;
    }
}
