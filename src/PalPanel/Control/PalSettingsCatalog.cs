using System.Globalization;
using System.Text.RegularExpressions;

namespace PalPanel.Control;

public enum PalFieldKind { Bool, Number, Enum, Text }

// One editable setting, with everything the UI needs to render the right control and convert back
// to the raw ini value. `Value` holds the editable form (inner text for quoted strings,
// "True"/"False" for bools).
public sealed class PalSettingField
{
    public required string Key { get; init; }
    public required PalFieldKind Kind { get; init; }
    public required string Label { get; init; }
    public required string Group { get; init; }
    public string[] Options { get; init; } = [];
    public bool Quoted { get; init; }
    public bool IsFloat { get; init; }
    public bool Simple { get; init; }
    public string Value { get; set; } = "";

    public string ToRaw()
    {
        if (Kind == PalFieldKind.Text && Quoted)
            return "\"" + Value.Replace("\"", "") + "\"";
        if (Kind == PalFieldKind.Number && IsFloat && double.TryParse(Value, NumberStyles.Any, CultureInfo.InvariantCulture, out var d))
            return d.ToString("F6", CultureInfo.InvariantCulture);
        return Value;
    }
}

// Metadata that turns raw PalWorldSettings entries into friendly, typed, grouped fields. Types and
// groups are inferred from the key/value so unknown settings still render sensibly (Advanced view);
// a curated subset is flagged Simple.
public static partial class PalSettingsCatalog
{
    // Settings that shouldn't appear in the editor:
    //  - AdminPassword / RESTAPIEnabled / RESTAPIPort: panel-critical — changing them breaks the
    //    panel's own REST connection to the server.
    //  - bIsMultiplay: a genuine no-op on a dedicated server (leftover from the co-op world
    //    settings block), so it only confuses. (bIsPvP is a real, distinct setting — kept.)
    public static readonly HashSet<string> Hidden = new(StringComparer.OrdinalIgnoreCase)
        { "AdminPassword", "RESTAPIEnabled", "RESTAPIPort", "bIsMultiplay" };

    private static readonly Dictionary<string, string[]> Enums = new()
    {
        ["Difficulty"] = ["None", "Casual", "Normal", "Hard"],
        ["DeathPenalty"] = ["None", "Item", "ItemAndEquipment", "All"],
    };

    // Curated "Simple" view — the settings people actually tune, in a sensible order.
    private static readonly HashSet<string> SimpleKeys = new()
    {
        "Difficulty", "DeathPenalty", "DayTimeSpeedRate", "NightTimeSpeedRate",
        "ExpRate", "PalCaptureRate", "PalSpawnNumRate", "CollectionDropRate", "EnemyDropItemRate",
        "PalDamageRateAttack", "PlayerDamageRateAttack", "bIsPvP", "bEnablePlayerToPlayerDamage", "bEnableFriendlyFire",
        "ServerPlayerMaxNum", "ServerName", "ServerDescription",
    };

    public static List<PalSettingField> BuildFields(PalGameSettings settings)
    {
        var fields = new List<PalSettingField>();
        foreach (var (key, raw) in settings.Entries)
        {
            if (Hidden.Contains(key)) continue;

            var quoted = raw.Length >= 2 && raw[0] == '"' && raw[^1] == '"';
            var inner = quoted ? raw[1..^1] : raw;
            PalFieldKind kind;
            var isFloat = false;

            if (Enums.ContainsKey(key)) kind = PalFieldKind.Enum;
            else if (raw is "True" or "False") kind = PalFieldKind.Bool;
            else if (quoted) kind = PalFieldKind.Text;
            else if (double.TryParse(raw, NumberStyles.Any, CultureInfo.InvariantCulture, out _))
            { kind = PalFieldKind.Number; isFloat = raw.Contains('.'); }
            else kind = PalFieldKind.Text; // bare word / unknown enum

            fields.Add(new PalSettingField
            {
                Key = key,
                Kind = kind,
                Label = Humanize(key),
                Group = GroupOf(key),
                Options = Enums.GetValueOrDefault(key, []),
                Quoted = quoted,
                IsFloat = isFloat,
                Simple = SimpleKeys.Contains(key),
                Value = kind == PalFieldKind.Text ? inner : raw,
            });
        }
        return fields;
    }

    public static void Apply(IEnumerable<PalSettingField> fields, PalGameSettings settings)
    {
        foreach (var f in fields) settings.Set(f.Key, f.ToRaw());
    }

    private static string GroupOf(string key)
    {
        if (key is "Difficulty" or "DeathPenalty" or "DayTimeSpeedRate" or "NightTimeSpeedRate") return "World";
        if (key.Contains("Damage") || key is "bIsPvP" or "bEnablePlayerToPlayerDamage" or "bEnableFriendlyFire") return "Combat & PvP";
        if (key.StartsWith('b')) return "Toggles";
        if (key.Contains("Rate") || key.Contains("Rank") || key.Contains("Num")) return "Rates & amounts";
        if (key.StartsWith("Server") || key.Contains("Port") || key.Contains("Password") || key.Contains("Guild")) return "Server";
        if (key.StartsWith("Player")) return "Players";
        if (key.StartsWith("Pal")) return "Pals";
        return "Other";
    }

    private static string Humanize(string key)
    {
        var k = key.StartsWith('b') && key.Length > 1 && char.IsUpper(key[1]) ? key[1..] : key;
        return CamelBreak().Replace(k, "$1 $2").Replace("H P", "HP").Replace("U N K O", "UNKO");
    }

    [GeneratedRegex(@"([a-z0-9])([A-Z])")]
    private static partial Regex CamelBreak();
}
