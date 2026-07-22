using Microsoft.AspNetCore.Mvc.Testing;

namespace PalPanel.Tests;

public class SmokeTests(WebApplicationFactory<Program> factory) : IClassFixture<WebApplicationFactory<Program>>
{
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
        var client = factory.WithWebHostBuilder(b =>
            b.UseSetting("Panel:AuthDisabled", "true")
             .UseSetting("Panel:DbPath", Path.GetTempFileName())).CreateClient();
        var resp = await client.GetAsync("/players");
        Assert.Equal(System.Net.HttpStatusCode.OK, resp.StatusCode);
        var html = await resp.Content.ReadAsStringAsync();
        Assert.Contains("Players", html);
    }

    [Fact]
    public async Task History_RendersWithAuthDisabled()
    {
        var client = factory.WithWebHostBuilder(b =>
            b.UseSetting("Panel:AuthDisabled", "true")
             .UseSetting("Panel:DbPath", Path.GetTempFileName())).CreateClient();
        var resp = await client.GetAsync("/history");
        Assert.Equal(System.Net.HttpStatusCode.OK, resp.StatusCode);
        var html = await resp.Content.ReadAsStringAsync();
        Assert.Contains("History", html);
    }

    [Fact]
    public async Task Backups_RendersWithAuthDisabled()
    {
        var client = factory.WithWebHostBuilder(b =>
            b.UseSetting("Panel:AuthDisabled", "true")
             .UseSetting("Panel:DbPath", Path.GetTempFileName())
             .UseSetting("Panel:BackupDirectory", Directory.CreateTempSubdirectory().FullName)).CreateClient();
        var resp = await client.GetAsync("/backups");
        Assert.Equal(System.Net.HttpStatusCode.OK, resp.StatusCode);
        var html = await resp.Content.ReadAsStringAsync();
        Assert.Contains("Backups", html);
    }

    [Fact]
    public async Task Settings_RendersWithAuthDisabled()
    {
        var client = factory.WithWebHostBuilder(b =>
            b.UseSetting("Panel:AuthDisabled", "true")
             .UseSetting("Panel:DbPath", Path.GetTempFileName())).CreateClient();
        var resp = await client.GetAsync("/settings");
        Assert.Equal(System.Net.HttpStatusCode.OK, resp.StatusCode);
        var html = await resp.Content.ReadAsStringAsync();
        Assert.Contains("Schedules", html);
    }
}
