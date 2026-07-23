using System.Net;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using PalPanel.Auth;
using PalPanel.Data;

namespace PalPanel.Tests;

// Google sign-in never gets called against the real Google servers in these tests (Task 3's
// controller-testability learning: an OAuth handshake can't be driven headlessly). Instead the
// allow-list DECISION logic lives in the public AuthEndpoints.CompleteGoogleSignInAsync(ctx,
// email, factory, events) -- it takes a Google-verified email as a plain string and a real
// HttpContext (so ctx.SignInAsync/SignOutAsync exercise the actual cookie-auth pipeline), with
// zero dependency on the OAuth handshake itself. These tests call it directly with a DefaultHttpContext
// whose RequestServices comes from the real WebApplicationFactory DI container (so
// ctx.SignInAsync("Cookies", ...) and ctx.SignOutAsync("External") resolve real
// CookieAuthenticationHandler/options exactly as production does) -- this genuinely exercises the
// email -> PanelUser -> cookie mapping without faking the whole handshake. Only the
// GoogleChallenge test below goes through real HTTP, and only as far as the 302 Google redirects
// browsers to (no token exchange, no calling Google).
public class GoogleAuthTests : IAsyncLifetime
{
    private WebApplicationFactory<Program> _factory = null!;
    private string _dbPath = null!;

    public Task InitializeAsync()
    {
        _dbPath = Path.GetTempFileName();
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            // Same Local.json gotcha workaround as AuthEndpointsTests/AuthPipelineTests:
            // ConfigureAppConfiguration reliably wins over the gitignored appsettings.Local.json.
            builder.ConfigureAppConfiguration((_, config) =>
            {
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Panel:AuthDisabled"] = "false",
                    ["Panel:DbPath"] = _dbPath,
                });
            });
        });
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        await _factory.DisposeAsync();
        try { File.Delete(_dbPath); } catch { /* best-effort cleanup */ }
    }

    private async Task<PanelUser> SeedUserAsync(string email, string role, DateTimeOffset lastSeen)
    {
        using var scope = _factory.Services.CreateScope();
        var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<PanelDb>>();
        await using var db = await factory.CreateDbContextAsync();
        var user = new PanelUser { Email = email, Role = role, FirstSeen = lastSeen, LastSeen = lastSeen };
        db.Users.Add(user);
        await db.SaveChangesAsync();
        return user;
    }

    private async Task<List<EventLog>> GetEventsAsync(string type)
    {
        using var scope = _factory.Services.CreateScope();
        var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<PanelDb>>();
        await using var db = await factory.CreateDbContextAsync();
        return await db.Events.Where(e => e.Type == type).ToListAsync();
    }

    private async Task<PanelUser> GetUserAsync(string email)
    {
        using var scope = _factory.Services.CreateScope();
        var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<PanelDb>>();
        await using var db = await factory.CreateDbContextAsync();
        return await db.Users.SingleAsync(u => u.Email == email);
    }

    // Builds a real DefaultHttpContext backed by a live DI scope from the WebApplicationFactory,
    // so ctx.SignInAsync/ctx.SignOutAsync resolve the ACTUAL CookieAuthenticationHandler/options
    // (including the "External" temp scheme) rather than throwing for missing services -- this is
    // what makes calling CompleteGoogleSignInAsync directly a genuine exercise of the cookie
    // pipeline, not just the decision-only branches.
    private (HttpContext ctx, IServiceScope scope) NewHttpContext()
    {
        var scope = _factory.Services.CreateScope();
        var ctx = new DefaultHttpContext { RequestServices = scope.ServiceProvider };
        return (ctx, scope);
    }

    private static async Task<(int StatusCode, string? Location)> ExecuteAsync(IResult result, HttpContext ctx)
    {
        await result.ExecuteAsync(ctx);
        return (ctx.Response.StatusCode, ctx.Response.Headers.Location.ToString());
    }

    private static bool HasAuthCookie(HttpContext ctx) =>
        ctx.Response.Headers.SetCookie.Any(v => v != null && v.Contains("PalPanel.Auth"));

    [Fact]
    public async Task CompleteGoogleSignInAsync_KnownViewer_IssuesCookie_LogsSuccess_UpdatesLastSeen()
    {
        var oldLastSeen = DateTimeOffset.UtcNow.AddDays(-30);
        await SeedUserAsync("viewer@example.com", "Viewer", oldLastSeen);

        using var scope0 = _factory.Services.CreateScope();
        var dbFactory = scope0.ServiceProvider.GetRequiredService<IDbContextFactory<PanelDb>>();
        var events = scope0.ServiceProvider.GetRequiredService<IEventSink>();

        var (ctx, scope) = NewHttpContext();
        // Mixed-case + surrounding claim value, exactly like a real Google email claim, to prove
        // normalization happens before the lookup.
        var result = await AuthEndpoints.CompleteGoogleSignInAsync(ctx, "Viewer@Example.com", dbFactory, events);
        var (status, location) = await ExecuteAsync(result, ctx);
        scope.Dispose();
        Assert.Equal(StatusCodes.Status302Found, status);
        Assert.Equal("/", location);
        Assert.True(HasAuthCookie(ctx), "Expected a PalPanel.Auth Set-Cookie header on success.");

        var successEvents = await GetEventsAsync("login-success");
        Assert.Contains(successEvents, e => e.ActorEmail == "viewer@example.com" && e.Detail.Contains("method=google"));

        var user = await GetUserAsync("viewer@example.com");
        Assert.True(user.LastSeen > oldLastSeen);
    }

    [Fact]
    public async Task CompleteGoogleSignInAsync_UnknownEmail_Denied_NoCookie()
    {
        using var scope0 = _factory.Services.CreateScope();
        var dbFactory = scope0.ServiceProvider.GetRequiredService<IDbContextFactory<PanelDb>>();
        var events = scope0.ServiceProvider.GetRequiredService<IEventSink>();

        var (ctx, scope) = NewHttpContext();
        var result = await AuthEndpoints.CompleteGoogleSignInAsync(ctx, "stranger@example.com", dbFactory, events);
        var (status, location) = await ExecuteAsync(result, ctx);
        scope.Dispose();
        Assert.Equal(StatusCodes.Status302Found, status);
        Assert.Equal("/login?denied=1", location);
        Assert.False(HasAuthCookie(ctx), "An unknown email must never receive an app cookie.");

        var deniedEvents = await GetEventsAsync("login-denied-unknown");
        Assert.Contains(deniedEvents, e => e.Detail.Contains("stranger@example.com"));
    }

    [Fact]
    public async Task CompleteGoogleSignInAsync_BlockedEmail_Denied_NoCookie()
    {
        await SeedUserAsync("blocked@example.com", "Blocked", DateTimeOffset.UtcNow);

        using var scope0 = _factory.Services.CreateScope();
        var dbFactory = scope0.ServiceProvider.GetRequiredService<IDbContextFactory<PanelDb>>();
        var events = scope0.ServiceProvider.GetRequiredService<IEventSink>();

        var (ctx, scope) = NewHttpContext();
        var result = await AuthEndpoints.CompleteGoogleSignInAsync(ctx, "blocked@example.com", dbFactory, events);
        var (status, location) = await ExecuteAsync(result, ctx);
        scope.Dispose();
        Assert.Equal(StatusCodes.Status302Found, status);
        Assert.Equal("/login?denied=1", location);
        Assert.False(HasAuthCookie(ctx), "A Blocked user must never receive an app cookie.");

        var deniedEvents = await GetEventsAsync("login-denied-blocked");
        Assert.Contains(deniedEvents, e => e.Detail.Contains("blocked@example.com"));
    }

    [Fact]
    public async Task GoogleChallenge_WithClientIdConfigured_RedirectsToGoogle()
    {
        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, config) =>
            {
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Panel:AuthDisabled"] = "false",
                    ["Panel:DbPath"] = Path.GetTempFileName(),
                    // A fake but syntactically real (non-empty, non-placeholder) ClientId --
                    // enough for the Google OAuth handler to build a real authorization-endpoint
                    // redirect. No real Google credentials are needed for a Challenge redirect.
                    ["Panel:GoogleClientId"] = "fake-client-id.apps.googleusercontent.com",
                    ["Panel:GoogleClientSecret"] = "fake-client-secret",
                });
            });
        });

        // SetupGateMiddleware redirects EVERY request (except a small exempt list) to /setup
        // until at least one PanelUser row exists -- an empty DB would otherwise mask the
        // Challenge redirect this test actually cares about behind a /setup redirect instead.
        using (var scope = factory.Services.CreateScope())
        {
            var dbf = scope.ServiceProvider.GetRequiredService<IDbContextFactory<PanelDb>>();
            await using var db = await dbf.CreateDbContextAsync();
            db.Users.Add(new PanelUser { Email = "owner@x.com", Role = "Admin", FirstSeen = DateTimeOffset.UtcNow, LastSeen = DateTimeOffset.UtcNow });
            await db.SaveChangesAsync();
        }

        var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            BaseAddress = new Uri("https://localhost"),
        });

        var resp = await client.GetAsync("/auth/google");

        Assert.Equal(HttpStatusCode.Redirect, resp.StatusCode);
        Assert.Equal("accounts.google.com", resp.Headers.Location!.Host);
    }
}
