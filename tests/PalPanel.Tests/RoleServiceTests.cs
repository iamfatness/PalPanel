using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PalPanel.Auth;
using PalPanel.Data;

public class RoleServiceTests
{
    private static (RoleService Rs, IDbContextFactory<PanelDb> Dbf, RoleChangeNotifier Notifier) MakeRoleServiceWithDb()
    {
        var services = new ServiceCollection();
        services.AddDbContextFactory<PanelDb>(b => b.UseSqlite($"Data Source={Path.GetTempFileName()}"));
        services.AddSingleton<IEventSink, DbEventSink>();
        var sp = services.BuildServiceProvider();
        var dbf = sp.GetRequiredService<IDbContextFactory<PanelDb>>();
        using (var db = dbf.CreateDbContext()) db.Database.EnsureCreated();
        var notifier = new RoleChangeNotifier();
        return (new RoleService(dbf, sp.GetRequiredService<IEventSink>(), notifier), dbf, notifier);
    }

    private static RoleService MakeRoleService() => MakeRoleServiceWithDb().Rs;

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
        var (rs, dbf, _) = MakeRoleServiceWithDb();
        await rs.GetOrCreateAsync("owner@x.com");
        DateTimeOffset initialLastSeen;
        using (var db = dbf.CreateDbContext())
            initialLastSeen = db.Users.Single(u => u.Email == "owner@x.com").LastSeen;

        await Task.Delay(20); // ensure the clock advances past SQLite's stored precision
        await rs.GetOrCreateAsync("owner@x.com");

        using var check = dbf.CreateDbContext();
        var updatedLastSeen = check.Users.Single(u => u.Email == "owner@x.com").LastSeen;
        Assert.True(updatedLastSeen > initialLastSeen,
            $"LastSeen should advance on revisit (was {initialLastSeen:O}, now {updatedLastSeen:O})");
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
    public async Task SetRoleAsync_FiresRoleChangeNotifier()
    {
        var (rs, _, notifier) = MakeRoleServiceWithDb();
        await rs.GetOrCreateAsync("owner@x.com");
        await rs.GetOrCreateAsync("friend@x.com");

        (string Email, string Role)? seen = null;
        notifier.RoleChanged += (email, role) => seen = (email, role);
        await rs.SetRoleAsync("friend@x.com", "Blocked", "owner@x.com");

        Assert.Equal(("friend@x.com", "Blocked"), seen);
    }

    [Fact]
    public async Task SetRoleAsync_LogsRoleChangeEvent()
    {
        var (rs, dbf, _) = MakeRoleServiceWithDb();
        await rs.GetOrCreateAsync("owner@x.com");
        await rs.GetOrCreateAsync("friend@x.com");
        await rs.SetRoleAsync("friend@x.com", "Admin", "owner@x.com");

        using var db = dbf.CreateDbContext();
        var evt = db.Events.Single(e => e.Type == "role-change");
        Assert.Equal("owner@x.com", evt.ActorEmail);
        Assert.Contains("friend@x.com", evt.Detail);
        Assert.Contains("Admin", evt.Detail);
    }
}
