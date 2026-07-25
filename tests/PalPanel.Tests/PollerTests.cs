using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using PalPanel; using PalPanel.Auth; using PalPanel.Control; using PalPanel.Data;
using PalPanel.Monitoring; using PalPanel.PalApi; using PalPanel.Servers; using PalPanel.Supervisor;

public class PollerTests : IAsyncLifetime
{
    private StubPalServer _stub = null!;
    private PollerService _poller = null!;
    private ServerRuntime _rt = null!;
    private ProcessSupervisor _sup = null!;
    private IDbContextFactory<PanelDb> _dbf = null!;
    private FakeLauncher _launcher = null!;
    private readonly Guid _sid = Guid.NewGuid();

    private class AllowAllGuard : IAdminGuard
    { public Task EnsureAdminAsync(string a, string x, CancellationToken ct) => Task.CompletedTask; }

    private SnapshotService Snap => _rt.Snapshot;

    // Build a ServerRuntime around a supervisor/api/sink, reusing the real per-server services.
    private ServerRuntime MakeRuntime(Guid id, IPalApi api, ProcessSupervisor sup, IEventSink sink)
    {
        var cfg = new ServerConfig { Id = id, Name = "t", ApiBaseUrl = "http://localhost:1", ProcessName = $"p-{id:N}", PollIntervalSeconds = 60 };
        var opts = Options.Create(new PanelOptions { SaveDirectory = Path.GetTempPath(), BackupDirectory = Path.GetTempPath(), GracefulStopTimeoutSeconds = 1 });
        var backups = new BackupService(opts, sup, sink);
        var orch = new ServerOrchestrator(sup, api, backups, sink, new AllowAllGuard());
        return ServerRuntime.FromParts(cfg, sup, api, orch, backups, new SnapshotService(), sink);
    }

    public Task InitializeAsync()
    {
        _stub = new StubPalServer { PlayerNames = ["Alice"] };
        var o = Options.Create(new PanelOptions { GracefulStopTimeoutSeconds = 1 });
        _launcher = new FakeLauncher();
        _sup = new ProcessSupervisor(_launcher, o) { RestartDelay = _ => Task.CompletedTask };
        var services = new ServiceCollection();
        services.AddDbContextFactory<PanelDb>(b => b.UseSqlite($"Data Source={Path.GetTempFileName()}"));
        var sp = services.BuildServiceProvider();
        _dbf = sp.GetRequiredService<IDbContextFactory<PanelDb>>();
        using (var db = _dbf.CreateDbContext()) db.Database.EnsureCreated();
        var api = new PalApiClient(new HttpClient(), new PalApiSettings(_stub.BaseUrl, "pw"));
        var sink = new ServerEventSink(_dbf, _sid);
        _rt = MakeRuntime(_sid, api, _sup, sink);
        _poller = new PollerService(NullManager(), _dbf);
        return Task.CompletedTask;
    }
    public async Task DisposeAsync() => await _stub.DisposeAsync();

    // A ServerManager isn't needed to exercise TickServerAsync directly; supply a minimal one.
    private ServerManager NullManager()
    {
        var services = new ServiceCollection();
        services.AddDbContextFactory<PanelDb>(b => b.UseSqlite($"Data Source={Path.GetTempFileName()}"));
        var sp = services.BuildServiceProvider();
        var dbf = sp.GetRequiredService<IDbContextFactory<PanelDb>>();
        using (var db = dbf.CreateDbContext()) db.Database.EnsureCreated();
        return new ServerManager(dbf, new FakeLauncher(), new StubHttpFactory(), new AllowAllGuard(), new IdentityProtector());
    }
    private class StubHttpFactory : IHttpClientFactory { public HttpClient CreateClient(string name) => new(); }
    private class IdentityProtector : ISecretProtector { public string Protect(string p) => p; public string Unprotect(string c) => c; }

    [Fact]
    public async Task Tick_WhileStarting_PromotesToRunning_AndSamplesWithServerId()
    {
        await _sup.StartAsync(default);
        await _poller.TickServerAsync(_rt, default);
        Assert.Equal(ServerState.Running, _sup.State);
        Assert.True(Snap.Current.ApiReachable);
        Assert.Single(Snap.Current.Players);
        using var db = _dbf.CreateDbContext();
        var sample = Assert.Single(db.Samples);
        Assert.Equal(_sid, sample.ServerId);
    }

    [Fact]
    public async Task PlayerJoinLeave_CreatesAndClosesSessions_ScopedToServer()
    {
        await _sup.StartAsync(default);
        await _poller.TickServerAsync(_rt, default);
        _stub.PlayerNames = ["Alice", "Bob"];
        await _poller.TickServerAsync(_rt, default);
        _stub.PlayerNames = ["Bob"];
        await _poller.TickServerAsync(_rt, default);
        using var db = _dbf.CreateDbContext();
        var alice = db.Sessions.Single(s => s.Name == "Alice");
        Assert.NotNull(alice.LeftAt);
        Assert.Equal(_sid, alice.ServerId);
        var bob = db.Sessions.Single(s => s.Name == "Bob");
        Assert.Null(bob.LeftAt);
        Assert.Contains(db.Events, e => e.Type == "player-join" && e.Detail.Contains("Bob") && e.ServerId == _sid);
    }

    [Fact]
    public async Task FirstTick_ReconcilesStaleSessionsForThisServerOnly()
    {
        var otherServer = Guid.NewGuid();
        using (var db = _dbf.CreateDbContext())
        {
            db.Sessions.Add(new PlayerSession { ServerId = _sid, UserId = "steam_ghost", Name = "Ghost", JoinedAt = DateTimeOffset.UtcNow.AddHours(-2) });
            db.Sessions.Add(new PlayerSession { ServerId = otherServer, UserId = "steam_other", Name = "OtherGhost", JoinedAt = DateTimeOffset.UtcNow.AddHours(-2) });
            db.SaveChanges();
        }
        await _sup.StartAsync(default);
        await _poller.TickServerAsync(_rt, default);
        using var check = _dbf.CreateDbContext();
        Assert.NotNull(check.Sessions.Single(s => s.Name == "Ghost").LeftAt);       // this server's stale row closed
        Assert.Null(check.Sessions.Single(s => s.Name == "OtherGhost").LeftAt);     // other server's row untouched
        Assert.Contains(check.Events, e => e.Type == "sessions-reconciled" && e.ServerId == _sid);
    }

    [Fact]
    public async Task ApiRecovery_LogsUnreachableAndRecoveredExactlyOnce()
    {
        await _sup.StartAsync(default);
        await _poller.TickServerAsync(_rt, default);
        _stub.Healthy = false;
        await _poller.TickServerAsync(_rt, default);
        await _poller.TickServerAsync(_rt, default);
        _stub.Healthy = true;
        await _poller.TickServerAsync(_rt, default);
        await _poller.TickServerAsync(_rt, default);
        using var db = _dbf.CreateDbContext();
        Assert.Equal(1, db.Events.Count(e => e.Type == "api-unreachable"));
        Assert.Equal(1, db.Events.Count(e => e.Type == "api-recovered"));
    }

    [Fact]
    public async Task Snapshot_ReportsRealGameProcessMemory_NotTheTrackedLauncher()
    {
        // The tracked (launcher) process reports 123 MB; the real game process reports ~5 GB.
        _launcher.WorkingSetByName = 5_000_000_000;
        await _sup.StartAsync(default);
        await _poller.TickServerAsync(_rt, default);
        Assert.Equal(5_000_000_000, Snap.Current.MemoryBytes);
    }

    [Fact]
    public async Task SingleFailedPoll_IsToleratedBeforeMarkingUnreachable()
    {
        await _sup.StartAsync(default);
        await _poller.TickServerAsync(_rt, default);   // reachable
        _stub.Healthy = false;
        await _poller.TickServerAsync(_rt, default);    // 1st failure — hysteresis tolerates it
        Assert.True(Snap.Current.ApiReachable);         // not yet flapped to unreachable
        using (var db1 = _dbf.CreateDbContext())
            Assert.Empty(db1.Events.Where(e => e.Type == "api-unreachable"));
        await _poller.TickServerAsync(_rt, default);    // 2nd consecutive failure — now unreachable
        Assert.False(Snap.Current.ApiReachable);
        using var db = _dbf.CreateDbContext();
        Assert.Equal(1, db.Events.Count(e => e.Type == "api-unreachable"));
    }

    [Fact]
    public async Task AutoRestart_TriggersOnMemoryBlowup_WhenEnabled()
    {
        _launcher.WorkingSetByName = 5_000_000_000; // 5 GB real process
        _rt.Config.AutoRestartMemoryGb = 1;         // restart when over 1 GB
        await _sup.StartAsync(default);
        await _poller.TickServerAsync(_rt, default);
        using var db = _dbf.CreateDbContext();
        Assert.Contains(db.Events, e => e.Type == "auto-restart" && e.Detail.Contains("memory"));
    }

    [Fact]
    public async Task AutoRestart_DoesNotTrigger_WhenDisabled()
    {
        _launcher.WorkingSetByName = 5_000_000_000;
        _rt.Config.AutoRestartMemoryGb = 0;         // off
        _rt.Config.AutoRestartUnreachableMinutes = 0;
        await _sup.StartAsync(default);
        await _poller.TickServerAsync(_rt, default);
        using var db = _dbf.CreateDbContext();
        Assert.DoesNotContain(db.Events, e => e.Type == "auto-restart");
    }

    [Fact]
    public async Task OneServerDown_DoesNotStallAnother()
    {
        // Reachable server (the stub) and an unreachable server (throwing API). Ticking both
        // must succeed for the healthy one and not throw for the broken one.
        await _sup.StartAsync(default);
        var downSid = Guid.NewGuid();
        var downSup = new ProcessSupervisor(new FakeLauncher(), Options.Create(new PanelOptions())) { RestartDelay = _ => Task.CompletedTask };
        await downSup.StartAsync(default);
        var downRt = MakeRuntime(downSid, new ThrowingApi(), downSup, new ServerEventSink(_dbf, downSid));

        await _poller.TickServerAsync(_rt, default);                                 // healthy
        var ex = await Record.ExceptionAsync(() => TickGuarded(downRt));             // broken, guarded like the loop does
        Assert.Null(ex);

        Assert.True(Snap.Current.ApiReachable);                                      // healthy server sampled
        using var db = _dbf.CreateDbContext();
        Assert.Equal(_sid, db.Samples.Single().ServerId);
    }

    // Mirror ExecuteAsync's per-server guard so a throwing tick is contained.
    private async Task TickGuarded(ServerRuntime rt)
    {
        try { await _poller.TickServerAsync(rt, default); }
        catch (Exception ex) { try { await rt.Events.LogAsync("poller-error", ex.Message); } catch { } }
    }

    private sealed class ThrowingApi : IPalApi
    {
        public Task<ServerInfo?> GetInfoAsync(CancellationToken ct) => throw new InvalidOperationException("api exploded");
        public Task<IReadOnlyList<PlayerInfo>> GetPlayersAsync(CancellationToken ct) => throw new NotSupportedException();
        public Task<ServerMetrics?> GetMetricsAsync(CancellationToken ct) => throw new NotSupportedException();
        public Task AnnounceAsync(string message, CancellationToken ct) => throw new NotSupportedException();
        public Task KickAsync(string userId, string message, CancellationToken ct) => throw new NotSupportedException();
        public Task BanAsync(string userId, string message, CancellationToken ct) => throw new NotSupportedException();
        public Task UnbanAsync(string userId, CancellationToken ct) => throw new NotSupportedException();
        public Task SaveAsync(CancellationToken ct) => throw new NotSupportedException();
        public Task ShutdownAsync(int waitSeconds, string message, CancellationToken ct) => throw new NotSupportedException();
    }
}
