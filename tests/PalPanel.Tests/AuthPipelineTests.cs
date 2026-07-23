using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using PalPanel.Data;

namespace PalPanel.Tests;

// Pins the replacement for AccessJwtMiddleware: cookie+Google auth gates the app, and the
// AuthDisabled dev bypass still gets a full Admin principal (both the ClaimsPrincipal for
// AuthorizeView AND the DB-backed IAdminGuard) without needing a real login.
//
// Uses ConfigureAppConfiguration (not UseSetting) for the overrides: on a dev box with a
// gitignored appsettings.Local.json (Program.cs loads it after Configure<PanelOptions>'s
// section is registered), that file's own values would otherwise win over UseSetting for the
// same keys, since Program.cs's own AddJsonFile call runs after the WebApplicationFactory's
// UseSetting hook applies. ConfigureAppConfiguration callbacks are layered on top of
// Program.cs's own configuration sources instead, so they reliably override appsettings.Local.json.
public class AuthPipelineTests
{
    private static WebApplicationFactory<Program> MakeFactory(bool authDisabled) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
            b.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Panel:AuthDisabled"] = authDisabled ? "true" : "false",
                ["Panel:DbPath"] = Path.GetTempFileName(),
            })));

    [Fact]
    public async Task AuthDisabled_RendersAsAdmin()
    {
        await using var factory = MakeFactory(authDisabled: true);
        var client = factory.CreateClient();

        var resp = await client.GetAsync("/");

        Assert.Equal(System.Net.HttpStatusCode.OK, resp.StatusCode);
        var html = await resp.Content.ReadAsStringAsync();
        Assert.Contains("dev@localhost", html);
        Assert.Contains("Admin", html);
        // NavMenu only renders the Backups/Settings links inside <AuthorizeView Roles="Admin">
        Assert.Contains("href=\"/settings\"", html);
    }

    [Fact]
    public async Task AuthEnabled_NoCookie_RedirectsToLogin()
    {
        await using var factory = MakeFactory(authDisabled: false);

        // A user must already exist, otherwise SetupGateMiddleware (Task 4) wins over the
        // ordinary "no cookie" challenge and redirects to /setup instead of /login -- that
        // first-run behavior has its own dedicated coverage in AuthEndpointsTests. This test's
        // subject is specifically "authenticated required once the app is past first-run".
        using (var scope = factory.Services.CreateScope())
        {
            var dbf = scope.ServiceProvider.GetRequiredService<IDbContextFactory<PalPanel.Data.PanelDb>>();
            await using var db = await dbf.CreateDbContextAsync();
            db.Users.Add(new PanelUser { Email = "owner@x.com", Role = "Admin", FirstSeen = DateTimeOffset.UtcNow, LastSeen = DateTimeOffset.UtcNow });
            await db.SaveChangesAsync();
        }

        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var resp = await client.GetAsync("/");

        Assert.Equal(System.Net.HttpStatusCode.Redirect, resp.StatusCode);
        Assert.Contains("/login", resp.Headers.Location!.ToString());
    }
}
