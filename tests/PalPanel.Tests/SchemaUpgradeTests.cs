using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using PalPanel;
using PalPanel.Auth;
using PalPanel.Data;

public class SchemaUpgradeTests
{
    private class IdentityProtector : ISecretProtector
    {
        public string Protect(string p) => p;
        public string Unprotect(string c) => c;
    }

    // Build a database with the OLD (pre-multi-server) schema: Samples/Events without ServerId
    // and no Servers table, seeded with a legacy row.
    private static string MakeLegacyDb()
    {
        var path = Path.GetTempFileName();
        using var conn = new SqliteConnection($"Data Source={path}");
        conn.Open();
        void Exec(string sql) { using var c = conn.CreateCommand(); c.CommandText = sql; c.ExecuteNonQuery(); }
        // Mirror the real pre-multi-server schema: all per-server tables present, none with ServerId.
        Exec("""CREATE TABLE "Samples" ("Id" INTEGER NOT NULL CONSTRAINT "PK_Samples" PRIMARY KEY AUTOINCREMENT, "Ts" TEXT NOT NULL, "Players" INTEGER NOT NULL, "Fps" REAL NOT NULL, "FrameTimeMs" REAL NOT NULL, "MemoryBytes" INTEGER NOT NULL, "UptimeSeconds" INTEGER NOT NULL);""");
        Exec("""CREATE TABLE "SampleRollups" ("Id" INTEGER NOT NULL CONSTRAINT "PK_SampleRollups" PRIMARY KEY AUTOINCREMENT, "Ts" TEXT NOT NULL, "Granularity" TEXT NOT NULL, "AvgPlayers" REAL NOT NULL, "MaxPlayers" INTEGER NOT NULL, "AvgFps" REAL NOT NULL, "AvgMemoryBytes" INTEGER NOT NULL);""");
        Exec("""CREATE TABLE "Sessions" ("Id" INTEGER NOT NULL CONSTRAINT "PK_Sessions" PRIMARY KEY AUTOINCREMENT, "UserId" TEXT NOT NULL, "Name" TEXT NOT NULL, "JoinedAt" TEXT NOT NULL, "LeftAt" TEXT NULL);""");
        Exec("""CREATE TABLE "Events" ("Id" INTEGER NOT NULL CONSTRAINT "PK_Events" PRIMARY KEY AUTOINCREMENT, "Ts" TEXT NOT NULL, "Type" TEXT NOT NULL, "Detail" TEXT NOT NULL, "ActorEmail" TEXT NULL);""");
        Exec("""CREATE TABLE "Schedules" ("Id" INTEGER NOT NULL CONSTRAINT "PK_Schedules" PRIMARY KEY AUTOINCREMENT, "Cron" TEXT NOT NULL, "Action" TEXT NOT NULL, "Parameters" TEXT NULL, "Enabled" INTEGER NOT NULL);""");
        Exec("INSERT INTO \"Samples\" (\"Ts\",\"Players\",\"Fps\",\"FrameTimeMs\",\"MemoryBytes\",\"UptimeSeconds\") VALUES ('2026-07-01T00:00:00+00:00', 4, 60, 16.6, 1000, 120);");
        Exec("INSERT INTO \"Events\" (\"Ts\",\"Type\",\"Detail\") VALUES ('2026-07-01T00:00:00+00:00', 'start', 'legacy');");
        Exec("INSERT INTO \"Schedules\" (\"Cron\",\"Action\",\"Enabled\") VALUES ('0 4 * * *', 'restart', 1);");
        return path;
    }

    private static PanelDb Open(string path)
    {
        var opts = new DbContextOptionsBuilder<PanelDb>().UseSqlite($"Data Source={path}").Options;
        return new PanelDb(opts);
    }

    [Fact]
    public async Task UpgradesLegacyDb_AddsServersTableAndServerIdColumns()
    {
        var path = MakeLegacyDb();
        await using (var db = Open(path))
        {
            await SchemaUpgrade.ApplyAsync(db);
        }

        // After the patch, EF can query the new shape and the seed migration can stamp rows.
        await using (var db = Open(path))
        {
            var id = await LegacyServerMigration.EnsureSeededAsync(db, new PanelOptions { ServerExePath = @"C:\P\P.exe", ApiBaseUrl = "http://localhost:8212", AdminPassword = "pw" }, new IdentityProtector());
            db.ChangeTracker.Clear();

            Assert.Single(db.Servers);
            Assert.Equal(id, db.Samples.Single().ServerId);   // legacy row stamped
            Assert.Equal(id, db.Events.Single().ServerId);
        }
    }

    [Fact]
    public async Task IsIdempotent_RunningTwiceIsSafe()
    {
        var path = MakeLegacyDb();
        await using var db = Open(path);
        await SchemaUpgrade.ApplyAsync(db);
        await SchemaUpgrade.ApplyAsync(db);   // must not throw (columns/table already present)

        Assert.Equal(1, await db.Samples.CountAsync());
    }
}
