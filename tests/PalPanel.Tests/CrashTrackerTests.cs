using PalPanel.Supervisor;

public class CrashTrackerTests
{
    static readonly DateTimeOffset T0 = DateTimeOffset.Parse("2026-01-01T00:00:00Z");

    [Fact]
    public void UnderThreshold_NoHold()
    {
        var t = new CrashTracker(3, TimeSpan.FromMinutes(10));
        Assert.False(t.RecordCrashAndCheckHold(T0));
        Assert.False(t.RecordCrashAndCheckHold(T0.AddMinutes(1)));
    }

    [Fact]
    public void ThirdCrashInWindow_Holds()
    {
        var t = new CrashTracker(3, TimeSpan.FromMinutes(10));
        t.RecordCrashAndCheckHold(T0);
        t.RecordCrashAndCheckHold(T0.AddMinutes(2));
        Assert.True(t.RecordCrashAndCheckHold(T0.AddMinutes(4)));
    }

    [Fact]
    public void OldCrashesExpire()
    {
        var t = new CrashTracker(3, TimeSpan.FromMinutes(10));
        t.RecordCrashAndCheckHold(T0);
        t.RecordCrashAndCheckHold(T0.AddMinutes(1));
        Assert.False(t.RecordCrashAndCheckHold(T0.AddMinutes(15))); // first two aged out
    }

    [Fact]
    public void Reset_ClearsHistory()
    {
        var t = new CrashTracker(2, TimeSpan.FromMinutes(10));
        t.RecordCrashAndCheckHold(T0);
        t.Reset();
        Assert.False(t.RecordCrashAndCheckHold(T0.AddSeconds(1)));
    }
}
