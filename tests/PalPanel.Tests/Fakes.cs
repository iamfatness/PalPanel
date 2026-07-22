using PalPanel.Supervisor;

public class FakeProcess : IServerProcess
{
    private readonly TaskCompletionSource _exit = new();
    public int Pid { get; } = Random.Shared.Next(1000, 9999);
    public bool HasExited { get; private set; }
    public long WorkingSetBytes => 123_000_000;
    public bool WasKilled { get; private set; }
    public Task WaitForExitAsync(CancellationToken ct) => _exit.Task.WaitAsync(ct);
    public void Kill() { WasKilled = true; SimulateExit(); }
    public void SimulateExit() { HasExited = true; _exit.TrySetResult(); }
}

public class FakeLauncher : IProcessLauncher
{
    public List<FakeProcess> Launched { get; } = [];
    public FakeProcess? Existing { get; set; }
    public Action<int>? OnLaunch { get; set; }   // fires with the new launch count; lets tests await the Nth launch
    public IServerProcess? FindExisting(string name) => Existing;
    public IServerProcess Launch(string exe, string args, string wd)
    { var p = new FakeProcess(); Launched.Add(p); OnLaunch?.Invoke(Launched.Count); return p; }
}

public class ThrowingLauncher : IProcessLauncher
{
    public IServerProcess? FindExisting(string name) => null;
    public IServerProcess Launch(string exe, string args, string wd)
        => throw new InvalidOperationException("exe not found");
}

public class FlakyLauncher : IProcessLauncher
{
    public List<FakeProcess> Launched { get; } = [];
    public IServerProcess? FindExisting(string name) => null;
    public IServerProcess Launch(string exe, string args, string wd)
    {
        if (Launched.Count >= 1) throw new InvalidOperationException("exe vanished before relaunch");
        var p = new FakeProcess(); Launched.Add(p); return p;
    }
}

public class BlockingLauncher : IProcessLauncher
{
    public readonly SemaphoreSlim Entered = new(0);
    public readonly SemaphoreSlim Proceed = new(0);
    public List<FakeProcess> Launched { get; } = [];
    public IServerProcess? FindExisting(string name) => null;
    public IServerProcess Launch(string exe, string args, string wd)
    {
        Entered.Release();       // signal: launch in progress
        Proceed.Wait();          // park (on the caller's worker thread) until the test releases it
        var p = new FakeProcess(); Launched.Add(p); return p;
    }
}
