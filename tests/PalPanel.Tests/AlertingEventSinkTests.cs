using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PalPanel.Control;
using PalPanel.Data;
using PalPanel.Servers;

public class AlertingEventSinkTests
{
    private sealed class NoopNotifier : IAlertNotifier
    {
        public Task SendAsync(Alert alert, CancellationToken ct = default) => Task.CompletedTask;
        public Task SendTestAsync(CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class RecordingSink : IEventSink
    {
        public List<(string Type, string Detail)> Events { get; } = [];
        public Task LogAsync(string type, string detail, string? actorEmail = null)
        { Events.Add((type, detail)); return Task.CompletedTask; }
    }

    private static IDbContextFactory<PanelDb> NewDb()
    {
        var services = new ServiceCollection();
        services.AddDbContextFactory<PanelDb>(b => b.UseSqlite($"Data Source={Path.GetTempFileName()}"));
        var dbf = services.BuildServiceProvider().GetRequiredService<IDbContextFactory<PanelDb>>();
        using (var db = dbf.CreateDbContext()) db.Database.EnsureCreated();
        return dbf;
    }

    private static (AlertingEventSink sink, RecordingSink inner, AlertService svc) Make()
    {
        var svc = new AlertService(NewDb(), new NoopNotifier());
        var inner = new RecordingSink();
        return (new AlertingEventSink(inner, svc, Guid.NewGuid(), "S"), inner, svc);
    }

    [Fact]
    public async Task Crash_ForwardsEvent_AndRaisesServerDownWarning()
    {
        var (sink, inner, svc) = Make();
        await sink.LogAsync("crash", "boom");

        Assert.Contains(("crash", "boom"), inner.Events);   // raw event still persisted
        var a = Assert.Single(await svc.ListAsync());
        Assert.Equal("server-down", a.Key);
        Assert.Equal(AlertSeverity.Warning, a.Severity);
        Assert.Null(a.ResolvedAt);
    }

    [Fact]
    public async Task CrashThenHeld_EscalatesToCritical_StillOneActiveAlert()
    {
        var (sink, _, svc) = Make();
        await sink.LogAsync("crash", "boom");
        await sink.LogAsync("held", "3 crashes in 10 min");

        var active = (await svc.ListAsync()).Where(a => a.ResolvedAt == null).ToList();
        Assert.Single(active);
        Assert.Equal(AlertSeverity.Critical, active[0].Severity);
    }

    [Fact]
    public async Task ApiRecovered_ResolvesReachableAndServerDown()
    {
        var (sink, _, svc) = Make();
        await sink.LogAsync("api-unreachable", "no answer");
        await sink.LogAsync("crash", "boom");
        await sink.LogAsync("api-recovered", "answering");

        var active = (await svc.ListAsync()).Where(a => a.ResolvedAt == null).ToList();
        Assert.Empty(active);
    }

    [Fact]
    public async Task Start_ResolvesServerDown()
    {
        var (sink, _, svc) = Make();
        await sink.LogAsync("held", "loop");
        await sink.LogAsync("start", "Start requested");

        var active = (await svc.ListAsync()).Where(a => a.ResolvedAt == null).ToList();
        Assert.Empty(active);
    }

    [Fact]
    public async Task UnmappedEvent_RaisesNoAlert()
    {
        var (sink, inner, svc) = Make();
        await sink.LogAsync("player-join", "Alice joined");
        Assert.Contains(("player-join", "Alice joined"), inner.Events);
        Assert.Empty(await svc.ListAsync());
    }
}
