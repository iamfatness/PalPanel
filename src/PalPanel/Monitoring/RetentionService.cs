using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using PalPanel.Data;
namespace PalPanel.Monitoring;

public class RetentionService(IDbContextFactory<PanelDb> dbf, ILogger<RetentionService>? log = null) : BackgroundService
{
    private readonly ILogger _log = log ?? NullLogger<RetentionService>.Instance;

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { await RunOnceAsync(DateTimeOffset.UtcNow, ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; } // shutdown, not an error
            catch (Exception ex) { _log.LogError(ex, "Retention pass failed"); }
            try { await Task.Delay(TimeSpan.FromHours(1), ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
        }
    }

    public async Task RunOnceAsync(DateTimeOffset now, CancellationToken ct)
    {
        await using var db = await dbf.CreateDbContextAsync(ct);

        // SQLite's EF provider can't translate DateTimeOffset comparisons/GroupBy in SQL, so we
        // fetch the tables and filter/group client-side throughout. Raw samples are capped at
        // 48 h by rule (2) and minute rollups at 30 d by rule (4); hour rollups grow unbounded
        // but only ~9k rows/year, so full fetches stay trivially small for years of retention.
        var allSamples = await db.Samples.ToListAsync(ct);
        var allRollups = await db.SampleRollups.ToListAsync(ct);

        // Only COMPLETE buckets are ever rolled up: a bucket is eligible when its END is at or
        // before the cutoff. Rolling a bucket the cutoff splits mid-way would aggregate only its
        // older samples, and the skip-existing check would then permanently ignore the rest —
        // which rule (2)/(4) would later delete unaggregated.

        // (1) roll raw samples into minute buckets whose end is past the 1h line,
        //     skipping buckets already rolled up.
        var minuteLine = now.AddHours(-1);
        // Buckets are keyed by (server, time) so each server rolls up independently — otherwise
        // per-server history queries (which filter by ServerId) find nothing beyond raw samples.
        var existingMinuteBuckets = allRollups
            .Where(r => r.Granularity == "minute")
            .Select(r => (r.ServerId, r.Ts))
            .ToHashSet();
        foreach (var group in allSamples.GroupBy(s => (s.ServerId, Bucket: TruncateToMinute(s.Ts))))
        {
            if (group.Key.Bucket.AddMinutes(1) > minuteLine) continue;                     // incomplete bucket: wait
            if (existingMinuteBuckets.Contains((group.Key.ServerId, group.Key.Bucket))) continue; // already rolled up
            var rollup = new SampleRollup
            {
                ServerId = group.Key.ServerId,
                Ts = group.Key.Bucket,
                Granularity = "minute",
                AvgPlayers = group.Average(s => s.Players),
                MaxPlayers = group.Max(s => s.Players),
                AvgFps = group.Average(s => s.Fps),
                AvgMemoryBytes = (long)Math.Round(group.Average(s => s.MemoryBytes)),
                AvgCpu = group.Average(s => s.Cpu),
            };
            db.SampleRollups.Add(rollup);
            allRollups.Add(rollup); // keep in-memory view current for step 3's aggregation below
        }
        await db.SaveChangesAsync(ct);

        // (2) delete raw samples older than 48h. This can never outrun step (1): any sample
        // older than 48h sits in a bucket whose end is at most (now-48h)+1min, far past the
        // 1h rollup line, so it was already aggregated by step (1) earlier in this same pass.
        var fortyEightHoursAgo = now.AddHours(-48);
        var expiredSamples = allSamples.Where(s => s.Ts < fortyEightHoursAgo).ToList();
        if (expiredSamples.Count > 0)
        {
            db.Samples.RemoveRange(expiredSamples);
            await db.SaveChangesAsync(ct);
        }

        // (3) roll minute rollups into hour buckets whose end is past the 48h line,
        //     skipping buckets already rolled up.
        var existingHourBuckets = allRollups
            .Where(r => r.Granularity == "hour")
            .Select(r => (r.ServerId, r.Ts))
            .ToHashSet();
        foreach (var group in allRollups.Where(r => r.Granularity == "minute").GroupBy(r => (r.ServerId, Bucket: TruncateToHour(r.Ts))))
        {
            if (group.Key.Bucket.AddHours(1) > fortyEightHoursAgo) continue;                 // incomplete bucket: wait
            if (existingHourBuckets.Contains((group.Key.ServerId, group.Key.Bucket))) continue; // already rolled up
            db.SampleRollups.Add(new SampleRollup
            {
                ServerId = group.Key.ServerId,
                Ts = group.Key.Bucket,
                Granularity = "hour",
                AvgPlayers = group.Average(r => r.AvgPlayers),
                MaxPlayers = group.Max(r => r.MaxPlayers),
                AvgFps = group.Average(r => r.AvgFps),
                AvgMemoryBytes = (long)Math.Round(group.Average(r => r.AvgMemoryBytes)),
                AvgCpu = group.Average(r => r.AvgCpu),
            });
        }
        await db.SaveChangesAsync(ct);

        // (4) delete minute rollups older than 30 days. Same non-outrunning argument as (2):
        // a minute rollup older than 30d is in an hour bucket ending at most (now-30d)+1h,
        // far past the 48h line, so step (3) already rolled it into an hour bucket above.
        var thirtyDaysAgo = now.AddDays(-30);
        var expiredMinuteRollups = allRollups
            .Where(r => r.Granularity == "minute" && r.Ts < thirtyDaysAgo)
            .ToList();
        if (expiredMinuteRollups.Count > 0)
        {
            db.SampleRollups.RemoveRange(expiredMinuteRollups);
            await db.SaveChangesAsync(ct);
        }
    }

    private static DateTimeOffset TruncateToMinute(DateTimeOffset t) =>
        new(t.Year, t.Month, t.Day, t.Hour, t.Minute, 0, t.Offset);
    private static DateTimeOffset TruncateToHour(DateTimeOffset t) =>
        new(t.Year, t.Month, t.Day, t.Hour, 0, 0, t.Offset);
}
