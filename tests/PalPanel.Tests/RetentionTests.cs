using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PalPanel.Data; using PalPanel.Monitoring;

public class RetentionTests
{
    private static IDbContextFactory<PanelDb> NewDb()
    {
        var services = new ServiceCollection();
        services.AddDbContextFactory<PanelDb>(b => b.UseSqlite($"Data Source={Path.GetTempFileName()}"));
        var dbf = services.BuildServiceProvider().GetRequiredService<IDbContextFactory<PanelDb>>();
        using (var db = dbf.CreateDbContext()) db.Database.EnsureCreated();
        return dbf;
    }

    [Fact]
    public async Task OldSamples_RolledUpAndPruned()
    {
        var dbf = NewDb();

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
        var dbf = NewDb();

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

    [Fact]
    public async Task StraddledMinuteBucket_NotRolledUpUntilComplete_ThenRolledUpWhole()
    {
        // Pins the partial-bucket data-loss trace: when the 1h cutoff lands mid-minute-bucket,
        // the bucket must NOT be rolled up from only its older samples (a later pass would then
        // skip the bucket as "already rolled up" and the remaining samples would eventually be
        // deleted unaggregated). Only complete buckets — bucket END <= cutoff — are eligible.
        var dbf = NewDb();

        var minuteStart = DateTimeOffset.Parse("2026-01-09T23:00:00Z");
        using (var db = dbf.CreateDbContext())
        {
            for (int i = 0; i < 6; i++)  // 6 samples spanning one minute bucket
                db.Samples.Add(new Sample { Ts = minuteStart.AddSeconds(i * 10), Players = i, Fps = 60, MemoryBytes = 100, UptimeSeconds = i });
            db.SaveChanges();
        }

        var svc = new RetentionService(dbf);

        // First pass: cutoff (now - 1h) = minuteStart + 30s — mid-bucket. 3 of 6 samples are
        // older than the cutoff, but the bucket is incomplete: no rollup may be created.
        var now1 = minuteStart.AddHours(1).AddSeconds(30);
        await svc.RunOnceAsync(now1, default);
        using (var check = dbf.CreateDbContext())
        {
            Assert.Empty(check.SampleRollups);          // no partial bucket
            Assert.Equal(6, check.Samples.Count());     // nothing deleted (< 48h)
        }

        // Second pass: now advanced; the bucket's end is past the cutoff. All 6 samples
        // must aggregate into the single minute rollup.
        var now2 = now1.AddMinutes(10);
        await svc.RunOnceAsync(now2, default);
        using (var check = dbf.CreateDbContext())
        {
            var minute = check.SampleRollups.Single(r => r.Granularity == "minute");
            Assert.Equal(minuteStart, minute.Ts);
            Assert.Equal(2.5, minute.AvgPlayers);       // (0+1+2+3+4+5)/6 — all six, not the older three
            Assert.Equal(5, minute.MaxPlayers);
        }
    }

    [Fact]
    public async Task RunOnceTwice_SameNow_IsIdempotent()
    {
        var dbf = NewDb();

        var now = DateTimeOffset.Parse("2026-01-10T00:00:00Z");
        using (var db = dbf.CreateDbContext())
        {
            for (int i = 0; i < 6; i++)  // one complete minute bucket, 3 days old
                db.Samples.Add(new Sample { Ts = now.AddDays(-3).AddSeconds(i * 10), Players = i, Fps = 60, MemoryBytes = 100, UptimeSeconds = i });
            // Two minute rollups in one complete hour bucket, 3 days old
            var hourStart = now.AddDays(-3).AddHours(-2);
            db.SampleRollups.Add(new SampleRollup { Ts = hourStart, Granularity = "minute", AvgPlayers = 2, MaxPlayers = 4, AvgFps = 60, AvgMemoryBytes = 100 });
            db.SampleRollups.Add(new SampleRollup { Ts = hourStart.AddMinutes(1), Granularity = "minute", AvgPlayers = 4, MaxPlayers = 8, AvgFps = 60, AvgMemoryBytes = 200 });
            db.SaveChanges();
        }

        var svc = new RetentionService(dbf);
        await svc.RunOnceAsync(now, default);

        int minuteCount, hourCount, sampleCount;
        using (var check = dbf.CreateDbContext())
        {
            minuteCount = check.SampleRollups.Count(r => r.Granularity == "minute");
            hourCount = check.SampleRollups.Count(r => r.Granularity == "hour");
            sampleCount = check.Samples.Count();
        }

        await svc.RunOnceAsync(now, default);   // same now: must be a no-op

        using (var check = dbf.CreateDbContext())
        {
            Assert.Equal(minuteCount, check.SampleRollups.Count(r => r.Granularity == "minute"));
            Assert.Equal(hourCount, check.SampleRollups.Count(r => r.Granularity == "hour"));
            Assert.Equal(sampleCount, check.Samples.Count());
            // no duplicate buckets at any granularity
            Assert.Equal(check.SampleRollups.Count(),
                check.SampleRollups.AsEnumerable().Select(r => (r.Granularity, r.Ts)).Distinct().Count());
        }
    }
}
