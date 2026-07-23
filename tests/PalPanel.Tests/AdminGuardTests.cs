using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using PalPanel;
using PalPanel.Auth;
using PalPanel.Data;

public class AdminGuardTests
{
    private static (AdminGuard Guard, IDbContextFactory<PanelDb> Dbf, List<(string Type, string Detail, string? Actor)> Events)
        Make(bool authDisabled)
    {
        var services = new ServiceCollection();
        services.AddDbContextFactory<PanelDb>(b => b.UseSqlite($"Data Source={Path.GetTempFileName()}"));
        var sp = services.BuildServiceProvider();
        var dbf = sp.GetRequiredService<IDbContextFactory<PanelDb>>();
        using (var db = dbf.CreateDbContext()) db.Database.EnsureCreated();

        var events = new List<(string Type, string Detail, string? Actor)>();
        var sink = new RecordingEventSink(events);
        var options = Options.Create(new PanelOptions { AuthDisabled = authDisabled });
        var guard = new AdminGuard(dbf, sink, options);
        return (guard, dbf, events);
    }

    private class RecordingEventSink(List<(string Type, string Detail, string? Actor)> events) : IEventSink
    {
        public Task LogAsync(string type, string detail, string? actorEmail = null)
        { events.Add((type, detail, actorEmail)); return Task.CompletedTask; }
    }

    // AuthDisabled is the master "no auth" switch (see the dev-bypass middleware in
    // Program.cs, which short-circuits every request straight to a dev principal). The
    // guard must honor the same switch: with AuthDisabled=true, an actor
    // with NO row in the database at all must still be allowed through, and no
    // unauthorized-action event should be logged for them.
    [Fact]
    public async Task AuthDisabled_AllowsAnyActor_EvenWithNoDbRow_AndLogsNoUnauthorizedEvent()
    {
        var (guard, _, events) = Make(authDisabled: true);

        await guard.EnsureAdminAsync("anyone@nowhere", "announce", default);

        Assert.DoesNotContain(events, e => e.Type == "unauthorized-action");
    }

    [Fact]
    public async Task AuthEnabled_NonAdminActor_ThrowsAndLogsUnauthorizedEvent()
    {
        var (guard, dbf, events) = Make(authDisabled: false);
        await using (var db = await dbf.CreateDbContextAsync())
        {
            db.Users.Add(new PanelUser { Email = "viewer@x.com", Role = "Viewer", FirstSeen = DateTimeOffset.UtcNow, LastSeen = DateTimeOffset.UtcNow });
            await db.SaveChangesAsync();
        }

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => guard.EnsureAdminAsync("viewer@x.com", "announce", default));

        Assert.Contains(events, e => e.Type == "unauthorized-action" && e.Actor == "viewer@x.com");
    }

    [Fact]
    public async Task SchedulerActor_AlwaysExempt_RegardlessOfAuthDisabled()
    {
        var (guard, _, events) = Make(authDisabled: false);

        await guard.EnsureAdminAsync(AdminGuard.SchedulerActor, "restart", default);

        Assert.DoesNotContain(events, e => e.Type == "unauthorized-action");
    }
}
