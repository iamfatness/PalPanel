using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace PalPanel.PalApi;

// Per-server connection settings for the Palworld REST API. Previously sourced from the
// single-server PanelOptions; now supplied per ServerRuntime so each managed server talks to
// its own endpoint with its own admin password.
public record PalApiSettings(string BaseUrl, string AdminPassword);

public class PalApiClient : IPalApi
{
    private readonly HttpClient _http;

    public PalApiClient(HttpClient http, PalApiSettings settings)
    {
        _http = http;
        _http.BaseAddress = new Uri(settings.BaseUrl.TrimEnd('/') + "/v1/api/");
        // Palworld's REST API runs on the game thread and periodically stalls (world saves, load
        // spikes). A tight timeout turns those transient stalls into false "API unreachable"
        // flapping, so allow generous headroom; the poller adds hysteresis on top.
        _http.Timeout = TimeSpan.FromSeconds(12);
        var cred = Convert.ToBase64String(Encoding.UTF8.GetBytes($"admin:{settings.AdminPassword}"));
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

    private async Task Post(string path, object? body, CancellationToken ct)
    {
        // Palworld's minimal REST server REQUIRES a Content-Length and rejects chunked transfer
        // encoding with 411. JsonContent streams chunked, silently breaking every POST (announce,
        // save, kick, ...). Serialize to a buffered StringContent so the request carries a known
        // Content-Length (0 for bodyless actions).
        var json = body is null ? "" : JsonSerializer.Serialize(body);
        using var content = new StringContent(json, Encoding.UTF8, "application/json");
        var resp = await _http.PostAsync(path, content, ct);
        // Verify the server accepted the action so callers can honestly confirm success instead of
        // fire-and-forget. All callers already surface or log exceptions.
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"The server rejected '{path}' ({(int)resp.StatusCode} {resp.ReasonPhrase}).");
    }

    public Task AnnounceAsync(string message, CancellationToken ct) => Post("announce", new { message }, ct);
    public Task KickAsync(string userId, string message, CancellationToken ct) => Post("kick", new { userid = userId, message }, ct);
    public Task BanAsync(string userId, string message, CancellationToken ct) => Post("ban", new { userid = userId, message }, ct);
    public Task UnbanAsync(string userId, CancellationToken ct) => Post("unban", new { userid = userId }, ct);
    public Task SaveAsync(CancellationToken ct) => Post("save", null, ct);
    public Task ShutdownAsync(int waitSeconds, string message, CancellationToken ct) => Post("shutdown", new { waittime = waitSeconds, message }, ct);
}
