using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PalPanel.Data;

namespace PalPanel.Control;

// Records alerts to the panel DB (the in-panel feed) and dispatches them to the notifier (email).
// Condition alerts dedup+escalate per (ServerId, Key) so a flapping/looping condition yields one
// evolving alert, not a storm; notifications are point-in-time. All DB predicates avoid comparing
// DateTimeOffset (SQLite can't translate that) — only IS NULL checks and equality are used.
public class AlertService(
    IDbContextFactory<PanelDb> dbf, IAlertNotifier notifier, ILogger<AlertService>? log = null)
{
    private readonly ILogger _log = log ?? NullLogger<AlertService>.Instance;

    // Raise/escalate a CONDITION alert. If one is already active for (serverId, key): update it in
    // place, and only re-notify when severity actually increased (so a persistent condition doesn't
    // email every poll). Otherwise create a new active alert and notify.
    public async Task RaiseAsync(Guid? serverId, string serverName, string key, AlertSeverity sev,
        string title, string detail, CancellationToken ct = default)
    {
        try
        {
            await using var db = await dbf.CreateDbContextAsync(ct);
            var active = await db.Alerts
                .Where(a => a.ServerId == serverId && a.Key == key && a.ResolvedAt == null)
                .OrderByDescending(a => a.Id).FirstOrDefaultAsync(ct);
            var now = DateTimeOffset.UtcNow;

            if (active is not null)
            {
                var escalated = sev > active.Severity;
                active.Title = title; active.Detail = detail; active.UpdatedAt = now;
                if (escalated) active.Severity = sev;
                await db.SaveChangesAsync(ct);
                if (escalated) await DispatchAsync(active, ct);   // only email on escalation
                return;
            }

            var alert = new Alert
            {
                ServerId = serverId, ServerName = serverName, Key = key, Severity = sev,
                Title = title, Detail = detail, CreatedAt = now, UpdatedAt = now,
            };
            db.Alerts.Add(alert);
            await db.SaveChangesAsync(ct);
            await DispatchAsync(alert, ct);
        }
        catch (Exception ex) { _log.LogError(ex, "Alert raise failed: {Key}", key); }
    }

    // Point-in-time NOTIFICATION (created already-resolved): always recorded + dispatched.
    public async Task NotifyAsync(Guid? serverId, string serverName, string key, AlertSeverity sev,
        string title, string detail, CancellationToken ct = default)
    {
        try
        {
            await using var db = await dbf.CreateDbContextAsync(ct);
            var now = DateTimeOffset.UtcNow;
            var alert = new Alert
            {
                ServerId = serverId, ServerName = serverName, Key = key, Severity = sev,
                Title = title, Detail = detail, CreatedAt = now, UpdatedAt = now, ResolvedAt = now,
            };
            db.Alerts.Add(alert);
            await db.SaveChangesAsync(ct);
            await DispatchAsync(alert, ct);
        }
        catch (Exception ex) { _log.LogError(ex, "Alert notify failed: {Key}", key); }
    }

    // Clear any active alert for (serverId, key). When recoveredTitle is given AND something was
    // actually active, records an Info "recovered" entry (in-panel only; Info never emails).
    public async Task ResolveAsync(Guid? serverId, string key, string? recoveredTitle,
        CancellationToken ct = default)
    {
        try
        {
            await using var db = await dbf.CreateDbContextAsync(ct);
            var active = await db.Alerts
                .Where(a => a.ServerId == serverId && a.Key == key && a.ResolvedAt == null).ToListAsync(ct);
            if (active.Count == 0) return;
            var now = DateTimeOffset.UtcNow;
            foreach (var a in active) a.ResolvedAt = now;
            var name = active[0].ServerName;
            if (recoveredTitle is not null)
                db.Alerts.Add(new Alert
                {
                    ServerId = serverId, ServerName = name, Key = key + ":recovered",
                    Severity = AlertSeverity.Info, Title = recoveredTitle, Detail = "",
                    CreatedAt = now, UpdatedAt = now, ResolvedAt = now,
                });
            await db.SaveChangesAsync(ct);
        }
        catch (Exception ex) { _log.LogError(ex, "Alert resolve failed: {Key}", key); }
    }

    public async Task<List<Alert>> ListAsync(int limit = 200, CancellationToken ct = default)
    {
        await using var db = await dbf.CreateDbContextAsync(ct);
        return await db.Alerts.OrderByDescending(a => a.Id).Take(limit).ToListAsync(ct);
    }

    public async Task<int> UnacknowledgedCountAsync(CancellationToken ct = default)
    {
        await using var db = await dbf.CreateDbContextAsync(ct);
        return await db.Alerts.CountAsync(a => !a.Acknowledged, ct);
    }

    public async Task AcknowledgeAsync(long id, CancellationToken ct = default)
    {
        await using var db = await dbf.CreateDbContextAsync(ct);
        var a = await db.Alerts.FindAsync([id], ct);
        if (a is { Acknowledged: false }) { a.Acknowledged = true; await db.SaveChangesAsync(ct); }
    }

    public async Task AcknowledgeAllAsync(CancellationToken ct = default)
    {
        await using var db = await dbf.CreateDbContextAsync(ct);
        var unacked = await db.Alerts.Where(a => !a.Acknowledged).ToListAsync(ct);
        foreach (var a in unacked) a.Acknowledged = true;
        if (unacked.Count > 0) await db.SaveChangesAsync(ct);
    }

    private async Task DispatchAsync(Alert alert, CancellationToken ct)
    {
        // Notifier failures must never break the caller (poller/event pipeline).
        try { await notifier.SendAsync(alert, ct); }
        catch (Exception ex) { _log.LogError(ex, "Alert dispatch failed: {Title}", alert.Title); }
    }
}
