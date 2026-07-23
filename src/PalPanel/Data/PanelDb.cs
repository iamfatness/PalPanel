using Microsoft.EntityFrameworkCore;

namespace PalPanel.Data;

public class PanelDb(DbContextOptions<PanelDb> options) : DbContext(options)
{
    public DbSet<Sample> Samples => Set<Sample>();
    public DbSet<SampleRollup> SampleRollups => Set<SampleRollup>();
    public DbSet<PlayerSession> Sessions => Set<PlayerSession>();
    public DbSet<EventLog> Events => Set<EventLog>();
    public DbSet<PanelUser> Users => Set<PanelUser>();
    public DbSet<Schedule> Schedules => Set<Schedule>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<Sample>().HasIndex(s => s.Ts);
        b.Entity<SampleRollup>().HasIndex(r => new { r.Granularity, r.Ts });
        b.Entity<PanelUser>().HasIndex(u => u.Email).IsUnique();
        b.Entity<PlayerSession>().HasIndex(s => s.UserId);
        b.Entity<EventLog>().HasIndex(e => e.Ts);
    }
}

public interface IEventSink
{
    Task LogAsync(string type, string detail, string? actorEmail = null);
}

public class DbEventSink(IDbContextFactory<PanelDb> factory) : IEventSink
{
    public async Task LogAsync(string type, string detail, string? actorEmail = null)
    {
        await using var db = await factory.CreateDbContextAsync();
        db.Events.Add(new EventLog { Ts = DateTimeOffset.UtcNow, Type = type, Detail = detail, ActorEmail = actorEmail });
        await db.SaveChangesAsync();
    }
}
