using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;

public sealed class StubPalServer : IAsyncDisposable
{
    private readonly WebApplication _app;
    public string BaseUrl { get; }
    public List<(string Path, string Body)> Posts { get; } = [];
    public bool Healthy { get; set; } = true;
    public int PlayerCount { get; set; } = 0;
    public List<string> PlayerNames { get; set; } = [];

    public StubPalServer()
    {
        var b = WebApplication.CreateBuilder();
        // Find a random available port
        using var socket = new System.Net.Sockets.Socket(System.Net.Sockets.AddressFamily.InterNetwork, System.Net.Sockets.SocketType.Stream, System.Net.Sockets.ProtocolType.Tcp);
        socket.Bind(new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, 0));
        int port = ((System.Net.IPEndPoint)socket.LocalEndPoint!).Port;
        socket.Close();

        b.WebHost.UseUrls($"http://127.0.0.1:{port}");
        _app = b.Build();
        _app.Use(async (ctx, next) =>
        {
            if (!Healthy) { ctx.Response.StatusCode = 503; return; }
            var auth = ctx.Request.Headers.Authorization.ToString();
            if (!auth.StartsWith("Basic ")) { ctx.Response.StatusCode = 401; return; }
            await next();
        });
        _app.MapGet("/v1/api/info", () => Results.Json(new { version = "v0.4.11", servername = "FatShack" }));
        _app.MapGet("/v1/api/players", () => Results.Json(new
        {
            players = PlayerNames.Select((n, i) => new
            { name = n, playerId = $"pid{i}", userId = $"steam_{i}", level = 10 + i, ping = 30.0 }).ToArray()
        }));
        _app.MapGet("/v1/api/metrics", () => Results.Json(new
        { serverfps = 58, currentplayernum = PlayerNames.Count, serverframetime = 16.7, maxplayernum = 32, uptime = 3600 }));
        foreach (var ep in new[] { "announce", "kick", "ban", "unban", "save", "shutdown" })
            _app.MapPost($"/v1/api/{ep}", async ctx =>
            {
                using var r = new StreamReader(ctx.Request.Body);
                Posts.Add((ep, await r.ReadToEndAsync()));
                ctx.Response.StatusCode = 200;
            });
        _app.StartAsync().GetAwaiter().GetResult();
        BaseUrl = _app.Urls.First();
    }

    public async ValueTask DisposeAsync() => await _app.DisposeAsync();
}
