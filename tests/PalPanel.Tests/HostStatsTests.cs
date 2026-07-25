using PalPanel.Control;

public class HostStatsTests
{
    [Fact]
    public void CpuPercent_HalfIdle_IsFifty()
    {
        // kernel bucket includes idle: kernel delta 100 (idle 50) + user delta 0 => busy 50/100.
        Assert.Equal(50.0, HostStats.CpuPercentFromTimes(0, 0, 0, 50, 100, 0), 3);
    }

    [Fact]
    public void CpuPercent_FullyIdle_IsZero()
    {
        Assert.Equal(0.0, HostStats.CpuPercentFromTimes(0, 0, 0, 100, 100, 0), 3);
    }

    [Fact]
    public void CpuPercent_FullyBusy_IsHundred()
    {
        // No idle progress, all delta in user time.
        Assert.Equal(100.0, HostStats.CpuPercentFromTimes(0, 0, 0, 0, 0, 100), 3);
    }

    [Fact]
    public void CpuPercent_NoElapsedTime_IsZero()
    {
        Assert.Equal(0.0, HostStats.CpuPercentFromTimes(5, 5, 5, 5, 5, 5), 3);
    }

    [Fact]
    public void FormatBytes_Scales()
    {
        Assert.Equal("512 B", HostStats.FormatBytes(512L));
        Assert.Equal("1.0 KB", HostStats.FormatBytes(1024L));
        Assert.Equal("1.0 MB", HostStats.FormatBytes(1024L * 1024));
        Assert.Equal("2.0 GB", HostStats.FormatBytes(2L * 1024 * 1024 * 1024));
    }

    [Fact]
    public void DirectorySize_SumsFiles_AndMissingIsZero()
    {
        var dir = Path.Combine(Path.GetTempPath(), "palpanel-hs-" + Guid.NewGuid());
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllBytes(Path.Combine(dir, "a.bin"), new byte[1000]);
            var sub = Path.Combine(dir, "sub");
            Directory.CreateDirectory(sub);
            File.WriteAllBytes(Path.Combine(sub, "b.bin"), new byte[500]);
            Assert.Equal(1500, HostStats.DirectorySize(dir));
        }
        finally { Directory.Delete(dir, true); }

        Assert.Equal(0, HostStats.DirectorySize(Path.Combine(Path.GetTempPath(), "palpanel-none-" + Guid.NewGuid())));
    }
}
