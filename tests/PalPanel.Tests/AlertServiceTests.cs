using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PalPanel.Control;
using PalPanel.Data;

public class AlertServiceTests
{
    private sealed class RecordingNotifier : IAlertNotifier
    {
        public List<Alert> Sent { get; } = [];
        public Task SendAsync(Alert alert, CancellationToken ct = default) { Sent.Add(alert); return Task.CompletedTask; }
        public Task SendTestAsync(CancellationToken ct = default) => Task.CompletedTask;
    }

    private static IDbContextFactory<PanelDb> NewDb()
    {
        var services = new ServiceCollection();
        services.AddDbContextFactory<PanelDb>(b => b.UseSqlite($"Data Source={Path.GetTempFileName()}"));
        var dbf = services.BuildServiceProvider().GetRequiredService<IDbContextFactory<PanelDb>>();
        using (var db = dbf.CreateDbContext()) db.Database.EnsureCreated();
        return dbf;
    }

    [Fact]
    public async Task Raise_CreatesActiveAlert_AndNotifies()
    {
        var n = new RecordingNotifier();
        var svc = new AlertService(NewDb(), n);
        var sid = Guid.NewGuid();

        await svc.RaiseAsync(sid, "S", "server-down", AlertSeverity.Warning, "crashed", "d");

        var list = await svc.ListAsync();
        Assert.Single(list);
        Assert.Null(list[0].ResolvedAt);
        Assert.Equal(AlertSeverity.Warning, list[0].Severity);
        Assert.Single(n.Sent);
    }

    [Fact]
    public async Task Raise_SameKeyNotEscalated_UpdatesInPlace_NoSecondAlertNoSecondEmail()
    {
        var n = new RecordingNotifier();
        var svc = new AlertService(NewDb(), n);
        var sid = Guid.NewGuid();

        await svc.RaiseAsync(sid, "S", "server-down", AlertSeverity.Warning, "crashed", "d1");
        await svc.RaiseAsync(sid, "S", "server-down", AlertSeverity.Warning, "crashed again", "d2");

        var list = await svc.ListAsync();
        Assert.Single(list);                       // deduped: still one alert
        Assert.Equal("crashed again", list[0].Title);  // updated in place
        Assert.Single(n.Sent);                     // no repeat email
    }

    [Fact]
    public async Task Raise_Escalation_UpdatesSeverity_AndEmailsAgain()
    {
        var n = new RecordingNotifier();
        var svc = new AlertService(NewDb(), n);
        var sid = Guid.NewGuid();

        await svc.RaiseAsync(sid, "S", "server-down", AlertSeverity.Warning, "crashed", "d");
        await svc.RaiseAsync(sid, "S", "server-down", AlertSeverity.Critical, "crash loop held", "d");

        var list = await svc.ListAsync();
        Assert.Single(list);
        Assert.Equal(AlertSeverity.Critical, list[0].Severity);
        Assert.Equal(2, n.Sent.Count);             // escalation re-notified
    }

    [Fact]
    public async Task Resolve_ClearsActive_AddsRecoveredInfo_NotEmailed()
    {
        var n = new RecordingNotifier();
        var svc = new AlertService(NewDb(), n);
        var sid = Guid.NewGuid();

        await svc.RaiseAsync(sid, "S", "reachable", AlertSeverity.Warning, "unreachable", "d");
        await svc.ResolveAsync(sid, "reachable", "recovered");

        var list = await svc.ListAsync();
        Assert.Equal(2, list.Count);
        Assert.All(list, a => Assert.NotNull(a.ResolvedAt));    // original resolved + recovered self-resolved
        Assert.Contains(list, a => a.Title == "recovered" && a.Severity == AlertSeverity.Info);
        Assert.Single(n.Sent);                                  // recovered (Info) is in-panel only
    }

    [Fact]
    public async Task Resolve_NothingActive_IsNoOp()
    {
        var n = new RecordingNotifier();
        var svc = new AlertService(NewDb(), n);
        await svc.ResolveAsync(Guid.NewGuid(), "reachable", "recovered");
        Assert.Empty(await svc.ListAsync());
        Assert.Empty(n.Sent);
    }

    [Fact]
    public async Task HostAlert_NullServerId_DedupsCorrectly()
    {
        var n = new RecordingNotifier();
        var svc = new AlertService(NewDb(), n);

        await svc.RaiseAsync(null, "host", "disk:C:\\", AlertSeverity.Warning, "low disk", "d1");
        await svc.RaiseAsync(null, "host", "disk:C:\\", AlertSeverity.Warning, "low disk", "d2");

        Assert.Single(await svc.ListAsync());   // null ServerId is null-aware in the dedup query
        Assert.Single(n.Sent);
    }

    [Fact]
    public async Task Notify_CreatesResolvedRow_AndEmails()
    {
        var n = new RecordingNotifier();
        var svc = new AlertService(NewDb(), n);
        await svc.NotifyAsync(Guid.NewGuid(), "S", "auto-restart", AlertSeverity.Info, "auto-restarted", "r");
        var list = await svc.ListAsync();
        Assert.Single(list);
        Assert.NotNull(list[0].ResolvedAt);
        Assert.Single(n.Sent);
    }

    [Fact]
    public async Task Acknowledge_DrivesUnackedCount()
    {
        var svc = new AlertService(NewDb(), new RecordingNotifier());
        var sid = Guid.NewGuid();
        await svc.RaiseAsync(sid, "S", "server-down", AlertSeverity.Warning, "a", "d");
        await svc.NotifyAsync(sid, "S", "auto-restart", AlertSeverity.Info, "b", "d");
        Assert.Equal(2, await svc.UnacknowledgedCountAsync());

        var first = (await svc.ListAsync())[0].Id;
        await svc.AcknowledgeAsync(first);
        Assert.Equal(1, await svc.UnacknowledgedCountAsync());

        await svc.AcknowledgeAllAsync();
        Assert.Equal(0, await svc.UnacknowledgedCountAsync());
    }
}
