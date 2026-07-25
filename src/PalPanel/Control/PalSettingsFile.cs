namespace PalPanel.Control;

// Reads/writes a server's PalWorldSettings.ini. The file lives under the save directory at
// Config/WindowsServer/PalWorldSettings.ini. Writes are preceded by a .bak copy.
public static class PalSettingsFile
{
    public static string PathFor(string saveDirectory) =>
        Path.Combine(saveDirectory, "Config", "WindowsServer", "PalWorldSettings.ini");

    public static async Task<PalGameSettings?> LoadAsync(string saveDirectory)
    {
        var path = PathFor(saveDirectory);
        if (string.IsNullOrWhiteSpace(saveDirectory) || !File.Exists(path)) return null;
        return PalGameSettings.Parse(await File.ReadAllTextAsync(path));
    }

    public static async Task SaveAsync(string saveDirectory, PalGameSettings settings)
    {
        var path = PathFor(saveDirectory);
        try { if (File.Exists(path)) File.Copy(path, path + ".bak", overwrite: true); }
        catch { /* best-effort backup; don't block the save on it */ }
        await File.WriteAllTextAsync(path, settings.ToIniText());
    }
}
