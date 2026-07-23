using System.Security.Claims;
using Microsoft.AspNetCore.Antiforgery;
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
    // Server-side minimum password length for setup. The Razor form also sets minlength=8, but
    // that's a client-side convenience only (trivially bypassed by POSTing directly), so the
    // real enforcement lives here.
    private const int MinPasswordLength = 8;

    public static void MapAuthEndpoints(this WebApplication app)
    {
        app.MapPost("/auth/login", LoginAsync).AllowAnonymous();
        app.MapPost("/auth/logout", LogoutAsync).AllowAnonymous();
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
            // Constant-cost dummy verify (exactly one PBKDF2 round against a PRECOMPUTED hash --
            // NOT a fresh per-request Hash(), which would be two rounds and make unknown emails
            // measurably slower than a real wrong-password attempt, i.e. an enumeration signal in
            // the opposite direction plus free CPU amplification). Never signs anyone in.
            passwords.VerifyDummy(password);
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

    // Antiforgery on logout is validated EXPLICITLY here rather than via the [FromForm]
    // auto-requirement used by login/setup. Reason: logout has no real form field to bind, and a
    // *dummy optional* `[FromForm] string?` param does NOT attach the middleware's
    // "requires-validation" antiforgery metadata (only a required form binding does) -- so the
    // middleware skips it and the generated form-read then throws an *unchecked-antiforgery*
    // InvalidOperationException (HTTP 500) on a tokenless POST instead of a clean 400. Calling
    // ValidateRequestAsync ourselves rejects the CSRF forced-logout with a proper 400 and no
    // dependence on binding-shape magic. The logout form/button (added with the logged-out UX in
    // a later task) must render <AntiforgeryToken /> so a legitimate logout carries the token.
    private static async Task<IResult> LogoutAsync(HttpContext ctx, IAntiforgery antiforgery)
    {
        try
        {
            await antiforgery.ValidateRequestAsync(ctx);
        }
        catch (AntiforgeryValidationException)
        {
            return Results.BadRequest();
        }

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

        // Server-side minimum length -- the client `minlength=8` is bypassable by POSTing
        // directly, so the owner account can't be created with a trivially weak password.
        if (password.Length < MinPasswordLength)
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
    // protocol-relative URL -- so a crafted `returnUrl` (e.g. "https://evil.example",
    // "//evil.example", or "/\evil.example") can't turn this into an open redirect off the
    // panel.
    internal static string SafeReturnUrl(string? returnUrl)
    {
        if (string.IsNullOrWhiteSpace(returnUrl)) return "/";

        // Reject any ASCII control character (< 0x20) up front, BEFORE the leading-slash checks.
        // Browsers strip/normalize embedded tab/CR/LF when resolving a URL, so a payload like
        // "/%09/evil.example" (decoded to "/\t/evil.example") could otherwise sneak past a naive
        // prefix check and then be normalized by the browser into a protocol-relative-ish
        // target. Anything with a control char is never a legitimate local path here.
        foreach (var c in returnUrl)
            if (c < 0x20) return "/";

        if (returnUrl[0] != '/') return "/";
        if (returnUrl.Length > 1 && (returnUrl[1] == '/' || returnUrl[1] == '\\')) return "/";
        return returnUrl;
    }
}
