using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using ObsidianSync.Server.Contracts;
using ObsidianSync.Server.Data;
using ObsidianSync.Server.Storage;

namespace ObsidianSync.Server.Services;

public sealed class AdminService(
    SyncDbContext db,
    IPasswordHasher<User> passwordHasher,
    IObjectStore objectStore)
{
    public async Task<AdminDashboardResponse> GetDashboardAsync(CancellationToken cancellationToken)
    {
        var users = await db.Users.AsNoTracking().OrderBy(user => user.UserName).ToListAsync(cancellationToken);
        var vaults = await db.Vaults.AsNoTracking().OrderBy(vault => vault.Name).ToListAsync(cancellationToken);
        var devices = await db.Devices.AsNoTracking().ToListAsync(cancellationToken);
        var members = await db.VaultMembers.AsNoTracking().ToListAsync(cancellationToken);
        var files = await db.Files.AsNoTracking().ToListAsync(cancellationToken);
        var revisions = await db.FileRevisions.AsNoTracking().ToListAsync(cancellationToken);
        var sessions = await db.SyncSessions.AsNoTracking().ToListAsync(cancellationToken);
        var objectUsage = await objectStore.GetUsageAsync(cancellationToken);

        var userDtos = users.Select(user =>
        {
            var userDevices = devices.Where(device => device.UserId == user.Id).ToList();
            return new AdminUserDto(
                user.Id,
                user.UserName,
                user.IsAdmin,
                user.CreatedUtc,
                userDevices.Count,
                members.Count(member => member.UserId == user.Id),
                vaults
                    .Where(vault => members.Any(member => member.UserId == user.Id && member.VaultId == vault.Id))
                    .Select(vault => vault.Name)
                    .ToList(),
                userDevices.Count == 0 ? null : userDevices.Max(device => device.LastSeenUtc));
        }).ToList();

        var vaultDtos = vaults.Select(vault =>
        {
            var vaultFiles = files.Where(file => file.VaultId == vault.Id && !file.IsDeleted).ToList();
            var vaultSessions = sessions
                .Where(session => session.VaultId == vault.Id && session.Status == "completed" && session.CompletedUtc.HasValue)
                .Select(session => session.CompletedUtc!.Value)
                .ToList();
            var owner = users.FirstOrDefault(user => user.Id == vault.OwnerId);
            return new AdminVaultDto(
                vault.Id,
                vault.Name,
                owner?.UserName ?? "<unknown>",
                vault.CurrentRevision,
                members.Count(member => member.VaultId == vault.Id),
                vaultFiles.Count,
                vaultFiles.Sum(file => file.CurrentSize),
                revisions.Count(revision => revision.VaultId == vault.Id),
                vaultSessions.Count == 0 ? null : vaultSessions.Max());
        }).ToList();

        var successfulSessions = sessions
            .Where(session => session.Status == "completed" && session.CompletedUtc.HasValue)
            .Select(session => session.CompletedUtc!.Value)
            .ToList();
        var activeFiles = files.Where(file => !file.IsDeleted).ToList();
        var overview = new AdminOverviewDto(
            users.Count,
            vaults.Count,
            devices.Count,
            activeFiles.Count,
            activeFiles.Sum(file => file.CurrentSize),
            objectUsage.ObjectCount,
            objectUsage.ByteCount,
            successfulSessions.Count == 0 ? null : successfulSessions.Max());

        return new AdminDashboardResponse(overview, userDtos, vaultDtos);
    }

    public async Task<AdminUserDto> CreateUserAsync(AdminCreateUserRequest request, CancellationToken cancellationToken)
    {
        var userName = ValidateUserName(request.UserName);
        ValidatePassword(request.Password);
        var normalized = SyncService.NormalizeUserName(userName);
        if (await db.Users.AnyAsync(user => user.NormalizedUserName == normalized, cancellationToken))
        {
            throw new SyncConflictException("That username is already registered.");
        }

        var user = new User
        {
            Id = Guid.NewGuid(),
            UserName = userName,
            NormalizedUserName = normalized,
            PasswordHash = string.Empty,
            IsAdmin = request.IsAdmin,
            CreatedUtc = DateTime.UtcNow
        };
        user.PasswordHash = passwordHasher.HashPassword(user, request.Password);
        db.Users.Add(user);
        await db.SaveChangesAsync(cancellationToken);
        return new AdminUserDto(user.Id, user.UserName, user.IsAdmin, user.CreatedUtc, 0, 0, [], null);
    }

    public async Task SetPasswordAsync(Guid userId, AdminSetPasswordRequest request, CancellationToken cancellationToken)
    {
        ValidatePassword(request.Password);
        var user = await db.Users.SingleOrDefaultAsync(item => item.Id == userId, cancellationToken)
            ?? throw new KeyNotFoundException("The user does not exist.");
        user.PasswordHash = passwordHasher.HashPassword(user, request.Password);
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task SetMembershipAsync(Guid vaultId, AdminMemberRequest request, CancellationToken cancellationToken)
    {
        if (request.Role is not (VaultRole.Editor or VaultRole.ReadOnly))
        {
            throw new ArgumentException("Administrators can assign Editor or ReadOnly membership; the vault owner cannot be reassigned here.", nameof(request));
        }

        var vault = await db.Vaults.SingleOrDefaultAsync(item => item.Id == vaultId, cancellationToken)
            ?? throw new KeyNotFoundException("The vault does not exist.");
        var normalized = SyncService.NormalizeUserName(request.UserName);
        var user = await db.Users.SingleOrDefaultAsync(item => item.NormalizedUserName == normalized, cancellationToken)
            ?? throw new KeyNotFoundException("The user does not exist. Create the user before assigning vault access.");
        if (user.Id == vault.OwnerId)
        {
            throw new SyncConflictException("The vault owner already has Owner access.");
        }

        var member = await db.VaultMembers.FindAsync(new object[] { vaultId, user.Id }, cancellationToken);
        if (member is null)
        {
            db.VaultMembers.Add(new VaultMember
            {
                VaultId = vaultId,
                UserId = user.Id,
                Role = request.Role,
                CreatedUtc = DateTime.UtcNow
            });
        }
        else
        {
            member.Role = request.Role;
        }

        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task RemoveMembershipAsync(Guid vaultId, Guid userId, CancellationToken cancellationToken)
    {
        var vault = await db.Vaults.SingleOrDefaultAsync(item => item.Id == vaultId, cancellationToken)
            ?? throw new KeyNotFoundException("The vault does not exist.");
        if (vault.OwnerId == userId)
        {
            throw new SyncConflictException("The vault owner cannot be removed from the vault.");
        }

        var member = await db.VaultMembers.FindAsync(new object[] { vaultId, userId }, cancellationToken)
            ?? throw new KeyNotFoundException("The user is not a member of this vault.");
        db.VaultMembers.Remove(member);
        await db.SaveChangesAsync(cancellationToken);
    }

    private static string ValidateUserName(string userName)
    {
        if (string.IsNullOrWhiteSpace(userName) || userName.Trim().Length > 200)
        {
            throw new ArgumentException("A username between 1 and 200 characters is required.", nameof(userName));
        }

        return userName.Trim();
    }

    private static void ValidatePassword(string password)
    {
        if (string.IsNullOrEmpty(password) || password.Length < 8)
        {
            throw new ArgumentException("Passwords must contain at least 8 characters.", nameof(password));
        }
    }
}
