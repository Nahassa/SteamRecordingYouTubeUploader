using System.Globalization;
using System.Text.RegularExpressions;

namespace SteamClipRemuxer.Core.Youtube;

/// <summary>
/// Expands the {placeholder} variables used in YouTube title and description templates.
/// Pure, so every rule here is covered by tests rather than discovered during an upload.
/// </summary>
public static class TitleTemplate
{
    /// <summary>
    /// Default for clips read from Steam's own folders. Carries no timestamp: the moment is sent
    /// as the video's recording date instead, which is where YouTube can actually use it.
    /// </summary>
    public const string DefaultClipTitle = "{game} - {highlight_full}";

    /// <summary>
    /// Default for a compilation. There is no one highlight to name, so the title says what the
    /// video is; the individual plays are named in the timestamp list in the description.
    /// </summary>
    public const string DefaultCompilationTitle = "{game} - {count} clip compilation";

    public static string Expand(
        string template,
        string filePath,
        bool removeDateFromFilename = false,
        string removeTextPatterns = "",
        DateTime? now = null,
        Highlights.Highlight? highlight = null,
        DateTimeOffset? recordedAt = null,
        string? game = null,
        int? count = null)
    {
        DateTime timestamp = now ?? DateTime.Now;
        string stem = Path.GetFileNameWithoutExtension(filePath);
        ClipName clip = ClipName.Parse(stem);

        // A clip read from Steam's folders knows exactly when it was recorded, so that beats
        // anything guessed from the filename.
        DateTimeOffset? recorded = recordedAt
            ?? (clip.RecordedAt is { } fromName ? new DateTimeOffset(fromName, TimeSpan.Zero) : null);

        string recordingDate = recorded?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? "";
        string recordingTime = recorded?.ToString("HH:mm:ss", CultureInfo.InvariantCulture) ?? "";

        string result = template
            .Replace("{highlight}", highlight?.Label ?? "")
            .Replace("{highlight_full}", highlight?.Describe() ?? "")
            .Replace("{weapon}", highlight?.Weapon ?? "")
            .Replace("{map}", highlight?.Map ?? "")
            .Replace("{mode}", highlight?.Mode ?? "")
            .Replace("{round}", highlight?.Round?.ToString(CultureInfo.InvariantCulture) ?? "")
            .Replace("{kills}", highlight is null ? "" : highlight.KillCount.ToString(CultureInfo.InvariantCulture))
            .Replace("{filename}", stem)
            .Replace("{filename_ext}", Path.GetFileName(filePath))
            .Replace("{clip}", clip.Title)
            // A clip folder names its game only by app id, so the caller supplies it; the
            // filename fallback only works for names Steam itself produced.
            .Replace("{game}", game ?? clip.Game)
            .Replace("{recording_date}", recordingDate)
            .Replace("{recording_time}", recordingTime)
            // Only a compilation has a count; elsewhere it collapses away with its separators.
            .Replace("{count}", count?.ToString(CultureInfo.InvariantCulture) ?? "")
            .Replace("{date}", timestamp.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))
            .Replace("{time}", timestamp.ToString("HH:mm:ss", CultureInfo.InvariantCulture))
            .Replace("{datetime}", timestamp.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture))
            .Replace("{year}", timestamp.Year.ToString(CultureInfo.InvariantCulture))
            .Replace("{month}", timestamp.Month.ToString("D2", CultureInfo.InvariantCulture))
            .Replace("{day}", timestamp.Day.ToString("D2", CultureInfo.InvariantCulture));

        if (removeDateFromFilename) result = RemoveDates(result);
        if (!string.IsNullOrWhiteSpace(removeTextPatterns)) result = RemovePatterns(result, removeTextPatterns);

        return result.Trim();
    }

    /// <summary>
    /// Strips every timestamp layout Steam and this tool have produced, so a title can be about
    /// the play rather than when it happened. The upload carries the real moment in the video's
    /// recording date instead, where YouTube can use it.
    ///
    /// Order matters: the combined date-and-time form is removed first, because taking the date
    /// out of "2026-08-28 21-52-42" would leave a bare "21-52-42" behind.
    /// </summary>
    public static string RemoveDates(string input)
    {
        // Steam's own: "20260808_104557_PM".
        string result = Regex.Replace(input, @"\d{8}_\d{6}(?:_(?:AM|PM))?", "", RegexOptions.IgnoreCase);

        // This tool's clip names: "2026-08-28 21-52-42".
        result = Regex.Replace(result, @"\d{4}-\d{2}-\d{2}[ T_]\d{2}[-:]\d{2}[-:]\d{2}", "");

        // ISO date on its own.
        result = Regex.Replace(result, @"\d{4}-\d{2}-\d{2}", "");

        // A time left on its own, in either separator. Anchored so it cannot eat the tail of a
        // date or a score like "13 : 9".
        result = Regex.Replace(result, @"(?<!\d)\d{2}[-:]\d{2}[-:]\d{2}(?!\d)", "");

        return Tidy(result);
    }

    public static string RemovePatterns(string input, string commaSeparatedPatterns)
    {
        if (string.IsNullOrWhiteSpace(input) || string.IsNullOrWhiteSpace(commaSeparatedPatterns))
            return input;

        string result = input;
        foreach (string pattern in commaSeparatedPatterns
                     .Split(',')
                     .Select(p => p.Trim())
                     .Where(p => p.Length > 0))
        {
            result = Regex.Replace(result, Regex.Escape(pattern), "", RegexOptions.IgnoreCase);
        }

        return Tidy(result);
    }

    /// <summary>Collapses the whitespace and orphaned separators left behind by a removal.</summary>
    private static string Tidy(string input)
    {
        string result = Regex.Replace(input, @"\s+", " ");
        result = Regex.Replace(result, @"[-_]{2,}", "-");
        result = Regex.Replace(result, @"\s*-\s*-\s*", " - ");
        return result.Trim(' ', '-', '_', '.');
    }
}
