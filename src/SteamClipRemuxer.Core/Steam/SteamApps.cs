using System.Globalization;

namespace SteamClipRemuxer.Core.Steam;

/// <summary>
/// Turns a Steam application id into a name.
///
/// A clip folder identifies its game only by id, so without this every clip read from Steam's
/// own folders would be named "App 730". The list is deliberately tiny: it covers what this
/// tool is used on, and anything else falls back to something readable rather than blank.
/// </summary>
public static class SteamApps
{
    private static readonly Dictionary<int, string> Names = new()
    {
        [730] = "Counter-Strike 2",
    };

    public static string NameFor(int appId) =>
        appId <= 0 ? "Steam"
        : Names.TryGetValue(appId, out string? name) ? name
        : "App " + appId.ToString(CultureInfo.InvariantCulture);
}
