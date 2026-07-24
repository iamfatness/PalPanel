using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PalPanel.Auth;
using PalPanel.Data;
using PalPanel.Servers;
using PalPanel.Supervisor;

public class ServerRuntimeTests
{
    private class IdentityProtector : ISecretProtector
    {
        public string Protect(string p) => p;
        public string Unprotect(string c) => c;
    }

    private class AllowAllGuard : IAdminGuard
    {
        public Task EnsureAdminAsync(string actor, string action, CancellationToken ct) => Task.CompletedTask;
    }

    private static IDbContextFactory<PanelDb> Dbf()
    {
        var services = new ServiceCollection();
        services.AddDbContextFactory<PanelDb>(b => b.UseSqlite($"Data Source={Path.GetTempFileName()}"));
        var sp = services.BuildServiceProvider();
        var dbf = sp.GetRequiredService<IDbContextFactory<PanelDb>>();
        using var db = dbf.CreateDbContext();
        db.Database.EnsureCreated();
        return dbf;
    }

    [Fact]
    public void Build_YieldsRuntimeBoundToConfig_StoppedInitially()
    {
        var cfg = new ServerConfig { Id = Guid.NewGuid(), Name = "A", ApiBaseUrl = "http://localhost:8212", AdminPasswordEnc = "pw" };
        var rt = ServerRuntime.Build(cfg, new FakeLauncher(), new HttpClient(), Dbf(), new AllowAllGuard(), new IdentityProtector());

        Assert.Equal(cfg.Id, rt.Id);
        Assert.Equal(ServerState.Stopped, rt.State);
        Assert.NotNull(rt.Orchestrator);
        Assert.NotNull(rt.Backups);
        Assert.NotNull(rt.Snapshot);
    }

    [Fact]
    public async Task SupervisorEvents_AreStampedWithServerId()
    {
        var dbf = Dbf();
        var cfg = new ServerConfig { Id = Guid.NewGuid(), Name = "A", ApiBaseUrl = "http://localhost:8212", AdminPasswordEnc = "pw" };
        var rt = ServerRuntime.Build(cfg, new FakeLauncher(), new HttpClient(), dbf, new AllowAllGuard(), new IdentityProtector());

        await rt.Events.LogAsync("test-event", "detail");

        await using var db = await dbf.CreateDbContextAsync();
        var row = db.Events.Single(e => e.Type == "test-event");
        Assert.Equal(cfg.Id, row.ServerId);
    }
}
