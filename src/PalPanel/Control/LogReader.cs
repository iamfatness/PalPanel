using System.Text.RegularExpressions;

namespace PalPanel.Control;

// Reads the tail of a Palworld server log without loading the whole file (logs grow large) and
// while the server is actively writing to it (shared read). Palworld only writes
// Pal/Saved/Logs/Pal.log when the dedicated server is launched with the -log switch, so callers
// surface guidance (see HasLogArg) when the file is absent.
public static partial class LogReader
{
    public record LogView(bool Exists, string Path, long SizeBytes, DateTimeOffset? ModifiedUtc, IReadOnlyList<string> Lines);

    // {SaveDirectory}/Logs/Pal.log — the console log UE writes when -log is present.
    public static string PalLogPath(string saveDirectory) => Path.Combine(saveDirectory, "Logs", "Pal.log");

    // Last `maxLines` lines of `content`, oldest-first, with a single trailing blank line (from a
    // final newline) trimmed so the view doesn't end on an empty row.
    public static IReadOnlyList<string> Tail(string content, int maxLines)
    {
        if (string.IsNullOrEmpty(content) || maxLines <= 0) return Array.Empty<string>();
        var lines = content.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        int end = lines.Length;
        if (end > 0 && lines[end - 1].Length == 0) end--; // drop trailing empty from a final \n
        int start = Math.Max(0, end - maxLines);
        return lines[start..end];
    }

    // Reads at most `tailBytes` from the END of the file (plenty for `maxLines`) with a shared
    // read/write handle so an actively-writing server is never blocked. Returns Exists=false with
    // the resolved path when the file is missing, so the UI can point the operator at it.
    public static async Task<LogView> ReadTailAsync(string path, int maxLines, int tailBytes = 256 * 1024, CancellationToken ct = default)
    {
        if (!File.Exists(path)) return new LogView(false, path, 0, null, Array.Empty<string>());
        var fi = new FileInfo(path);
        await using var fs = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        long start = Math.Max(0, fs.Length - tailBytes);
        fs.Seek(start, SeekOrigin.Begin);
        using var reader = new StreamReader(fs);
        if (start > 0) await reader.ReadLineAsync(ct); // drop the partial first line we seeked into
        var content = await reader.ReadToEndAsync(ct);
        return new LogView(true, path, fi.Length, fi.LastWriteTimeUtc, Tail(content, maxLines));
    }

    // Whether the launch arguments already request a log file (-log). Word-boundary matched so
    // "-logcmds" or a "-nolog" wouldn't false-positive.
    public static bool HasLogArg(string? launchArgs) =>
        !string.IsNullOrWhiteSpace(launchArgs) && LogArgRegex().IsMatch(launchArgs);

    [GeneratedRegex(@"(^|\s)-log(\s|$)", RegexOptions.IgnoreCase)]
    private static partial Regex LogArgRegex();
}
