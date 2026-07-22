using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using PalPanel;
using PalPanel.Control;
using PalPanel.Data;
using PalPanel.PalApi;
using PalPanel.Supervisor;

public class OrchestratorTests
{
    // Records announce/save/shutdown calls into a shared `order` list. Announce messages
    // carrying a leading number ("Server restarting in {W} minute(s)") are recorded as
    // "announce:{W}"; announce calls with no number (e.g. the ritual's internal "Server is
    // shutting down") are recorded as "info" so tests can filter them out alongside event-log
    // noise, per the brief's `order.Where(o => o is not "info")` assertion.
    private class RecordingApi(List<string> order, Func<FakeProcess> currentProcess) : IPalApi
    {
        public Task<ServerInfo?> GetInfoAsync(CancellationToken ct) => Task.FromResult<ServerInfo?>(null);
        public Task<IReadOnlyList<PlayerInfo>> GetPlayersAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<PlayerInfo>>([]);
        public Task<ServerMetrics?> GetMetricsAsync(CancellationToken ct) => Task.FromResult<ServerMetrics?>(null);

        public Task AnnounceAsync(string message, CancellationToken ct)
        {
            var m = Regex.Match(message, @"\d+");
            order.Add(m.Success ? $"announce:{m.Value}" : "info");
            return Task.CompletedTask;
        }

        public Task KickAsync(string userId, string message, CancellationToken ct) => Task.CompletedTask;
        public Task BanAsync(string userId, string message, CancellationToken ct) => Task.CompletedTask;
        public Task UnbanAsync(string userId, CancellationToken ct) => Task.CompletedTask;

        public Task SaveAsync(CancellationToken ct)
        { order.Add("save"); return Task.CompletedTask; }

        public Task ShutdownAsync(int waitSeconds, string message, CancellationToken ct)
        {
            order.Add("shutdown");
            currentProcess().SimulateExit(); // supervisor's StopAsync waits on this to complete
            return Task.CompletedTask;
        }
    }

    private class FakeBackup(List<string> order) : IBackupService
    {
        public List<string> Reasons { get; } = [];

        public Task<string> CreateBackupAsync(string reason, CancellationToken ct)
        { Reasons.Add(reason); order.Add("backup"); return Task.FromResult("b.zip"); }

        public IReadOnlyList<BackupInfo> List() => [];
        public Task RestoreAsync(string fileName, CancellationToken ct) => Task.CompletedTask;
    }

    private class FakeEventSink : IEventSink
    {
        public List<(string Type, string Detail, string? Actor)> Events { get; } = [];
        public Task LogAsync(string type, string detail, string? actorEmail = null)
        { Events.Add((type, detail, actorEmail)); return Task.CompletedTask; }
    }

    private static (ServerOrchestrator Orch, ProcessSupervisor Sup, FakeLauncher Launcher, List<string> Order,
        FakeBackup Backup, FakeEventSink Events) Make()
    {
        var order = new List<string>();
        var launcher = new FakeLauncher();
        var o = new PanelOptions { MaxCrashesInWindow = 3, CrashWindowMinutes = 10, GracefulStopTimeoutSeconds = 5 };
        var sup = new ProcessSupervisor(launcher, Options.Create(o)) { RestartDelay = _ => Task.CompletedTask };
        var api = new RecordingApi(order, () => launcher.Launched[^1]);
        var backup = new FakeBackup(order);
        var events = new FakeEventSink();
        var orch = new ServerOrchestrator(sup, api, backup, events) { Delay = (_, _) => Task.CompletedTask };
        return (orch, sup, launcher, order, backup, events);
    }

    [Fact]
    public async Task Restart_RunsFullRitualInOrder()
    {
        var (orch, sup, launcher, order, backup, _) = Make();
        await sup.StartAsync(default);
        sup.MarkRunning();
        launcher.OnLaunch = _ => order.Add("start"); // set only after the initial launch above

        await orch.RestartAsync("admin@x.com", [10, 5, 1], default);

        Assert.Equal(
            new[] { "announce:10", "announce:5", "announce:1", "save", "shutdown", "backup", "start" },
            order.Where(o => o is not "info").ToList());
        Assert.Equal(ServerState.Starting, sup.State);
        Assert.Equal(["pre-stop (admin@x.com)"], backup.Reasons);
    }

    [Fact]
    public async Task Restart_NoWarningMinutes_StillStopsBacksUpAndStarts()
    {
        var (orch, sup, launcher, order, _, _) = Make();
        await sup.StartAsync(default);
        sup.MarkRunning();
        launcher.OnLaunch = _ => order.Add("start");

        await orch.RestartAsync("admin@x.com", null, default);

        Assert.Equal(["save", "shutdown", "backup", "start"], order.Where(o => o is not "info").ToList());
    }

    [Fact]
    public async Task Stop_AnnouncesSavesShutsDownAndBacksUp()
    {
        var (orch, sup, launcher, order, backup, _) = Make();
        await sup.StartAsync(default);
        sup.MarkRunning();

        await orch.StopAsync("admin@x.com", default);

        Assert.Equal(["info", "save", "shutdown", "backup"], order);
        Assert.Equal(ServerState.Stopped, sup.State);
        Assert.Equal(["pre-stop (admin@x.com)"], backup.Reasons);
    }

    [Fact]
    public async Task Start_LogsEventAndStartsSupervisor()
    {
        var (orch, sup, _, _, _, events) = Make();
        await orch.StartAsync("admin@x.com", default);
        Assert.Equal(ServerState.Starting, sup.State);
        Assert.Contains(events.Events, e => e.Type == "start" && e.Actor == "admin@x.com");
    }

    [Fact]
    public async Task Save_CallsApiAndLogsEvent()
    {
        var (orch, _, _, order, _, events) = Make();
        await orch.SaveAsync("admin@x.com", default);
        Assert.Equal(["save"], order);
        Assert.Contains(events.Events, e => e.Type == "save" && e.Actor == "admin@x.com");
    }

    [Fact]
    public async Task Announce_CallsApiAndLogsEvent()
    {
        var (orch, _, _, order, _, events) = Make();
        await orch.AnnounceAsync("admin@x.com", "hello everyone", default);
        Assert.Equal(["info"], order); // no digit in the message
        Assert.Contains(events.Events, e => e.Type == "announce" && e.Detail == "hello everyone" && e.Actor == "admin@x.com");
    }
}
