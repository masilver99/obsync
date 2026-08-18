using Microsoft.AspNetCore.Identity;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using ObsidianSync.Server.Data;
using ObsidianSync.Server.Security;
using Xunit;

namespace ObsidianSync.Server.Tests;

public sealed class AdminBootstrapperTests
{
    [Fact]
    public async Task AdminSettingsCreateAnHashedAdministratorWithoutResettingItOnRestart()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<SyncDbContext>()
            .UseSqlite(connection)
            .Options;
        await using var db = new SyncDbContext(options);
        await DbInitializer.InitializeAsync(db);

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Admin:UserName"] = "settings-admin",
                ["Admin:Password"] = "settings-password"
            })
            .Build();
        var passwordHasher = new PasswordHasher<User>();

        await AdminBootstrapper.EnsureAsync(db, passwordHasher, configuration, NullLogger.Instance);
        var user = await db.Users.SingleAsync(item => item.UserName == "settings-admin");
        Assert.True(user.IsAdmin);
        Assert.Equal(PasswordVerificationResult.Success, passwordHasher.VerifyHashedPassword(user, user.PasswordHash, "settings-password"));

        await AdminBootstrapper.EnsureAsync(db, passwordHasher, configuration, NullLogger.Instance);
        var restartedUser = await db.Users.SingleAsync(item => item.UserName == "settings-admin");
        Assert.Equal(PasswordVerificationResult.Success, passwordHasher.VerifyHashedPassword(restartedUser, restartedUser.PasswordHash, "settings-password"));
        Assert.Equal(1, await db.Users.CountAsync());

        var resetConfiguration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Admin:UserName"] = "settings-admin",
                ["Admin:Password"] = "replacement-password",
                ["Admin:ResetPassword"] = "true"
            })
            .Build();
        await AdminBootstrapper.EnsureAsync(db, passwordHasher, resetConfiguration, NullLogger.Instance);
        var resetUser = await db.Users.SingleAsync(item => item.UserName == "settings-admin");
        Assert.Equal(PasswordVerificationResult.Failed, passwordHasher.VerifyHashedPassword(resetUser, resetUser.PasswordHash, "settings-password"));
        Assert.Equal(PasswordVerificationResult.Success, passwordHasher.VerifyHashedPassword(resetUser, resetUser.PasswordHash, "replacement-password"));
    }
}
