using Microsoft.AspNetCore.Components.Authorization;
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
builder.Services.AddHttpContextAccessor();
builder.Services.AddCascadingAuthenticationState();
builder.Services.AddAuthorization();
builder.Services.AddScoped<AuthenticationStateProvider, HttpContextAuthStateProvider>();
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

app.UseStaticFiles();
app.UseMiddleware<AccessJwtMiddleware>();
app.UseAntiforgery();
app.MapGet("/healthz", () => "ok");

// Admin-only backup download. Mapped after AccessJwtMiddleware so ctx.Items["PanelPrincipal"]
// is always populated (or the middleware has already 401'd). The physical path is built ONLY
// from the matched BackupInfo.FileName (never the raw route-value `file`) — List() only ever
// enumerates *.zip actually present in BackupDirectory, so this can't be steered off that
// directory or onto an arbitrary name, unlike Path.Combine(dir, file) with the raw input.
app.MapGet("/backups/download/{file}", (string file, PalPanel.Control.IBackupService backups,
    HttpContext ctx, IOptions<PalPanel.PanelOptions> opts) =>
{
    // Results.Forbid() calls HttpContext.ForbidAsync(), which requires a registered
    // IAuthenticationService (AddAuthentication()) — this app has none, since
    // AccessJwtMiddleware validates the Cf-Access-Jwt-Assertion header itself rather than
    // going through the ASP.NET Core authentication handler pipeline. A plain 403 status
    // code needs no such service and matches how AccessJwtMiddleware itself signals 401/403.
    if (ctx.Items["PanelPrincipal"] is not PalPanel.Auth.PanelPrincipal p || p.Role != "Admin")
        return Results.StatusCode(StatusCodes.Status403Forbidden);
    var info = backups.List().FirstOrDefault(b => b.FileName == file);
    if (info is null) return Results.NotFound();
    var path = Path.Combine(opts.Value.BackupDirectory, info.FileName);
    return Results.File(path, "application/zip", info.FileName);
});

app.MapRazorComponents<App>().AddInteractiveServerRenderMode();
app.Run();

public partial class Program { } // for WebApplicationFactory
