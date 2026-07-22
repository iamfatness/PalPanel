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
        _proc = existing; RunningSince = DateTimeOffset.UtcNow;
        EnsureTracker(); SetState(ServerState.Running); Watch(existing);
        OnEvent?.Invoke("adopt", $"Adopted running PalServer pid {existing.Pid}");
    }

    public async Task StartAsync(CancellationToken ct)
    {
        lock (_lock)
        {
            if (State is ServerState.Starting or ServerState.Running or ServerState.Stopping) return;
            _epoch++; _stopRequested = false; EnsureTracker(); _crashes.Reset(); _restartAttempt = 0;
            SetState(ServerState.Starting);
        }
        await LaunchAndWatchOrHoldAsync();
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
    private async Task LaunchAndWatchOrHoldAsync()
    {
        Exception? err = null;
        try
        {
            var exeDir = Path.GetDirectoryName(_o.ServerExePath) ?? ".";
            var proc = launcher.Launch(_o.ServerExePath, _o.ServerArgs, exeDir);
            lock (_lock) { _proc = proc; Watch(proc); }
        }
        catch (Exception ex)
        {
            err = ex;
            lock (_lock) SetState(ServerState.Held);
        }
        if (err is not null) await Fire("launch-failed", err.Message);
    }

    // Note: deliberately NOT wrapped in Task.Run (which would hop to a threadpool
    // thread and race with WaitForIdleAsync reading `_pending` from the caller's
    // thread), and deliberately does NOT assign `_pending` itself — see
    // NotifyProcessExitedAsync, which owns `_pending` for exactly the duration of
    // one crash-handling cycle so a relaunch's own perpetual watch never leaks
    // into what WaitForIdleAsync awaits.
    private void Watch(IServerProcess p)
    {
        _watchCts = new CancellationTokenSource();
        var ct = _watchCts.Token;
        _ = RunWatchLoop(p, ct);
    }

    private async Task RunWatchLoop(IServerProcess p, CancellationToken ct)
    {
        try { await p.WaitForExitAsync(ct); } catch (OperationCanceledException) { return; }
        if (!_stopRequested) await NotifyProcessExitedAsync();
    }

    public Task NotifyProcessExitedAsync()
    {
        // Own `_pending` for the full crash-handling cycle: assigned synchronously
        // here (before any await, before any relaunch's Watch() call can run), and
        // resolved only once this cycle (including any relaunch) is fully done.
        // WaitForIdleAsync() then always awaits "this crash's handling", never the
        // new process's own (potentially unbounded) exit-watch.
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending = tcs.Task;
        _ = RunCrashHandling(tcs);
        return tcs.Task;
    }

    private async Task RunCrashHandling(TaskCompletionSource tcs)
    {
        try
        {
            EnsureTracker();
            await Fire("crash", "PalServer exited unexpectedly");
            bool held; int epoch;
            lock (_lock)
            {
                epoch = _epoch; // lifecycle generation this handler belongs to
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
                // owns the transition to Stopped), and any Start/Stop since our
                // crash decision bumped _epoch — in either case this handler is
                // stale and must not relaunch behind the new lifecycle.
                if (_stopRequested || epoch != _epoch) return;
                SetState(ServerState.Starting);
            }
            await LaunchAndWatchOrHoldAsync();
        }
        finally { tcs.TrySetResult(); }
    }

    private Task Fire(string type, string detail) => OnEvent?.Invoke(type, detail) ?? Task.CompletedTask;
}
