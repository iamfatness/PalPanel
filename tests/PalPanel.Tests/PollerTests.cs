using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using PalPanel; using PalPanel.Data; using PalPanel.Monitoring; using PalPanel.PalApi; using PalPanel.Supervisor;

public class PollerTests : IAsyncLifetime
{
    private StubPalServer _stub = null!;
    private PollerService _poller = null!;
    private SnapshotService _snap = null!;
    private ProcessSupervisor _sup = null!;
    private IDbContextFactory<PanelDb> _dbf = null!;
    private FakeLauncher _launcher = null!;

    public Task InitializeAsync()
    {
        _stub = new StubPalServer { PlayerNames = ["Alice"] };
        var o = Options.Create(new PanelOptions { ApiBaseUrl = _stub.BaseUrl, AdminPassword = "pw", GracefulStopTimeoutSeconds = 1 });
        _launcher = new FakeLauncher();
        _sup = new ProcessSupervisor(_launcher, o) { RestartDelay = _ => Task.CompletedTask };
        _snap = new SnapshotService();
        var services = new ServiceCollection();
        services.AddDbContextFactory<PanelDb>(b => b.UseSqlite($"Data Source={Path.GetTempFileName()}"));
        var sp = services.BuildServiceProvider();
        _dbf = sp.GetRequiredService<IDbContextFactory<PanelDb>>();
        using (var db = _dbf.CreateDbContext()) db.Database.EnsureCreated();
        var api = new PalApiClient(new HttpClient(), o);
        _poller = new PollerService(api, _sup, _snap, _dbf, new DbEventSink(_dbf), o);
        return Task.CompletedTask;
    }
    public async Task DisposeAsync() => await _stub.DisposeAsync();

    [Fact]
    public async Task Tick_WhileStarting_PromotesToRunning_AndSamples()
    {
        await _sup.StartAsync(default);
        await _poller.TickAsync(default);
        Assert.Equal(ServerState.Running, _sup.State);
        Assert.True(_snap.Current.ApiReachable);
        Assert.Single(_snap.Current.Players);
        using var db = _dbf.CreateDbContext();
        Assert.Equal(1, db.Samples.Count());
    }

    [Fact]
    public async Task PlayerJoinLeave_CreatesAndClosesSessions()
    {
        await _sup.StartAsync(default);
        await _poller.TickAsync(default);                       // Alice online
        _stub.PlayerNames = ["Alice", "Bob"];
        await _poller.TickAsync(default);                       // Bob joins
        _stub.PlayerNames = ["Bob"];
        await _poller.TickAsync(default);                       // Alice leaves
        using var db = _dbf.CreateDbContext();
        var alice = db.Sessions.Single(s => s.Name == "Alice");
        Assert.NotNull(alice.LeftAt);
        var bob = db.Sessions.Single(s => s.Name == "Bob");
        Assert.Null(bob.LeftAt);
        Assert.Contains(db.Events, e => e.Type == "player-join" && e.Detail.Contains("Bob"));
    }

    [Fact]
    public async Task FirstTick_ReconcilesStaleSessionsFromPreviousRun()
    {
        // Simulates a panel restart while PalServer kept running: the previous run's
        // session row is still open in the DB but _online is a fresh empty dictionary.
        using (var db = _dbf.CreateDbContext())
        {
            db.Sessions.Add(new PlayerSession { UserId = "steam_ghost", Name = "Ghost", JoinedAt = DateTimeOffset.UtcNow.AddHours(-2) });
            db.SaveChanges();
        }
        await _sup.StartAsync(default);
        await _poller.TickAsync(default);                       // first tick: reconcile, then Alice joins fresh
        using var check = _dbf.CreateDbContext();
        var ghost = check.Sessions.Single(s => s.Name == "Ghost");
        Assert.NotNull(ghost.LeftAt);                           // stale row closed
        var alice = check.Sessions.Single(s => s.Name == "Alice");
        Assert.Null(alice.LeftAt);                              // fresh join row created
        Assert.Contains(check.Events, e => e.Type == "sessions-reconciled" && e.Detail.Contains("1"));
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

    private sealed class ThrowingSink : IEventSink
    {
        public SemaphoreSlim Called { get; } = new(0);
        public Task LogAsync(string type, string detail, string? actorEmail = null)
        { Called.Release(); throw new InvalidOperationException("event sink down (disk full)"); }
    }

    [Fact]
    public async Task ExecuteLoop_SurvivesTickFailure_EvenWhenEventSinkAlsoThrows()
    {
        await _sup.StartAsync(default);
        var o = Options.Create(new PanelOptions { ApiBaseUrl = _stub.BaseUrl, AdminPassword = "pw", PollIntervalSeconds = 60 });
        var sink = new ThrowingSink();
        var poller = new PollerService(new ThrowingApi(), _sup, _snap, _dbf, sink, o);
        await poller.StartAsync(default);
        Assert.True(await sink.Called.WaitAsync(TimeSpan.FromSeconds(5)));   // tick threw; poller-error write attempted and threw
        await Task.WhenAny(poller.ExecuteTask!, Task.Delay(250));            // give an unguarded loop time to fault
        Assert.False(poller.ExecuteTask!.IsFaulted);                         // loop must survive both failures
        await poller.StopAsync(default);
    }

    [Fact]
    public async Task ApiRecovery_LogsUnreachableAndRecoveredExactlyOnce()
    {
        await _sup.StartAsync(default);
        await _poller.TickAsync(default);                       // reachable
        _stub.Healthy = false;
        await _poller.TickAsync(default);                       // down -> api-unreachable
        await _poller.TickAsync(default);                       // still down (deduped)
        _stub.Healthy = true;
        await _poller.TickAsync(default);                       // back -> api-recovered
        await _poller.TickAsync(default);                       // still up (deduped)
        using var db = _dbf.CreateDbContext();
        Assert.Equal(1, db.Events.Count(e => e.Type == "api-unreachable"));
        Assert.Equal(1, db.Events.Count(e => e.Type == "api-recovered"));
    }

    [Fact]
    public async Task RunningButApiDown_PublishesDegradedOnce()
    {
        await _sup.StartAsync(default);
        await _poller.TickAsync(default);
        _stub.Healthy = false;
        await _poller.TickAsync(default);
        await _poller.TickAsync(default);
        Assert.Equal(ServerState.Running, _sup.State);
        Assert.False(_snap.Current.ApiReachable);
        using var db = _dbf.CreateDbContext();
        Assert.Equal(1, db.Events.Count(e => e.Type == "api-unreachable")); // deduped
    }
}
