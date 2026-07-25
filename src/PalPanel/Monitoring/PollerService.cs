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
    ILogger<PollerService>? log = null, PalPanel.Control.AlertService? alerts = null) : BackgroundService
{
    private readonly ILogger _log = log ?? NullLogger<PollerService>.Instance;
    private readonly ConcurrentDictionary<Guid, PollState> _state = new();

    // Raise a low-disk alert when a fixed drive drops below this share of free space (critical
    // below the tighter bound). Server crash/health alerts come from the event pipeline; disk is
    // host-level, so the poller checks it directly each tick.
    private const double DiskLowFreePercent = 10.0;
    private const double DiskCriticalFreePercent = 3.0;

    private sealed class PollState
    {
        public Dictionary<string, (long SessionId, string Name)> Online = [];
        public bool ApiWasReachable = true;
        public int ConsecutiveApiFailures;
        public bool Reconciled;
        public TimeSpan? PrevCpu;          // last observed cumulative CPU time
        public DateTimeOffset PrevCpuAt;   // when it was observed (for the wall-clock delta)
        public DateTimeOffset? FirstUnreachableAt;               // start of the current unreachable streak
        public DateTimeOffset LastAutoRestartAt = DateTimeOffset.MinValue;  // cooldown anchor
    }

    // Minimum gap between health-triggered auto-restarts, so a restart cycle (or a server that
    // stays bad) can't restart-storm.
    private static readonly TimeSpan AutoRestartCooldown = TimeSpan.FromMinutes(5);

    // A single failed poll (a transient Palworld API stall) is tolerated; the UI is only marked
    // unreachable after this many consecutive failures, which stops the status from flapping.
    private const int UnreachableThreshold = 2;

    // The real Palworld server process (the supervisor tracks the thin PalServer.exe launcher).
    // Memory is reported from this so the fleet shows the server's actual RAM, not ~0.
    private const string GameProcessName = "PalServer-Win64-Shipping-Cmd";

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
            if (alerts is not null)
            {
                try { await CheckDisksAsync(ct); }
                catch (Exception ex) { _log.LogError(ex, "Disk-space alert check failed"); }
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

        // A server started OUTSIDE the panel (e.g. relaunched via Steam after a panel stop) should
        // be picked up so the panel reflects reality and — crucially — starts watching it for
        // crashes. AdoptExistingIfRunning only acts when Stopped/Held, so this is a no-op otherwise
        // and adopts at most once (state flips to Running).
        if (state is ServerState.Stopped or ServerState.Held)
        {
            sup.AdoptExistingIfRunning();
            state = sup.State;
        }

        if (state is ServerState.Stopped or ServerState.Held or ServerState.Stopping)
        {
            st.PrevCpu = null; // reset the CPU baseline so a fresh run starts clean
            st.FirstUnreachableAt = null;
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

        var memBytes = sup.GameMemoryBytes(GameProcessName);

        // CPU %: diff the process's cumulative CPU time against the previous poll, normalized by
        // elapsed wall time and core count → 0-100% of the whole machine. First poll of a run has
        // no baseline, so it reports 0 until the next tick.
        var cpuNow = sup.GameCpuTime(GameProcessName);
        var wallNow = DateTimeOffset.UtcNow;
        double cpuPct = 0;
        if (st.PrevCpu is { } prev)
        {
            var wallMs = (wallNow - st.PrevCpuAt).TotalMilliseconds;
            if (wallMs > 0)
                cpuPct = Math.Clamp((cpuNow - prev).TotalMilliseconds / (wallMs * Environment.ProcessorCount) * 100.0, 0, 100);
        }
        st.PrevCpu = cpuNow; st.PrevCpuAt = wallNow;

        rt.Snapshot.Publish(new ServerSnapshot(state, reachable, info, players, metrics,
            memBytes, sup.RunningSince, DateTimeOffset.UtcNow) { CpuPercent = cpuPct });

        if (state == ServerState.Running && rawReachable)
        {
            await using var db = await dbf.CreateDbContextAsync(ct);
            db.Samples.Add(new Sample
            {
                ServerId = rt.Id, Ts = DateTimeOffset.UtcNow, Players = players.Count,
                Fps = metrics?.ServerFps ?? 0, FrameTimeMs = metrics?.ServerFrameTime ?? 0,
                MemoryBytes = memBytes, Cpu = cpuPct, UptimeSeconds = metrics?.Uptime ?? 0
            });
            await db.SaveChangesAsync(ct);
            await DiffSessionsAsync(rt, st, players);
        }

        await EvaluateAutoRestartAsync(rt, st, state, rawReachable, memBytes, wallNow);
    }

    // Opt-in per server: restart when the API has been unreachable too long, or the process is
    // eating too much RAM. Only while Running (not mid-startup), guarded by a cooldown; the
    // orchestrator's own gate + crash-loop protection prevent storms.
    private async Task EvaluateAutoRestartAsync(ServerRuntime rt, PollState st, ServerState state,
        bool rawReachable, long memBytes, DateTimeOffset now)
    {
        if (state != ServerState.Running) { st.FirstUnreachableAt = null; return; }

        st.FirstUnreachableAt = rawReachable ? null : (st.FirstUnreachableAt ?? now);

        var cfg = rt.Config;
        string? reason = null;
        if (cfg.AutoRestartUnreachableMinutes > 0 && st.FirstUnreachableAt is { } since
            && now - since >= TimeSpan.FromMinutes(cfg.AutoRestartUnreachableMinutes))
            reason = $"API unreachable for {cfg.AutoRestartUnreachableMinutes} min";
        else if (cfg.AutoRestartMemoryGb > 0 && memBytes / 1_073_741_824.0 >= cfg.AutoRestartMemoryGb)
            reason = $"memory over {cfg.AutoRestartMemoryGb:0.#} GB";

        if (reason is null || now - st.LastAutoRestartAt < AutoRestartCooldown) return;

        st.LastAutoRestartAt = now;
        st.FirstUnreachableAt = null;
        await rt.Events.LogAsync("auto-restart", $"Auto-restarting: {reason}");
        _ = SafeRestartAsync(rt);   // fire-and-forget; the restart ritual is long and self-serializing
    }

    private static async Task SafeRestartAsync(ServerRuntime rt)
    {
        // Synthetic actor bypasses the admin guard (same as scheduled restarts); no warnings since
        // an unreachable server can't relay them anyway.
        try { await rt.Orchestrator.RestartAsync(PalPanel.Auth.AdminGuard.SchedulerActor, null, CancellationToken.None); }
        catch (Exception ex) { try { await rt.Events.LogAsync("auto-restart-failed", ex.Message); } catch { } }
    }

    // Host-level low-disk alerting. Dedup in AlertService means one alert per drive per episode;
    // recovery silently resolves it. Guarded by the caller so it's off when no AlertService is wired.
    private async Task CheckDisksAsync(CancellationToken ct)
    {
        foreach (var d in PalPanel.Control.HostStats.Disks())
        {
            var freePct = d.TotalBytes > 0 ? (double)d.FreeBytes / d.TotalBytes * 100.0 : 100.0;
            var key = "disk:" + d.Name;
            if (freePct < DiskLowFreePercent)
            {
                var sev = freePct < DiskCriticalFreePercent
                    ? PalPanel.Data.AlertSeverity.Critical : PalPanel.Data.AlertSeverity.Warning;
                await alerts!.RaiseAsync(null, "host", key, sev,
                    $"Low disk space on {d.Name.TrimEnd('\\')}",
                    $"{PalPanel.Control.HostStats.FormatBytes(d.FreeBytes)} free ({freePct:0.#}%) of {PalPanel.Control.HostStats.FormatBytes(d.TotalBytes)}", ct);
            }
            else
            {
                await alerts!.ResolveAsync(null, key, null, ct);
            }
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
