using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PalPanel.Data; using PalPanel.PalApi; using PalPanel.Supervisor;
namespace PalPanel.Monitoring;

public class PollerService(IPalApi api, ProcessSupervisor sup, SnapshotService snap,
    IDbContextFactory<PanelDb> dbf, IEventSink events, IOptions<PanelOptions> opts) : BackgroundService
{
    private Dictionary<string, (long SessionId, string Name)> _online = [];
    private bool _apiWasReachable = true;

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { await TickAsync(ct); }
            catch (Exception ex) { await events.LogAsync("poller-error", ex.Message); }
            await Task.Delay(TimeSpan.FromSeconds(opts.Value.PollIntervalSeconds), ct);
        }
    }

    public async Task TickAsync(CancellationToken ct)
    {
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
