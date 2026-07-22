using System.Diagnostics;
namespace PalPanel.Supervisor;

public class RealProcessLauncher : IProcessLauncher
{
    public IServerProcess? FindExisting(string processName)
    {
        var p = Process.GetProcessesByName(processName).FirstOrDefault();
        return p is null ? null : new RealServerProcess(p);
    }

    public IServerProcess Launch(string exePath, string args, string workingDir)
    {
        var p = Process.Start(new ProcessStartInfo(exePath, args)
        { WorkingDirectory = workingDir, UseShellExecute = false })!;
        return new RealServerProcess(p);
    }

    private sealed class RealServerProcess(Process p) : IServerProcess
    {
        public int Pid => p.Id;
        public bool HasExited => p.HasExited;
        public long WorkingSetBytes { get { try { p.Refresh(); return p.WorkingSet64; } catch { return 0; } } }
        public Task WaitForExitAsync(CancellationToken ct) => p.WaitForExitAsync(ct);
        public void Kill() { try { p.Kill(entireProcessTree: true); } catch { } }
    }
}
