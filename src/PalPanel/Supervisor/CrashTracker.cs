namespace PalPanel.Supervisor;

public class CrashTracker(int maxCrashes, TimeSpan window)
{
    private readonly List<DateTimeOffset> _crashes = [];

    public bool RecordCrashAndCheckHold(DateTimeOffset now)
    {
        _crashes.Add(now);
        _crashes.RemoveAll(c => now - c > window);
        return _crashes.Count >= maxCrashes;
    }

    public void Reset() => _crashes.Clear();
}
