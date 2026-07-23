namespace PalPanel.PalApi;

public record ServerInfo(string Version, string ServerName);
public record PlayerInfo(string Name, string PlayerId, string UserId, int Level, int Ping);
public record ServerMetrics(double ServerFps, int CurrentPlayerNum, double ServerFrameTime, int MaxPlayerNum, int Uptime);

public interface IPalApi
{
    Task<ServerInfo?> GetInfoAsync(CancellationToken ct);
    Task<IReadOnlyList<PlayerInfo>> GetPlayersAsync(CancellationToken ct);
    Task<ServerMetrics?> GetMetricsAsync(CancellationToken ct);
    Task AnnounceAsync(string message, CancellationToken ct);
    Task KickAsync(string userId, string message, CancellationToken ct);
    Task BanAsync(string userId, string message, CancellationToken ct);
    Task UnbanAsync(string userId, CancellationToken ct);
    Task SaveAsync(CancellationToken ct);
    Task ShutdownAsync(int waitSeconds, string message, CancellationToken ct);
}
