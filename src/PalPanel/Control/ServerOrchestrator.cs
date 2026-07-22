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

    public async Task StartAsync(string actor, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try { await events.LogAsync("start", "Start requested", actor); await sup.StartAsync(ct); }
        finally { _gate.Release(); }
    }

    public async Task StopAsync(string actor, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try { await StopCoreAsync(actor, ct); }
        finally { _gate.Release(); }
    }

    private async Task StopCoreAsync(string actor, CancellationToken ct)
    {
        await events.LogAsync("stop", "Stop requested", actor);
        await sup.StopAsync(async () =>
        {
            await api.AnnounceAsync("Server is shutting down", ct);
            await api.SaveAsync(ct);
            await api.ShutdownAsync(30, "Shutting down", ct);
        }, ct);
        await backups.CreateBackupAsync($"pre-stop ({actor})", ct);
    }

    public async Task RestartAsync(string actor, IReadOnlyList<int>? warningMinutes, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            await events.LogAsync("restart", "Restart requested", actor);
            var warnings = (warningMinutes ?? []).OrderByDescending(m => m).ToList();
            for (int i = 0; i < warnings.Count; i++)
            {
                await api.AnnounceAsync($"Server restarting in {warnings[i]} minute(s)", ct);
                var next = i + 1 < warnings.Count ? warnings[i + 1] : 0;
                await Delay(TimeSpan.FromMinutes(warnings[i] - next), ct);
            }
            await StopCoreAsync(actor, ct);
            await sup.StartAsync(ct);
            await events.LogAsync("restart-launched", "Server relaunched after restart", actor);
        }
        finally { _gate.Release(); }
    }

    public async Task SaveAsync(string actor, CancellationToken ct)
    { await api.SaveAsync(ct); await events.LogAsync("save", "World save requested", actor); }

    public async Task AnnounceAsync(string actor, string message, CancellationToken ct)
    { await api.AnnounceAsync(message, ct); await events.LogAsync("announce", message, actor); }
}
