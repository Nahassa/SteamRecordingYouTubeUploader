using System.Globalization;
using System.Text.RegularExpressions;

namespace SteamClipRemuxer.Core.Highlights;

/// <summary>
/// Names the file written for a Steam clip.
///
/// Needed because reading Steam's clip folders replaces the export step, and an unexported clip
/// has no name yet: the suggested name in clip.pb only appears once a clip has been exported at
/// least once, and on the samples measured it is the moment Save was pressed rather than the
/// moment the clip contains - 25 minutes adrift on one of them.
/// </summary>
public static class ClipNaming
{
    /// <summary>
    /// Default layout. The time is included because a title alone collides readily: two "Double
    /// kill" clips from one evening on the same map would otherwise fight over one filename.
    /// The timestamp is the clip's own recorded moment, taken from clip.pb, so re-running the
    /// batch produces the same name rather than a new one each time.
    /// </summary>
    public const string DefaultTemplate = "{game} - {recording_date} {recording_time} - {highlight}";

    public static string Expand(
        string template,
        string game,
        DateTimeOffset recordedAt,
        Highlight? highlight)
    {
        string result = template
            .Replace("{game}", game)
            .Replace("{recording_date}", recordedAt.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))
            // Colons are not legal in a Windows filename, so the time is hyphenated here rather
            // than left for the sanitiser to mangle into something unreadable.
            .Replace("{recording_time}", recordedAt.ToString("HH-mm-ss", CultureInfo.InvariantCulture))
            .Replace("{highlight}", highlight?.Label ?? "Clip")
            .Replace("{highlight_full}", highlight?.Describe() ?? "Clip")
            .Replace("{weapon}", highlight?.Weapon ?? string.Empty)
            .Replace("{map}", highlight?.Map ?? string.Empty)
            .Replace("{mode}", highlight?.Mode ?? string.Empty)
            .Replace("{round}", highlight?.Round?.ToString(CultureInfo.InvariantCulture) ?? string.Empty)
            .Replace("{kills}", highlight?.KillCount.ToString(CultureInfo.InvariantCulture) ?? "0");

        return Sanitise(result);
    }

    /// <summary>
    /// Makes a name safe to write. Map names carry no awkward characters, but weapon names and
    /// anything a template pulls from Steam can, and a title is not worth an IOException.
    /// </summary>
    public static string Sanitise(string name)
    {
        var cleaned = new string(name
            .Select(c => Path.GetInvalidFileNameChars().Contains(c) ? ' ' : c)
            .ToArray());

        // Collapse the gaps a blank placeholder leaves behind, so an absent weapon does not
        // produce "Counter-Strike 2 -  - Double kill".
        cleaned = Regex.Replace(cleaned, @"\s+", " ");
        cleaned = Regex.Replace(cleaned, @"(\s*-\s*){2,}", " - ");
        cleaned = cleaned.Trim(' ', '-', '.', '_');

        return cleaned.Length == 0 ? "Clip" : cleaned;
    }
}
