using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PalPanel.Data; using PalPanel.PalApi; using PalPanel.Supervisor;
namespace PalPanel.Monitoring;

public class PollerService(IPalApi api, ProcessSupervisor sup, SnapshotService snap,
    IDbContextFactory<PanelDb> dbf, IEventSink events, IOptions<PanelOptions> opts,
    ILogger<PollerService>? log = null) : BackgroundService
{
    private readonly ILogger _log = log ?? NullLogger<PollerService>.Instance;
    private Dictionary<string, (long SessionId, string Name)> _online = [];
    private bool _apiWasReachable = true;
    private bool _reconciled;

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { await TickAsync(ct); }
            catch (Exception ex)
            {
                // The event sink itself can fail (disk full, DB locked); that must never
                // kill the poll loop / host — fall back to loud ILogger output instead.
                try { await events.LogAsync("poller-error", ex.Message); }
                catch (Exception sinkEx)
                { _log.LogError(sinkEx, "Event sink write failed for poller-error: {Detail}", ex.Message); }
            }
            await Task.Delay(TimeSpan.FromSeconds(opts.Value.PollIntervalSeconds), ct);
        }
    }

    public async Task TickAsync(CancellationToken ct)
    {
        // One-time startup reconciliation: after a panel restart (with or without
        // adoption of a still-running PalServer), _online is empty but the DB may
        // still hold open PlayerSession rows from the previous run. Close them all;
        // players still online get fresh join rows from this tick's diff, which is
        // correct-by-construction. Flag is set only after success so a transient DB
        // failure retries on the next tick instead of leaving stale rows forever.
        if (!_reconciled) { await ReconcileStaleSessionsAsync(ct); _reconciled = true; }

        var state = sup.State;
        if (state is ServerState.Stopped or ServerState.Held or ServerState.Stopping)
        {
            snap.Publish(new ServerSnapshot(state, false, null, [], null, 0, null, DateTimeOffset.UtcNow));
            await CloseAllSessionsAsync();
            return;
        }

        var info = await api.GetInfoAsync(ct);
        var reachable = info is not null;
        var players = reachable ? await api.GetPlayersAsync(ct) : [];
        var metrics = reachable ? await api.GetMetricsAsync(ct) : null;

        if (reachable && state == ServerState.Starting) { sup.MarkRunning(); state = sup.State; }
        if (!reachable && state == ServerState.Running && _apiWasReachable)
            await events.LogAsync("api-unreachable", "Process alive but REST API not answering");
        if (reachable && !_apiWasReachable)
            await events.LogAsync("api-recovered", "REST API answering again");
        _apiWasReachable = reachable;

        snap.Publish(new ServerSnapshot(state, reachable, info, players, metrics,
            sup.CurrentMemoryBytes, sup.RunningSince, DateTimeOffset.UtcNow));

        if (state == ServerState.Running && reachable)
        {
            await using var db = await dbf.CreateDbContextAsync(ct);
            db.Samples.Add(new Sample
            {
                Ts = DateTimeOffset.UtcNow, Players = players.Count,
                Fps = metrics?.ServerFps ?? 0, FrameTimeMs = metrics?.ServerFrameTime ?? 0,
                MemoryBytes = sup.CurrentMemoryBytes, UptimeSeconds = metrics?.Uptime ?? 0
            });
            await db.SaveChangesAsync(ct);
            await DiffSessionsAsync(players);
        }
    }

    private async Task DiffSessionsAsync(IReadOnlyList<PlayerInfo> players)
    {
        await using var db = await dbf.CreateDbContextAsync();
        var current = players.ToDictionary(p => p.UserId);
        foreach (var p in players.Where(p => !_online.ContainsKey(p.UserId)))
        {
            var s = new PlayerSession { UserId = p.UserId, Name = p.Name, JoinedAt = DateTimeOffset.UtcNow };
            db.Sessions.Add(s); await db.SaveChangesAsync();
            _online[p.UserId] = (s.Id, p.Name);
            await events.LogAsync("player-join", $"{p.Name} joined");
        }
        foreach (var (userId, v) in _online.Where(kv => !current.ContainsKey(kv.Key)).ToList())
        {
            var s = await db.Sessions.FindAsync(v.SessionId);
            if (s is not null) { s.LeftAt = DateTimeOffset.UtcNow; await db.SaveChangesAsync(); }
            _online.Remove(userId);
            await events.LogAsync("player-leave", $"{v.Name} left");
        }
    }

    private async Task ReconcileStaleSessionsAsync(CancellationToken ct)
    {
        await using var db = await dbf.CreateDbContextAsync(ct);
        var stale = await db.Sessions.Where(s => s.LeftAt == null).ToListAsync(ct);
        if (stale.Count == 0) return;
        var now = DateTimeOffset.UtcNow;
        foreach (var s in stale) s.LeftAt = now;
        await db.SaveChangesAsync(ct);
        await events.LogAsync("sessions-reconciled", $"Closed {stale.Count} stale session(s) left open by a previous panel run");
    }

    private async Task CloseAllSessionsAsync()
    {
        if (_online.Count == 0) return;
        await using var db = await dbf.CreateDbContextAsync();
        foreach (var (_, v) in _online)
        {
            var s = await db.Sessions.FindAsync(v.SessionId);
            if (s is not null) s.LeftAt = DateTimeOffset.UtcNow;
        }
        await db.SaveChangesAsync();
        _online.Clear();
    }
}
