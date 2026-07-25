using System.Diagnostics;
using System.IO.Compression;
using System.Text.Json;

namespace PalPanel.Control;

// Installs a self-contained SteamCMD on demand and drives it to update a dedicated server. Kept
// outside the app dir so it survives panel deploys. Update checks use the public steamcmd.net API
// (graceful) so we don't have to run SteamCMD just to see if an update exists.
public class SteamCmdService(IHttpClientFactory httpFactory, ILogger<SteamCmdService>? log = null)
{
    private const string SteamCmdZipUrl = "https://steamcdn-a.akamaihd.net/client/installer/steamcmd.zip";
    private static readonly string SteamCmdDir =
        Path.Combine(Path.GetDirectoryName(AppContext.BaseDirectory.TrimEnd('\\', '/')) ?? @"C:\PalPanel", "steamcmd");
    private static string ExePath => Path.Combine(SteamCmdDir, "steamcmd.exe");

    private readonly SemaphoreSlim _installGate = new(1, 1);

    public bool IsInstalled => File.Exists(ExePath);

    // Download + extract SteamCMD if it isn't present. First run self-updates its own binaries.
    public async Task<string> EnsureInstalledAsync(CancellationToken ct = default)
    {
        if (IsInstalled) return ExePath;
        await _installGate.WaitAsync(ct);
        try
        {
            if (IsInstalled) return ExePath;
            Directory.CreateDirectory(SteamCmdDir);
            var zip = Path.Combine(SteamCmdDir, "steamcmd.zip");
            using (var http = httpFactory.CreateClient())
            await using (var s = await http.GetStreamAsync(SteamCmdZipUrl, ct))
            await using (var f = File.Create(zip))
                await s.CopyToAsync(f, ct);
            ZipFile.ExtractToDirectory(zip, SteamCmdDir, overwriteFiles: true);
            File.Delete(zip);
            return ExePath;
        }
        finally { _installGate.Release(); }
    }

    // Latest public build id for an app, via the community steamcmd.net API. Null if unavailable.
    public async Task<long?> GetLatestBuildAsync(long appId, CancellationToken ct = default)
    {
        try
        {
            using var http = httpFactory.CreateClient();
            http.Timeout = TimeSpan.FromSeconds(10);
            using var doc = JsonDocument.Parse(await http.GetStringAsync($"https://api.steamcmd.net/v1/info/{appId}", ct));
            var build = doc.RootElement.GetProperty("data").GetProperty(appId.ToString())
                .GetProperty("depots").GetProperty("branches").GetProperty("public").GetProperty("buildid").GetString();
            return long.TryParse(build, out var b) ? b : null;
        }
        catch (Exception ex) { log?.LogWarning(ex, "steamcmd.net build check failed for {App}", appId); return null; }
    }

    // Run `app_update <appId> validate` into the install dir, streaming output lines to onLine.
    public async Task<bool> UpdateAsync(string installDir, long appId, Func<string, Task> onLine, CancellationToken ct)
    {
        var exe = await EnsureInstalledAsync(ct);
        var psi = new ProcessStartInfo(exe,
            $"+force_install_dir \"{installDir}\" +login anonymous +app_update {appId} validate +quit")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        using var proc = Process.Start(psi)!;
        // Stream stdout; SteamCMD emits progress lines we forward to the event log.
        var pump = Task.Run(async () =>
        {
            string? line;
            while ((line = await proc.StandardOutput.ReadLineAsync(ct)) is not null)
                if (line.Trim().Length > 0) await onLine(line.Trim());
        }, ct);
        await proc.WaitForExitAsync(ct);
        await pump;
        return proc.ExitCode == 0;
    }
}
