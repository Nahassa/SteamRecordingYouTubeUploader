using System.Text.Json;
using System.Text.Json.Serialization;
using SteamClipRemuxer.Core.Probing;

namespace SteamClipRemuxer.Core.Configuration;

/// <summary>
/// Persisted settings. Small by design: with the pipeline reduced to a lossless remux there
/// are no encoder, resolution or colour options left to configure.
/// </summary>
public sealed class AppSettings
{
    public string InputFolder { get; set; } = string.Empty;
    public string OutputFolder { get; set; } = string.Empty;

    /// <summary>Display aspect the output should present at, as "N:D". 16:9 gives the stretched look.</summary>
    public string TargetDisplayAspect { get; set; } = "16:9";

    public bool MoveProcessedFiles { get; set; } = true;
    public bool FastStart { get; set; } = true;

    public bool EnableYouTubeUpload { get; set; }
    public string YouTubeTitleTemplate { get; set; } = "{game} - {clip}";
    public string YouTubeDescriptionTemplate { get; set; } = "Recorded {recording_date}";
    public string YouTubeTags { get; set; } = "gaming,gameplay";
    public string YouTubePrivacyStatus { get; set; } = "private";
    public string YouTubeCategoryId { get; set; } = "20";
    public bool YouTubeMadeForKids { get; set; }
    public bool YouTubeAgeRestricted { get; set; }
    public bool YouTubeRemoveDateFromFilename { get; set; }
    public string YouTubeRemoveTextPatterns { get; set; } = string.Empty;

    [JsonIgnore]
    public AspectRatio ParsedTargetAspect =>
        AspectRatio.TryParse(TargetDisplayAspect) ?? AspectRatio.Widescreen;

    [JsonIgnore]
    public string[] ParsedTags =>
        YouTubeTags.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    /// <summary>Loads settings, falling back to defaults. Never throws; reports trouble through <paramref name="onError"/>.</summary>
    public static AppSettings Load(string? path = null, Action<string>? onError = null)
    {
        path ??= AppPaths.SettingsFile;
        try
        {
            if (!File.Exists(path)) return new AppSettings();
            return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path)) ?? new AppSettings();
        }
        catch (Exception ex)
        {
            onError?.Invoke($"Could not read settings ({ex.Message}); using defaults.");
            return new AppSettings();
        }
    }

    public void Save(string? path = null, Action<string>? onError = null)
    {
        path ??= AppPaths.SettingsFile;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(this, Options));
        }
        catch (Exception ex)
        {
            onError?.Invoke($"Could not save settings: {ex.Message}");
        }
    }
}
