using PalPanel.PalApi;
using PalPanel.Supervisor;

namespace PalPanel.Monitoring;

public record ServerSnapshot(
    ServerState State, bool ApiReachable, ServerInfo? Info,
    IReadOnlyList<PlayerInfo> Players, ServerMetrics? Metrics,
    long MemoryBytes, DateTimeOffset? RunningSince, DateTimeOffset TakenAt)
{
    // Server-process CPU %, 0-100 across all cores. Non-positional so existing constructions
    // keep working; the poller sets it via an object initializer.
    public double CpuPercent { get; init; }
}
