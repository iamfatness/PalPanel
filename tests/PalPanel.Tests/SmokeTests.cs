using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace PalPanel.Tests;

public class SmokeTests(WebApplicationFactory<Program> factory) : IClassFixture<WebApplicationFactory<Program>>
{
    // The seeded "Main" server's id from a built factory (LegacyServerMigration seeds one at
    // startup from PanelOptions). Per-server pages live under /s/{id}/...
    private static Guid SeededServerId(WebApplicationFactory<Program> f) =>
        f.Services.GetRequiredService<PalPanel.Servers.ServerManager>().All().First().Id;

    [Fact]
    public async Task Root_RendersOverview_WithAuthDisabled()
    {
        var client = factory.WithWebHostBuilder(b =>
            b.UseSetting("Panel:AuthDisabled", "true")
             .UseSetting("Panel:DbPath", Path.GetTempFileName())).CreateClient();
        var html = await client.GetStringAsync("/");
        Assert.Contains("PalPanel", html);
        Assert.Contains("Stopped", html); // initial supervisor state rendered
    }

    [Fact]
    public async Task Players_RendersWithAuthDisabled()
    {
        var f = factory.WithWebHostBuilder(b =>
            b.UseSetting("Panel:AuthDisabled", "true")
             .UseSetting("Panel:DbPath", Path.GetTempFileName()));
        var resp = await f.CreateClient().GetAsync($"/s/{SeededServerId(f)}/players");
        Assert.Equal(System.Net.HttpStatusCode.OK, resp.StatusCode);
        var html = await resp.Content.ReadAsStringAsync();
        Assert.Contains("Players", html);
    }

    [Fact]
    public async Task History_RendersWithAuthDisabled()
    {
        var f = factory.WithWebHostBuilder(b =>
            b.UseSetting("Panel:AuthDisabled", "true")
             .UseSetting("Panel:DbPath", Path.GetTempFileName()));
        var resp = await f.CreateClient().GetAsync($"/s/{SeededServerId(f)}/history");
        Assert.Equal(System.Net.HttpStatusCode.OK, resp.StatusCode);
        var html = await resp.Content.ReadAsStringAsync();
        Assert.Contains("History", html);
    }

    [Fact]
    public async Task Backups_RendersWithAuthDisabled()
    {
        var f = factory.WithWebHostBuilder(b =>
            b.UseSetting("Panel:AuthDisabled", "true")
             .UseSetting("Panel:DbPath", Path.GetTempFileName())
             .UseSetting("Panel:BackupDirectory", Directory.CreateTempSubdirectory().FullName));
        var resp = await f.CreateClient().GetAsync($"/s/{SeededServerId(f)}/backups");
        Assert.Equal(System.Net.HttpStatusCode.OK, resp.StatusCode);
        var html = await resp.Content.ReadAsStringAsync();
        Assert.Contains("Backups", html);
    }

    [Fact]
    public async Task PanelSettings_RendersUsersWithAuthDisabled()
    {
        var client = factory.WithWebHostBuilder(b =>
            b.UseSetting("Panel:AuthDisabled", "true")
             .UseSetting("Panel:DbPath", Path.GetTempFileName())).CreateClient();
        var resp = await client.GetAsync("/settings");
        Assert.Equal(System.Net.HttpStatusCode.OK, resp.StatusCode);
        var html = await resp.Content.ReadAsStringAsync();
        Assert.Contains("Users", html);
    }

    [Fact]
    public async Task ServerSettings_RendersSchedulesWithAuthDisabled()
    {
        var f = factory.WithWebHostBuilder(b =>
            b.UseSetting("Panel:AuthDisabled", "true")
             .UseSetting("Panel:DbPath", Path.GetTempFileName()));
        var resp = await f.CreateClient().GetAsync($"/s/{SeededServerId(f)}/settings");
        Assert.Equal(System.Net.HttpStatusCode.OK, resp.StatusCode);
        var html = await resp.Content.ReadAsStringAsync();
        Assert.Contains("Schedules", html);
    }

    // Logged-out/session UX (Task 7): with AuthDisabled=true the dev bypass signs in as
    // dev@localhost/Admin (see Program.cs), so MainLayout's <AuthorizeView><Authorized> branch
    // renders -- footer must show that email plus a Logout control (a form posting to
    // /auth/logout, carrying the antiforgery hidden field so AuthEndpoints.LogoutAsync's
    // ValidateRequestAsync accepts it).
    [Fact]
    public async Task Root_FooterShowsDevEmailAndLogoutControl_WithAuthDisabled()
    {
        var client = factory.WithWebHostBuilder(b =>
            b.UseSetting("Panel:AuthDisabled", "true")
             .UseSetting("Panel:DbPath", Path.GetTempFileName())).CreateClient();
        var html = await client.GetStringAsync("/");
        Assert.Contains("dev@localhost", html);
        Assert.Contains("action=\"/auth/logout\"", html);
        Assert.Contains("Sign out", html);
        Assert.Contains("__RequestVerificationToken", html); // antiforgery token rendered in the logout form
    }

    // /login is reached via MainLayout's <NotAuthorized> branch (unauthenticated request, no
    // dev bypass claim on this client) and must render its own full content: both sign-in paths
    // (password fields + "Sign in with Google"), not the authenticated nav/footer chrome.
    [Fact]
    public async Task Login_RendersGoogleLinkAndPasswordFields()
    {
        var dbPath = Path.GetTempFileName();

        // SetupGateMiddleware redirects every non-exempt path -- including /login -- to /setup
        // until at least one PanelUser row exists. Seed one via the AuthDisabled dev bypass
        // (which creates dev@localhost as Admin) on a throwaway client sharing the same sqlite
        // file, so the real client below hits /login's normal (post-setup) rendering rather
        // than being redirected to /setup.
        var seedClient = factory.WithWebHostBuilder(b =>
            b.UseSetting("Panel:AuthDisabled", "true")
             .UseSetting("Panel:DbPath", dbPath)).CreateClient();
        await seedClient.GetAsync("/");

        // This dev box's gitignored src/PalPanel/appsettings.Local.json sets
        // Panel:AuthDisabled=true, and Program.cs's own AddJsonFile("appsettings.Local.json")
        // call runs AFTER whatever hook applies WebApplicationFactory's UseSetting(...) for the
        // same key -- so UseSetting("Panel:AuthDisabled", "false") would be silently clobbered
        // back to "true" by that file (see AuthEndpointsTests for the same gotcha/fix).
        // ConfigureAppConfiguration callbacks layer on top of Program.cs's own configuration
        // sources instead, so they reliably win, giving a real unauthenticated client here.
        var client = factory.WithWebHostBuilder(b =>
            b.ConfigureAppConfiguration((_, config) =>
            {
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Panel:AuthDisabled"] = "false",
                    ["Panel:DbPath"] = dbPath,
                });
            })).CreateClient();
        var html = await client.GetStringAsync("/login");
        Assert.Contains("Sign in with Google", html);
        Assert.Contains("name=\"email\"", html);
        Assert.Contains("name=\"password\"", html);
        Assert.DoesNotContain("nav-menu", html); // no authenticated nav sidebar on the login page
    }
}
