using System.Net;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using PalPanel.Data;

namespace PalPanel.Tests;

// Exercises the real Cf-Access-Jwt-Assertion path (not the AuthDisabled dev shortcut) so the
// endpoint's own `p.Role != "Admin"` check is genuinely proven for both roles, matching the
// established pattern in AccessJwtTests.cs.
public class BackupDownloadEndpointTests : IAsyncLifetime
{
    private const string Aud = "test-aud-tag";

    private StubJwksServer _jwks = null!;
    private WebApplicationFactory<Program> _factory = null!;
    private string _dbPath = null!;
    private string _backupDir = null!;
    private string _saveDir = null!;

    public Task InitializeAsync()
    {
        _jwks = new StubJwksServer();
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
                    ["Panel:AccessTeamDomain"] = _jwks.BaseUrl,
                    ["Panel:AccessAud"] = Aud,
                    ["Panel:AuthDisabled"] = "false",
                    ["Panel:DbPath"] = _dbPath,
                    ["Panel:BackupDirectory"] = _backupDir,
                    ["Panel:SaveDirectory"] = _saveDir,
                });
            });
        });

        // First-user-ever-becomes-Admin: seed the admin, then a second user starts Viewer.
        using var scope = _factory.Services.CreateScope();
        var dbf = scope.ServiceProvider.GetRequiredService<IDbContextFactory<PanelDb>>();
        using var db = dbf.CreateDbContext();
        db.Users.Add(new PanelUser
        {
            Email = "admin@x.com",
            Role = "Admin",
            FirstSeen = DateTimeOffset.UtcNow,
            LastSeen = DateTimeOffset.UtcNow,
        });
        db.SaveChanges();
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        await _factory.DisposeAsync();
        await _jwks.DisposeAsync();
        try { File.Delete(_dbPath); } catch { /* best-effort cleanup */ }
        try { Directory.Delete(_backupDir, recursive: true); } catch { /* best-effort cleanup */ }
        try { Directory.Delete(_saveDir, recursive: true); } catch { /* best-effort cleanup */ }
    }

    private HttpClient ClientWithToken(string email)
    {
        var client = _factory.CreateClient();
        var token = _jwks.IssueToken(_jwks.BaseUrl, Aud, email);
        client.DefaultRequestHeaders.Add("Cf-Access-Jwt-Assertion", token);
        return client;
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
        var fileName = await CreateBackupAsync();
        var resp = await ClientWithToken("admin@x.com").GetAsync($"/backups/download/{fileName}");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal("application/zip", resp.Content.Headers.ContentType?.MediaType);
        var bytes = await resp.Content.ReadAsByteArrayAsync();
        Assert.True(bytes.Length > 0);
    }

    [Fact]
    public async Task Admin_UnknownFile_Returns404()
    {
        var resp = await ClientWithToken("admin@x.com").GetAsync("/backups/download/nonexistent.zip");
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    [Fact]
    public async Task Viewer_KnownFile_Returns403()
    {
        var fileName = await CreateBackupAsync();
        // Second-ever user defaults to Viewer.
        var resp = await ClientWithToken("viewer@x.com").GetAsync($"/backups/download/{fileName}");
        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
    }

    [Fact]
    public async Task Admin_PathTraversalName_Returns404_NeverEscapesBackupDirectory()
    {
        // The endpoint matches against List() (which only ever enumerates real files in
        // BackupDirectory) rather than combining the raw route value into a path, so a
        // traversal attempt simply isn't found — it can never resolve outside BackupDirectory.
        var resp = await ClientWithToken("admin@x.com").GetAsync("/backups/download/..%2f..%2fWindows%2fwin.ini");
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }
}
