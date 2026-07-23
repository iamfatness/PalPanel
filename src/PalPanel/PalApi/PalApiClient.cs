using Microsoft.Extensions.Options;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;

namespace PalPanel.PalApi;

public class PalApiClient : IPalApi
{
    private readonly HttpClient _http;

    public PalApiClient(HttpClient http, IOptions<PanelOptions> opts)
    {
        _http = http;
        _http.BaseAddress = new Uri(opts.Value.ApiBaseUrl.TrimEnd('/') + "/v1/api/");
        _http.Timeout = TimeSpan.FromSeconds(5);
        var cred = Convert.ToBase64String(Encoding.UTF8.GetBytes($"admin:{opts.Value.AdminPassword}"));
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", cred);
    }

    private async Task<T?> GetOrDefault<T>(string path, CancellationToken ct) where T : class
    {
        try
        {
            var resp = await _http.GetAsync(path, ct);
            if (!resp.IsSuccessStatusCode) return null;
            return await resp.Content.ReadFromJsonAsync<T>(cancellationToken: ct);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException) { return null; }
    }

    public async Task<ServerInfo?> GetInfoAsync(CancellationToken ct)
    { var d = await GetOrDefault<InfoDto>("info", ct); return d is null ? null : new(d.Version, d.ServerName); }

    public async Task<IReadOnlyList<PlayerInfo>> GetPlayersAsync(CancellationToken ct)
    {
        var d = await GetOrDefault<PlayersDto>("players", ct);
        return d?.Players.Select(p => new PlayerInfo(p.Name, p.PlayerId, p.UserId, p.Level, (int)Math.Round(p.Ping)))
                 .ToList() ?? (IReadOnlyList<PlayerInfo>)[];
    }

    public async Task<ServerMetrics?> GetMetricsAsync(CancellationToken ct)
    {
        var d = await GetOrDefault<MetricsDto>("metrics", ct);
        return d is null ? null : new(d.ServerFps, d.CurrentPlayerNum, d.ServerFrameTime, d.MaxPlayerNum, d.Uptime);
    }

    private Task Post(string path, object? body, CancellationToken ct) =>
        _http.PostAsync(path, body is null ? null : JsonContent.Create(body), ct);

    public Task AnnounceAsync(string message, CancellationToken ct) => Post("announce", new { message }, ct);
    public Task KickAsync(string userId, string message, CancellationToken ct) => Post("kick", new { userid = userId, message }, ct);
    public Task BanAsync(string userId, string message, CancellationToken ct) => Post("ban", new { userid = userId, message }, ct);
    public Task UnbanAsync(string userId, CancellationToken ct) => Post("unban", new { userid = userId }, ct);
    public Task SaveAsync(CancellationToken ct) => Post("save", null, ct);
    public Task ShutdownAsync(int waitSeconds, string message, CancellationToken ct) => Post("shutdown", new { waittime = waitSeconds, message }, ct);
}
