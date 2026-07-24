using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PalPanel.Auth;
using PalPanel.Data;
using PalPanel.Servers;

public class ServerManagerTests
{
    private class IdentityProtector : ISecretProtector
    {
        public string Protect(string p) => p;
        public string Unprotect(string c) => c;
    }
    private class AllowAllGuard : IAdminGuard
    {
        public Task EnsureAdminAsync(string a, string x, CancellationToken ct) => Task.CompletedTask;
    }
    private class StubHttpFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }

    private static (ServerManager mgr, IDbContextFactory<PanelDb> dbf) Make()
    {
        var services = new ServiceCollection();
        services.AddDbContextFactory<PanelDb>(b => b.UseSqlite($"Data Source={Path.GetTempFileName()}"));
        var sp = services.BuildServiceProvider();
        var dbf = sp.GetRequiredService<IDbContextFactory<PanelDb>>();
        using (var db = dbf.CreateDbContext()) db.Database.EnsureCreated();
        var mgr = new ServerManager(dbf, new FakeLauncher(), new StubHttpFactory(), new AllowAllGuard(), new IdentityProtector());
        return (mgr, dbf);
    }

    private static ServerConfig Cfg(string name, string proc) =>
        new() { Id = Guid.NewGuid(), Name = name, ProcessName = proc, ApiBaseUrl = "http://localhost:8212" };

    [Fact]
    public async Task Add_PersistsRow_AndCreatesRuntime()
    {
        var (mgr, dbf) = Make();
        var id = await mgr.AddAsync(Cfg("A", "PalA"), "pw");

        Assert.NotNull(mgr.Get(id));
        await using var db = await dbf.CreateDbContextAsync();
        Assert.Single(db.Servers);
        Assert.Equal("pw", db.Servers.Single().AdminPasswordEnc); // identity protector
    }

    [Fact]
    public async Task Initialize_LoadsOnlyEnabledServers()
    {
        var (mgr, dbf) = Make();
        await using (var db = await dbf.CreateDbContextAsync())
        {
            db.Servers.Add(new ServerConfig { Id = Guid.NewGuid(), Name = "On", ProcessName = "P1", ApiBaseUrl = "http://localhost:8212", Enabled = true });
            db.Servers.Add(new ServerConfig { Id = Guid.NewGuid(), Name = "Off", ProcessName = "P2", ApiBaseUrl = "http://localhost:8213", Enabled = false });
            await db.SaveChangesAsync();
        }
        await mgr.InitializeAsync();
        Assert.Single(mgr.All());
    }

    [Fact]
    public async Task Remove_DropsRuntimeAndRow()
    {
        var (mgr, dbf) = Make();
        var id = await mgr.AddAsync(Cfg("A", "PalA"), "pw");
        await mgr.RemoveAsync(id, "admin@test");

        Assert.Null(mgr.Get(id));
        await using var db = await dbf.CreateDbContextAsync();
        Assert.Empty(db.Servers);
    }

    [Fact]
    public async Task Add_RejectsDuplicateProcessName()
    {
        var (mgr, _) = Make();
        await mgr.AddAsync(Cfg("A", "PalShared"), "pw");
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => mgr.AddAsync(Cfg("B", "PalShared"), "pw"));
    }

    [Fact]
    public async Task Get_UnknownId_ReturnsNull()
    {
        var (mgr, _) = Make();
        Assert.Null(mgr.Get(Guid.NewGuid()));
    }
}
