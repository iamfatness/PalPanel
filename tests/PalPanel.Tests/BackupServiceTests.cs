using Microsoft.Extensions.Options;
using PalPanel;
using PalPanel.Control;
using PalPanel.Supervisor;

public class BackupServiceTests
{
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
}
