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
            catch (Exception ex) { _log.LogError(ex, "Retention pass failed"); }
            await Task.Delay(TimeSpan.FromHours(1), ct);
        }
    }

    public async Task RunOnceAsync(DateTimeOffset now, CancellationToken ct)
    {
        await using var db = await dbf.CreateDbContextAsync(ct);

        // SQLite's EF provider can't translate DateTimeOffset comparisons/GroupBy in SQL, so we
        // fetch the (small, at this data size) tables and filter/group client-side throughout.
        var allSamples = await db.Samples.ToListAsync(ct);
        var allRollups = await db.SampleRollups.ToListAsync(ct);

        // (1) roll raw samples older than 1h into minute buckets, skipping buckets already rolled up.
        var oneHourAgo = now.AddHours(-1);
        var oldSamples = allSamples.Where(s => s.Ts < oneHourAgo).ToList();
        if (oldSamples.Count > 0)
        {
            var existingMinuteBuckets = allRollups
                .Where(r => r.Granularity == "minute")
                .Select(r => r.Ts)
                .ToHashSet();

            foreach (var group in oldSamples.GroupBy(TruncateToMinute))
            {
                if (existingMinuteBuckets.Contains(group.Key)) continue;
                var rollup = new SampleRollup
                {
                    Ts = group.Key,
                    Granularity = "minute",
                    AvgPlayers = group.Average(s => s.Players),
                    MaxPlayers = group.Max(s => s.Players),
                    AvgFps = group.Average(s => s.Fps),
                    AvgMemoryBytes = (long)group.Average(s => s.MemoryBytes),
                };
                db.SampleRollups.Add(rollup);
                allRollups.Add(rollup); // keep in-memory view current for step 3's aggregation below
            }
            await db.SaveChangesAsync(ct);
        }

        // (2) delete raw samples older than 48h.
        var fortyEightHoursAgo = now.AddHours(-48);
        var expiredSamples = allSamples.Where(s => s.Ts < fortyEightHoursAgo).ToList();
        if (expiredSamples.Count > 0)
        {
            db.Samples.RemoveRange(expiredSamples);
            await db.SaveChangesAsync(ct);
        }

        // (3) roll minute rollups older than 48h into hour buckets, skipping buckets already rolled up.
        var oldMinuteRollups = allRollups
            .Where(r => r.Granularity == "minute" && r.Ts < fortyEightHoursAgo)
            .ToList();
        if (oldMinuteRollups.Count > 0)
        {
            var existingHourBuckets = allRollups
                .Where(r => r.Granularity == "hour")
                .Select(r => r.Ts)
                .ToHashSet();

            foreach (var group in oldMinuteRollups.GroupBy(r => TruncateToHour(r.Ts)))
            {
                if (existingHourBuckets.Contains(group.Key)) continue;
                db.SampleRollups.Add(new SampleRollup
                {
                    Ts = group.Key,
                    Granularity = "hour",
                    AvgPlayers = group.Average(r => r.AvgPlayers),
                    MaxPlayers = group.Max(r => r.MaxPlayers),
                    AvgFps = group.Average(r => r.AvgFps),
                    AvgMemoryBytes = (long)group.Average(r => r.AvgMemoryBytes),
                });
            }
            await db.SaveChangesAsync(ct);
        }

        // (4) delete minute rollups older than 30 days.
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

    private static DateTimeOffset TruncateToMinute(Sample s) => Truncate(s.Ts);
    private static DateTimeOffset Truncate(DateTimeOffset t) =>
        new(t.Year, t.Month, t.Day, t.Hour, t.Minute, 0, t.Offset);
    private static DateTimeOffset TruncateToHour(DateTimeOffset t) =>
        new(t.Year, t.Month, t.Day, t.Hour, 0, 0, t.Offset);
}
