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

    // Palworld ships DefaultPalWorldSettings.ini alongside PalServer.exe with the COMPLETE set of
    // options (and their defaults) for the installed version. The live PalWorldSettings.ini often
    // lists only a subset, so we read the default file to backfill the rest into the editor.
    public static string? DefaultPathFor(string exePath)
    {
        if (string.IsNullOrWhiteSpace(exePath)) return null;
        var dir = Path.GetDirectoryName(exePath);
        return string.IsNullOrEmpty(dir) ? null : Path.Combine(dir, "DefaultPalWorldSettings.ini");
    }

    public static async Task<PalGameSettings?> LoadDefaultsAsync(string exePath)
    {
        var path = DefaultPathFor(exePath);
        if (path is null || !File.Exists(path)) return null;
        // A missing/malformed default file must never break the editor — just fall back to the
        // live keys (return null → no backfill).
        try { return PalGameSettings.Parse(await File.ReadAllTextAsync(path)); }
        catch { return null; }
    }

    public static async Task SaveAsync(string saveDirectory, PalGameSettings settings)
    {
        var path = PathFor(saveDirectory);
        try { if (File.Exists(path)) File.Copy(path, path + ".bak", overwrite: true); }
        catch { /* best-effort backup; don't block the save on it */ }
        await File.WriteAllTextAsync(path, settings.ToIniText());
    }
}
