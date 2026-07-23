using System.Net;
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using PalPanel.Data;

namespace PalPanel.Tests;

// Exercises the real cookie-auth path (not the AuthDisabled dev shortcut) so the endpoint's
// authorization -- DB-authoritative via IAdminGuard.EnsureAdminAsync, not the Role claim baked
// into the cookie -- is genuinely proven for both roles. There is no login endpoint yet (that's
// a later task), so we mint a valid PalPanel.Auth cookie directly via the same
// CookieAuthenticationOptions.TicketDataFormat the real handler uses -- this is the standard
// way to test cookie-gated endpoints with WebApplicationFactory without a real login POST. The
// cookie's own Role claim is now deliberately irrelevant to authorization (see
// StaleAdminCookie_DbRoleDemotedToViewer_Returns403 below) -- what matters is the Users row in
// the DB, so every test that expects success/403 based on role seeds (or omits/mutates) that row
// explicitly rather than relying on the claim.
public class BackupDownloadEndpointTests : IAsyncLifetime
{
    private WebApplicationFactory<Program> _factory = null!;
    private string _dbPath = null!;
    private string _backupDir = null!;
    private string _saveDir = null!;

    public Task InitializeAsync()
    {
        _dbPath = Path.GetTempFileName();
        _backupDir = Directory.CreateTempSubdirectory().FullName;
        _saveDir = Directory.CreateTempSubdirectory().FullName;
        File.WriteAllText(Path.Combine(_saveDir, "Level.sav"), "worlddata");

        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, config) =>
            {
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Panel:AuthDisabled"] = "false",
                    ["Panel:DbPath"] = _dbPath,
                    ["Panel:BackupDirectory"] = _backupDir,
                    ["Panel:SaveDirectory"] = _saveDir,
                });
            });
        });
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        await _factory.DisposeAsync();
        try { File.Delete(_dbPath); } catch { /* best-effort cleanup */ }
        try { Directory.Delete(_backupDir, recursive: true); } catch { /* best-effort cleanup */ }
        try { Directory.Delete(_saveDir, recursive: true); } catch { /* best-effort cleanup */ }
    }

    private HttpClient ClientWithCookie(string email, string role)
    {
        using var scope = _factory.Services.CreateScope();
        var optionsMonitor = scope.ServiceProvider.GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>();
        var options = optionsMonitor.Get(CookieAuthenticationDefaults.AuthenticationScheme);
        var identity = new ClaimsIdentity(
        [
            new Claim(ClaimTypes.Email, email),
            new Claim(ClaimTypes.Role, role),
        ], CookieAuthenticationDefaults.AuthenticationScheme);
        var ticket = new Microsoft.AspNetCore.Authentication.AuthenticationTicket(
            new ClaimsPrincipal(identity), CookieAuthenticationDefaults.AuthenticationScheme);
        var cookieValue = options.TicketDataFormat.Protect(ticket);

        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("Cookie", $"{options.Cookie.Name}={Uri.EscapeDataString(cookieValue)}");
        return client;
    }

    // Seeds (or updates) the Users row backing a given email, since authorization is now
    // DB-authoritative (IAdminGuard.EnsureAdminAsync) rather than claims-based -- the cookie's own
    // Role claim, minted by ClientWithCookie, no longer has any bearing on the 403 decision.
    private async Task SeedUserAsync(string email, string role)
    {
        using var scope = _factory.Services.CreateScope();
        var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<PanelDb>>();
        await using var db = await factory.CreateDbContextAsync();
        var existing = await db.Users.SingleOrDefaultAsync(u => u.Email == email);
        if (existing is null)
        {
            var now = DateTimeOffset.UtcNow;
            db.Users.Add(new PanelUser { Email = email, Role = role, FirstSeen = now, LastSeen = now });
        }
        else
        {
            existing.Role = role;
        }
        await db.SaveChangesAsync();
    }

    private async Task<string> CreateBackupAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var backups = scope.ServiceProvider.GetRequiredService<PalPanel.Control.IBackupService>();
        var path = await backups.CreateBackupAsync("test", CancellationToken.None);
        return Path.GetFileName(path);
    }

    [Fact]
    public async Task Admin_KnownFile_Returns200_WithZipContent()
    {
        await SeedUserAsync("admin@x.com", "Admin");
        var fileName = await CreateBackupAsync();
        var resp = await ClientWithCookie("admin@x.com", "Admin").GetAsync($"/backups/download/{fileName}");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal("application/zip", resp.Content.Headers.ContentType?.MediaType);
        var bytes = await resp.Content.ReadAsByteArrayAsync();
        Assert.True(bytes.Length > 0);
    }

    [Fact]
    public async Task Admin_UnknownFile_Returns404()
    {
        await SeedUserAsync("admin@x.com", "Admin");
        var resp = await ClientWithCookie("admin@x.com", "Admin").GetAsync("/backups/download/nonexistent.zip");
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    [Fact]
    public async Task Viewer_KnownFile_Returns403()
    {
        // The DB row -- not the cookie's Role claim -- is what must drive this 403: seed the
        // authenticated user as Viewer in the DB so the check is proven DB-authoritative rather
        // than merely "no matching Users row" (which would also 403 for an unrelated reason).
        await SeedUserAsync("viewer@x.com", "Viewer");
        var fileName = await CreateBackupAsync();
        var resp = await ClientWithCookie("viewer@x.com", "Viewer").GetAsync($"/backups/download/{fileName}");
        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
    }

    [Fact]
    public async Task StaleAdminCookie_DbRoleDemotedToViewer_Returns403()
    {
        // Pins the DB-authoritative behavior directly: the cookie still carries a Role=Admin
        // claim minted at sign-in (exactly what a 7-day-old session would look like), but the DB
        // row backing that email has since been demoted to Viewer. Authorization must follow the
        // DB, not the stale claim baked into the cookie.
        await SeedUserAsync("stale-admin@x.com", "Admin");
        var fileName = await CreateBackupAsync();
        var client = ClientWithCookie("stale-admin@x.com", "Admin"); // cookie claim says Admin

        await SeedUserAsync("stale-admin@x.com", "Viewer"); // DB now says Viewer

        var resp = await client.GetAsync($"/backups/download/{fileName}");
        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
    }

    [Fact]
    public async Task Unauthenticated_KnownFile_RedirectsToLogin()
    {
        var fileName = await CreateBackupAsync();
        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var resp = await client.GetAsync($"/backups/download/{fileName}");
        Assert.Equal(HttpStatusCode.Redirect, resp.StatusCode);
        Assert.Contains("/login", resp.Headers.Location!.ToString());
    }

    [Fact]
    public async Task Admin_PathTraversalName_Returns404_NeverEscapesBackupDirectory()
    {
        // The endpoint matches against List() (which only ever enumerates real files in
        // BackupDirectory) rather than combining the raw route value into a path, so a
        // traversal attempt simply isn't found -- it can never resolve outside BackupDirectory.
        await SeedUserAsync("admin@x.com", "Admin");
        var resp = await ClientWithCookie("admin@x.com", "Admin").GetAsync("/backups/download/..%2f..%2fWindows%2fwin.ini");
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }
}
