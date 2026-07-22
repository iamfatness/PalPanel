using System.IO.Compression;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using PalPanel.Data;
using PalPanel.Supervisor;

namespace PalPanel.Control;

public class BackupService(IOptions<PanelOptions> opts, ProcessSupervisor sup, IEventSink events) : IBackupService
{
    private readonly PanelOptions _o = opts.Value;

    public async Task<string> CreateBackupAsync(string reason, CancellationToken ct)
    {
        Directory.CreateDirectory(_o.BackupDirectory);
        var sanitizedReason = SanitizeReason(reason);
        var timestamp = DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss");
        var fileName = $"palworld-{timestamp}-{sanitizedReason}.zip";
        var finalPath = Path.Combine(_o.BackupDirectory, fileName);

        // Avoid clobbering an existing backup if two land in the same second (same
        // timestamp + reason): add a short numeric suffix on collision.
        if (File.Exists(finalPath))
        {
            var baseName = $"palworld-{timestamp}-{sanitizedReason}";
            var n = 1;
            do { finalPath = Path.Combine(_o.BackupDirectory, $"{baseName}-{n}.zip"); n++; }
            while (File.Exists(finalPath));
        }

        var tempPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.zip");
        try
        {
            ZipFile.CreateFromDirectory(_o.SaveDirectory, tempPath);
            File.Move(tempPath, finalPath);
        }
        finally
        {
            if (File.Exists(tempPath)) File.Delete(tempPath);
        }

        Prune();
        await events.LogAsync("backup", $"Backup created: {Path.GetFileName(finalPath)} ({reason})");
        return finalPath;
    }

    private static string SanitizeReason(string reason)
    {
        var sanitized = Regex.Replace(reason, "[^a-zA-Z0-9-_]", "-");
        return sanitized.Length > 40 ? sanitized[..40] : sanitized;
    }

    private void Prune()
    {
        var all = List();
        if (all.Count <= _o.BackupsToKeep) return;
        foreach (var stale in all.Skip(_o.BackupsToKeep))
        {
            try { File.Delete(Path.Combine(_o.BackupDirectory, stale.FileName)); }
            catch (IOException) { /* best-effort prune; next pass retries */ }
        }
    }

    public IReadOnlyList<BackupInfo> List()
    {
        if (!Directory.Exists(_o.BackupDirectory)) return [];
        return Directory.GetFiles(_o.BackupDirectory, "*.zip")
            .Select(f => new FileInfo(f))
            .Select(fi => new BackupInfo(fi.Name, fi.Length, fi.CreationTimeUtc))
            .OrderByDescending(b => b.CreatedAt)
            .ToList();
    }

    public async Task RestoreAsync(string fileName, CancellationToken ct)
    {
        if (sup.State != ServerState.Stopped)
            throw new InvalidOperationException("Cannot restore while the server is not stopped.");

        if (fileName.Contains('/') || fileName.Contains('\\'))
            throw new InvalidOperationException("Invalid backup file name.");

        var fullPath = Path.GetFullPath(Path.Combine(_o.BackupDirectory, fileName));
        var backupDirFull = Path.GetFullPath(_o.BackupDirectory);
        if (!fullPath.StartsWith(backupDirFull, StringComparison.OrdinalIgnoreCase) || !File.Exists(fullPath))
            throw new InvalidOperationException("Invalid backup file name.");

        try
        {
            await CreateBackupAsync("pre-restore", ct);

            if (Directory.Exists(_o.SaveDirectory))
            {
                foreach (var file in Directory.GetFiles(_o.SaveDirectory)) File.Delete(file);
                foreach (var dir in Directory.GetDirectories(_o.SaveDirectory)) Directory.Delete(dir, true);
            }
            else
            {
                Directory.CreateDirectory(_o.SaveDirectory);
            }

            ZipFile.ExtractToDirectory(fullPath, _o.SaveDirectory);
            await events.LogAsync("restore", $"Restored from {fileName}");
        }
        catch (Exception ex)
        {
            await events.LogAsync("restore-failed", $"Restore from {fileName} failed: {ex.Message}");
            throw;
        }
    }
}
