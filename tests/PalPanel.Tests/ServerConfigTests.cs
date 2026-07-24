using Microsoft.EntityFrameworkCore;
using PalPanel.Data;

public class ServerConfigTests
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
    public void DefaultsAreSensible()
    {
        var cfg = new ServerConfig();
        Assert.True(cfg.Enabled);
        Assert.True(cfg.AutoRestart);
        Assert.Equal(20, cfg.BackupsToKeep);
        Assert.Equal(10, cfg.PollIntervalSeconds);
        Assert.NotEqual(Guid.Empty, cfg.Id);
    }

    [Fact]
    public void RoundTripsThroughDb()
    {
        using var db = NewDb();
        var id = Guid.NewGuid();
        db.Servers.Add(new ServerConfig
        {
            Id = id, Name = "Main", ExePath = @"C:\PalServer\PalServer.exe",
            ApiBaseUrl = "http://localhost:8212", AdminPasswordEnc = "enc-blob",
            Enabled = true
        });
        db.SaveChanges();
        db.ChangeTracker.Clear();

        var loaded = db.Servers.Single(s => s.Id == id);
        Assert.Equal("Main", loaded.Name);
        Assert.Equal("enc-blob", loaded.AdminPasswordEnc);
    }

    [Fact]
    public void PerServerRowsCarryServerId()
    {
        using var db = NewDb();
        var sid = Guid.NewGuid();
        db.Samples.Add(new Sample { ServerId = sid, Ts = DateTimeOffset.UtcNow, Players = 1 });
        db.Events.Add(new EventLog { ServerId = sid, Ts = DateTimeOffset.UtcNow, Type = "t", Detail = "d" });
        db.Sessions.Add(new PlayerSession { ServerId = sid, UserId = "u", Name = "n", JoinedAt = DateTimeOffset.UtcNow });
        db.Schedules.Add(new Schedule { ServerId = sid, Cron = "0 4 * * *", Action = "restart" });
        db.SaveChanges();
        db.ChangeTracker.Clear();

        Assert.Equal(sid, db.Samples.Single().ServerId);
        Assert.Equal(sid, db.Events.Single().ServerId);
        Assert.Equal(sid, db.Sessions.Single().ServerId);
        Assert.Equal(sid, db.Schedules.Single().ServerId);
    }
}
