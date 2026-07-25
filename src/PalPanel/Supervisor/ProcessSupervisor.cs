using Microsoft.Extensions.Options;
namespace PalPanel.Supervisor;

public class ProcessSupervisor(IProcessLauncher launcher, IOptions<PanelOptions> opts)
{
    private readonly PanelOptions _o = opts.Value;
    private readonly object _lock = new();
    private IServerProcess? _proc;
    private CancellationTokenSource? _watchCts;
    private bool _stopRequested;
    private int _epoch;               // lifecycle generation; bumped by Start/Stop to fence stale crash handlers
    private int _restartAttempt;
    private Task _pending = Task.CompletedTask;
    private CrashTracker _crashes = null!;

    public ServerState State { get; private set; } = ServerState.Stopped;
    public DateTimeOffset? RunningSince { get; private set; }
    public long CurrentMemoryBytes => _proc is { HasExited: false } p ? p.WorkingSetBytes : 0;

    // Palworld's PalServer.exe launcher (what this supervisor tracks) is a thin ~6 MB shim that
    // spawns the real server (PalServer-Win64-Shipping-Cmd) holding the world + RAM. Report the
    // real server's working set for metrics, falling back to the tracked process if not found.
    public long GameMemoryBytes(string gameProcessName)
    {
        if (State is ServerState.Stopped or ServerState.Held) return 0;
        if (!string.IsNullOrWhiteSpace(gameProcessName))
        {
            var ws = launcher.GetWorkingSetByName(gameProcessName);
            if (ws > 0) return ws;
        }
        return CurrentMemoryBytes;
    }

    // Cumulative CPU time of the real game server process(es); the poller diffs it across polls
    // to compute a CPU %. Zero when not running.
    public TimeSpan GameCpuTime(string gameProcessName)
    {
        if (State is ServerState.Stopped or ServerState.Held || string.IsNullOrWhiteSpace(gameProcessName))
            return TimeSpan.Zero;
        return launcher.GetCpuTimeByName(gameProcessName);
    }
    public event Action<ServerState>? StateChanged;
    public Func<string, string, Task>? OnEvent { get; set; }
    // injectable for tests; real delay is exponential backoff capped at 60 s
    public Func<int, Task> RestartDelay { get; set; } =
        attempt => Task.Delay(TimeSpan.FromSeconds(Math.Min(60, Math.Pow(2, attempt))));

    public Task WaitForIdleAsync() => _pending;

    private void SetState(ServerState s)
    { State = s; StateChanged?.Invoke(s); }

    public void AdoptExistingIfRunning()
    {
        var existing = launcher.FindExisting(_o.ServerProcessName);
        if (existing is null) return;
        lock (_lock)
        {
            if (State is not ServerState.Stopped and not ServerState.Held) return;
            _epoch++; _stopRequested = false;   // adoption starts a new lifecycle generation
            _proc = existing; RunningSince = DateTimeOffset.UtcNow;
            EnsureTracker(); SetState(ServerState.Running); Watch(existing);
        }
        OnEvent?.Invoke("adopt", $"Adopted running PalServer pid {existing.Pid}");
    }

    public async Task StartAsync(CancellationToken ct)
    {
        int epoch;
        lock (_lock)
        {
            if (State is ServerState.Starting or ServerState.Running or ServerState.Stopping) return;
            _epoch++; _stopRequested = false; EnsureTracker(); _crashes.Reset(); _restartAttempt = 0;
            epoch = _epoch;
            SetState(ServerState.Starting);
        }
        await LaunchAndWatchOrHoldAsync(epoch);
    }

    public void MarkRunning()
    {
        if (State != ServerState.Starting) return;
        RunningSince = DateTimeOffset.UtcNow; _restartAttempt = 0;
        SetState(ServerState.Running);
    }

    public async Task StopAsync(Func<Task> gracefulShutdown, CancellationToken ct)
    {
        IServerProcess? p;
        lock (_lock)
        {
            if (State is ServerState.Stopped or ServerState.Held) { SetState(ServerState.Stopped); return; }
            _epoch++; _stopRequested = true; p = _proc; SetState(ServerState.Stopping);
        }
        _watchCts?.Cancel();
        try { await gracefulShutdown(); } catch (Exception ex) { await Fire("stop-error", ex.Message); }
        if (p is not null)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(_o.GracefulStopTimeoutSeconds));
            try { await p.WaitForExitAsync(timeout.Token); }
            catch (OperationCanceledException)
            { await Fire("force-kill", "Graceful stop timed out; killing process"); p.Kill(); }
        }
        _proc = null; RunningSince = null; SetState(ServerState.Stopped);
    }

    private void EnsureTracker() =>
        _crashes ??= new CrashTracker(_o.MaxCrashesInWindow, TimeSpan.FromMinutes(_o.CrashWindowMinutes));

    // Loud launch: a failed Launch (bad exe path, access denied, ...) must never
    // leave the supervisor stuck in Starting with nothing watching. It fires
    // "launch-failed" and lands in Held — manual intervention required.
    // Process.Start (potentially blocking) runs OUTSIDE _lock; only the cheap
    // bookkeeping (_proc assignment, watcher start, state flip) is under it.
    // The install is epoch-fenced: if a Stop (or any lifecycle change) won the
    // race while Launch was in flight, the freshly launched process would be a
    // live orphan under state Stopped — kill it loudly instead of installing it.
    private async Task LaunchAndWatchOrHoldAsync(int epoch)
    {
        Exception? err = null;
        IServerProcess? orphan = null;
        try
        {
            var exeDir = Path.GetDirectoryName(_o.ServerExePath) ?? ".";
            var proc = launcher.Launch(_o.ServerExePath, _o.ServerArgs, exeDir);
            lock (_lock)
            {
                if (_stopRequested || epoch != _epoch) orphan = proc;
                else { _proc = proc; Watch(proc); }
            }
        }
        catch (Exception ex)
        {
            err = ex;
            lock (_lock)
            {
                // Only land in Held if this launch still belongs to the current
                // lifecycle; never clobber a Stop that won the race.
                if (!_stopRequested && epoch == _epoch) SetState(ServerState.Held);
            }
        }
        if (orphan is not null)
        {
            orphan.Kill();
            await Fire("launch-aborted", $"stop won the race; killed pid {orphan.Pid}");
            return;
        }
        if (err is not null) await Fire("launch-failed", err.Message);
    }

    // Note: deliberately NOT wrapped in Task.Run (which would hop to a threadpool
    // thread and race with WaitForIdleAsync reading `_pending` from the caller's
    // thread), and deliberately does NOT assign `_pending` itself — see
    // NotifyProcessExitedAsync, which owns `_pending` for exactly the duration of
    // one crash-handling cycle so a relaunch's own perpetual watch never leaks
    // into what WaitForIdleAsync awaits.
    // Must be called under _lock: it captures _epoch at watcher creation, binding
    // the watcher (and any crash handling it triggers) to the lifecycle generation
    // that launched/adopted this process.
    private void Watch(IServerProcess p)
    {
        _watchCts = new CancellationTokenSource();
        var ct = _watchCts.Token;
        _ = RunWatchLoop(p, ct, _epoch);
    }

    private async Task RunWatchLoop(IServerProcess p, CancellationToken ct, int epoch)
    {
        try { await p.WaitForExitAsync(ct); } catch (OperationCanceledException) { return; }
        if (!_stopRequested) await NotifyProcessExited(epoch);
    }

    public Task NotifyProcessExitedAsync()
    {
        // Public entry (tests/poller): bind to the current lifecycle generation.
        int epoch;
        lock (_lock) epoch = _epoch;
        return NotifyProcessExited(epoch);
    }

    private Task NotifyProcessExited(int epoch)
    {
        // Own `_pending` for the full crash-handling cycle: assigned synchronously
        // here (before any await, before any relaunch's Watch() call can run), and
        // resolved only once this cycle (including any relaunch) is fully done.
        // WaitForIdleAsync() then always awaits "this crash's handling", never the
        // new process's own (potentially unbounded) exit-watch.
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending = tcs.Task;
        _ = RunCrashHandling(tcs, epoch);
        return tcs.Task;
    }

    private async Task RunCrashHandling(TaskCompletionSource tcs, int epoch)
    {
        try
        {
            EnsureTracker();
            await Fire("crash", "PalServer exited unexpectedly");
            bool held;
            lock (_lock)
            {
                // The OnEvent sink above may be a slow async write; if a Stop or
                // Start completed inside that await, this handler belongs to a
                // dead lifecycle — it must not record a phantom crash into the
                // new tracker or clobber the new lifecycle's state.
                if (epoch != _epoch) return;
                held = _crashes.RecordCrashAndCheckHold(DateTimeOffset.UtcNow);
                if (held) { RunningSince = null; SetState(ServerState.Held); }
                else SetState(ServerState.Crashed);
            }
            if (held)
            {
                await Fire("held", $"{_o.MaxCrashesInWindow} crashes in {_o.CrashWindowMinutes} min — auto-restart held");
                return;
            }
            await RestartDelay(_restartAttempt++);
            lock (_lock)
            {
                // A stop that arrived during the backoff is terminal (StopAsync
                // owns the transition to Stopped), and any Start/Stop since bumped
                // _epoch — in either case this handler is stale and must not
                // relaunch behind the new lifecycle.
                if (_stopRequested || epoch != _epoch) return;
                SetState(ServerState.Starting);
            }
            await LaunchAndWatchOrHoldAsync(epoch);
        }
        finally { tcs.TrySetResult(); }
    }

    private Task Fire(string type, string detail) => OnEvent?.Invoke(type, detail) ?? Task.CompletedTask;
}
