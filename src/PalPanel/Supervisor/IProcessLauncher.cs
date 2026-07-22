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
}
