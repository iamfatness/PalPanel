using PalPanel.Data; using PalPanel.PalApi; using PalPanel.Supervisor;
namespace PalPanel.Control;

public interface IServerOrchestrator
{
    Task StartAsync(string actor, CancellationToken ct);
    Task StopAsync(string actor, CancellationToken ct);
    Task RestartAsync(string actor, IReadOnlyList<int>? warningMinutes, CancellationToken ct);
    Task SaveAsync(string actor, CancellationToken ct);
    Task AnnounceAsync(string actor, string message, CancellationToken ct);
}

public class ServerOrchestrator(ProcessSupervisor sup, IPalApi api, IBackupService backups, IEventSink events)
    : IServerOrchestrator
{
    public Func<TimeSpan, CancellationToken, Task> Delay { get; set; } = Task.Delay;
    private readonly SemaphoreSlim _gate = new(1, 1); // one lifecycle op at a time

    // The gate can be held for a LONG time: a restart ritual with [10,5,1] warnings occupies
    // it for 10+ minutes. An operator issuing Stop during that window would otherwise just
    // see their click hang. Tradeoff accepted for v1 (ops still serialize — that's the point
    // of the gate), but the queuing must be loud: log lifecycle-busy before blocking.
    private async Task AcquireGateAsync(string op, string actor, CancellationToken ct)
    {
        if (await _gate.WaitAsync(0, ct)) return; // fast path: gate free
        await events.LogAsync("lifecycle-busy", $"{op} by {actor} queued behind an in-progress lifecycle operation", actor);
        await _gate.WaitAsync(ct);
    }

    public async Task StartAsync(string actor, CancellationToken ct)
    {
        await AcquireGateAsync("Start", actor, ct);
        try { await events.LogAsync("start", "Start requested", actor); await sup.StartAsync(ct); }
        finally { _gate.Release(); }
    }

    public async Task StopAsync(string actor, CancellationToken ct)
    {
        await AcquireGateAsync("Stop", actor, ct);
        try { await StopCoreAsync(actor, ct); }
        finally { _gate.Release(); }
    }

    private async Task StopCoreAsync(string actor, CancellationToken ct)
    {
        // Capture whether a server was actually up BEFORE stopping: when it was already
        // Stopped/Held there is no world state to snapshot, so the pre-stop backup is skipped.
        var hadServer = sup.State is not (ServerState.Stopped or ServerState.Held);
        await events.LogAsync("stop", "Stop requested", actor);
        await sup.StopAsync(async () =>
        {
            await api.AnnounceAsync("Server is shutting down", ct);
            await api.SaveAsync(ct);
            await api.ShutdownAsync(30, "Shutting down", ct);
        }, ct);
        if (!hadServer) return;
        // A failed backup must never abort the lifecycle op (server availability first) —
        // but it must be loud: operators need to know their safety net is broken.
        try { await backups.CreateBackupAsync($"pre-stop ({actor})", ct); }
        catch (Exception ex)
        { await events.LogAsync("backup-failed", $"Pre-stop backup failed: {ex.Message}", actor); }
    }

    public async Task RestartAsync(string actor, IReadOnlyList<int>? warningMinutes, CancellationToken ct)
    {
        await AcquireGateAsync("Restart", actor, ct);
        try
        {
            await events.LogAsync("restart", "Restart requested", actor);
            // Warnings are best-effort: skipped entirely when no server is Running to relay
            // them, and a failing announce (API down, server crashed mid-ritual) is logged
            // and swallowed — it must never abort the restart.
            if (sup.State == ServerState.Running)
            {
                var warnings = (warningMinutes ?? []).OrderByDescending(m => m).ToList();
                for (int i = 0; i < warnings.Count; i++)
                {
                    try { await api.AnnounceAsync($"Server restarting in {warnings[i]} minute(s)", ct); }
                    catch (Exception ex)
                    { await events.LogAsync("announce-failed", $"Restart warning announce failed: {ex.Message}", actor); }
                    var next = i + 1 < warnings.Count ? warnings[i + 1] : 0;
                    await Delay(TimeSpan.FromMinutes(warnings[i] - next), ct);
                }
            }
            // The relaunch below must ALWAYS be attempted: a restart that aborts mid-ritual
            // and leaves the server down until the next cron occurrence is the worst outcome.
            // Any stop-phase failure is logged loudly and we proceed to StartAsync anyway.
            try { await StopCoreAsync(actor, ct); }
            catch (Exception ex)
            { await events.LogAsync("restart-stop-phase-error", $"Stop phase failed during restart: {ex.Message}", actor); }
            try
            {
                await sup.StartAsync(ct);
                await events.LogAsync("restart-launched", "Server relaunched after restart", actor);
            }
            catch (Exception ex)
            { await events.LogAsync("restart-start-failed", $"Relaunch failed after restart: {ex.Message}", actor); }
        }
        finally { _gate.Release(); }
    }

    public async Task SaveAsync(string actor, CancellationToken ct)
    { await api.SaveAsync(ct); await events.LogAsync("save", "World save requested", actor); }

    public async Task AnnounceAsync(string actor, string message, CancellationToken ct)
    { await api.AnnounceAsync(message, ct); await events.LogAsync("announce", message, actor); }
}
