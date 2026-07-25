using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace PalPanel.Control;

// Host-level vitals for the box the panel runs on: whole-machine CPU%, physical RAM, fixed-disk
// free space, and on-disk size of each server's backup folder. Uses Win32 directly (the host is
// a Windows service) so there's no extra package dependency; every native call is guarded so a
// failure degrades to a null/empty reading rather than throwing into the UI.
public static class HostStats
{
    public record MemInfo(ulong TotalBytes, ulong AvailBytes)
    {
        public ulong UsedBytes => TotalBytes >= AvailBytes ? TotalBytes - AvailBytes : 0;
        public double UsedPercent => TotalBytes == 0 ? 0 : (double)UsedBytes / TotalBytes * 100.0;
    }

    public record DiskInfo(string Name, string Label, long TotalBytes, long FreeBytes)
    {
        public long UsedBytes => TotalBytes >= FreeBytes ? TotalBytes - FreeBytes : 0;
        public double UsedPercent => TotalBytes == 0 ? 0 : (double)UsedBytes / TotalBytes * 100.0;
    }

    // Whole-machine CPU% over `sampleMs`. GetSystemTimes' kernel bucket already includes idle,
    // so busy = (kernel + user) - idle over the same window.
    [SupportedOSPlatform("windows")]
    public static async Task<double> HostCpuPercentAsync(int sampleMs = 250, CancellationToken ct = default)
    {
        if (!OperatingSystem.IsWindows()) return 0;
        if (!GetSystemTimes(out var i1, out var k1, out var u1)) return 0;
        await Task.Delay(sampleMs, ct);
        if (!GetSystemTimes(out var i2, out var k2, out var u2)) return 0;
        return CpuPercentFromTimes(i1, k1, u1, i2, k2, u2);
    }

    // Pure so it's unit-testable without touching the OS clock. Times are 100ns FILETIME ticks.
    public static double CpuPercentFromTimes(long idle1, long kernel1, long user1, long idle2, long kernel2, long user2)
    {
        double idle = idle2 - idle1;
        double total = (kernel2 - kernel1) + (user2 - user1); // kernel already counts idle
        if (total <= 0) return 0;
        return Math.Clamp((total - idle) / total * 100.0, 0, 100);
    }

    [SupportedOSPlatform("windows")]
    public static MemInfo? HostMemory()
    {
        if (!OperatingSystem.IsWindows()) return null;
        var m = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
        return GlobalMemoryStatusEx(ref m) ? new MemInfo(m.ullTotalPhys, m.ullAvailPhys) : null;
    }

    public static IReadOnlyList<DiskInfo> Disks()
    {
        var list = new List<DiskInfo>();
        foreach (var d in DriveInfo.GetDrives())
        {
            try
            {
                if (d.DriveType != DriveType.Fixed || !d.IsReady) continue;
                list.Add(new DiskInfo(d.Name, string.IsNullOrWhiteSpace(d.VolumeLabel) ? "Local disk" : d.VolumeLabel,
                    d.TotalSize, d.AvailableFreeSpace));
            }
            catch { /* a drive that races to not-ready is simply skipped */ }
        }
        return list;
    }

    // Recursive on-disk size of a folder; missing/inaccessible paths and files degrade to 0.
    public static long DirectorySize(string path)
    {
        try
        {
            var di = new DirectoryInfo(path);
            if (!di.Exists) return 0;
            long sum = 0;
            foreach (var f in di.EnumerateFiles("*", SearchOption.AllDirectories))
            {
                try { sum += f.Length; } catch { /* file vanished / locked — skip */ }
            }
            return sum;
        }
        catch { return 0; }
    }

    public static string FormatBytes(long b) => FormatBytes(b < 0 ? 0 : (ulong)b);
    public static string FormatBytes(ulong b) => b switch
    {
        >= 1UL << 40 => $"{b / (double)(1UL << 40):0.0} TB",
        >= 1UL << 30 => $"{b / (double)(1UL << 30):0.0} GB",
        >= 1UL << 20 => $"{b / (double)(1UL << 20):0.0} MB",
        >= 1UL << 10 => $"{b / (double)(1UL << 10):0.0} KB",
        _ => $"{b} B"
    };

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemTimes(out long lpIdleTime, out long lpKernelTime, out long lpUserTime);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);

    [StructLayout(LayoutKind.Sequential)]
    private struct MEMORYSTATUSEX
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }
}
