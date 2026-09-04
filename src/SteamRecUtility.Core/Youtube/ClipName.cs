using System.Globalization;
using System.Text.RegularExpressions;

namespace SteamRecUtility.Core.Youtube;

/// <summary>
/// The parts Steam encodes into a recording's filename, e.g.
/// "CounterStrike_2__20260808_104557_PM__Double_kill.mp4" -> game, timestamp, clip title.
/// </summary>
public sealed record ClipName(string Game, DateTime? RecordedAt, string Title)
{
    // Steam's own format: <Game>__<yyyyMMdd>_<hhmmss>_<AM|PM>__<title>
    private static readonly Regex SteamStamp = new(
        @"^(?<d>\d{8})_(?<t>\d{6})(?:_(?<m>AM|PM))?$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // The older ISO-style date this tool used to look for.
    private static readonly Regex IsoDate = new(@"\d{4}-\d{2}-\d{2}", RegexOptions.Compiled);

    public static ClipName Parse(string fileNameWithoutExtension)
    {
        string[] parts = fileNameWithoutExtension.Split("__", StringSplitOptions.None);

        if (parts.Length >= 3)
        {
            Match stamp = SteamStamp.Match(parts[1]);
            if (stamp.Success)
            {
                return new ClipName(
                    Humanise(parts[0]),
                    ParseSteamStamp(stamp),
                    Humanise(string.Join("__", parts.Skip(2))));
            }
        }

        // Not Steam's layout; fall back to the ISO date if one is present anywhere.
        Match iso = IsoDate.Match(fileNameWithoutExtension);
        DateTime? isoDate = null;
        if (iso.Success && DateTime.TryParseExact(
                iso.Value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime d))
        {
            isoDate = d;
        }

        return new ClipName(string.Empty, isoDate, Humanise(fileNameWithoutExtension));
    }

    private static DateTime? ParseSteamStamp(Match stamp)
    {
        string date = stamp.Groups["d"].Value;
        string time = stamp.Groups["t"].Value;
        string meridiem = stamp.Groups["m"].Value.ToUpperInvariant();

        string text = $"{date} {time}";
        string format = "yyyyMMdd HHmmss";

        if (meridiem.Length > 0)
        {
            text = $"{date} {time} {meridiem}";
            format = "yyyyMMdd hhmmss tt";
        }

        return DateTime.TryParseExact(
            text, format, CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime parsed)
            ? parsed
            : null;
    }

    /// <summary>Turns "Double_kill" into "Double kill" without collapsing intentional spacing.</summary>
    private static string Humanise(string raw) =>
        Regex.Replace(raw.Replace('_', ' '), @"\s{2,}", " ").Trim();
}
