using Microsoft.EntityFrameworkCore;
using PalPanel.Components;
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
builder.Services.AddRazorComponents().AddInteractiveServerComponents();

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    scope.ServiceProvider.GetRequiredService<Microsoft.EntityFrameworkCore.IDbContextFactory<PalPanel.Data.PanelDb>>().CreateDbContext().Database.EnsureCreated();
}
app.UseStaticFiles();
app.UseAntiforgery();
app.MapRazorComponents<App>().AddInteractiveServerRenderMode();
app.Run();

public partial class Program { } // for WebApplicationFactory
