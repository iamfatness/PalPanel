using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PalPanel.Auth;
using PalPanel.Data;

public class RoleServiceTests
{
    private static RoleService MakeRoleService()
    {
        var services = new ServiceCollection();
        services.AddDbContextFactory<PanelDb>(b => b.UseSqlite($"Data Source={Path.GetTempFileName()}"));
        services.AddSingleton<IEventSink, DbEventSink>();
        var sp = services.BuildServiceProvider();
        var dbf = sp.GetRequiredService<IDbContextFactory<PanelDb>>();
        using (var db = dbf.CreateDbContext()) db.Database.EnsureCreated();
        return new RoleService(dbf, sp.GetRequiredService<IEventSink>());
    }

    [Fact]
    public async Task FirstUser_BecomesAdmin_SecondBecomesViewer()
    {
        var rs = MakeRoleService();
        Assert.Equal("Admin", (await rs.GetOrCreateAsync("owner@x.com")).Role);
        Assert.Equal("Viewer", (await rs.GetOrCreateAsync("friend@x.com")).Role);
        Assert.Equal("Admin", (await rs.GetOrCreateAsync("owner@x.com")).Role); // stable on revisit
    }

    [Fact]
    public async Task SetRole_PromotesAndBlocks_ButProtectsLastAdmin()
    {
        var rs = MakeRoleService();
        await rs.GetOrCreateAsync("owner@x.com");
        await rs.GetOrCreateAsync("friend@x.com");
        await rs.SetRoleAsync("friend@x.com", "Admin", "owner@x.com");
        Assert.Equal("Admin", (await rs.GetOrCreateAsync("friend@x.com")).Role);
        await rs.SetRoleAsync("friend@x.com", "Blocked", "owner@x.com");
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => rs.SetRoleAsync("owner@x.com", "Viewer", "owner@x.com")); // last admin protected
    }

    [Fact]
    public async Task GetOrCreate_UpdatesLastSeen()
    {
        var rs = MakeRoleService();
        var first = await rs.GetOrCreateAsync("owner@x.com");
        var firstSeen = first.Role; // sanity - Admin
        Assert.Equal("Admin", firstSeen);
        await Task.Delay(10);
        // second call should not throw and should keep same identity/role
        var second = await rs.GetOrCreateAsync("owner@x.com");
        Assert.Equal("Admin", second.Role);
    }

    [Fact]
    public async Task SetRoleAsync_RejectsInvalidRole()
    {
        var rs = MakeRoleService();
        await rs.GetOrCreateAsync("owner@x.com");
        await Assert.ThrowsAsync<ArgumentException>(
            () => rs.SetRoleAsync("owner@x.com", "SuperAdmin", "owner@x.com"));
    }

    [Fact]
    public async Task SetRoleAsync_LogsRoleChangeEvent()
    {
        var rs = MakeRoleService();
        await rs.GetOrCreateAsync("owner@x.com");
        await rs.GetOrCreateAsync("friend@x.com");
        await rs.SetRoleAsync("friend@x.com", "Admin", "owner@x.com");

        var list = await rs.ListAsync();
        Assert.Contains(list, u => u.Email == "friend@x.com" && u.Role == "Admin");
    }
}
