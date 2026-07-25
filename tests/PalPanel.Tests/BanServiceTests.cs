using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PalPanel.Control;
using PalPanel.Data;

public class BanServiceTests
{
    private static (BanService svc, Guid server) Make()
    {
        var services = new ServiceCollection();
        services.AddDbContextFactory<PanelDb>(b => b.UseSqlite($"Data Source={Path.GetTempFileName()}"));
        var dbf = services.BuildServiceProvider().GetRequiredService<IDbContextFactory<PanelDb>>();
        using (var db = dbf.CreateDbContext()) db.Database.EnsureCreated();
        return (new BanService(dbf), Guid.NewGuid());
    }

    [Fact]
    public async Task Record_List_Remove_RoundTrips()
    {
        var (svc, sid) = Make();
        await svc.RecordAsync(sid, "steam_1", "Alice", "griefing", "admin@x");

        var list = await svc.ListAsync(sid);
        Assert.Single(list);
        Assert.Equal("Alice", list[0].Name);
        Assert.Equal("griefing", list[0].Reason);
        Assert.True(await svc.IsBannedAsync(sid, "steam_1"));

        await svc.RemoveAsync(sid, "steam_1");
        Assert.Empty(await svc.ListAsync(sid));
        Assert.False(await svc.IsBannedAsync(sid, "steam_1"));
    }

    [Fact]
    public async Task Record_IsIdempotent_UpdatesInsteadOfDuplicating()
    {
        var (svc, sid) = Make();
        await svc.RecordAsync(sid, "steam_1", "Alice", "first", "admin@x");
        await svc.RecordAsync(sid, "steam_1", "Alice", "updated reason", "admin@x");

        var list = await svc.ListAsync(sid);
        Assert.Single(list);
        Assert.Equal("updated reason", list[0].Reason);
    }

    [Fact]
    public async Task Bans_AreScopedPerServer()
    {
        var (svc, a) = Make();
        var b = Guid.NewGuid();
        await svc.RecordAsync(a, "steam_1", "Alice", "", "admin@x");
        Assert.Single(await svc.ListAsync(a));
        Assert.Empty(await svc.ListAsync(b));
    }
}
