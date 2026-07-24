using Cronos;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PalPanel.Data;
using PalPanel.Servers;
namespace PalPanel.Control;

// Checks enabled Schedule rows once a minute and fires the corresponding action (restart
// ritual or on-demand backup) at most once per due minute, against the runtime named by the
// schedule's ServerId. Schedules whose server is gone/disabled are logged and skipped.
public class SchedulerService(
    IDbContextFactory<PanelDb> dbf,
    IServerRegistry registry,
    IEventSink events,
    ILogger<SchedulerService>? logger = null,
    DateTimeOffset? initialLastCheck = null) : BackgroundService
{
    private readonly ILogger _log = logger ?? NullLogger<SchedulerService>.Instance;

    // In-memory cursor of "last time schedules were checked", seeded to real startup time in
    // production (the optional ctor param exists purely so tests can control the fire window
    // without depending on wall-clock time). There is no persisted "last fired" state, so a
    // schedule whose due time falls entirely within panel downtime is simply missed — accepted
    // v1 behavior; a v2 could persist this to survive restarts.
    private DateTimeOffset _lastCheck = initialLastCheck ?? DateTimeOffset.UtcNow;
    private readonly Dictionary<int, DateTimeOffset> _lastFired = new();
    private readonly HashSet<int> _invalidCronWarned = new();

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { await CheckDueAsync(DateTimeOffset.UtcNow, ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; } // shutdown, not an error
            catch (Exception ex) { _log.LogError(ex, "Scheduler pass failed"); }
            try { await Task.Delay(TimeSpan.FromSeconds(60), ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
        }
    }

    // Sequential awaits by design: a due restart fires its full ritual inline, so the 10+
    // minutes of warning delays pause this loop — and every other schedule's evaluation —
    // until the ritual completes. Accepted v1 tradeoff for a single-server panel: concurrent
    // firing would only serialize on the orchestrator's lifecycle gate anyway, and any
    // occurrence that came due during the pause still fires on the next pass (its occurrence
    // time is <= the new `now` and > last-fired); multiple missed occurrences of one schedule
    // collapse into a single late firing.
    public async Task CheckDueAsync(DateTimeOffset now, CancellationToken ct)
    {
        var checkedFrom = _lastCheck;
        await using var db = await dbf.CreateDbContextAsync(ct);
        var schedules = await db.Schedules.Where(s => s.Enabled).ToListAsync(ct);

        foreach (var s in schedules)
        {
            try { await RunIfDueAsync(s, checkedFrom, now, ct); }
            catch (Exception ex)
            {
                // Loud, never fatal to the loop: one misbehaving schedule (bad DB row, a
                // throwing orchestrator/backup call, ...) must not block every other schedule.
                await events.LogAsync("schedule-error", $"Schedule {s.Id} ({s.Cron}/{s.Action}) failed: {ex.Message}");
            }
        }

        _lastCheck = now; // advance the watched window regardless of what fired this pass
    }

    private async Task RunIfDueAsync(Schedule s, DateTimeOffset checkedFrom, DateTimeOffset now, CancellationToken ct)
    {
        CronExpression cron;
        try { cron = CronExpression.Parse(s.Cron); }
        catch (Exception)
        {
            if (_invalidCronWarned.Add(s.Id))
                await events.LogAsync("schedule-error", $"Invalid cron '{s.Cron}' on schedule {s.Id}; skipping");
            return;
        }

        var occurrence = cron.GetNextOccurrence(checkedFrom.UtcDateTime, inclusive: true);
        if (occurrence is null) return;
        var occurrenceOffset = new DateTimeOffset(DateTime.SpecifyKind(occurrence.Value, DateTimeKind.Utc));
        if (occurrenceOffset > now) return;

        var lastFired = _lastFired.TryGetValue(s.Id, out var lf) ? lf : DateTimeOffset.MinValue;
        if (occurrenceOffset <= lastFired) return; // already fired for this due minute

        var rt = registry.Get(s.ServerId);
        if (rt is null)
        {
            // Server removed/disabled out from under the schedule — warn once, don't retry-storm.
            if (_invalidCronWarned.Add(s.Id))
                await events.LogAsync("schedule-error", $"Schedule {s.Id} targets unknown/disabled server {s.ServerId}; skipping");
            return;
        }

        _lastFired[s.Id] = occurrenceOffset; // record before firing: a throwing action must not retry-storm
        switch (s.Action)
        {
            case "restart":
                await rt.Orchestrator.RestartAsync("scheduler", [10, 5, 1], ct);
                break;
            case "backup":
                await rt.Backups.CreateBackupAsync("scheduled", ct);
                break;
            default:
                await rt.Events.LogAsync("schedule-error", $"Unknown schedule action '{s.Action}' on schedule {s.Id}");
                break;
        }
    }
}
