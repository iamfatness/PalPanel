using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using PalPanel.Data; using PalPanel.PalApi; using PalPanel.Servers; using PalPanel.Supervisor;
namespace PalPanel.Monitoring;

// Single background poller for the whole fleet: each tick it polls every live ServerRuntime
// independently. Per-server state (online players, API-reachability edge, one-time session
// reconciliation) is keyed by server id. A failure polling one server is caught and logged to
// that server's event log; it never stalls the others or kills the loop.
public class PollerService(ServerManager manager, IDbContextFactory<PanelDb> dbf,
    ILogger<PollerService>? log = null) : BackgroundService
{
    private readonly ILogger _log = log ?? NullLogger<PollerService>.Instance;
    private readonly ConcurrentDictionary<Guid, PollState> _state = new();

    private sealed class PollState
    {
        public Dictionary<string, (long SessionId, string Name)> Online = [];
        public bool ApiWasReachable = true;
        public int ConsecutiveApiFailures;
        public bool Reconciled;
    }

    // A single failed poll (a transient Palworld API stall) is tolerated; the UI is only marked
    // unreachable after this many consecutive failures, which stops the status from flapping.
    private const int UnreachableThreshold = 2;

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var runtimes = manager.All();
            foreach (var rt in runtimes)
            {
                try { await TickServerAsync(rt, ct); }
                catch (Exception ex)
                {
                    try { await rt.Events.LogAsync("poller-error", ex.Message); }
                    catch (Exception sinkEx)
                    { _log.LogError(sinkEx, "Event sink write failed for poller-error on {Server}: {Detail}", rt.Id, ex.Message); }
                }
            }
            // Base cadence = the shortest configured interval among live servers (fallback 10s);
            // every server is polled each tick. Keeps the shared loop simple and responsive.
            var interval = runtimes.Count == 0 ? 10 : runtimes.Min(r => Math.Max(1, r.Config.PollIntervalSeconds));
            await Task.Delay(TimeSpan.FromSeconds(interval), ct);
        }
    }

    public async Task TickServerAsync(ServerRuntime rt, CancellationToken ct)
    {
        var st = _state.GetOrAdd(rt.Id, _ => new PollState());

        // One-time startup reconciliation: close PlayerSession rows this server left open in a
        // previous panel run. Players still online get fresh join rows from this tick's diff.
        if (!st.Reconciled) { await ReconcileStaleSessionsAsync(rt, ct); st.Reconciled = true; }

        var sup = rt.Supervisor;
        var state = sup.State;
        if (state is ServerState.Stopped or ServerState.Held or ServerState.Stopping)
        {
            rt.Snapshot.Publish(new ServerSnapshot(state, false, null, [], null, 0, null, DateTimeOffset.UtcNow));
            await CloseAllSessionsAsync(rt, st);
            return;
        }

        var info = await rt.Api.GetInfoAsync(ct);
        var rawReachable = info is not null;
        st.ConsecutiveApiFailures = rawReachable ? 0 : st.ConsecutiveApiFailures + 1;

        // Hysteresis: report unreachable only after repeated failures, so one stalled poll
        // (common with Palworld's game-thread API) doesn't flap the status true<->false.
        var reachable = rawReachable || st.ConsecutiveApiFailures < UnreachableThreshold;

        // Only fetch/sample real data when the API actually answered this tick.
        var players = rawReachable ? await rt.Api.GetPlayersAsync(ct) : [];
        var metrics = rawReachable ? await rt.Api.GetMetricsAsync(ct) : null;

        if (rawReachable && state == ServerState.Starting) { sup.MarkRunning(); state = sup.State; }
        if (!reachable && state == ServerState.Running && st.ApiWasReachable)
            await rt.Events.LogAsync("api-unreachable", "Process alive but REST API not answering");
        if (reachable && !st.ApiWasReachable)
            await rt.Events.LogAsync("api-recovered", "REST API answering again");
        st.ApiWasReachable = reachable;

        rt.Snapshot.Publish(new ServerSnapshot(state, reachable, info, players, metrics,
            sup.CurrentMemoryBytes, sup.RunningSince, DateTimeOffset.UtcNow));

        if (state == ServerState.Running && rawReachable)
        {
            await using var db = await dbf.CreateDbContextAsync(ct);
            db.Samples.Add(new Sample
            {
                ServerId = rt.Id, Ts = DateTimeOffset.UtcNow, Players = players.Count,
                Fps = metrics?.ServerFps ?? 0, FrameTimeMs = metrics?.ServerFrameTime ?? 0,
                MemoryBytes = sup.CurrentMemoryBytes, UptimeSeconds = metrics?.Uptime ?? 0
            });
            await db.SaveChangesAsync(ct);
            await DiffSessionsAsync(rt, st, players);
        }
    }

    private async Task DiffSessionsAsync(ServerRuntime rt, PollState st, IReadOnlyList<PlayerInfo> players)
    {
        await using var db = await dbf.CreateDbContextAsync();
        var current = players.ToDictionary(p => p.UserId);
        foreach (var p in players.Where(p => !st.Online.ContainsKey(p.UserId)))
        {
            var s = new PlayerSession { ServerId = rt.Id, UserId = p.UserId, Name = p.Name, JoinedAt = DateTimeOffset.UtcNow };
            db.Sessions.Add(s); await db.SaveChangesAsync();
            st.Online[p.UserId] = (s.Id, p.Name);
            await rt.Events.LogAsync("player-join", $"{p.Name} joined");
        }
        foreach (var (userId, v) in st.Online.Where(kv => !current.ContainsKey(kv.Key)).ToList())
        {
            var s = await db.Sessions.FindAsync(v.SessionId);
            if (s is not null) { s.LeftAt = DateTimeOffset.UtcNow; await db.SaveChangesAsync(); }
            st.Online.Remove(userId);
            await rt.Events.LogAsync("player-leave", $"{v.Name} left");
        }
    }

    private async Task ReconcileStaleSessionsAsync(ServerRuntime rt, CancellationToken ct)
    {
        await using var db = await dbf.CreateDbContextAsync(ct);
        var stale = await db.Sessions.Where(s => s.ServerId == rt.Id && s.LeftAt == null).ToListAsync(ct);
        if (stale.Count == 0) return;
        var now = DateTimeOffset.UtcNow;
        foreach (var s in stale) s.LeftAt = now;
        await db.SaveChangesAsync(ct);
        await rt.Events.LogAsync("sessions-reconciled", $"Closed {stale.Count} stale session(s) left open by a previous panel run");
    }

    private async Task CloseAllSessionsAsync(ServerRuntime rt, PollState st)
    {
        if (st.Online.Count == 0) return;
        await using var db = await dbf.CreateDbContextAsync();
        foreach (var (_, v) in st.Online)
        {
            var s = await db.Sessions.FindAsync(v.SessionId);
            if (s is not null) s.LeftAt = DateTimeOffset.UtcNow;
        }
        await db.SaveChangesAsync();
        st.Online.Clear();
    }
}
