using System.Text;

namespace PalPanel.Control;

// Parses and rewrites the single `OptionSettings=(k=v,k=v,...)` line in PalWorldSettings.ini.
// Preserves the surrounding file text, key order, and every key (including ones we don't render),
// so a round-trip is lossless and unknown settings are never dropped. Comma/paren splitting is
// quote-aware so quoted string values (ServerName, ServerDescription) survive.
public sealed class PalGameSettings
{
    private const string Marker = "OptionSettings=(";

    private readonly string _prefix;   // file text up to and including "OptionSettings=("
    private readonly string _suffix;   // file text from the matching ")" to the end
    private readonly List<KeyValuePair<string, string>> _entries;

    private PalGameSettings(string prefix, string suffix, List<KeyValuePair<string, string>> entries)
    { _prefix = prefix; _suffix = suffix; _entries = entries; }

    public IReadOnlyList<KeyValuePair<string, string>> Entries => _entries;

    public string? Get(string key)
    {
        foreach (var e in _entries) if (e.Key == key) return e.Value;
        return null;
    }

    public void Set(string key, string value)
    {
        for (int i = 0; i < _entries.Count; i++)
            if (_entries[i].Key == key) { _entries[i] = new(key, value); return; }
        _entries.Add(new(key, value)); // append unknown keys rather than fail
    }

    public static PalGameSettings Parse(string iniText)
    {
        int idx = iniText.IndexOf(Marker, StringComparison.Ordinal);
        if (idx < 0) throw new FormatException("PalWorldSettings.ini has no OptionSettings line.");
        int openParen = idx + Marker.Length - 1;           // index of '('
        int close = FindMatchingClose(iniText, openParen);
        if (close < 0) throw new FormatException("PalWorldSettings.ini OptionSettings is not closed.");

        string inner = iniText.Substring(openParen + 1, close - openParen - 1);
        string prefix = iniText.Substring(0, openParen + 1);
        string suffix = iniText.Substring(close);           // starts at ')'
        return new PalGameSettings(prefix, suffix, ParseEntries(inner));
    }

    public string ToIniText()
    {
        var sb = new StringBuilder(_prefix);
        for (int i = 0; i < _entries.Count; i++)
        {
            if (i > 0) sb.Append(',');
            sb.Append(_entries[i].Key).Append('=').Append(_entries[i].Value);
        }
        return sb.Append(_suffix).ToString();
    }

    private static int FindMatchingClose(string s, int openIdx)
    {
        int depth = 0; bool q = false;
        for (int i = openIdx; i < s.Length; i++)
        {
            char c = s[i];
            if (c == '"') q = !q;
            else if (!q && c == '(') depth++;
            else if (!q && c == ')') { depth--; if (depth == 0) return i; }
        }
        return -1;
    }

    private static List<KeyValuePair<string, string>> ParseEntries(string inner)
    {
        var list = new List<KeyValuePair<string, string>>();
        if (string.IsNullOrEmpty(inner)) return list;
        int start = 0, depth = 0; bool q = false;
        for (int i = 0; i <= inner.Length; i++)
        {
            if (i == inner.Length || (!q && depth == 0 && inner[i] == ','))
            {
                var token = inner.Substring(start, i - start);
                int eq = token.IndexOf('=');
                if (eq > 0) list.Add(new(token[..eq], token[(eq + 1)..]));
                start = i + 1;
            }
            else
            {
                char c = inner[i];
                if (c == '"') q = !q;
                else if (!q && c == '(') depth++;
                else if (!q && c == ')') depth--;
            }
        }
        return list;
    }
}
