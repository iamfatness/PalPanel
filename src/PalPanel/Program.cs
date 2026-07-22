using PalPanel.Components;

var builder = WebApplication.CreateBuilder(args);
builder.Host.UseWindowsService();
builder.WebHost.UseUrls("http://localhost:5080");
builder.Configuration.AddJsonFile("appsettings.Local.json", optional: true);

builder.Services.Configure<PalPanel.PanelOptions>(builder.Configuration.GetSection("Panel"));
builder.Services.AddRazorComponents().AddInteractiveServerComponents();

var app = builder.Build();
app.UseStaticFiles();
app.UseAntiforgery();
app.MapRazorComponents<App>().AddInteractiveServerRenderMode();
app.Run();

public partial class Program { } // for WebApplicationFactory
