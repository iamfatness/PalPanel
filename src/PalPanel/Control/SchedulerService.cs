using Cronos;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PalPanel.Data;
namespace PalPanel.Control;

// Checks enabled Schedule rows once a minute and fires the corresponding action (restart
// ritual or on-demand backup) at most once per due minute.
public class SchedulerService(
    IDbContextFactory<PanelDb> dbf,
    IServerOrchestrator orchestrator,
    IBackupService backups,
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

        _lastFired[s.Id] = occurrenceOffset; // record before firing: a throwing action must not retry-storm
        switch (s.Action)
        {
            case "restart":
                await orchestrator.RestartAsync("scheduler", [10, 5, 1], ct);
                break;
            case "backup":
                await backups.CreateBackupAsync("scheduled", ct);
                break;
            default:
                await events.LogAsync("schedule-error", $"Unknown schedule action '{s.Action}' on schedule {s.Id}");
                break;
        }
    }
}
