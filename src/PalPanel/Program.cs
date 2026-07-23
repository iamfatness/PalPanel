using System.Security.Claims;
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
builder.Services.AddSingleton<ProcessSupervisor>();
builder.Services.AddHttpClient<IPalApi, PalApiClient>();
builder.Services.AddDbContextFactory<PalPanel.Data.PanelDb>(o =>
    o.UseSqlite($"Data Source={builder.Configuration["Panel:DbPath"] ?? "palpanel.db"}"));
builder.Services.AddSingleton<PalPanel.Data.IEventSink, PalPanel.Data.DbEventSink>();
builder.Services.AddSingleton<PalPanel.Control.IBackupService, PalPanel.Control.BackupService>();
builder.Services.AddSingleton<PalPanel.Auth.IAdminGuard, PalPanel.Auth.AdminGuard>();
builder.Services.AddSingleton<PalPanel.Auth.IPasswordService, PalPanel.Auth.PasswordService>();
builder.Services.AddSingleton<PalPanel.Control.IServerOrchestrator, PalPanel.Control.ServerOrchestrator>();
builder.Services.AddHostedService(sp => new PalPanel.Control.SchedulerService(
    sp.GetRequiredService<IDbContextFactory<PalPanel.Data.PanelDb>>(),
    sp.GetRequiredService<PalPanel.Control.IServerOrchestrator>(),
    sp.GetRequiredService<PalPanel.Control.IBackupService>(),
    sp.GetRequiredService<PalPanel.Data.IEventSink>(),
    sp.GetRequiredService<ILogger<PalPanel.Control.SchedulerService>>()));
builder.Services.AddSingleton<SnapshotService>();
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

builder.Services.AddRazorComponents().AddInteractiveServerComponents();

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    scope.ServiceProvider.GetRequiredService<Microsoft.EntityFrameworkCore.IDbContextFactory<PalPanel.Data.PanelDb>>().CreateDbContext().Database.EnsureCreated();
}

var supervisor = app.Services.GetRequiredService<ProcessSupervisor>();
var eventSink = app.Services.GetRequiredService<PalPanel.Data.IEventSink>();
// A failing event sink (disk full, DB locked) must be loud but must never propagate
// into supervisor internals — fall back to ILogger error output.
supervisor.OnEvent = async (t, d) =>
{
    try { await eventSink.LogAsync(t, d); }
    catch (Exception ex) { app.Logger.LogError(ex, "Event sink write failed for {Type}: {Detail}", t, d); }
};
supervisor.AdoptExistingIfRunning();

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

// Admin-only backup download. Authorization here is now purely claims-based: the global
// FallbackPolicy (RequireAuthenticatedUser, registered above) already rejects anonymous
// requests before this delegate ever runs; this just adds the Admin-only role check on top,
// reading the Role claim that either real cookie/Google sign-in or the AuthDisabled dev-bypass
// middleware put on HttpContext.User. The physical path is built ONLY from the matched
// BackupInfo.FileName (never the raw route-value `file`) — List() only ever enumerates *.zip
// actually present in BackupDirectory, so this can't be steered off that directory or onto an
// arbitrary name, unlike Path.Combine(dir, file) with the raw input.
app.MapGet("/backups/download/{file}", (string file, PalPanel.Control.IBackupService backups,
    HttpContext ctx, IOptions<PalPanel.PanelOptions> opts) =>
{
    if (ctx.User.FindFirst(ClaimTypes.Role)?.Value != "Admin")
        return Results.StatusCode(StatusCodes.Status403Forbidden);
    var info = backups.List().FirstOrDefault(b => b.FileName == file);
    if (info is null) return Results.NotFound();
    var path = Path.Combine(opts.Value.BackupDirectory, info.FileName);
    return Results.File(path, "application/zip", info.FileName);
});

app.MapRazorComponents<App>().AddInteractiveServerRenderMode();
app.Run();

public partial class Program { } // for WebApplicationFactory
