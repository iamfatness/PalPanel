namespace PalPanel.Control;

public interface IBackupService
{
    Task<string> CreateBackupAsync(string reason, CancellationToken ct); // returns zip path
    IReadOnlyList<BackupInfo> List();
    Task RestoreAsync(string fileName, CancellationToken ct);            // throws unless server Stopped
}

public record BackupInfo(string FileName, long SizeBytes, DateTimeOffset CreatedAt);
