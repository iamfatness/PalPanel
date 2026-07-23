using System.Net;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using PalPanel.Data;

namespace PalPanel.Tests;

// Exercises the real password login/setup/logout flow end to end through the actual HTTP
// pipeline (not the AuthDisabled dev shortcut, and not a hand-minted cookie like
// BackupDownloadEndpointTests -- this is the code path that MINTS that cookie in the first
// place). Every test uses a brand-new temp DB via ConfigureAppConfiguration (NOT UseSetting --
// see the class remarks on the Local.json gotcha) so the first-run setup gate always starts
// from an empty Users table.
public class AuthEndpointsTests : IAsyncLifetime
{
    private WebApplicationFactory<Program> _factory = null!;
    private string _dbPath = null!;

    public Task InitializeAsync()
    {
        _dbPath = Path.GetTempFileName();
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            // This dev box's gitignored src/PalPanel/appsettings.Local.json sets
            // Panel:AuthDisabled=true, and Program.cs's own AddJsonFile("appsettings.Local.json")
            // call runs AFTER whatever hook applies WebApplicationFactory's UseSetting(...) for
            // the same key -- so UseSetting("Panel:AuthDisabled", "false") would be silently
            // clobbered back to "true" by that file. ConfigureAppConfiguration callbacks layer
            // on top of Program.cs's own configuration sources instead, so they reliably win.
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

    // BaseAddress MUST be https:// even though TestServer never does a real TLS handshake: the
    // auth cookie is configured with CookieSecurePolicy.Always (Program.cs), and the client's
    // CookieContainer decides whether to RESEND a Secure-flagged cookie purely by checking the
    // outgoing request URI's scheme -- WebApplicationFactory's own default BaseAddress is
    // http://localhost, which would silently make the container drop the auth cookie on every
    // request after the one that received it, breaking every test that relies on automatic
    // cookie round-tripping (as opposed to BackupDownloadEndpointTests, which sidesteps this by
    // adding the Cookie header manually instead of going through a real login).
    private static readonly Uri HttpsLocal = new("https://localhost");

    private HttpClient NoRedirectClient() =>
        _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, BaseAddress = HttpsLocal });

    private HttpClient RedirectingClient() =>
        _factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = HttpsLocal });

    private static readonly Regex TokenRegex = new(
        "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"|value=\"([^\"]+)\"[^>]*name=\"__RequestVerificationToken\"",
        RegexOptions.IgnoreCase);

    // Fetches a page (GET, follows no redirects since these pages are always 200) and extracts
    // its antiforgery hidden-field token. The matching antiforgery cookie is captured
    // automatically by the client's CookieContainerHandler (WebApplicationFactoryClientOptions
    // defaults HandleCookies to true), so as long as the SAME HttpClient instance is reused for
    // the subsequent POST, both token and cookie line up.
    private static async Task<string> GetAntiforgeryTokenAsync(HttpClient client, string path)
    {
        var resp = await client.GetAsync(path);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var html = await resp.Content.ReadAsStringAsync();
        var match = TokenRegex.Match(html);
        Assert.True(match.Success, $"Could not find antiforgery token in {path} response:\n{html}");
        return match.Groups[1].Success ? match.Groups[1].Value : match.Groups[2].Value;
    }

    private static FormUrlEncodedContent Form(string token, params (string Key, string Value)[] fields)
    {
        var pairs = new List<KeyValuePair<string, string>> { new("__RequestVerificationToken", token) };
        pairs.AddRange(fields.Select(f => new KeyValuePair<string, string>(f.Key, f.Value)));
        return new FormUrlEncodedContent(pairs);
    }

    // Seeds an owner account via a throwaway no-redirect client, for tests whose subject is
    // something OTHER than the setup flow itself (login, lockout, logout). Deliberately does
    // NOT reuse the caller's own client, since that client's AllowAutoRedirect setting is the
    // caller's concern, not this fixture step's.
    private async Task SeedOwnerAsync(string email, string password)
    {
        var client = NoRedirectClient();
        var token = await GetAntiforgeryTokenAsync(client, "/setup");
        var resp = await client.PostAsync("/auth/setup",
            Form(token, ("email", email), ("password", password), ("confirmPassword", password)));
        Assert.Equal(HttpStatusCode.Redirect, resp.StatusCode);
        Assert.Equal("/", resp.Headers.Location!.ToString());
    }

    private async Task<PanelUser> GetUserAsync(string email)
    {
        using var scope = _factory.Services.CreateScope();
        var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<PanelDb>>();
        await using var db = await factory.CreateDbContextAsync();
        return await db.Users.SingleAsync(u => u.Email == email);
    }

    private async Task<List<EventLog>> GetEventsAsync(string type)
    {
        using var scope = _factory.Services.CreateScope();
        var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<PanelDb>>();
        await using var db = await factory.CreateDbContextAsync();
        return await db.Events.Where(e => e.Type == type).ToListAsync();
    }

    [Fact]
    public async Task EmptyDb_RootAndLogin_RedirectToSetup()
    {
        var client = NoRedirectClient();

        var root = await client.GetAsync("/");
        Assert.Equal(HttpStatusCode.Redirect, root.StatusCode);
        Assert.Contains("/setup", root.Headers.Location!.ToString());

        // The setup gate wins over the ordinary "no cookie -> /login" challenge.
        var login = await client.GetAsync("/login");
        Assert.Equal(HttpStatusCode.Redirect, login.StatusCode);
        Assert.Contains("/setup", login.Headers.Location!.ToString());
    }

    [Fact]
    public async Task Setup_CreatesAdminOwner_SignsIn_AndPersists()
    {
        var client = RedirectingClient(); // follows redirects for this one

        var token = await GetAntiforgeryTokenAsync(client, "/setup");
        var resp = await client.PostAsync("/auth/setup",
            Form(token, ("email", "Owner@Example.com"), ("password", "Sup3rSecret!"), ("confirmPassword", "Sup3rSecret!")));

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode); // followed the 302 -> /
        var html = await resp.Content.ReadAsStringAsync();
        Assert.Contains("owner@example.com", html);
        Assert.Contains("Admin", html);

        var user = await GetUserAsync("owner@example.com"); // lowercased
        Assert.Equal("Admin", user.Role);
        Assert.False(string.IsNullOrEmpty(user.PasswordHash));
    }

    [Fact]
    public async Task Setup_ClosesAfterFirstOwner_NoSecondAdminCreated()
    {
        await SeedOwnerAsync("owner@example.com", "Sup3rSecret!");

        var noRedirect = NoRedirectClient();
        var getSetup = await noRedirect.GetAsync("/setup");
        Assert.Equal(HttpStatusCode.Redirect, getSetup.StatusCode);
        Assert.Contains("/login", getSetup.Headers.Location!.ToString());

        // A second POST /auth/setup must be refused. The setup gate now redirects GET /setup ->
        // /login (asserted above), so there is no legitimate page left to harvest a fresh
        // antiforgery token from -- simulating a replayed/forged attempt with no real token at
        // all is the realistic attack shape here, and it must fail closed regardless of whether
        // antiforgery or the endpoint's own Users.Any() re-check is what catches it.
        var secondClient = NoRedirectClient();
        var resp = await secondClient.PostAsync("/auth/setup", new FormUrlEncodedContent(
        [
            new KeyValuePair<string, string>("email", "second@example.com"),
            new KeyValuePair<string, string>("password", "Whatever1!"),
            new KeyValuePair<string, string>("confirmPassword", "Whatever1!"),
        ]));

        Assert.True(resp.StatusCode is HttpStatusCode.Redirect or HttpStatusCode.BadRequest);

        using var scope = _factory.Services.CreateScope();
        var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<PanelDb>>();
        await using var db = await factory.CreateDbContextAsync();
        Assert.Equal(1, await db.Users.CountAsync());
    }

    [Fact]
    public async Task Login_WrongPassword_Fails_CorrectPassword_Succeeds()
    {
        await SeedOwnerAsync("owner@example.com", "Sup3rSecret!");

        var client = NoRedirectClient();
        var token = await GetAntiforgeryTokenAsync(client, "/login");

        var wrong = await client.PostAsync("/auth/login",
            Form(token, ("email", "owner@example.com"), ("password", "WrongPassword1!"), ("returnUrl", "")));
        Assert.Equal(HttpStatusCode.Redirect, wrong.StatusCode);
        Assert.Contains("/login?error=1", wrong.Headers.Location!.ToString());
        Assert.DoesNotContain(wrong.Headers, h => h.Key == "Set-Cookie" && h.Value.Any(v => v.Contains("PalPanel.Auth")));

        var correct = await client.PostAsync("/auth/login",
            Form(token, ("email", "owner@example.com"), ("password", "Sup3rSecret!"), ("returnUrl", "")));
        Assert.Equal(HttpStatusCode.Redirect, correct.StatusCode);
        Assert.Equal("/", correct.Headers.Location!.ToString());
        Assert.Contains(correct.Headers, h => h.Key == "Set-Cookie" && h.Value.Any(v => v.Contains("PalPanel.Auth")));

        var events = await GetEventsAsync("login-success");
        Assert.Contains(events, e => e.ActorEmail == "owner@example.com" && e.Detail.Contains("method=password"));
    }

    [Fact]
    public async Task Login_FiveWrongAttempts_Locks_ThenCorrectPasswordStillRefused()
    {
        await SeedOwnerAsync("owner@example.com", "Sup3rSecret!");

        var client = NoRedirectClient();
        var token = await GetAntiforgeryTokenAsync(client, "/login");

        for (var i = 0; i < 5; i++)
        {
            var resp = await client.PostAsync("/auth/login",
                Form(token, ("email", "owner@example.com"), ("password", "WrongPassword1!"), ("returnUrl", "")));
            Assert.Equal(HttpStatusCode.Redirect, resp.StatusCode);
            Assert.Contains("/login?error=1", resp.Headers.Location!.ToString());
        }

        // Account is now locked; even the CORRECT password must still be refused.
        var stillLocked = await client.PostAsync("/auth/login",
            Form(token, ("email", "owner@example.com"), ("password", "Sup3rSecret!"), ("returnUrl", "")));
        Assert.Equal(HttpStatusCode.Redirect, stillLocked.StatusCode);
        Assert.Contains("/login?error=1", stillLocked.Headers.Location!.ToString());
        Assert.DoesNotContain(stillLocked.Headers, h => h.Key == "Set-Cookie" && h.Value.Any(v => v.Contains("PalPanel.Auth")));

        var lockedEvents = await GetEventsAsync("login-locked");
        Assert.Contains(lockedEvents, e => e.ActorEmail == "owner@example.com");

        var user = await GetUserAsync("owner@example.com");
        Assert.NotNull(user.LockedUntil);
    }

    [Fact]
    public async Task Logout_ClearsCookie_SubsequentRootRedirectsToLogin()
    {
        await SeedOwnerAsync("owner@example.com", "Sup3rSecret!");

        var client = NoRedirectClient();
        var loginToken = await GetAntiforgeryTokenAsync(client, "/login");
        var login = await client.PostAsync("/auth/login",
            Form(loginToken, ("email", "owner@example.com"), ("password", "Sup3rSecret!"), ("returnUrl", "")));
        Assert.Equal(HttpStatusCode.Redirect, login.StatusCode);
        Assert.Equal("/", login.Headers.Location!.ToString());

        // Confirm we really are authenticated before logging out.
        var beforeLogout = await client.GetAsync("/");
        Assert.Equal(HttpStatusCode.OK, beforeLogout.StatusCode);

        // Logout is now antiforgery-protected (a form-bound endpoint), so the token+cookie must
        // be supplied exactly like login/setup. Crucially, the token must be fetched AFTER
        // sign-in: ASP.NET's antiforgery token embeds the authenticated user's identity, so the
        // `loginToken` minted earlier while anonymous is no longer valid now that we hold an auth
        // cookie -- a fresh GET (of any page that renders <AntiforgeryToken />) issues one bound
        // to the current user.
        var logoutToken = await GetAntiforgeryTokenAsync(client, "/login");
        var logout = await client.PostAsync("/auth/logout", Form(logoutToken));
        Assert.Equal(HttpStatusCode.Redirect, logout.StatusCode);
        Assert.Equal("/login", logout.Headers.Location!.ToString());

        var afterLogout = await client.GetAsync("/");
        Assert.Equal(HttpStatusCode.Redirect, afterLogout.StatusCode);
        Assert.Contains("/login", afterLogout.Headers.Location!.ToString());
    }

    [Fact]
    public async Task Login_NoAntiforgeryToken_Rejected()
    {
        await SeedOwnerAsync("owner@example.com", "Sup3rSecret!");

        // A fresh client with no antiforgery cookie/token at all -- proves the [FromForm]
        // binding on /auth/login really does enforce antiforgery, not just happen to pass
        // because our helper always supplies a valid one.
        var client = NoRedirectClient();
        var resp = await client.PostAsync("/auth/login", new FormUrlEncodedContent(
        [
            new KeyValuePair<string, string>("email", "owner@example.com"),
            new KeyValuePair<string, string>("password", "Sup3rSecret!"),
        ]));

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task Logout_NoAntiforgeryToken_Rejected()
    {
        // Proves the logout CSRF hole is closed: the dummy [FromForm] param means /auth/logout
        // now carries the same automatic antiforgery requirement as login/setup, so a tokenless
        // (i.e. cross-site) POST is rejected rather than silently signing the victim out. A fresh
        // client with no antiforgery cookie/token at all is exactly the shape of a CSRF attempt.
        await SeedOwnerAsync("owner@example.com", "Sup3rSecret!");

        var client = NoRedirectClient();
        var resp = await client.PostAsync("/auth/logout", new FormUrlEncodedContent([]));

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task Setup_NoAntiforgeryToken_Rejected()
    {
        // Parity with Login_NoAntiforgeryToken_Rejected: an empty-DB fresh install, POST
        // /auth/setup with no antiforgery cookie/token, must be rejected before any owner is
        // created -- proving the [FromForm] binding enforces antiforgery on setup too.
        var client = NoRedirectClient();
        var resp = await client.PostAsync("/auth/setup", new FormUrlEncodedContent(
        [
            new KeyValuePair<string, string>("email", "owner@example.com"),
            new KeyValuePair<string, string>("password", "Sup3rSecret!"),
            new KeyValuePair<string, string>("confirmPassword", "Sup3rSecret!"),
        ]));

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);

        using var scope = _factory.Services.CreateScope();
        var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<PanelDb>>();
        await using var db = await factory.CreateDbContextAsync();
        Assert.Equal(0, await db.Users.CountAsync());
    }

    [Fact]
    public async Task Setup_PasswordTooShort_Rejected_NoOwnerCreated()
    {
        var client = NoRedirectClient();
        var token = await GetAntiforgeryTokenAsync(client, "/setup");
        var resp = await client.PostAsync("/auth/setup",
            Form(token, ("email", "owner@example.com"), ("password", "short7!"), ("confirmPassword", "short7!"))); // 7 chars

        Assert.Equal(HttpStatusCode.Redirect, resp.StatusCode);
        Assert.Contains("/setup", resp.Headers.Location!.ToString());

        using var scope = _factory.Services.CreateScope();
        var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<PanelDb>>();
        await using var db = await factory.CreateDbContextAsync();
        Assert.Equal(0, await db.Users.CountAsync());
    }

    // Open-redirect hardening: a successful login with a hostile returnUrl must never redirect
    // off-panel. Each malicious form is one of the classic open-redirect payloads; all must
    // land on the local "/" root, while a genuine local path is honored verbatim.
    [Theory]
    [InlineData("https://evil.example", "/")]
    [InlineData("//evil.example", "/")]
    [InlineData("/\\evil.example", "/")]        // "/\evil.example" -- backslash after the slash
    [InlineData("/\t/evil.example", "/")]        // embedded control char (tab) the browser would normalize
    [InlineData("/history", "/history")]         // legit local path -- honored
    [InlineData("", "/")]                         // empty -> root
    public async Task Login_ReturnUrl_OnlyHonorsLocalPaths(string returnUrl, string expectedLocation)
    {
        await SeedOwnerAsync("owner@example.com", "Sup3rSecret!");

        var client = NoRedirectClient();
        var token = await GetAntiforgeryTokenAsync(client, "/login");
        var resp = await client.PostAsync("/auth/login",
            Form(token, ("email", "owner@example.com"), ("password", "Sup3rSecret!"), ("returnUrl", returnUrl)));

        Assert.Equal(HttpStatusCode.Redirect, resp.StatusCode);
        Assert.Equal(expectedLocation, resp.Headers.Location!.ToString());
    }
}
