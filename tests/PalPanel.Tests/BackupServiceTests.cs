using Microsoft.Extensions.Options;
using PalPanel;
using PalPanel.Control;
using PalPanel.Supervisor;

public class BackupServiceTests
{
    private class RecordingSink : PalPanel.Data.IEventSink
    {
        public List<(string Type, string Detail)> Events { get; } = [];
        public Task LogAsync(string type, string detail, string? actorEmail = null)
        { Events.Add((type, detail)); return Task.CompletedTask; }
    }

    private static (BackupService svc, string saveDir, string bakDir, ProcessSupervisor sup) Make(int keep = 20)
    {
        var saveDir = Directory.CreateTempSubdirectory().FullName;
        File.WriteAllText(Path.Combine(saveDir, "Level.sav"), "worlddata");
        Directory.CreateDirectory(Path.Combine(saveDir, "Players"));
        File.WriteAllText(Path.Combine(saveDir, "Players", "p1.sav"), "playerdata");
        var bakDir = Directory.CreateTempSubdirectory().FullName;
        var o = Options.Create(new PanelOptions { SaveDirectory = saveDir, BackupDirectory = bakDir, BackupsToKeep = keep });
        var sup = new ProcessSupervisor(new FakeLauncher(), o);
        return (new BackupService(o, sup, new NullEventSink()), saveDir, bakDir, sup);
    }

    private static (BackupService svc, string saveDir, string bakDir, RecordingSink sink) MakeRecording(int keep = 20)
    {
        var saveDir = Directory.CreateTempSubdirectory().FullName;
        File.WriteAllText(Path.Combine(saveDir, "Level.sav"), "worlddata");
        Directory.CreateDirectory(Path.Combine(saveDir, "Players"));
        File.WriteAllText(Path.Combine(saveDir, "Players", "p1.sav"), "playerdata");
        var bakDir = Directory.CreateTempSubdirectory().FullName;
        var o = Options.Create(new PanelOptions { SaveDirectory = saveDir, BackupDirectory = bakDir, BackupsToKeep = keep });
        var sup = new ProcessSupervisor(new FakeLauncher(), o);
        var sink = new RecordingSink();
        return (new BackupService(o, sup, sink), saveDir, bakDir, sink);
    }

    [Fact]
    public async Task CreateBackup_ZipsSaveDirAndPrunes()
    {
        var (svc, _, bakDir, _) = Make(keep: 2);
        await svc.CreateBackupAsync("one", default);
        await svc.CreateBackupAsync("two", default);
        await svc.CreateBackupAsync("three", default);
        var list = svc.List();
        Assert.Equal(2, list.Count);                       // pruned to keep=2
        Assert.DoesNotContain(list, b => b.FileName.Contains("one"));
    }

    [Fact]
    public async Task Restore_RefusesWhileRunning()
    {
        var (svc, _, _, sup) = Make();
        var path = await svc.CreateBackupAsync("x", default);
        await sup.StartAsync(default); sup.MarkRunning();
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.RestoreAsync(Path.GetFileName(path), default));
    }

    [Fact]
    public async Task Restore_ReplacesSaveDir_AndSnapshotsCurrentFirst()
    {
        var (svc, saveDir, _, _) = Make();
        var path = await svc.CreateBackupAsync("golden", default);
        File.WriteAllText(Path.Combine(saveDir, "Level.sav"), "corrupted");
        await svc.RestoreAsync(Path.GetFileName(path), default);
        Assert.Equal("worlddata", File.ReadAllText(Path.Combine(saveDir, "Level.sav")));
        Assert.Contains(svc.List(), b => b.FileName.Contains("pre-restore"));
    }

    [Fact]
    public async Task Restore_OldestBackupAtRetentionCap_SurvivesPreRestorePrune()
    {
        // At the retention cap, the pre-restore snapshot pushes the oldest backup out of the
        // window — and the oldest backup may be exactly the zip being restored. The restore
        // must still succeed (working from a side copy), not FileNotFound after the save dir
        // has already been emptied.
        var (svc, saveDir, _, sink) = MakeRecording(keep: 2);
        var golden = await svc.CreateBackupAsync("golden", default);       // oldest
        File.WriteAllText(Path.Combine(saveDir, "Level.sav"), "seconddata");
        await svc.CreateBackupAsync("second", default);                    // cap now full at keep=2

        File.WriteAllText(Path.Combine(saveDir, "Level.sav"), "corrupted");
        await svc.RestoreAsync(Path.GetFileName(golden), default);         // snapshot's prune evicts golden

        Assert.Equal("worlddata", File.ReadAllText(Path.Combine(saveDir, "Level.sav")));
        Assert.Equal("playerdata", File.ReadAllText(Path.Combine(saveDir, "Players", "p1.sav")));
        Assert.Contains(sink.Events, e => e.Type == "restore");
    }

    [Fact]
    public async Task CreateBackup_SameSecondCollision_ProducesDistinctFiles()
    {
        // Regression pin for a rare flake: two backups requested with the same reason in the
        // same wall-clock second would otherwise collide on filename (the name is derived from
        // DateTimeOffset.UtcNow truncated to the second, plus the reason). Force the collision
        // deterministically -- rather than relying on two real CreateBackupAsync calls happening
        // to land in the same second -- by pre-creating the exact file CreateBackupAsync is
        // about to target, then asserting BackupService detects the clash and writes to a
        // distinct (suffixed) path instead of clobbering the pre-existing file.
        var (svc, _, bakDir, _) = Make();

        var timestamp = DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss");
        var collidingPath = Path.Combine(bakDir, $"palworld-{timestamp}-dup.zip");
        await File.WriteAllTextAsync(collidingPath, "pre-existing");

        var path = await svc.CreateBackupAsync("dup", default);

        Assert.NotEqual(collidingPath, path);
        Assert.True(File.Exists(collidingPath), "pre-existing colliding file must not be deleted");
        Assert.True(File.Exists(path), "new backup must land at a distinct path");
        Assert.Equal("pre-existing", await File.ReadAllTextAsync(collidingPath)); // untouched, not clobbered
    }

    [Theory]
    [InlineData("..\\evil.zip")]
    [InlineData("../evil.zip")]
    [InlineData("C:\\other\\x.zip")]
    [InlineData("nonexistent.zip")]
    public async Task Restore_RejectsTraversalAndUnknownNames_WithoutTouchingSaveDir(string fileName)
    {
        var (svc, saveDir, _, _) = Make();
        await svc.CreateBackupAsync("golden", default);

        await Assert.ThrowsAsync<InvalidOperationException>(() => svc.RestoreAsync(fileName, default));

        // rejection must happen before any snapshot/delete: save dir untouched
        Assert.Equal("worlddata", File.ReadAllText(Path.Combine(saveDir, "Level.sav")));
        Assert.Equal("playerdata", File.ReadAllText(Path.Combine(saveDir, "Players", "p1.sav")));
    }
}
