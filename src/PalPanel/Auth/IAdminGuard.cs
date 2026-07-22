using Microsoft.EntityFrameworkCore;
using PalPanel.Data;

namespace PalPanel.Auth;

// Server-side authorization backstop for every mutating orchestrator action. AuthorizeView
// in the Razor pages is a rendering/UX concern only (it hides buttons) — it is NOT a
// security boundary on its own, since a Blazor Server circuit's C# method calls are not
// gated by what markup happens to be visible. Every mutating IServerOrchestrator method
// must call EnsureAdminAsync FIRST, before doing anything else, so a non-Admin actor can
// never reach the API/supervisor/backup layers no matter how the call was made.
public interface IAdminGuard
{
    Task EnsureAdminAsync(string actor, string action, CancellationToken ct);
}

public class AdminGuard(IDbContextFactory<PanelDb> factory, IEventSink events) : IAdminGuard
{
    // SchedulerService fires scheduled restarts using this synthetic actor — there is no
    // logged-in user behind a cron-triggered restart, so it is always authorized and never
    // looked up in the Users table. Keep this in sync with SchedulerService's actor string.
    public const string SchedulerActor = "scheduler";

    public async Task EnsureAdminAsync(string actor, string action, CancellationToken ct)
    {
        if (actor == SchedulerActor) return;

        await using var db = await factory.CreateDbContextAsync(ct);
        var user = await db.Users.FirstOrDefaultAsync(u => u.Email == actor, ct);
        if (user?.Role == "Admin") return;

        await events.LogAsync("unauthorized-action", $"{actor} attempted '{action}' without Admin role", actor);
        throw new UnauthorizedAccessException($"Actor '{actor}' is not authorized to perform '{action}'.");
    }
}
