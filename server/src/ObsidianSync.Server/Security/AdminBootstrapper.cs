using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using ObsidianSync.Server.Data;
using ObsidianSync.Server.Services;

namespace ObsidianSync.Server.Security;

public static class AdminBootstrapper
{
    public static async Task EnsureAsync(
        SyncDbContext db,
        IPasswordHasher<User> passwordHasher,
        IConfiguration configuration,
        ILogger logger,
        CancellationToken cancellationToken = default)
    {
        var configuredUserName = configuration["ADMIN_USERNAME"];
        if (string.IsNullOrWhiteSpace(configuredUserName))
        {
            configuredUserName = configuration["Admin:UserName"];
        }

        var configuredPassword = configuration["ADMIN_PASSWORD"];
        if (string.IsNullOrWhiteSpace(configuredPassword))
        {
            configuredPassword = configuration["Admin:Password"];
        }

        var resetPasswordSetting = configuration["ADMIN_RESET_PASSWORD"];
        if (string.IsNullOrWhiteSpace(resetPasswordSetting))
        {
            resetPasswordSetting = configuration["Admin:ResetPassword"];
        }
        var resetPassword = bool.TryParse(resetPasswordSetting, out var resetPasswordValue) && resetPasswordValue;

        if (string.IsNullOrWhiteSpace(configuredUserName) && string.IsNullOrWhiteSpace(configuredPassword))
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(configuredUserName) || string.IsNullOrWhiteSpace(configuredPassword))
        {
            throw new InvalidOperationException("ADMIN_USERNAME and ADMIN_PASSWORD must be configured together to bootstrap an administrator.");
        }

        var userName = configuredUserName.Trim();
        if (userName.Length > 200 || configuredPassword.Length < 8)
        {
            throw new InvalidOperationException("The configured admin username must be at most 200 characters and the admin password must contain at least 8 characters.");
        }

        var normalized = SyncService.NormalizeUserName(userName);
        var user = await db.Users.SingleOrDefaultAsync(item => item.NormalizedUserName == normalized, cancellationToken);
        if (user is null)
        {
            user = new User
            {
                Id = Guid.NewGuid(),
                UserName = userName,
                NormalizedUserName = normalized,
                PasswordHash = string.Empty,
                IsAdmin = true,
                CreatedUtc = DateTime.UtcNow
            };
            user.PasswordHash = passwordHasher.HashPassword(user, configuredPassword);
            db.Users.Add(user);
            await db.SaveChangesAsync(cancellationToken);
            logger.LogInformation("Bootstrapped configured administrator account {UserName}.", user.UserName);
            return;
        }

        var changed = false;
        if (!user.IsAdmin)
        {
            user.IsAdmin = true;
            changed = true;
        }

        if (resetPassword)
        {
            user.PasswordHash = passwordHasher.HashPassword(user, configuredPassword);
            changed = true;
        }

        if (changed)
        {
            await db.SaveChangesAsync(cancellationToken);
            logger.LogInformation(
                resetPassword
                    ? "Applied the configured administrator password reset for account {UserName}."
                    : "Promoted configured administrator account {UserName}.",
                user.UserName);
        }
    }
}
