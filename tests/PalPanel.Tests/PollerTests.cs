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
