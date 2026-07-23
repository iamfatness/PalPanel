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

    [Fact]
    public void CanRoundTripPanelUserAuthFields()
    {
        using var db = NewDb();
        var now = DateTimeOffset.UtcNow;
        var lockedUntil = now.AddHours(1);
        db.Users.Add(new PanelUser
        {
            Email = "auth@test.com",
            Role = "Admin",
            FirstSeen = now,
            LastSeen = now,
            PasswordHash = "bcrypt$2b$12$...",
            MustChangePassword = true,
            FailedLoginCount = 3,
            LockedUntil = lockedUntil
        });
        db.SaveChanges();

        // Reload from db
        db.ChangeTracker.Clear();
        var user = db.Users.Single(u => u.Email == "auth@test.com");

        Assert.Equal("bcrypt$2b$12$...", user.PasswordHash);
        Assert.True(user.MustChangePassword);
        Assert.Equal(3, user.FailedLoginCount);
        Assert.Equal(lockedUntil, user.LockedUntil);
    }
}
