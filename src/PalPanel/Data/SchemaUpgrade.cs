using Microsoft.EntityFrameworkCore;

namespace PalPanel.Data;

// EnsureCreated() builds the full schema on a FRESH database (new installs, tests) but does
// nothing to an existing one — so upgrading a database created before multi-server would leave
// it missing the Servers table and the ServerId columns. This idempotent patch adds exactly
// those, so an existing single-server palpanel.db upgrades cleanly with zero data loss. Safe to
// run every startup: CREATE TABLE IF NOT EXISTS and column-existence guards make it a no-op once
// applied (and on fresh DBs where EnsureCreated already created everything).
public static class SchemaUpgrade
{
    private static readonly (string Table, string Column, string Type, string Default)[] ServerIdColumns =
    {
        ("Samples", "ServerId", "TEXT", "'00000000-0000-0000-0000-000000000000'"),
        ("SampleRollups", "ServerId", "TEXT", "'00000000-0000-0000-0000-000000000000'"),
        ("Sessions", "ServerId", "TEXT", "'00000000-0000-0000-0000-000000000000'"),
        ("Events", "ServerId", "TEXT", "'00000000-0000-0000-0000-000000000000'"),
        ("Schedules", "ServerId", "TEXT", "'00000000-0000-0000-0000-000000000000'"),
        // CPU metrics added later; default 0 for existing rows.
        ("Samples", "Cpu", "REAL", "0"),
        ("SampleRollups", "AvgCpu", "REAL", "0"),
        // Health-based auto-restart config (0 = off).
        ("Servers", "AutoRestartUnreachableMinutes", "INTEGER", "0"),
        ("Servers", "AutoRestartMemoryGb", "REAL", "0"),
        // SteamCMD app id for updates (Palworld Dedicated Server default).
        ("Servers", "SteamAppId", "INTEGER", "2394010"),
        // Optional public hostname players connect to, for the reachability/DNS check.
        ("Servers", "PublicHostname", "TEXT", "''"),
    };

    public static async Task ApplyAsync(PanelDb db, CancellationToken ct = default)
    {
        // BannedPlayers table (added after multi-server; created here for existing DBs).
        await db.Database.ExecuteSqlRawAsync("""
            CREATE TABLE IF NOT EXISTS "BannedPlayers" (
                "Id" INTEGER NOT NULL CONSTRAINT "PK_BannedPlayers" PRIMARY KEY AUTOINCREMENT,
                "ServerId" TEXT NOT NULL,
                "UserId" TEXT NOT NULL DEFAULT '',
                "Name" TEXT NOT NULL DEFAULT '',
                "Reason" TEXT NOT NULL DEFAULT '',
                "BannedBy" TEXT NULL,
                "BannedAt" TEXT NOT NULL
            );
            """, ct);

        // Alerts table (crash/health alerting; added after multi-server).
        await db.Database.ExecuteSqlRawAsync("""
            CREATE TABLE IF NOT EXISTS "Alerts" (
                "Id" INTEGER NOT NULL CONSTRAINT "PK_Alerts" PRIMARY KEY AUTOINCREMENT,
                "ServerId" TEXT NULL,
                "ServerName" TEXT NOT NULL DEFAULT '',
                "Key" TEXT NOT NULL DEFAULT '',
                "Severity" INTEGER NOT NULL DEFAULT 0,
                "Title" TEXT NOT NULL DEFAULT '',
                "Detail" TEXT NOT NULL DEFAULT '',
                "CreatedAt" TEXT NOT NULL,
                "UpdatedAt" TEXT NOT NULL,
                "ResolvedAt" TEXT NULL,
                "Acknowledged" INTEGER NOT NULL DEFAULT 0
            );
            """, ct);

        // AlertSettings table (single-row panel email config; password stored encrypted).
        await db.Database.ExecuteSqlRawAsync("""
            CREATE TABLE IF NOT EXISTS "AlertSettings" (
                "Id" INTEGER NOT NULL CONSTRAINT "PK_AlertSettings" PRIMARY KEY AUTOINCREMENT,
                "EmailEnabled" INTEGER NOT NULL DEFAULT 0,
                "SmtpHost" TEXT NOT NULL DEFAULT 'smtp.gmail.com',
                "SmtpPort" INTEGER NOT NULL DEFAULT 587,
                "SmtpUser" TEXT NOT NULL DEFAULT '',
                "SmtpPasswordEnc" TEXT NOT NULL DEFAULT '',
                "From" TEXT NOT NULL DEFAULT '',
                "To" TEXT NOT NULL DEFAULT ''
            );
            """, ct);

        // Servers table (EF maps Guid/string -> TEXT, int -> INTEGER, bool -> INTEGER).
        await db.Database.ExecuteSqlRawAsync("""
            CREATE TABLE IF NOT EXISTS "Servers" (
                "Id" TEXT NOT NULL CONSTRAINT "PK_Servers" PRIMARY KEY,
                "Name" TEXT NOT NULL DEFAULT '',
                "ExePath" TEXT NOT NULL DEFAULT '',
                "WorkingDir" TEXT NOT NULL DEFAULT '',
                "LaunchArgs" TEXT NOT NULL DEFAULT '',
                "ProcessName" TEXT NOT NULL DEFAULT '',
                "SaveDirectory" TEXT NOT NULL DEFAULT '',
                "BackupDirectory" TEXT NOT NULL DEFAULT '',
                "BackupsToKeep" INTEGER NOT NULL DEFAULT 20,
                "ApiBaseUrl" TEXT NOT NULL DEFAULT '',
                "AdminPasswordEnc" TEXT NOT NULL DEFAULT '',
                "GracefulStopTimeoutSeconds" INTEGER NOT NULL DEFAULT 60,
                "CrashWindowMinutes" INTEGER NOT NULL DEFAULT 10,
                "MaxCrashesInWindow" INTEGER NOT NULL DEFAULT 3,
                "PollIntervalSeconds" INTEGER NOT NULL DEFAULT 10,
                "AutoRestart" INTEGER NOT NULL DEFAULT 1,
                "Enabled" INTEGER NOT NULL DEFAULT 1
            );
            """, ct);

        foreach (var (table, column, type, def) in ServerIdColumns)
        {
            // Skip a table that doesn't exist (defensive: a partial/older schema). On a fresh DB
            // EnsureCreated already created the table WITH the column, so the column check skips it.
            if (!await TableExistsAsync(db, table, ct)) continue;
            if (!await ColumnExistsAsync(db, table, column, ct))
            {
                // SQLite requires a constant default when adding a NOT NULL column to a table
                // that may already have rows; Guid.Empty is the sentinel LegacyServerMigration
                // then stamps onto the seeded server.
                await db.Database.ExecuteSqlRawAsync(
                    $"ALTER TABLE \"{table}\" ADD COLUMN \"{column}\" {type} NOT NULL DEFAULT {def};", ct);
            }
        }
    }

    private static async Task<bool> TableExistsAsync(PanelDb db, string table, CancellationToken ct) =>
        await ScalarCountAsync(db, $"SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='{table}';", ct) > 0;

    private static async Task<bool> ColumnExistsAsync(PanelDb db, string table, string column, CancellationToken ct) =>
        await ScalarCountAsync(db, $"SELECT COUNT(*) FROM pragma_table_info('{table}') WHERE name = '{column}';", ct) > 0;

    private static async Task<long> ScalarCountAsync(PanelDb db, string sql, CancellationToken ct)
    {
        var conn = db.Database.GetDbConnection();
        if (conn.State != System.Data.ConnectionState.Open) await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        return Convert.ToInt64(await cmd.ExecuteScalarAsync(ct));
    }
}
