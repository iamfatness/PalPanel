using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using PalPanel;
using PalPanel.Auth;
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
        // When set, AnnounceAsync throws for any message the predicate matches (before
        // recording) — simulates the REST API being down / the server having crashed.
        public Func<string, bool>? AnnounceThrows { get; set; }

        public Task<ServerInfo?> GetInfoAsync(CancellationToken ct) => Task.FromResult<ServerInfo?>(null);
        public Task<IReadOnlyList<PlayerInfo>> GetPlayersAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<PlayerInfo>>([]);
        public Task<ServerMetrics?> GetMetricsAsync(CancellationToken ct) => Task.FromResult<ServerMetrics?>(null);

        public Task AnnounceAsync(string message, CancellationToken ct)
        {
            if (AnnounceThrows?.Invoke(message) == true) throw new HttpRequestException("api unreachable");
            var m = Regex.Match(message, @"\d+");
            order.Add(m.Success ? $"announce:{m.Value}" : "info");
            return Task.CompletedTask;
        }

        public Task KickAsync(string userId, string message, CancellationToken ct)
        { order.Add($"kick:{userId}"); return Task.CompletedTask; }
        public Task BanAsync(string userId, string message, CancellationToken ct)
        { order.Add($"ban:{userId}"); return Task.CompletedTask; }
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
        public bool ThrowOnCreate { get; set; }
        public List<string> Reasons { get; } = [];

        public Task<string> CreateBackupAsync(string reason, CancellationToken ct)
        {
            if (ThrowOnCreate) throw new IOException("disk full");
            Reasons.Add(reason); order.Add("backup"); return Task.FromResult("b.zip");
        }

        public IReadOnlyList<BackupInfo> List() => [];
        public Task RestoreAsync(string fileName, CancellationToken ct) => Task.CompletedTask;
    }

    private class FakeEventSink : IEventSink
    {
        public List<(string Type, string Detail, string? Actor)> Events { get; } = [];
        public Task LogAsync(string type, string detail, string? actorEmail = null)
        { Events.Add((type, detail, actorEmail)); return Task.CompletedTask; }
    }

    // Allow-all-by-default fake guard for tests that aren't exercising the authorization
    // backstop itself (every other OrchestratorTests fixture uses actor emails that were
    // never registered with a real RoleService-backed Admin/Viewer/Blocked role). The
    // authorization backstop itself is covered separately below against the REAL AdminGuard
    // wired to a real Sqlite-backed IDbContextFactory, so the bypass here doesn't hide it.
    private class FakeAdminGuard : IAdminGuard
    {
        public HashSet<string> Admins { get; } = ["admin@x.com", "ops@x.com"];
        public Task EnsureAdminAsync(string actor, string action, CancellationToken ct) =>
            actor == AdminGuard.SchedulerActor || Admins.Contains(actor)
                ? Task.CompletedTask
                : throw new UnauthorizedAccessException($"{actor} not admin");
    }

    private static (ServerOrchestrator Orch, ProcessSupervisor Sup, FakeLauncher Launcher, List<string> Order,
        FakeBackup Backup, FakeEventSink Events, RecordingApi Api) Make()
    {
        var order = new List<string>();
        var launcher = new FakeLauncher();
        var o = new PanelOptions { MaxCrashesInWindow = 3, CrashWindowMinutes = 10, GracefulStopTimeoutSeconds = 5 };
        var sup = new ProcessSupervisor(launcher, Options.Create(o)) { RestartDelay = _ => Task.CompletedTask };
        var api = new RecordingApi(order, () => launcher.Launched[^1]);
        var backup = new FakeBackup(order);
        var events = new FakeEventSink();
        var orch = new ServerOrchestrator(sup, api, backup, events, new FakeAdminGuard()) { Delay = (_, _) => Task.CompletedTask };
        return (orch, sup, launcher, order, backup, events, api);
    }

    // Sets up a ServerOrchestrator wired to the REAL AdminGuard against a real (temp-file)
    // Sqlite-backed IDbContextFactory<PanelDb>, seeded with the given (email, role) users.
    // Used to prove the server-side authorization backstop end-to-end: a non-Admin actor's
    // mutating call must throw UnauthorizedAccessException, log an unauthorized-action
    // event, and never reach the API/supervisor/backup layers.
    private static async Task<(ServerOrchestrator Orch, FakeEventSink Events, List<string> Order)>
        MakeWithRealGuardAsync(params (string Email, string Role)[] users)
    {
        var services = new ServiceCollection();
        services.AddDbContextFactory<PanelDb>(b => b.UseSqlite($"Data Source={Path.GetTempFileName()}"));
        var sp = services.BuildServiceProvider();
        var dbf = sp.GetRequiredService<IDbContextFactory<PanelDb>>();
        await using (var db = await dbf.CreateDbContextAsync())
        {
            await db.Database.EnsureCreatedAsync();
            foreach (var (email, role) in users)
                db.Users.Add(new PanelUser { Email = email, Role = role, FirstSeen = DateTimeOffset.UtcNow, LastSeen = DateTimeOffset.UtcNow });
            await db.SaveChangesAsync();
        }

        var order = new List<string>();
        var launcher = new FakeLauncher();
        var o = new PanelOptions { MaxCrashesInWindow = 3, CrashWindowMinutes = 10, GracefulStopTimeoutSeconds = 5 };
        var sup = new ProcessSupervisor(launcher, Options.Create(o)) { RestartDelay = _ => Task.CompletedTask };
        var api = new RecordingApi(order, () => launcher.Launched[^1]);
        var backup = new FakeBackup(order);
        var events = new FakeEventSink();
        var guard = new AdminGuard(dbf, events, Options.Create(new PanelOptions())); // AuthDisabled=false default: exercises the real DB-backed authz path
        var orch = new ServerOrchestrator(sup, api, backup, events, guard) { Delay = (_, _) => Task.CompletedTask };
        return (orch, events, order);
    }

    [Fact]
    public async Task Restart_RunsFullRitualInOrder()
    {
        var (orch, sup, launcher, order, backup, _, _) = Make();
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
        var (orch, sup, launcher, order, _, _, _) = Make();
        await sup.StartAsync(default);
        sup.MarkRunning();
        launcher.OnLaunch = _ => order.Add("start");

        await orch.RestartAsync("admin@x.com", null, default);

        Assert.Equal(["save", "shutdown", "backup", "start"], order.Where(o => o is not "info").ToList());
    }

    [Fact]
    public async Task Restart_BackupThrows_ServerStillRelaunched()
    {
        // A failing pre-stop backup (disk full, ...) must never leave the server down:
        // the relaunch is the priority; the failure is logged loudly instead.
        var (orch, sup, launcher, order, backup, events, _) = Make();
        await sup.StartAsync(default);
        sup.MarkRunning();
        launcher.OnLaunch = _ => order.Add("start");
        backup.ThrowOnCreate = true;

        await orch.RestartAsync("admin@x.com", [1], default);

        Assert.Equal(["announce:1", "save", "shutdown", "start"], order.Where(o => o is not "info").ToList());
        Assert.Equal(ServerState.Starting, sup.State);
        Assert.Contains(events.Events, e => e.Type == "backup-failed");
        Assert.Contains(events.Events, e => e.Type == "restart-launched");
    }

    [Fact]
    public async Task Restart_WhileHeldWithApiDown_SkipsWarningsAndStartsServer()
    {
        // Scheduled restart against a Held server (3 crashes tripped the hold) with the REST
        // API unreachable: warnings must be skipped (nobody is reachable to read them), no
        // exception may escape, and the server must come back up.
        var (orch, sup, launcher, order, backup, _, api) = Make();
        await sup.StartAsync(default);
        for (int i = 0; i < 3; i++)
        {
            sup.MarkRunning();
            launcher.Launched[^1].SimulateExit();
            await sup.WaitForIdleAsync();
        }
        Assert.Equal(ServerState.Held, sup.State);
        api.AnnounceThrows = _ => true;
        launcher.OnLaunch = _ => order.Add("start");

        await orch.RestartAsync("scheduler", [10, 5, 1], default);

        Assert.Equal(["start"], order);          // no announces, no save/shutdown (nothing was running), no backup
        Assert.Equal(ServerState.Starting, sup.State);
        Assert.Empty(backup.Reasons);            // no running server: nothing to back up
    }

    [Fact]
    public async Task Restart_AnnounceThrowsWhileRunning_WarningsBestEffortAndRitualCompletes()
    {
        // Warning announces are best-effort: each failure is logged as announce-failed and
        // the ritual proceeds to stop/backup/start regardless.
        var (orch, sup, launcher, order, _, events, api) = Make();
        await sup.StartAsync(default);
        sup.MarkRunning();
        launcher.OnLaunch = _ => order.Add("start");
        api.AnnounceThrows = m => m.Contains("restarting in"); // only warnings fail; the stop-phase announce succeeds

        await orch.RestartAsync("admin@x.com", [10, 5, 1], default);

        Assert.Equal(["save", "shutdown", "backup", "start"], order.Where(o => o is not "info").ToList());
        Assert.Equal(ServerState.Starting, sup.State);
        Assert.Equal(3, events.Events.Count(e => e.Type == "announce-failed"));
    }

    [Fact]
    public async Task ConcurrentOp_LogsLifecycleBusy_AndCompletesAfterRitual()
    {
        // The restart ritual can hold the lifecycle gate for 10+ minutes of warning delays;
        // an op silently queued behind it would look hung to the operator. It must be loud.
        var (orch, sup, launcher, order, _, events, _) = Make();
        await sup.StartAsync(default);
        sup.MarkRunning();
        launcher.OnLaunch = _ => order.Add("start");
        var delayGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        orch.Delay = (_, _) => delayGate.Task;

        var restart = orch.RestartAsync("admin@x.com", [1], default);
        Assert.False(restart.IsCompleted);       // parked in the gated warning delay, holding the gate

        var stop = orch.StopAsync("ops@x.com", default);
        Assert.False(stop.IsCompleted);
        Assert.Contains(events.Events, e => e.Type == "lifecycle-busy" && e.Actor == "ops@x.com");

        delayGate.SetResult();                   // warning delay elapses; ritual finishes and releases the gate
        await restart;
        await stop;
        Assert.Equal(ServerState.Stopped, sup.State);
    }

    [Fact]
    public async Task Stop_AnnouncesSavesShutsDownAndBacksUp()
    {
        var (orch, sup, launcher, order, backup, _, _) = Make();
        await sup.StartAsync(default);
        sup.MarkRunning();

        await orch.StopAsync("admin@x.com", default);

        Assert.Equal(["info", "save", "shutdown", "backup"], order);
        Assert.Equal(ServerState.Stopped, sup.State);
        Assert.Equal(["pre-stop (admin@x.com)"], backup.Reasons);
    }

    [Fact]
    public async Task Stop_WhenAlreadyStopped_SkipsBackup()
    {
        // No server was running: there is no world state worth snapshotting, and a
        // "pre-stop" backup of an idle save directory would just churn the retention window.
        var (orch, sup, _, order, backup, _, _) = Make();

        await orch.StopAsync("admin@x.com", default);

        Assert.Equal(ServerState.Stopped, sup.State);
        Assert.Empty(order);
        Assert.Empty(backup.Reasons);
    }

    [Fact]
    public async Task Start_LogsEventAndStartsSupervisor()
    {
        var (orch, sup, _, _, _, events, _) = Make();
        await orch.StartAsync("admin@x.com", default);
        Assert.Equal(ServerState.Starting, sup.State);
        Assert.Contains(events.Events, e => e.Type == "start" && e.Actor == "admin@x.com");
    }

    [Fact]
    public async Task Save_CallsApiAndLogsEvent()
    {
        var (orch, _, _, order, _, events, _) = Make();
        await orch.SaveAsync("admin@x.com", default);
        Assert.Equal(["save"], order);
        Assert.Contains(events.Events, e => e.Type == "save" && e.Actor == "admin@x.com");
    }

    [Fact]
    public async Task Announce_CallsApiAndLogsEvent()
    {
        var (orch, _, _, order, _, events, _) = Make();
        await orch.AnnounceAsync("admin@x.com", "hello everyone", default);
        Assert.Equal(["info"], order); // no digit in the message
        Assert.Contains(events.Events, e => e.Type == "announce" && e.Detail == "hello everyone" && e.Actor == "admin@x.com");
    }

    [Fact]
    public async Task Kick_CallsApiAndLogsEventWithActorAndName()
    {
        var (orch, _, _, order, _, events, _) = Make();
        await orch.KickAsync("admin@x.com", "user-123", "Alice", default);
        Assert.Equal(["kick:user-123"], order);
        Assert.Contains(events.Events, e => e.Type == "kick" && e.Actor == "admin@x.com"
            && e.Detail.Contains("Alice") && e.Detail.Contains("user-123"));
    }

    [Fact]
    public async Task Ban_CallsApiAndLogsEventWithActorAndName()
    {
        var (orch, _, _, order, _, events, _) = Make();
        await orch.BanAsync("admin@x.com", "user-123", "Alice", default);
        Assert.Equal(["ban:user-123"], order);
        Assert.Contains(events.Events, e => e.Type == "ban" && e.Actor == "admin@x.com"
            && e.Detail.Contains("Alice") && e.Detail.Contains("user-123"));
    }

    // --- Server-side authorization backstop (against the real AdminGuard + real DB) ---

    [Fact]
    public async Task Kick_ByViewer_ThrowsUnauthorized_LogsEvent_AndNeverCallsApi()
    {
        var (orch, events, order) = await MakeWithRealGuardAsync(("viewer@x.com", "Viewer"));

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => orch.KickAsync("viewer@x.com", "user-1", "Alice", default));

        Assert.Empty(order); // guard rejected before any api.KickAsync call was recorded
        Assert.Contains(events.Events, e => e.Type == "unauthorized-action" && e.Actor == "viewer@x.com"
            && e.Detail.Contains("viewer@x.com") && e.Detail.Contains("Kick"));
        Assert.DoesNotContain(events.Events, e => e.Type == "kick");
    }

    [Fact]
    public async Task Kick_ByAdmin_Proceeds()
    {
        var (orch, events, order) = await MakeWithRealGuardAsync(("admin2@x.com", "Admin"));

        await orch.KickAsync("admin2@x.com", "user-1", "Alice", default);

        Assert.Equal(["kick:user-1"], order);
        Assert.Contains(events.Events, e => e.Type == "kick" && e.Actor == "admin2@x.com");
    }

    [Fact]
    public async Task Kick_ByScheduler_ProceedsWithoutAnyDbRecord()
    {
        // No users seeded at all: "scheduler" must still be authorized purely by the
        // constant, never via a Users-table lookup.
        var (orch, _, order) = await MakeWithRealGuardAsync();

        await orch.KickAsync(AdminGuard.SchedulerActor, "user-1", "Alice", default);

        Assert.Equal(["kick:user-1"], order);
    }

    [Fact]
    public async Task AllMutatingMethods_RejectNonAdminActor()
    {
        var (orch, _, order) = await MakeWithRealGuardAsync(("viewer@x.com", "Viewer"));

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => orch.StartAsync("viewer@x.com", default));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => orch.StopAsync("viewer@x.com", default));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => orch.RestartAsync("viewer@x.com", null, default));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => orch.SaveAsync("viewer@x.com", default));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => orch.AnnounceAsync("viewer@x.com", "hi", default));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => orch.KickAsync("viewer@x.com", "u1", "Alice", default));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => orch.BanAsync("viewer@x.com", "u1", "Alice", default));

        Assert.Empty(order); // nothing ever reached the API/supervisor/backup layers
    }
}
