using PalPanel.Supervisor;

namespace PalPanel.Monitoring;

public class SnapshotService
{
    private readonly object _lock = new();
    private ServerSnapshot _current = new(ServerState.Stopped, false, null, [], null, 0, null, DateTimeOffset.UtcNow);

    public ServerSnapshot Current { get { lock (_lock) return _current; } }

    public event Action<ServerSnapshot>? Changed;

    public void Publish(ServerSnapshot s)
    {
        lock (_lock) _current = s;
        Changed?.Invoke(s);
    }
}
