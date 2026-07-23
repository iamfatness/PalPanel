using System.Net;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using PalPanel;
using PalPanel.Auth;
using PalPanel.Data;

namespace PalPanel.Tests;

// Unit-level coverage of UserAdminService: create/set-password/clear-password, all guarded by
// the REAL AdminGuard against a real (SQLite temp-file) DB, exactly like RoleServiceTests does
// for SetRoleAsync -- a fake always-allow guard would prove nothing about the actual
// authorization backstop these methods are supposed to provide.
public class UserAdminServiceTests
{
    private static (IUserAdminService Svc, RoleService Roles, IDbContextFactory<PanelDb> Dbf, List<(string Type, string Detail, string? Actor)> Events)
        Make()
    {
        var services = new ServiceCollection();
        services.AddDbContextFactory<PanelDb>(b => b.UseSqlite($"Data Source={Path.GetTempFileName()}"));
        var sp = services.BuildServiceProvider();
        var dbf = sp.GetRequiredService<IDbContextFactory<PanelDb>>();
        using (var db = dbf.CreateDbContext()) db.Database.EnsureCreated();

        var events = new List<(string Type, string Detail, string? Actor)>();
        var sink = new RecordingEventSink(events);
        var guard = new AdminGuard(dbf, sink, Options.Create(new PanelOptions())); // real guard, AuthDisabled=false
        var notifier = new RoleChangeNotifier();
        var roles = new RoleService(dbf, sink, notifier, guard);
        var svc = new UserAdminService(guard, new PasswordService(), dbf, sink);
        return (svc, roles, dbf, events);
    }

    private class RecordingEventSink(List<(string Type, string Detail, string? Actor)> events) : IEventSink
    {
        public Task LogAsync(string type, string detail, string? actorEmail = null)
        { events.Add((type, detail, actorEmail)); return Task.CompletedTask; }
    }

    [Fact]
    public async Task CreateUserAsync_WithPassword_SetsHashAndMustChange_LogsEvent()
    {
        var (svc, roles, dbf, events) = Make();
        await roles.GetOrCreateAsync("owner@x.com"); // first user ever -> Admin

        await svc.CreateUserAsync("new@x.com", "Viewer", "Sup3rSecret!", "owner@x.com");

        using var db = dbf.CreateDbContext();
        var u = db.Users.Single(x => x.Email == "new@x.com");
        Assert.Equal("Viewer", u.Role);
        Assert.False(string.IsNullOrEmpty(u.PasswordHash));
        Assert.True(u.MustChangePassword);
        Assert.Contains(events, e => e.Type == "user-created" && e.Actor == "owner@x.com" && e.Detail.Contains("Viewer"));
    }

    [Fact]
    public async Task CreateUserAsync_NoPassword_PasswordHashNull_GoogleOnly()
    {
        var (svc, roles, dbf, _) = Make();
        await roles.GetOrCreateAsync("owner@x.com");

        await svc.CreateUserAsync("new@x.com", "Viewer", null, "owner@x.com");

        using var db = dbf.CreateDbContext();
        var u = db.Users.Single(x => x.Email == "new@x.com");
        Assert.Null(u.PasswordHash);
        Assert.False(u.MustChangePassword);
    }

    [Fact]
    public async Task CreateUserAsync_DuplicateEmail_Throws_NoSecondRow()
    {
        var (svc, roles, dbf, _) = Make();
        await roles.GetOrCreateAsync("owner@x.com");
        await svc.CreateUserAsync("dup@x.com", "Viewer", null, "owner@x.com");

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.CreateUserAsync("dup@x.com", "Viewer", null, "owner@x.com"));

        using var db = dbf.CreateDbContext();
        Assert.Equal(1, db.Users.Count(u => u.Email == "dup@x.com"));
    }

    [Fact]
    public async Task CreateUserAsync_PasswordTooShort_Throws_NoUserCreated()
    {
        var (svc, roles, dbf, _) = Make();
        await roles.GetOrCreateAsync("owner@x.com");

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.CreateUserAsync("short@x.com", "Viewer", "short1", "owner@x.com")); // 6 chars < MinPasswordLength

        using var db = dbf.CreateDbContext();
        Assert.False(db.Users.Any(u => u.Email == "short@x.com"));
    }

    [Fact]
    public async Task CreateUserAsync_NonAdminActor_ThrowsAndLogsUnauthorized_NoUserCreated()
    {
        var (svc, roles, dbf, events) = Make();
        await roles.GetOrCreateAsync("owner@x.com"); // Admin
        await roles.GetOrCreateAsync("viewer@x.com"); // Viewer

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => svc.CreateUserAsync("new@x.com", "Viewer", null, "viewer@x.com"));

        using var db = dbf.CreateDbContext();
        Assert.False(db.Users.Any(u => u.Email == "new@x.com"));
        Assert.Contains(events, e => e.Type == "unauthorized-action" && e.Actor == "viewer@x.com");
    }

    [Fact]
    public async Task SetPasswordAsync_NonAdminActor_ThrowsAndLogsUnauthorized_NoChange()
    {
        var (svc, roles, dbf, events) = Make();
        await roles.GetOrCreateAsync("owner@x.com");
        await roles.GetOrCreateAsync("viewer@x.com");
        await svc.CreateUserAsync("target@x.com", "Viewer", null, "owner@x.com");

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => svc.SetPasswordAsync("target@x.com", "Sup3rSecret!", "viewer@x.com"));

        using var db = dbf.CreateDbContext();
        Assert.Null(db.Users.Single(u => u.Email == "target@x.com").PasswordHash);
        Assert.Contains(events, e => e.Type == "unauthorized-action" && e.Actor == "viewer@x.com");
    }

    [Fact]
    public async Task ClearPasswordAsync_NonAdminActor_ThrowsAndLogsUnauthorized_NoChange()
    {
        var (svc, roles, dbf, events) = Make();
        await roles.GetOrCreateAsync("owner@x.com");
        await roles.GetOrCreateAsync("viewer@x.com");
        await svc.CreateUserAsync("target@x.com", "Viewer", "Sup3rSecret!", "owner@x.com");

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => svc.ClearPasswordAsync("target@x.com", "viewer@x.com"));

        using var db = dbf.CreateDbContext();
        Assert.False(string.IsNullOrEmpty(db.Users.Single(u => u.Email == "target@x.com").PasswordHash));
        Assert.Contains(events, e => e.Type == "unauthorized-action" && e.Actor == "viewer@x.com");
    }

    [Fact]
    public async Task SetPasswordAsync_SetsHashAndMustChange_LogsEvent()
    {
        var (svc, roles, dbf, events) = Make();
        await roles.GetOrCreateAsync("owner@x.com");
        await svc.CreateUserAsync("target@x.com", "Viewer", null, "owner@x.com");

        await svc.SetPasswordAsync("target@x.com", "NewSecret1!", "owner@x.com");

        using var db = dbf.CreateDbContext();
        var u = db.Users.Single(x => x.Email == "target@x.com");
        Assert.False(string.IsNullOrEmpty(u.PasswordHash));
        Assert.True(u.MustChangePassword);
        Assert.Contains(events, e => e.Type == "password-reset" && e.Actor == "owner@x.com");
    }

    [Fact]
    public async Task ClearPasswordAsync_NullsHash_LogsEvent()
    {
        var (svc, roles, dbf, events) = Make();
        await roles.GetOrCreateAsync("owner@x.com");
        await svc.CreateUserAsync("target@x.com", "Viewer", "Sup3rSecret!", "owner@x.com");

        await svc.ClearPasswordAsync("target@x.com", "owner@x.com");

        using var db = dbf.CreateDbContext();
        var u = db.Users.Single(x => x.Email == "target@x.com");
        Assert.Null(u.PasswordHash);
        Assert.False(u.MustChangePassword);
        Assert.Contains(events, e => e.Type == "password-cleared" && e.Actor == "owner@x.com");
    }
}

// End-to-end coverage of POST /auth/change-password through the real HTTP pipeline (same
// WebApplicationFactory shape as AuthEndpointsTests, including the Local.json
// ConfigureAppConfiguration workaround -- see that class's remarks for why UseSetting alone
// would be silently clobbered by the gitignored appsettings.Local.json on this dev box).
public class ChangePasswordEndpointTests : IAsyncLifetime
{
    private WebApplicationFactory<Program> _factory = null!;
    private string _dbPath = null!;

    public Task InitializeAsync()
    {
        _dbPath = Path.GetTempFileName();
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
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

    // See AuthEndpointsTests.HttpsLocal: the auth cookie is CookieSecurePolicy.Always, so the
    // client's CookieContainer only resends it over an https:// base address even though
    // TestServer never does a real TLS handshake.
    private static readonly Uri HttpsLocal = new("https://localhost");

    private HttpClient NoRedirectClient() =>
        _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, BaseAddress = HttpsLocal });

    private static readonly Regex TokenRegex = new(
        "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"|value=\"([^\"]+)\"[^>]*name=\"__RequestVerificationToken\"",
        RegexOptions.IgnoreCase);

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

    // Seeds a PanelUser directly (bypassing RoleService.GetOrCreateAsync's auto-Admin-for-first-
    // user logic, which isn't the concern of these tests) with a known password so the login
    // flow used to obtain an authenticated cookie is the real one.
    private async Task SeedUserAsync(string email, string password, string role, bool mustChange)
    {
        using var scope = _factory.Services.CreateScope();
        var dbFactory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<PanelDb>>();
        var passwords = scope.ServiceProvider.GetRequiredService<IPasswordService>();
        await using var db = await dbFactory.CreateDbContextAsync();
        var now = DateTimeOffset.UtcNow;
        db.Users.Add(new PanelUser
        {
            Email = email,
            Role = role,
            PasswordHash = passwords.Hash(password),
            MustChangePassword = mustChange,
            FirstSeen = now,
            LastSeen = now,
        });
        await db.SaveChangesAsync();
    }

    private async Task<HttpClient> LoginAsync(string email, string password, string expectedRedirect)
    {
        var client = NoRedirectClient();
        var token = await GetAntiforgeryTokenAsync(client, "/login");
        var resp = await client.PostAsync("/auth/login",
            Form(token, ("email", email), ("password", password), ("returnUrl", "")));
        Assert.Equal(HttpStatusCode.Redirect, resp.StatusCode);
        Assert.Equal(expectedRedirect, resp.Headers.Location!.ToString());
        return client;
    }

    private async Task<PanelUser> GetUserAsync(string email)
    {
        using var scope = _factory.Services.CreateScope();
        var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<PanelDb>>();
        await using var db = await factory.CreateDbContextAsync();
        return await db.Users.SingleAsync(u => u.Email == email);
    }

    [Fact]
    public async Task MustChangeUser_CanSetNewPassword_WithoutCurrentPassword()
    {
        await SeedUserAsync("mustchange@x.com", "TempPass123!", "Viewer", mustChange: true);
        var client = await LoginAsync("mustchange@x.com", "TempPass123!", "/change-password");

        // /change-password requires auth (no AllowAnonymous) -- the client just authenticated,
        // so this GET must succeed and render the (no-current-password) form.
        var token = await GetAntiforgeryTokenAsync(client, "/change-password");
        var resp = await client.PostAsync("/auth/change-password",
            Form(token, ("newPassword", "BrandNewPass1!"), ("confirmPassword", "BrandNewPass1!")));

        Assert.Equal(HttpStatusCode.Redirect, resp.StatusCode);
        Assert.Equal("/", resp.Headers.Location!.ToString());

        var user = await GetUserAsync("mustchange@x.com");
        Assert.False(user.MustChangePassword);
        using var scope = _factory.Services.CreateScope();
        var passwords = scope.ServiceProvider.GetRequiredService<IPasswordService>();
        Assert.True(passwords.Verify(user.PasswordHash!, "BrandNewPass1!"));
        Assert.False(passwords.Verify(user.PasswordHash!, "TempPass123!"));
    }

    [Fact]
    public async Task NormalUser_WrongCurrentPassword_Errors_NoChange()
    {
        await SeedUserAsync("normal@x.com", "OldPass123!", "Viewer", mustChange: false);
        var client = await LoginAsync("normal@x.com", "OldPass123!", "/");

        var token = await GetAntiforgeryTokenAsync(client, "/change-password");
        var resp = await client.PostAsync("/auth/change-password",
            Form(token, ("currentPassword", "WrongPassword!"), ("newPassword", "BrandNewPass1!"), ("confirmPassword", "BrandNewPass1!")));

        Assert.Equal(HttpStatusCode.Redirect, resp.StatusCode);
        Assert.Contains("/change-password?error=1", resp.Headers.Location!.ToString());

        var user = await GetUserAsync("normal@x.com");
        using var scope = _factory.Services.CreateScope();
        var passwords = scope.ServiceProvider.GetRequiredService<IPasswordService>();
        Assert.True(passwords.Verify(user.PasswordHash!, "OldPass123!")); // unchanged
    }

    [Fact]
    public async Task NormalUser_CorrectCurrentPassword_ChangesPassword()
    {
        await SeedUserAsync("normal2@x.com", "OldPass123!", "Viewer", mustChange: false);
        var client = await LoginAsync("normal2@x.com", "OldPass123!", "/");

        var token = await GetAntiforgeryTokenAsync(client, "/change-password");
        var resp = await client.PostAsync("/auth/change-password",
            Form(token, ("currentPassword", "OldPass123!"), ("newPassword", "BrandNewPass1!"), ("confirmPassword", "BrandNewPass1!")));

        Assert.Equal(HttpStatusCode.Redirect, resp.StatusCode);
        Assert.Equal("/", resp.Headers.Location!.ToString());

        var user = await GetUserAsync("normal2@x.com");
        Assert.False(user.MustChangePassword);
        using var scope = _factory.Services.CreateScope();
        var passwords = scope.ServiceProvider.GetRequiredService<IPasswordService>();
        Assert.True(passwords.Verify(user.PasswordHash!, "BrandNewPass1!"));
    }
}
