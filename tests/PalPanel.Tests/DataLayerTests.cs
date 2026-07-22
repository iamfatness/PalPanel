using Microsoft.EntityFrameworkCore;
using PalPanel.Data;

public class DataLayerTests
{
    private static PanelDb NewDb()
    {
        var opts = new DbContextOptionsBuilder<PanelDb>()
            .UseSqlite($"Data Source={Path.GetTempFileName()}").Options;
        var db = new PanelDb(opts);
        db.Database.EnsureCreated();
        return db;
    }

    [Fact]
    public void CanRoundTripAllEntities()
    {
        using var db = NewDb();
        db.Samples.Add(new Sample { Ts = DateTimeOffset.UtcNow, Players = 3, Fps = 58, FrameTimeMs = 16.7, MemoryBytes = 1_000, UptimeSeconds = 60 });
        db.Events.Add(new EventLog { Ts = DateTimeOffset.UtcNow, Type = "start", Detail = "test", ActorEmail = "a@b.c" });
        db.Users.Add(new PanelUser { Email = "a@b.c", Role = "Admin", FirstSeen = DateTimeOffset.UtcNow, LastSeen = DateTimeOffset.UtcNow });
        db.Sessions.Add(new PlayerSession { UserId = "steam_1", Name = "Alice", JoinedAt = DateTimeOffset.UtcNow });
        db.Schedules.Add(new Schedule { Cron = "0 4 * * *", Action = "restart" });
        db.SaveChanges();
        Assert.Single(db.Samples); Assert.Single(db.Events); Assert.Single(db.Users);
        Assert.Single(db.Sessions); Assert.Single(db.Schedules);
    }
}
