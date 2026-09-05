using System.Globalization;
using System.Text.RegularExpressions;

namespace SteamClipRemuxer.Core.Youtube;

/// <summary>
/// Expands the {placeholder} variables used in YouTube title and description templates.
/// Pure, so every rule here is covered by tests rather than discovered during an upload.
/// </summary>
public static class TitleTemplate
{
    public static string Expand(
        string template,
        string filePath,
        bool removeDateFromFilename = false,
        string removeTextPatterns = "",
        DateTime? now = null)
    {
        DateTime timestamp = now ?? DateTime.Now;
        string stem = Path.GetFileNameWithoutExtension(filePath);
        ClipName clip = ClipName.Parse(stem);

        string recordingDate = clip.RecordedAt?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? "";
        string recordingTime = clip.RecordedAt?.ToString("HH:mm:ss", CultureInfo.InvariantCulture) ?? "";

        string result = template
            .Replace("{filename}", stem)
            .Replace("{filename_ext}", Path.GetFileName(filePath))
            .Replace("{clip}", clip.Title)
            .Replace("{game}", clip.Game)
            .Replace("{recording_date}", recordingDate)
            .Replace("{recording_time}", recordingTime)
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
    /// Strips both date layouts Steam and this tool have produced: ISO "2026-08-08" and
    /// Steam's own "20260808_104557_PM".
    /// </summary>
    public static string RemoveDates(string input)
    {
        string result = Regex.Replace(input, @"\d{8}_\d{6}(?:_(?:AM|PM))?", "", RegexOptions.IgnoreCase);
        result = Regex.Replace(result, @"\d{4}-\d{2}-\d{2}", "");
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
