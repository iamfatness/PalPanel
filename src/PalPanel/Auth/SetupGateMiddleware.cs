using Microsoft.EntityFrameworkCore;
using PalPanel.Data;

namespace PalPanel.Auth;

// Per-app-instance cache of "has any PanelUser row ever been observed by this middleware".
// Registered as a DI singleton (NOT a `static` field on the middleware type) specifically so
// each hosted app / WebApplicationFactory instance gets its own isolated flag -- a `static`
// field would leak across WebApplicationFactory instances that share the same CLR type within
// one test process, contaminating tests that expect a fresh empty DB.
public class SetupGateState
{
    public volatile bool UsersExist;
}

// First-run gate: until at least one PanelUser row exists, every request (except the setup
// page/endpoint themselves, static assets, the Blazor runtime, and /healthz) is redirected to
// /setup, so the app can never render open to an anonymous visitor before an owner account
// exists. Once a user exists, /setup itself closes and bounces to /login instead -- this is
// what actually enforces "POST /auth/setup only when Users is empty" for the PAGE (the
// endpoint itself independently re-checks inside its own transaction before creating anyone).
//
// Registered after UseAuthentication (so ctx.User is populated for any future authenticated-
// bypass logic) but BEFORE UseAuthorization, so this gate wins over the global FallbackPolicy's
// own "no cookie -> redirect /login" challenge on a fresh, user-less install.
public class SetupGateMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext ctx, IDbContextFactory<PanelDb> factory, SetupGateState state)
    {
        // Once we've observed at least one user, that can never regress (there is no "delete
        // user" feature -- only role changes via RoleService.SetRoleAsync), so we cache `true`
        // forever and skip the DB hit on every subsequent request for the life of the process.
        // Before that (the brief pre-setup window on a fresh install), we still hit the DB on
        // every request -- correctness during first-run matters far more than micro-optimizing
        // a window that closes forever the moment setup completes.
        if (!state.UsersExist)
        {
            await using var db = await factory.CreateDbContextAsync(ctx.RequestAborted);
            state.UsersExist = await db.Users.AnyAsync(ctx.RequestAborted);
        }

        var path = ctx.Request.Path;

        if (!state.UsersExist && !IsExempt(path))
        {
            ctx.Response.Redirect("/setup");
            return;
        }

        if (state.UsersExist && path.StartsWithSegments("/setup", out var remainder) && remainder.Value is "" or "/")
        {
            ctx.Response.Redirect("/login");
            return;
        }

        await next(ctx);
    }

    private static bool IsExempt(PathString path)
    {
        if (path.StartsWithSegments("/setup") ||
            path.StartsWithSegments("/auth/setup") ||
            path.StartsWithSegments("/auth/logout") ||
            path.StartsWithSegments("/_blazor") ||
            path.StartsWithSegments("/_framework") ||
            path.StartsWithSegments("/healthz"))
        {
            return true;
        }

        // Static assets served straight from wwwroot (app.css, PalPanel.styles.css,
        // js/charts.js, lib/chart.umd.js, favicon.ico, ...) -- anything with a file extension
        // on its last segment. The /setup page itself needs its CSS/JS to load even before any
        // user exists.
        var value = path.Value;
        return !string.IsNullOrEmpty(value) && Path.HasExtension(value);
    }
}

public static class SetupGateMiddlewareExtensions
{
    public static IApplicationBuilder UseSetupGate(this IApplicationBuilder app) =>
        app.UseMiddleware<SetupGateMiddleware>();
}
