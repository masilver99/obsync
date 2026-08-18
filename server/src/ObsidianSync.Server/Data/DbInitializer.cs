using System.Data;
using Microsoft.EntityFrameworkCore;

namespace ObsidianSync.Server.Data;

public static class DbInitializer
{
    public static async Task InitializeAsync(SyncDbContext db, CancellationToken cancellationToken = default)
    {
        await db.Database.EnsureCreatedAsync(cancellationToken);
        await EnsureUsersAdminColumnAsync(db, cancellationToken);
        await EnsureSyncSessionsTableAsync(db, cancellationToken);
        await db.Database.ExecuteSqlRawAsync("PRAGMA journal_mode=WAL;", cancellationToken);
        await db.Database.ExecuteSqlRawAsync("PRAGMA foreign_keys=ON;", cancellationToken);
    }

    private static async Task EnsureUsersAdminColumnAsync(SyncDbContext db, CancellationToken cancellationToken)
    {
        if (await HasColumnAsync(db, "Users", "IsAdmin", cancellationToken))
        {
            return;
        }

        await db.Database.ExecuteSqlRawAsync(
            "ALTER TABLE \"Users\" ADD COLUMN \"IsAdmin\" INTEGER NOT NULL DEFAULT 0;",
            cancellationToken);
    }

    private static async Task EnsureSyncSessionsTableAsync(SyncDbContext db, CancellationToken cancellationToken)
    {
        await db.Database.ExecuteSqlRawAsync(
            """
            CREATE TABLE IF NOT EXISTS "SyncSessions" (
                "Id" TEXT NOT NULL CONSTRAINT "PK_SyncSessions" PRIMARY KEY,
                "UserId" TEXT NOT NULL,
                "DeviceId" TEXT NOT NULL,
                "VaultId" TEXT NOT NULL,
                "StartedUtc" TEXT NOT NULL,
                "CompletedUtc" TEXT NULL,
                "Status" TEXT NOT NULL,
                "LastKnownRevision" INTEGER NOT NULL,
                "ErrorMessage" TEXT NULL
            );
            CREATE INDEX IF NOT EXISTS "IX_SyncSessions_VaultId_CompletedUtc"
                ON "SyncSessions" ("VaultId", "CompletedUtc");
            CREATE INDEX IF NOT EXISTS "IX_SyncSessions_UserId_StartedUtc"
                ON "SyncSessions" ("UserId", "StartedUtc");
            """,
            cancellationToken);
    }

    private static async Task<bool> HasColumnAsync(
        SyncDbContext db,
        string tableName,
        string columnName,
        CancellationToken cancellationToken)
    {
        var connection = db.Database.GetDbConnection();
        if (connection.State != ConnectionState.Open)
        {
            await connection.OpenAsync(cancellationToken);
        }

        await using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA table_info(\"{tableName}\");";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            if (string.Equals(reader.GetString(1), columnName, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
