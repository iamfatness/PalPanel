using Microsoft.EntityFrameworkCore;
using PalPanel.Auth;

namespace PalPanel.Data;

// One-time upgrade path from single-server (config-file) PalPanel to multi-server (DB-backed).
// On first startup after the upgrade, if no ServerConfig rows exist, seed exactly one from the
// legacy PanelOptions and stamp every pre-existing per-server row (ServerId == Guid.Empty) to
// that new server's id, so history/sessions/schedules carry over with zero data loss.
// Idempotent: once any server exists it returns that server's id and changes nothing.
public static class LegacyServerMigration
{
    public static async Task<Guid> EnsureSeededAsync(PanelDb db, PanelOptions legacy, ISecretProtector protector)
    {
        var existing = await db.Servers.OrderBy(s => s.Name).FirstOrDefaultAsync();
        if (existing is not null) return existing.Id;

        var server = new ServerConfig
        {
            Id = Guid.NewGuid(),
            Name = "Main",
            ExePath = legacy.ServerExePath,
            WorkingDir = Path.GetDirectoryName(legacy.ServerExePath) ?? "",
            LaunchArgs = legacy.ServerArgs,
            ProcessName = legacy.ServerProcessName,
            SaveDirectory = legacy.SaveDirectory,
            BackupDirectory = legacy.BackupDirectory,
            BackupsToKeep = legacy.BackupsToKeep,
            ApiBaseUrl = legacy.ApiBaseUrl,
            AdminPasswordEnc = protector.Protect(legacy.AdminPassword),
            GracefulStopTimeoutSeconds = legacy.GracefulStopTimeoutSeconds,
            CrashWindowMinutes = legacy.CrashWindowMinutes,
            MaxCrashesInWindow = legacy.MaxCrashesInWindow,
            PollIntervalSeconds = legacy.PollIntervalSeconds,
            AutoRestart = true,
            Enabled = true,
        };
        db.Servers.Add(server);
        await db.SaveChangesAsync();

        // Stamp legacy rows (written before ServerId existed => default Guid.Empty) onto the
        // seeded server. ExecuteUpdate issues a single UPDATE per table, no entity tracking.
        var id = server.Id;
        await db.Samples.Where(x => x.ServerId == Guid.Empty).ExecuteUpdateAsync(s => s.SetProperty(x => x.ServerId, id));
        await db.SampleRollups.Where(x => x.ServerId == Guid.Empty).ExecuteUpdateAsync(s => s.SetProperty(x => x.ServerId, id));
        await db.Sessions.Where(x => x.ServerId == Guid.Empty).ExecuteUpdateAsync(s => s.SetProperty(x => x.ServerId, id));
        await db.Events.Where(x => x.ServerId == Guid.Empty).ExecuteUpdateAsync(s => s.SetProperty(x => x.ServerId, id));
        await db.Schedules.Where(x => x.ServerId == Guid.Empty).ExecuteUpdateAsync(s => s.SetProperty(x => x.ServerId, id));
        return id;
    }
}
