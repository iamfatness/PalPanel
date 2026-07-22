using System.Net;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using PalPanel.Data;

namespace PalPanel.Tests;

public class AccessJwtTests : IAsyncLifetime
{
    private const string Aud = "test-aud-tag";

    private StubJwksServer _jwks = null!;
    private WebApplicationFactory<Program> _factory = null!;
    private string _dbPath = null!;

    public Task InitializeAsync()
    {
        _jwks = new StubJwksServer();
        _dbPath = Path.GetTempFileName();

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
                });
            });
        });

        // Seed an existing Admin so the tokens issued below don't land on an empty
        // Users table and get auto-promoted to Admin by the "first user ever" rule.
        using var scope = _factory.Services.CreateScope();
        var dbf = scope.ServiceProvider.GetRequiredService<IDbContextFactory<PanelDb>>();
        using var db = dbf.CreateDbContext();
        db.Users.Add(new PanelUser
        {
            Email = "seed-admin@x.com",
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
    }

    private HttpClient ClientWithToken(string? token)
    {
        var client = _factory.CreateClient();
        if (token is not null)
            client.DefaultRequestHeaders.Add("Cf-Access-Jwt-Assertion", token);
        return client;
    }

    [Fact]
    public async Task ValidToken_Returns200_AndCreatesUserAsViewer()
    {
        var token = _jwks.IssueToken(_jwks.BaseUrl, Aud, "newperson@x.com");
        var resp = await ClientWithToken(token).GetAsync("/healthz");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        using var scope = _factory.Services.CreateScope();
        var dbf = scope.ServiceProvider.GetRequiredService<IDbContextFactory<PanelDb>>();
        using var db = dbf.CreateDbContext();
        var user = db.Users.Single(u => u.Email == "newperson@x.com");
        Assert.Equal("Viewer", user.Role);
    }

    [Fact]
    public async Task MissingHeader_Returns401()
    {
        var resp = await ClientWithToken(null).GetAsync("/healthz");
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    [Fact]
    public async Task WrongAudience_Returns401()
    {
        var token = _jwks.IssueToken(_jwks.BaseUrl, "some-other-audience", "someone@x.com");
        var resp = await ClientWithToken(token).GetAsync("/healthz");
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    [Fact]
    public async Task WrongIssuer_Returns401()
    {
        var token = _jwks.IssueToken("https://not-the-team-domain.example", Aud, "someone@x.com");
        var resp = await ClientWithToken(token).GetAsync("/healthz");
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    [Fact]
    public async Task ExpiredToken_Returns401()
    {
        var token = _jwks.IssueToken(_jwks.BaseUrl, Aud, "someone@x.com", TimeSpan.FromMinutes(-5));
        var resp = await ClientWithToken(token).GetAsync("/healthz");
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    [Fact]
    public async Task BlockedUser_Returns403()
    {
        using (var scope = _factory.Services.CreateScope())
        {
            var dbf = scope.ServiceProvider.GetRequiredService<IDbContextFactory<PanelDb>>();
            using var db = dbf.CreateDbContext();
            db.Users.Add(new PanelUser
            {
                Email = "blocked@x.com",
                Role = "Blocked",
                FirstSeen = DateTimeOffset.UtcNow,
                LastSeen = DateTimeOffset.UtcNow,
            });
            db.SaveChanges();
        }

        var token = _jwks.IssueToken(_jwks.BaseUrl, Aud, "blocked@x.com");
        var resp = await ClientWithToken(token).GetAsync("/healthz");
        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
    }
}
