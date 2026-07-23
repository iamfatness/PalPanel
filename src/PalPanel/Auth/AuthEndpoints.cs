using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PalPanel.Data;

namespace PalPanel.Auth;

// Minimal-API endpoints for the password login flow: /auth/login, /auth/logout, /auth/setup.
//
// Antiforgery: all three endpoints bind their form fields via [FromForm], which in .NET 8
// minimal APIs automatically attaches antiforgery-validation metadata to the endpoint --
// exactly like Razor Pages' built-in [ValidateAntiForgeryToken] but with zero extra wiring on
// our side (Program.cs already calls app.UseAntiforgery(), and AddRazorComponents() already
// registers the antiforgery services that backs it). Login.razor/Setup.razor render the
// matching hidden field via the built-in <AntiforgeryToken /> component. This was verified
// empirically in AuthEndpointsTests (a POST with no token is rejected).
//
// AllowAnonymous: the app-wide FallbackPolicy (Program.cs) requires an authenticated user on
// every endpoint by default -- these three are the ones that must work WITHOUT a session yet
// (that's the whole point of a login/setup flow), so each is explicitly opted out.
public static class AuthEndpoints
{
    // Timing-attack mitigation: an unknown-email login attempt still runs a full password
    // verification against a fixed dummy hash, so the response takes comparable time/shape to
    // a real user lookup and can't be used to enumerate which emails have accounts.
    private const string DummyPasswordSeed = "dummy-nonexistent-user-Xx9!";

    public static void MapAuthEndpoints(this WebApplication app)
    {
        app.MapPost("/auth/login", LoginAsync).AllowAnonymous();
        // Cast to `Delegate` explicitly: LogoutAsync's signature (a single HttpContext
        // parameter, returning something Task-assignable) is an EXACT match for the
        // `RequestDelegate` delegate type, so plain method-group conversion resolves MapPost's
        // `(string, RequestDelegate)` overload instead of the minimal-API `(string, Delegate)`
        // one -- which silently discards the returned IResult instead of writing it to the
        // response (ASP0016). The cast forces the correct overload.
        app.MapPost("/auth/logout", (Delegate)LogoutAsync).AllowAnonymous();
        app.MapPost("/auth/setup", SetupAsync).AllowAnonymous();
    }

    private static async Task<IResult> LoginAsync(
        HttpContext ctx,
        IDbContextFactory<PanelDb> factory,
        IPasswordService passwords,
        IEventSink events,
        [FromForm] string email,
        [FromForm] string password,
        [FromForm] string? returnUrl)
    {
        var normalizedEmail = Normalize(email);
        var now = DateTimeOffset.UtcNow;

        await using var db = await factory.CreateDbContextAsync(ctx.RequestAborted);
        var user = await db.Users.FirstOrDefaultAsync(u => u.Email == normalizedEmail, ctx.RequestAborted);

        if (user is null)
        {
            // Dummy verify only, for timing parity -- never signs anyone in, regardless of
            // outcome, since there is no real user behind this email at all.
            var dummy = new PanelUser { Email = normalizedEmail, PasswordHash = passwords.Hash(DummyPasswordSeed) };
            _ = passwords.CheckPassword(dummy, password, now);
            await events.LogAsync("login-failed", $"email={normalizedEmail} outcome=unknown-user");
            return Results.Redirect("/login?error=1");
        }

        var check = passwords.CheckPassword(user, password, now);
        if (check.MutatedUser)
            await db.SaveChangesAsync(ctx.RequestAborted);

        if (check.Outcome == LoginOutcome.Success)
        {
            user.LastSeen = now;
            await db.SaveChangesAsync(ctx.RequestAborted);
            await SignInUserAsync(ctx, user);
            await events.LogAsync("login-success", "method=password", user.Email);
            return Results.Redirect(user.MustChangePassword ? "/change-password" : SafeReturnUrl(returnUrl));
        }

        if (check.Outcome == LoginOutcome.Locked)
            await events.LogAsync("login-locked", $"email={normalizedEmail}", normalizedEmail);
        else
            await events.LogAsync("login-failed", $"email={normalizedEmail} outcome={check.Outcome}", normalizedEmail);

        // Generic failure redirect for every non-Success outcome (BadCredentials, NoPassword,
        // Locked) -- the caller can't distinguish "wrong password" from "account locked" from
        // "no password set yet", which is deliberate: telling an attacker WHY a login failed
        // narrows down account state for free.
        return Results.Redirect("/login?error=1");
    }

    private static async Task<IResult> LogoutAsync(HttpContext ctx)
    {
        await ctx.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        return Results.Redirect("/login");
    }

    private static async Task<IResult> SetupAsync(
        HttpContext ctx,
        IDbContextFactory<PanelDb> factory,
        IPasswordService passwords,
        IEventSink events,
        [FromForm] string email,
        [FromForm] string password,
        [FromForm] string? confirmPassword)
    {
        var normalizedEmail = Normalize(email);

        await using var db = await factory.CreateDbContextAsync(ctx.RequestAborted);
        // Real DB transaction wrapping the re-check + insert: closes the obvious double-submit
        // window (two sequential/near-simultaneous POST /auth/setup calls) since the second
        // transaction's AnyAsync() re-check only observes the first transaction's insert once
        // it has committed. (A single-operator panel like this one accepts the same narrow
        // TOCTOU tradeoff RoleService already documents for GetOrCreateAsync/SetRoleAsync --
        // this is not hardened against a genuinely simultaneous multi-writer race, just against
        // the realistic "clicked submit twice" case.)
        await using var tx = await db.Database.BeginTransactionAsync(ctx.RequestAborted);

        if (await db.Users.AnyAsync(ctx.RequestAborted))
            return Results.Redirect("/login");

        if (string.IsNullOrWhiteSpace(normalizedEmail) || string.IsNullOrEmpty(password))
            return Results.Redirect("/setup?error=1");

        if (confirmPassword is not null && confirmPassword != password)
            return Results.Redirect("/setup?error=1");

        var now = DateTimeOffset.UtcNow;
        var user = new PanelUser
        {
            Email = normalizedEmail,
            Role = "Admin",
            PasswordHash = passwords.Hash(password),
            FirstSeen = now,
            LastSeen = now,
        };

        try
        {
            db.Users.Add(user);
            await db.SaveChangesAsync(ctx.RequestAborted);
            await tx.CommitAsync(ctx.RequestAborted);
        }
        catch (DbUpdateException)
        {
            // Defense in depth against a genuinely concurrent double-submit that raced past the
            // AnyAsync() check above: the unique index on Email (or any other constraint) would
            // surface here as a DbUpdateException rather than creating a second owner.
            return Results.Redirect("/login");
        }

        await SignInUserAsync(ctx, user);
        await events.LogAsync("setup-owner-created", $"email={normalizedEmail}", normalizedEmail);
        return Results.Redirect("/");
    }

    private static async Task SignInUserAsync(HttpContext ctx, PanelUser user)
    {
        var identity = new ClaimsIdentity(
        [
            new Claim(ClaimTypes.Email, user.Email),
            new Claim(ClaimTypes.Role, user.Role),
            new Claim(ClaimTypes.NameIdentifier, user.Email),
        ], CookieAuthenticationDefaults.AuthenticationScheme);
        await ctx.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, new ClaimsPrincipal(identity));
    }

    private static string Normalize(string? email) => (email ?? "").Trim().ToLowerInvariant();

    // Only ever redirect to a local path -- a bare "/foo", never a scheme-qualified or
    // protocol-relative URL -- so a crafted `returnUrl` (e.g. "https://evil.example" or
    // "//evil.example") can't turn this into an open redirect off the panel.
    private static string SafeReturnUrl(string? returnUrl)
    {
        if (string.IsNullOrWhiteSpace(returnUrl)) return "/";
        if (returnUrl[0] != '/') return "/";
        if (returnUrl.Length > 1 && (returnUrl[1] == '/' || returnUrl[1] == '\\')) return "/";
        return returnUrl;
    }
}
