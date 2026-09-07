using System.Globalization;

namespace SteamClipRemuxer.Core.Steam;

/// <summary>
/// Turns a Steam application id into a name.
///
/// A clip folder identifies its game only by id, so without this every clip read from Steam's
/// own folders would be named "App 730". Only Counter-Strike 2 is built in, because that is
/// what this tool was written against; anything else is either named by the user or falls back
/// to something readable rather than blank.
/// </summary>
public static class SteamApps
{
    private static readonly Dictionary<int, string> BuiltIn = new()
    {
        [730] = "Counter-Strike 2",
    };

    /// <summary>
    /// The name for an id. A name the user has set wins over the built-in one, so a wrong or
    /// stale built-in entry can always be corrected without a new build.
    /// </summary>
    public static string NameFor(int appId, IReadOnlyDictionary<string, string>? overrides = null)
    {
        if (appId <= 0) return "Steam";

        string key = appId.ToString(CultureInfo.InvariantCulture);
        if (overrides is not null &&
            overrides.TryGetValue(key, out string? custom) &&
            !string.IsNullOrWhiteSpace(custom))
        {
            return custom.Trim();
        }

        return BuiltIn.TryGetValue(appId, out string? known) ? known : "App " + key;
    }

    /// <summary>Whether an id would fall back to "App N", i.e. it is worth asking the user for a name.</summary>
    public static bool IsUnknown(int appId, IReadOnlyDictionary<string, string>? overrides = null) =>
        appId > 0
        && !BuiltIn.ContainsKey(appId)
        && (overrides is null
            || !overrides.TryGetValue(appId.ToString(CultureInfo.InvariantCulture), out string? name)
            || string.IsNullOrWhiteSpace(name));

    /// <summary>
    /// Reads the "730 = Counter-Strike 2" lines the settings window shows. Anything that is not
    /// an id followed by a name is ignored rather than rejected: a half-typed line should not
    /// throw away the rest of the list.
    /// </summary>
    public static Dictionary<string, string> ParseOverrides(string? text)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(text)) return map;

        foreach (string line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            string trimmed = line.Trim();
            if (trimmed.Length == 0 || trimmed.StartsWith('#')) continue;

            int separator = trimmed.IndexOfAny(new[] { '=', ':', '\t' });
            if (separator <= 0) continue;

            string id = trimmed[..separator].Trim();
            string name = trimmed[(separator + 1)..].Trim();

            if (name.Length == 0) continue;
            if (!int.TryParse(id, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed)) continue;
            if (parsed <= 0) continue;

            map[parsed.ToString(CultureInfo.InvariantCulture)] = name;
        }

        return map;
    }

    /// <summary>Renders the map back into editable lines, lowest id first so the order is stable.</summary>
    public static string FormatOverrides(IReadOnlyDictionary<string, string>? map)
    {
        if (map is null || map.Count == 0) return string.Empty;

        return string.Join(Environment.NewLine, map
            .Select(pair => (
                Id: int.TryParse(pair.Key, NumberStyles.Integer, CultureInfo.InvariantCulture, out int n)
                    ? n
                    : int.MaxValue,
                pair.Key,
                pair.Value))
            .OrderBy(x => x.Id)
            .Select(x => $"{x.Key} = {x.Value}"));
    }
}
