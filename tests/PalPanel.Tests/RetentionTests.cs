using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PalPanel.Data; using PalPanel.Monitoring;

public class RetentionTests
{
    [Fact]
    public async Task OldSamples_RolledUpAndPruned()
    {
        var services = new ServiceCollection();
        services.AddDbContextFactory<PanelDb>(b => b.UseSqlite($"Data Source={Path.GetTempFileName()}"));
        var dbf = services.BuildServiceProvider().GetRequiredService<IDbContextFactory<PanelDb>>();
        using (var db = dbf.CreateDbContext()) db.Database.EnsureCreated();

        var now = DateTimeOffset.Parse("2026-01-10T00:00:00Z");
        using (var db = dbf.CreateDbContext())
        {
            for (int i = 0; i < 6; i++)  // one minute of 10s samples, 3 days old
                db.Samples.Add(new Sample { Ts = now.AddDays(-3).AddSeconds(i * 10), Players = i, Fps = 60, MemoryBytes = 100, UptimeSeconds = i });
            db.Samples.Add(new Sample { Ts = now.AddMinutes(-5), Players = 9, Fps = 60, MemoryBytes = 100, UptimeSeconds = 1 }); // fresh, kept raw
            db.SaveChanges();
        }

        var svc = new RetentionService(dbf);
        await svc.RunOnceAsync(now, default);

        using var check = dbf.CreateDbContext();
        Assert.Equal(1, check.Samples.Count());                                  // only the fresh one survives
        var minute = check.SampleRollups.Single(r => r.Granularity == "minute");
        Assert.Equal(5, minute.MaxPlayers);
        Assert.Equal(2.5, minute.AvgPlayers);
    }

    [Fact]
    public async Task OldMinuteRollups_RolledUpToHour_MinuteRowsSurviveUntil30Days()
    {
        var services = new ServiceCollection();
        services.AddDbContextFactory<PanelDb>(b => b.UseSqlite($"Data Source={Path.GetTempFileName()}"));
        var dbf = services.BuildServiceProvider().GetRequiredService<IDbContextFactory<PanelDb>>();
        using (var db = dbf.CreateDbContext()) db.Database.EnsureCreated();

        var now = DateTimeOffset.Parse("2026-01-10T00:00:00Z");
        var hourStart = now.AddDays(-3).AddHours(-1); // 3 days old: older than 48h, younger than 30d
        using (var db = dbf.CreateDbContext())
        {
            // Two minute rollups within the same hour bucket.
            db.SampleRollups.Add(new SampleRollup { Ts = hourStart, Granularity = "minute", AvgPlayers = 2, MaxPlayers = 4, AvgFps = 60, AvgMemoryBytes = 100 });
            db.SampleRollups.Add(new SampleRollup { Ts = hourStart.AddMinutes(1), Granularity = "minute", AvgPlayers = 4, MaxPlayers = 8, AvgFps = 60, AvgMemoryBytes = 200 });
            db.SaveChanges();
        }

        var svc = new RetentionService(dbf);
        await svc.RunOnceAsync(now, default);

        using var check = dbf.CreateDbContext();
        var hour = check.SampleRollups.Single(r => r.Granularity == "hour");
        Assert.Equal(3, hour.AvgPlayers);      // avg of avgs: (2+4)/2
        Assert.Equal(8, hour.MaxPlayers);      // max of maxes
        Assert.Equal(2, check.SampleRollups.Count(r => r.Granularity == "minute")); // not yet 30 days old: survive
    }
}
