using PalPanel.Control;

public class SteamManifestTests
{
    private const string Acf = """
        "AppState"
        {
            "appid"        "2394010"
            "name"         "Palworld Dedicated Server"
            "StateFlags"   "4"
            "buildid"      "24181105"
            "SizeOnDisk"   "6041562196"
        }
        """;

    [Fact]
    public void Parse_ReadsBuildStateAndName()
    {
        var info = SteamManifest.Parse(Acf);
        Assert.Equal(24181105, info.BuildId);
        Assert.Equal(4, info.StateFlags);
        Assert.Equal("Palworld Dedicated Server", info.Name);
    }

    [Theory]
    [InlineData(4, false)]   // fully installed
    [InlineData(6, true)]    // fully installed + update required
    [InlineData(2, true)]
    public void UpdateRequired_ReadsBit(int flags, bool expected) =>
        Assert.Equal(expected, SteamManifest.UpdateRequired(flags));

    [Fact]
    public void ManifestPathFromExe_ResolvesTwoLevelsUp()
    {
        var path = SteamManifest.ManifestPathFromExe(@"D:\SteamLibrary\steamapps\common\PalServer\PalServer.exe", 2394010);
        Assert.NotNull(path);
        Assert.Equal("appmanifest_2394010.acf", Path.GetFileName(path));
        Assert.Equal("steamapps", Path.GetFileName(Path.GetDirectoryName(path)));
    }

    [Fact]
    public void InstallDirFromExe_IsTheAppFolder()
    {
        var dir = SteamManifest.InstallDirFromExe(@"D:\SteamLibrary\steamapps\common\PalServer\PalServer.exe");
        Assert.Equal("PalServer", Path.GetFileName(dir));
    }
}
