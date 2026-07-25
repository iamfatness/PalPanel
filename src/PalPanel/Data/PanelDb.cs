using Microsoft.EntityFrameworkCore;

namespace PalPanel.Data;

public class PanelDb(DbContextOptions<PanelDb> options) : DbContext(options)
{
    public DbSet<ServerConfig> Servers => Set<ServerConfig>();
    public DbSet<Sample> Samples => Set<Sample>();
    public DbSet<SampleRollup> SampleRollups => Set<SampleRollup>();
    public DbSet<PlayerSession> Sessions => Set<PlayerSession>();
    public DbSet<BannedPlayer> BannedPlayers => Set<BannedPlayer>();
    public DbSet<EventLog> Events => Set<EventLog>();
    public DbSet<PanelUser> Users => Set<PanelUser>();
    public DbSet<Schedule> Schedules => Set<Schedule>();
    public DbSet<Alert> Alerts => Set<Alert>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        // Per-server rows are almost always queried scoped to one server and ordered/keyed
        // within it, so the ServerId-leading composite indexes match the real access paths.
        b.Entity<Sample>().HasIndex(s => new { s.ServerId, s.Ts });
        b.Entity<SampleRollup>().HasIndex(r => new { r.ServerId, r.Granularity, r.Ts });
        b.Entity<PanelUser>().HasIndex(u => u.Email).IsUnique();
        b.Entity<PlayerSession>().HasIndex(s => new { s.ServerId, s.UserId });
        b.Entity<BannedPlayer>().HasIndex(x => new { x.ServerId, x.UserId }).IsUnique();
        b.Entity<EventLog>().HasIndex(e => new { e.ServerId, e.Ts });
        b.Entity<Schedule>().HasIndex(s => s.ServerId);
        // The alerts feed sorts newest-first and the badge counts un-acknowledged; dedup/resolve
        // looks up the active alert per (ServerId, Key).
        b.Entity<Alert>().HasIndex(a => new { a.ServerId, a.Key, a.ResolvedAt });
    }
}

public interface IEventSink
{
    Task LogAsync(string type, string detail, string? actorEmail = null);
}

// Panel-level (not server-scoped) events; ServerId stays Guid.Empty.
public class DbEventSink(IDbContextFactory<PanelDb> factory) : IEventSink
{
    public async Task LogAsync(string type, string detail, string? actorEmail = null)
    {
        await using var db = await factory.CreateDbContextAsync();
        db.Events.Add(new EventLog { Ts = DateTimeOffset.UtcNow, Type = type, Detail = detail, ActorEmail = actorEmail });
        await db.SaveChangesAsync();
    }
}

// Server-scoped events: every row is stamped with the owning server's id so History can be
// filtered per server. One instance per ServerRuntime.
public class ServerEventSink(IDbContextFactory<PanelDb> factory, Guid serverId) : IEventSink
{
    public async Task LogAsync(string type, string detail, string? actorEmail = null)
    {
        await using var db = await factory.CreateDbContextAsync();
        db.Events.Add(new EventLog { ServerId = serverId, Ts = DateTimeOffset.UtcNow, Type = type, Detail = detail, ActorEmail = actorEmail });
        await db.SaveChangesAsync();
    }
}
