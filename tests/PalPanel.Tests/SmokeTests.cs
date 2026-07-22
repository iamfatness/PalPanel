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
}
