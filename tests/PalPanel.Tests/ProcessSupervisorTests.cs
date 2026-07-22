using Microsoft.Extensions.Options;
using PalPanel;
using PalPanel.Supervisor;

class FakeProcess : IServerProcess
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

class FakeLauncher : IProcessLauncher
{
    public List<FakeProcess> Launched { get; } = [];
    public FakeProcess? Existing { get; set; }
    public IServerProcess? FindExisting(string name) => Existing;
    public IServerProcess Launch(string exe, string args, string wd)
    { var p = new FakeProcess(); Launched.Add(p); return p; }
}

class ThrowingLauncher : IProcessLauncher
{
    public IServerProcess? FindExisting(string name) => null;
    public IServerProcess Launch(string exe, string args, string wd)
        => throw new InvalidOperationException("exe not found");
}

public class ProcessSupervisorTests
{
    static ProcessSupervisor Make(FakeLauncher l, PanelOptions? o = null)
    {
        o ??= new PanelOptions { MaxCrashesInWindow = 3, CrashWindowMinutes = 10, GracefulStopTimeoutSeconds = 1 };
        return new ProcessSupervisor(l, Options.Create(o)) { RestartDelay = _ => Task.CompletedTask };
    }

    [Fact]
    public async Task Start_LaunchesAndEntersStarting()
    {
        var l = new FakeLauncher(); var s = Make(l);
        await s.StartAsync(CancellationToken.None);
        Assert.Equal(ServerState.Starting, s.State);
        Assert.Single(l.Launched);
        s.MarkRunning();
        Assert.Equal(ServerState.Running, s.State);
    }

    [Fact]
    public async Task UnexpectedExit_AutoRestarts()
    {
        var l = new FakeLauncher(); var s = Make(l);
        await s.StartAsync(CancellationToken.None);
        s.MarkRunning();
        l.Launched[0].SimulateExit();
        await s.WaitForIdleAsync();          // test helper: awaits pending crash handling
        Assert.Equal(2, l.Launched.Count);   // relaunched
        Assert.Equal(ServerState.Starting, s.State);
    }

    [Fact]
    public async Task ThreeCrashes_Holds()
    {
        var l = new FakeLauncher(); var s = Make(l);
        await s.StartAsync(CancellationToken.None);
        for (int i = 0; i < 3; i++)
        {
            s.MarkRunning();
            l.Launched[^1].SimulateExit();
            await s.WaitForIdleAsync();
        }
        Assert.Equal(ServerState.Held, s.State);
        Assert.Equal(3, l.Launched.Count); // no 4th launch
    }

    [Fact]
    public async Task Stop_GracefulThenExit_NotACrash()
    {
        var l = new FakeLauncher(); var s = Make(l);
        await s.StartAsync(CancellationToken.None);
        s.MarkRunning();
        var p = l.Launched[0];
        await s.StopAsync(gracefulShutdown: () => { p.SimulateExit(); return Task.CompletedTask; }, CancellationToken.None);
        Assert.Equal(ServerState.Stopped, s.State);
        Assert.False(p.WasKilled);
        Assert.Single(l.Launched); // no auto-restart
    }

    [Fact]
    public async Task Stop_TimeoutForceKills()
    {
        var l = new FakeLauncher(); var s = Make(l);
        await s.StartAsync(CancellationToken.None);
        s.MarkRunning();
        await s.StopAsync(gracefulShutdown: () => Task.CompletedTask /* never exits */, CancellationToken.None);
        Assert.True(l.Launched[0].WasKilled);
        Assert.Equal(ServerState.Stopped, s.State);
    }

    [Fact]
    public async Task Adopt_ExistingProcessBecomesRunning()
    {
        var l = new FakeLauncher { Existing = new FakeProcess() };
        var s = Make(l);
        s.AdoptExistingIfRunning();
        Assert.Equal(ServerState.Running, s.State);
    }

    [Fact]
    public async Task StopDuringCrashBackoff_DoesNotRelaunch()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var l = new FakeLauncher();
        var o = new PanelOptions { MaxCrashesInWindow = 3, CrashWindowMinutes = 10, GracefulStopTimeoutSeconds = 1 };
        var s = new ProcessSupervisor(l, Options.Create(o)) { RestartDelay = _ => gate.Task };
        await s.StartAsync(CancellationToken.None);
        s.MarkRunning();
        var p = l.Launched[0];
        var crashed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        s.StateChanged += st => { if (st == ServerState.Crashed) crashed.TrySetResult(); };
        p.SimulateExit();                                  // crash handling runs up to the gated backoff
        await crashed.Task;                                // deterministically parked in backoff
        Assert.Equal(ServerState.Crashed, s.State);
        await s.StopAsync(gracefulShutdown: () => Task.CompletedTask, CancellationToken.None);
        Assert.Equal(ServerState.Stopped, s.State);
        gate.TrySetResult();                               // backoff elapses after stop completed
        await s.WaitForIdleAsync();
        Assert.Single(l.Launched);                         // stop is terminal: no relaunch
        Assert.Equal(ServerState.Stopped, s.State);
    }

    [Fact]
    public async Task LaunchThrows_FiresEventAndHolds()
    {
        var events = new List<(string Type, string Detail)>();
        var o = new PanelOptions { MaxCrashesInWindow = 3, CrashWindowMinutes = 10, GracefulStopTimeoutSeconds = 1 };
        var s = new ProcessSupervisor(new ThrowingLauncher(), Options.Create(o))
        {
            RestartDelay = _ => Task.CompletedTask,
            OnEvent = (type, detail) => { events.Add((type, detail)); return Task.CompletedTask; }
        };
        await s.StartAsync(CancellationToken.None);
        Assert.Contains(events, e => e.Type == "launch-failed");
        Assert.Equal(ServerState.Held, s.State);
    }
}
