namespace PalPanel.Supervisor;

public interface IServerProcess
{
    int Pid { get; }
    bool HasExited { get; }
    long WorkingSetBytes { get; }
    Task WaitForExitAsync(CancellationToken ct);
    void Kill();
}

public interface IProcessLauncher
{
    IServerProcess? FindExisting(string processName);
    IServerProcess Launch(string exePath, string args, string workingDir);
    // Total working set of all processes with this name (0 if none). Used to report the real
    // game server's memory when the supervisor tracks a thin launcher process instead.
    long GetWorkingSetByName(string processName);
    // Total CPU time consumed by all processes with this name. The poller diffs this across
    // polls to derive a CPU %. Zero if none found.
    TimeSpan GetCpuTimeByName(string processName);
}
