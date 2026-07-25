using System.Text.RegularExpressions;

namespace PalPanel.Control;

public sealed record SteamAppInfo(long BuildId, int StateFlags, string Name);

// Reads Steam's appmanifest_<appid>.acf (a VDF key/value file) to learn the installed build and
// whether Steam has flagged an update. Enough for the panel to show update status without needing
// SteamCMD just to check.
public static partial class SteamManifest
{
    // Steam StateFlags bitfield: 4 = Fully Installed, 2 = Update Required.
    public static bool UpdateRequired(int stateFlags) => (stateFlags & 2) != 0;

    public static SteamAppInfo Parse(string acfText) => new(
        long.TryParse(Field(acfText, "buildid"), out var b) ? b : 0,
        int.TryParse(Field(acfText, "StateFlags"), out var s) ? s : 0,
        Field(acfText, "name") ?? "");

    public static SteamAppInfo? Read(string manifestPath) =>
        File.Exists(manifestPath) ? Parse(File.ReadAllText(manifestPath)) : null;

    // A server exe at  <library>/steamapps/common/<App>/x.exe  has its manifest at
    // <library>/steamapps/appmanifest_<appid>.acf  (two levels up from the app folder).
    public static string? ManifestPathFromExe(string exePath, long appId)
    {
        var appDir = Path.GetDirectoryName(exePath);
        if (string.IsNullOrWhiteSpace(appDir)) return null;
        var steamapps = Path.GetFullPath(Path.Combine(appDir, "..", ".."));
        return Path.Combine(steamapps, $"appmanifest_{appId}.acf");
    }

    // The install directory SteamCMD updates into (the app folder itself).
    public static string? InstallDirFromExe(string exePath) => Path.GetDirectoryName(exePath);

    private static string? Field(string acf, string key)
    {
        var m = Regex.Match(acf, "\"" + Regex.Escape(key) + "\"\\s+\"([^\"]*)\"");
        return m.Success ? m.Groups[1].Value : null;
    }
}
