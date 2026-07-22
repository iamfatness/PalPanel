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
    public Action<int>? OnLaunch { get; set; }   // fires with the new launch count; lets tests await the Nth launch
    public IServerProcess? FindExisting(string name) => Existing;
    public IServerProcess Launch(string exe, string args, string wd)
    { var p = new FakeProcess(); Launched.Add(p); OnLaunch?.Invoke(Launched.Count); return p; }
}

class ThrowingLauncher : IProcessLauncher
{
    public IServerProcess? FindExisting(string name) => null;
    public IServerProcess Launch(string exe, string args, string wd)
        => throw new InvalidOperationException("exe not found");
}

class FlakyLauncher : IProcessLauncher
{
    public List<FakeProcess> Launched { get; } = [];
    public IServerProcess? FindExisting(string name) => null;
    public IServerProcess Launch(string exe, string args, string wd)
    {
        if (Launched.Count >= 1) throw new InvalidOperationException("exe vanished before relaunch");
        var p = new FakeProcess(); Launched.Add(p); return p;
    }
}

class BlockingLauncher : IProcessLauncher
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

    [Fact]
    public async Task StopThenStartDuringBackoff_StaleHandlerDoesNotDoubleLaunch()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var l = new FakeLauncher();
        var o = new PanelOptions { MaxCrashesInWindow = 3, CrashWindowMinutes = 10, GracefulStopTimeoutSeconds = 1 };
        var s = new ProcessSupervisor(l, Options.Create(o)) { RestartDelay = _ => gate.Task };
        await s.StartAsync(CancellationToken.None);
        s.MarkRunning();
        var crashed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        s.StateChanged += st => { if (st == ServerState.Crashed) crashed.TrySetResult(); };
        l.Launched[0].SimulateExit();                      // process A crashes
        await crashed.Task;                                // handler parked in gated backoff
        await s.StopAsync(gracefulShutdown: () => Task.CompletedTask, CancellationToken.None);
        await s.StartAsync(CancellationToken.None);        // operator restarts: process B
        Assert.Equal(2, l.Launched.Count);
        Assert.Equal(ServerState.Starting, s.State);
        gate.TrySetResult();                               // stale handler for A's crash wakes up
        await s.WaitForIdleAsync();
        Assert.Equal(2, l.Launched.Count);                 // no phantom process C
        Assert.Equal(ServerState.Starting, s.State);       // B's lifecycle untouched
        s.MarkRunning();
        Assert.Equal(ServerState.Running, s.State);
    }

    [Fact]
    public async Task RelaunchThrows_FiresEventAndHolds()
    {
        var events = new List<(string Type, string Detail)>();
        var l = new FlakyLauncher();                       // first Launch succeeds, second throws
        var o = new PanelOptions { MaxCrashesInWindow = 3, CrashWindowMinutes = 10, GracefulStopTimeoutSeconds = 1 };
        var s = new ProcessSupervisor(l, Options.Create(o))
        {
            RestartDelay = _ => Task.CompletedTask,
            OnEvent = (type, detail) => { events.Add((type, detail)); return Task.CompletedTask; }
        };
        await s.StartAsync(CancellationToken.None);
        s.MarkRunning();
        l.Launched[0].SimulateExit();                      // crash -> backoff -> relaunch throws
        await s.WaitForIdleAsync();
        Assert.Contains(events, e => e.Type == "launch-failed");
        Assert.Equal(ServerState.Held, s.State);
    }

    [Fact]
    public async Task StopStartDuringCrashEventSink_StaleHandlerDoesNotTouchNewLifecycle()
    {
        var crashGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstCrash = true;
        var l = new FakeLauncher();
        var o = new PanelOptions { MaxCrashesInWindow = 3, CrashWindowMinutes = 10, GracefulStopTimeoutSeconds = 1 };
        var s = new ProcessSupervisor(l, Options.Create(o))
        {
            RestartDelay = _ => Task.CompletedTask,
            OnEvent = (type, detail) =>
            {
                if (type == "crash" && firstCrash) { firstCrash = false; return crashGate.Task; }
                return Task.CompletedTask;
            }
        };
        await s.StartAsync(CancellationToken.None);
        s.MarkRunning();
        l.Launched[0].SimulateExit();                      // handler parks awaiting the crash event sink
        await s.StopAsync(gracefulShutdown: () => Task.CompletedTask, CancellationToken.None);
        Assert.Equal(ServerState.Stopped, s.State);
        await s.StartAsync(CancellationToken.None);        // new lifecycle: process B
        Assert.Equal(2, l.Launched.Count);
        Assert.Equal(ServerState.Starting, s.State);
        var staleCycle = s.WaitForIdleAsync();             // A's crash cycle, captured before any new crash reuses _pending
        crashGate.TrySetResult();                          // stale handler resumes inside Fire("crash")
        await staleCycle;
        Assert.Equal(2, l.Launched.Count);                 // no phantom relaunch
        Assert.Equal(ServerState.Starting, s.State);       // B's Starting not clobbered by Crashed
        s.MarkRunning();
        Assert.Equal(ServerState.Running, s.State);
        // prove the phantom crash was NOT recorded against B's fresh tracker:
        // two real crashes must NOT trip the 3-crash hold (phantom would make them #2 and #3).
        // Await the relaunch via an explicit launch signal — no assumptions about
        // where the exit continuation is scheduled.
        var launched3 = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var launched4 = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        l.OnLaunch = n => { if (n == 3) launched3.TrySetResult(); if (n == 4) launched4.TrySetResult(); };
        l.Launched[1].SimulateExit();                      // real crash #1 -> relaunch C
        await launched3.Task;
        await s.WaitForIdleAsync();                        // crash cycle fully done (launch precedes cycle end)
        Assert.Equal(3, l.Launched.Count);
        Assert.Equal(ServerState.Starting, s.State);
        s.MarkRunning();
        l.Launched[2].SimulateExit();                      // real crash #2 -> relaunch D
        await launched4.Task;
        await s.WaitForIdleAsync();
        Assert.NotEqual(ServerState.Held, s.State);
        Assert.Equal(ServerState.Starting, s.State);
        Assert.Equal(4, l.Launched.Count);
    }

    [Fact]
    public async Task StopDuringLaunch_KillsOrphanAndStaysStopped()
    {
        var events = new List<(string Type, string Detail)>();
        var l = new BlockingLauncher();
        var o = new PanelOptions { MaxCrashesInWindow = 3, CrashWindowMinutes = 10, GracefulStopTimeoutSeconds = 1 };
        var s = new ProcessSupervisor(l, Options.Create(o))
        {
            RestartDelay = _ => Task.CompletedTask,
            OnEvent = (type, detail) => { lock (events) events.Add((type, detail)); return Task.CompletedTask; }
        };
        var startTask = Task.Run(() => s.StartAsync(CancellationToken.None));
        await l.Entered.WaitAsync();                       // Launch is parked on a worker thread
        await s.StopAsync(gracefulShutdown: () => Task.CompletedTask, CancellationToken.None);
        Assert.Equal(ServerState.Stopped, s.State);
        l.Proceed.Release();                               // Launch returns after stop already completed
        await startTask;
        Assert.True(l.Launched[0].WasKilled);              // orphan PalServer killed, not installed
        Assert.Equal(ServerState.Stopped, s.State);
        lock (events) Assert.Contains(events, e => e.Type == "launch-aborted");
    }
}
