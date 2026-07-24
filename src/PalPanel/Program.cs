using System.Security.Claims;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.Google;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PalPanel.Auth;
using PalPanel.Components;
using PalPanel.Monitoring;
using PalPanel.PalApi;
using PalPanel.Supervisor;

var builder = WebApplication.CreateBuilder(args);
builder.Host.UseWindowsService();
builder.WebHost.UseUrls("http://localhost:5080");
builder.Configuration.AddJsonFile("appsettings.Local.json", optional: true);

builder.Services.Configure<PalPanel.PanelOptions>(builder.Configuration.GetSection("Panel"));
builder.Services.AddSingleton<IProcessLauncher, RealProcessLauncher>();
builder.Services.AddHttpClient();   // IHttpClientFactory; ServerManager creates one client per server
builder.Services.AddDbContextFactory<PalPanel.Data.PanelDb>(o =>
    o.UseSqlite($"Data Source={builder.Configuration["Panel:DbPath"] ?? "palpanel.db"}"));

// Data Protection keyring persisted next to the DB so per-server secret encryption survives
// service restarts / account changes (a Windows-service host has no default per-user key store).
var keyRingDir = Path.Combine(
    Path.GetDirectoryName(Path.GetFullPath(builder.Configuration["Panel:DbPath"] ?? "palpanel.db")) ?? ".",
    "dp-keys");
Directory.CreateDirectory(keyRingDir);
builder.Services.AddDataProtection().PersistKeysToFileSystem(new DirectoryInfo(keyRingDir));
builder.Services.AddSingleton<PalPanel.Auth.ISecretProtector, PalPanel.Auth.DataProtectionSecretProtector>();

// Panel-level (non-server-scoped) event sink; per-server events go through each runtime's own
// ServerEventSink (built inside ServerRuntime).
builder.Services.AddSingleton<PalPanel.Data.IEventSink, PalPanel.Data.DbEventSink>();
builder.Services.AddSingleton<PalPanel.Auth.IAdminGuard, PalPanel.Auth.AdminGuard>();
builder.Services.AddSingleton<PalPanel.Auth.IPasswordService, PalPanel.Auth.PasswordService>();
builder.Services.AddSingleton<PalPanel.Auth.IUserAdminService, PalPanel.Auth.UserAdminService>();

// Multi-server core: ServerManager owns the per-server runtimes; it is the IServerRegistry the
// poller/scheduler/UI resolve servers through.
builder.Services.AddSingleton<PalPanel.Servers.ServerManager>();
builder.Services.AddSingleton<PalPanel.Servers.IServerRegistry>(sp => sp.GetRequiredService<PalPanel.Servers.ServerManager>());

builder.Services.AddHostedService(sp => new PalPanel.Control.SchedulerService(
    sp.GetRequiredService<IDbContextFactory<PalPanel.Data.PanelDb>>(),
    sp.GetRequiredService<PalPanel.Servers.IServerRegistry>(),
    sp.GetRequiredService<PalPanel.Data.IEventSink>(),
    sp.GetRequiredService<ILogger<PalPanel.Control.SchedulerService>>()));
builder.Services.AddSingleton<PollerService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<PollerService>());
builder.Services.AddHostedService(sp => new RetentionService(
    sp.GetRequiredService<IDbContextFactory<PalPanel.Data.PanelDb>>(),
    sp.GetRequiredService<ILogger<RetentionService>>()));
builder.Services.AddSingleton<RoleChangeNotifier>();
builder.Services.AddSingleton<RoleService>();
builder.Services.AddSingleton<SetupGateState>();
builder.Services.AddHttpContextAccessor();
builder.Services.AddCascadingAuthenticationState();
builder.Services.AddScoped<AuthenticationStateProvider, HttpContextAuthStateProvider>();

// Cookie auth is the session; Google is one of the two credential paths (the other,
// email+password, is wired in a later task) -- both end in the same SignInAsync issuing
// this cookie (Email+Role claims), so HttpContextAuthStateProvider/AuthorizeView/IAdminGuard
// downstream are identical regardless of which path signed the user in.
builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(o =>
    {
        o.LoginPath = "/login";
        o.AccessDeniedPath = "/login";
        o.LogoutPath = "/auth/logout";
        o.Cookie.HttpOnly = true;
        o.Cookie.SecurePolicy = CookieSecurePolicy.Always;
        o.Cookie.SameSite = SameSiteMode.Lax;
        o.Cookie.Name = "PalPanel.Auth";
        // Read via builder.Configuration (not a snapshot taken before Build()) so this
        // reflects the FINAL merged configuration -- including WebApplicationFactory test
        // overrides (e.g. UseSetting("Panel:CookieDaysValid", ...)) -- because the delegate
        // itself only runs later, when the options system actually materializes
        // CookieAuthenticationOptions (at first use, well after Build() completes), and
        // builder.Configuration/app.Configuration are the same ConfigurationManager instance.
        o.ExpireTimeSpan = TimeSpan.FromDays(builder.Configuration.GetValue("Panel:CookieDaysValid", 7));
        o.SlidingExpiration = true;
    })
    .AddCookie("External", o =>
    {
        // Temp holding pen for a just-verified Google identity, BEFORE the allow-list decision
        // (AuthEndpoints.CompleteGoogleSignInAsync) runs. Google's OAuthHandler (SignInScheme
        // below) signs the verified ticket into THIS scheme rather than the app's own "Cookies"
        // scheme -- otherwise every successful Google login would mint the real PalPanel.Auth
        // session cookie unconditionally, before we ever get a chance to check the email against
        // the Users table (unknown/Blocked emails would be signed in first and rejected never).
        // Short-lived and single-use: /auth/google-complete reads it once and immediately signs
        // it back out, whichever way the allow-list decision goes.
        o.Cookie.Name = "PalPanel.External";
        o.Cookie.HttpOnly = true;
        o.Cookie.SecurePolicy = CookieSecurePolicy.Always;
        // SameSite=None (not Lax like the app cookie): this cookie must survive the top-level
        // redirect BACK from accounts.google.com to our /signin-google callback, which is a
        // cross-site navigation from the browser's perspective.
        o.Cookie.SameSite = SameSiteMode.None;
        o.ExpireTimeSpan = TimeSpan.FromMinutes(10);
    })
    .AddGoogle(o =>
    {
        // OAuthOptions.Validate() runs EAGERLY on every request, not just when Google's
        // endpoints are hit: AuthenticationMiddleware initializes every "request handler
        // scheme" (Google's handler is one, since OAuthHandler implements
        // IAuthenticationRequestHandler) to ask ShouldHandleRequestAsync(), and that
        // initialization materializes GoogleOptions via the options system -- which runs
        // Validate() -- before the handler even checks whether this request is for it. A
        // blank ClientId/ClientSecret (dev box, Google not configured yet) would throw
        // ArgumentException there and 500 EVERY request, not just Google sign-in attempts.
        // Substitute an obviously-fake placeholder instead of empty string when unconfigured:
        // the scheme stays registered (satisfies "usable once configured"), but any real
        // OAuth attempt fails cleanly against Google's servers rather than crashing our own
        // pipeline.
        var clientId = builder.Configuration["Panel:GoogleClientId"];
        var clientSecret = builder.Configuration["Panel:GoogleClientSecret"];
        o.ClientId = string.IsNullOrWhiteSpace(clientId) ? "not-configured" : clientId;
        o.ClientSecret = string.IsNullOrWhiteSpace(clientSecret) ? "not-configured" : clientSecret;
        o.CallbackPath = "/signin-google"; // must match the Google OAuth client's redirect URI
        o.CorrelationCookie.SecurePolicy = CookieSecurePolicy.Always;
        o.CorrelationCookie.SameSite = SameSiteMode.None;
        // Defense in depth on the identity claim: capture Google's `email_verified` flag as a
        // claim so the completion wiring can REFUSE to trust an unverified email. Without this,
        // a userinfo response that returned an allow-listed address the caller doesn't actually
        // own (unverified) would be trusted for the Users lookup -> a session as someone else.
        // Google's OIDC userinfo uses the `email_verified` boolean key; MapJsonKey stores it as
        // the string "true"/"false" under this claim type. The verified gate lives in
        // AuthEndpoints.CompleteVerifiedGoogleSignInAsync (which reads this claim).
        o.ClaimActions.MapJsonKey("urn:google:email_verified", "email_verified", "boolean");
        // See the "External" AddCookie registration above: without this, RemoteAuthenticationHandler
        // would sign the verified Google ticket straight into the DEFAULT scheme (our app's own
        // "Cookies" scheme, since no SignInScheme was set and AddAuthentication's default scheme
        // is Cookies), handing out a real PalPanel.Auth session before the allow-list ever runs.
        o.SignInScheme = "External";
    });

builder.Services.AddAuthorization(o =>
{
    // The app login is now the SOLE gate (Cloudflare Access is gone) -- every endpoint
    // requires an authenticated user unless explicitly opted out with [AllowAnonymous]
    // (see /healthz below). This is what makes GET / redirect to /login instead of
    // rendering openly.
    o.FallbackPolicy = new AuthorizationPolicyBuilder()
        .RequireAuthenticatedUser()
        .Build();
});

// ForwardedHeaders so Secure cookies + the "is this HTTPS" check are correct behind the
// Cloudflare Tunnel (cloudflared terminates TLS at the edge and forwards plain HTTP to us
// over loopback, setting X-Forwarded-Proto: https).
builder.Services.Configure<ForwardedHeadersOptions>(o =>
{
    o.ForwardedHeaders = ForwardedHeaders.XForwardedProto | ForwardedHeaders.XForwardedFor;
    // SECURITY NOTE: clearing KnownNetworks/KnownProxies means we trust X-Forwarded-Proto/-For
    // from WHATEVER host makes the direct TCP connection to Kestrel, with no allowlist check
    // at all. This is acceptable ONLY because:
    //   1) the app binds http://localhost:5080 (loopback only -- see UseUrls above), so
    //      nothing off-box can ever be the direct caller, and
    //   2) the only process that runs on this box and forwards requests to us is cloudflared
    //      (the Cloudflare Tunnel connector), sitting behind Cloudflare's own edge TLS/auth.
    // If this app is EVER bound to a routable interface (0.0.0.0, a LAN IP, etc.) without a
    // real reverse proxy in front of it, this MUST be reverted to a proper KnownProxies/
    // KnownNetworks allowlist -- otherwise any direct caller could spoof "https" via
    // X-Forwarded-Proto and defeat the Secure-cookie assumptions above.
    o.KnownNetworks.Clear();
    o.KnownProxies.Clear();
});

builder.Services.AddRazorComponents().AddInteractiveServerComponents(o =>
{
    // Through the Cloudflare Tunnel a SignalR circuit can briefly drop (idle upgrade, edge
    // hiccup). Retain the disconnected circuit server-side for a few minutes so a client that
    // reconnects RESUMES its exact circuit (component state intact) instead of getting a fresh
    // one — the difference between "buttons work again" and "everything reset". Paired with the
    // extended client-side reconnect retries in App.razor.
    o.DisconnectedCircuitRetentionPeriod = TimeSpan.FromMinutes(3);
    o.DisconnectedCircuitMaxRetained = 100;
});

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    var dbf = scope.ServiceProvider.GetRequiredService<Microsoft.EntityFrameworkCore.IDbContextFactory<PalPanel.Data.PanelDb>>();
    await using var db = await dbf.CreateDbContextAsync();
    await db.Database.EnsureCreatedAsync();

    // EnsureCreated is a no-op on an existing (pre-multi-server) DB, so patch in the Servers
    // table + ServerId columns idempotently before anything reads the new shape.
    await PalPanel.Data.SchemaUpgrade.ApplyAsync(db);

    // Upgrade path: on first boot after multi-server, seed one server from the legacy
    // single-server PanelOptions and stamp existing rows to it (idempotent, no-op afterwards).
    var legacy = scope.ServiceProvider.GetRequiredService<IOptions<PalPanel.PanelOptions>>().Value;
    var protector = scope.ServiceProvider.GetRequiredService<PalPanel.Auth.ISecretProtector>();
    await PalPanel.Data.LegacyServerMigration.EnsureSeededAsync(db, legacy, protector);
}

// Build the per-server runtimes and adopt any already-running PalServer processes.
await app.Services.GetRequiredService<PalPanel.Servers.ServerManager>().InitializeAsync();

app.UseForwardedHeaders();
app.UseStaticFiles();

// AuthDisabled dev bypass: installs a synthetic dev@localhost Admin principal BEFORE
// UseAuthentication runs, so the app is fully usable without a real login/Google setup.
// This must run before UseAuthentication because CookieAuthenticationHandler's
// AuthenticateAsync, when it finds no cookie, returns AuthenticateResult.NoResult() (a null
// Principal) -- and AuthenticationMiddleware only overwrites HttpContext.User when the
// result's Principal is non-null -- so the dev principal we set here survives untouched into
// UseAuthorization and every downstream AuthorizeView/IAdminGuard check.
app.UseWhen(
    ctx => ctx.RequestServices.GetRequiredService<IOptions<PalPanel.PanelOptions>>().Value.AuthDisabled,
    devBranch => devBranch.Use(async (context, next) =>
    {
        var roles = context.RequestServices.GetRequiredService<RoleService>();
        const string devEmail = "dev@localhost";
        var principal = await roles.GetOrCreateAsync(devEmail);
        if (principal.Role != "Admin")
        {
            // Force Admin for the dev identity even if it wasn't the very first user ever
            // seen (e.g. a real account already claimed the auto-Admin slot before
            // AuthDisabled was flipped on for local testing). SetRoleAsync's own IAdminGuard
            // check is bypassed too under AuthDisabled (see AdminGuard.cs), so this never
            // throws for lack of an already-Admin actor.
            await roles.SetRoleAsync(devEmail, "Admin", actor: devEmail, context.RequestAborted);
        }
        var identity = new ClaimsIdentity(
        [
            new Claim(ClaimTypes.Email, devEmail),
            new Claim(ClaimTypes.Role, "Admin"),
        ], authenticationType: "Dev");
        context.User = new ClaimsPrincipal(identity);
        await next();
    }));

app.UseAuthentication();

// Must run after UseAuthentication (so ctx.User is populated) but BEFORE UseAuthorization: on
// a fresh, user-less install this gate has to win over the global FallbackPolicy's own
// "no cookie -> redirect /login" challenge, sending visitors to /setup instead.
app.UseSetupGate();

app.UseAuthorization();
app.UseAntiforgery();
app.MapAuthEndpoints();
app.MapGet("/healthz", () => "ok").AllowAnonymous();

// Admin-only backup download. The global FallbackPolicy (RequireAuthenticatedUser, registered
// above) already rejects anonymous requests before this delegate ever runs; on top of that,
// authorization is DB-authoritative via IAdminGuard.EnsureAdminAsync -- exactly like every other
// mutating admin action (see IAdminGuard.cs) -- rather than trusting the Role claim baked into
// the cookie at sign-in, which is never re-read for the cookie's whole 7-day life and would
// otherwise let a since-demoted-or-blocked admin keep downloading backups. The physical path is
// built ONLY from the matched BackupInfo.FileName (never the raw route-value `file`) -- List()
// only ever enumerates *.zip actually present in BackupDirectory, so this can't be steered off
// that directory or onto an arbitrary name, unlike Path.Combine(dir, file) with the raw input.
app.MapGet("/backups/download/{server:guid}/{file}", async (Guid server, string file,
    HttpContext ctx, PalPanel.Servers.ServerManager manager, PalPanel.Auth.IAdminGuard guard) =>
{
    var email = ctx.User.FindFirst(ClaimTypes.Email)?.Value ?? "";
    try
    {
        await guard.EnsureAdminAsync(email, "backup-download", ctx.RequestAborted);
    }
    catch (UnauthorizedAccessException)
    {
        return Results.StatusCode(StatusCodes.Status403Forbidden);
    }
    var rt = manager.Get(server);
    if (rt is null) return Results.NotFound();
    var info = rt.Backups.List().FirstOrDefault(b => b.FileName == file);
    if (info is null) return Results.NotFound();
    // Physical path built only from the matched BackupInfo.FileName (never the raw route value),
    // and only files actually present in this server's BackupDirectory are ever enumerated.
    var path = Path.Combine(rt.Config.BackupDirectory, info.FileName);
    return Results.File(path, "application/zip", info.FileName);
});

app.MapRazorComponents<App>().AddInteractiveServerRenderMode();
app.Run();

public partial class Program { } // for WebApplicationFactory
