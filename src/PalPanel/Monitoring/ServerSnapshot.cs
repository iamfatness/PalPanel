using PalPanel.PalApi;
using PalPanel.Supervisor;

namespace PalPanel.Monitoring;

public record ServerSnapshot(
    ServerState State, bool ApiReachable, ServerInfo? Info,
    IReadOnlyList<PlayerInfo> Players, ServerMetrics? Metrics,
    long MemoryBytes, DateTimeOffset? RunningSince, DateTimeOffset TakenAt);
