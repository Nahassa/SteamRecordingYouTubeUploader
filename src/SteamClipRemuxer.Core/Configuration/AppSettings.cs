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

    /// <summary>Where clips are read from. See <see cref="Configuration.ClipSource"/>.</summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public ClipSource ClipSource { get; set; } = ClipSource.ExportedFiles;

    /// <summary>
    /// Longest clip to process, in seconds. Steam writes an untouched clip at exactly the
    /// recording buffer length, so anything shorter is one the user actually cropped. The
    /// buffer is a Steam setting rather than a fixed value, hence configurable here; the
    /// comparison is strict, so leaving this at the buffer length excludes untouched clips
    /// without needing a tolerance.
    /// </summary>
    public int MaxClipSeconds { get; set; } = 120;

    /// <summary>
    /// Cut the output down to the range cropped in Steam. Steam's crop point usually falls
    /// mid-GOP, so the cut lands on the nearest earlier keyframe: still a verbatim packet copy,
    /// never a re-encode, but up to a segment shorter than an exact frame cut would be.
    /// Turn off to keep the whole of every DASH segment the clip touches, which leaves the
    /// output a byte-for-byte copy of the complete source stream.
    /// </summary>
    public bool RespectSteamCrop { get; set; } = true;

    /// <summary>
    /// Skip clips already handled, so Steam's clip list can be left alone instead of deleting
    /// clips there to avoid uploading the same highlight twice.
    /// </summary>
    public bool SkipAlreadyProcessed { get; set; } = true;

    /// <summary>
    /// Name for the file written from a Steam clip folder. Reading those folders replaces the
    /// export step, so the clip arrives with no name of its own. The time is part of the default
    /// because two double kills in one evening would otherwise collide, and it is the clip's own
    /// recorded moment rather than the run's, so reprocessing renames nothing.
    /// </summary>
    public string ClipFileNameTemplate { get; set; } = Highlights.ClipNaming.DefaultTemplate;

    /// <summary>
    /// YouTube title for a clip whose highlight is known. Deliberately carries no timestamp: the
    /// moment is sent as the video's recording date, where YouTube can use it, instead of being
    /// spent on title characters.
    /// </summary>
    public string YouTubeClipTitleTemplate { get; set; } = Youtube.TitleTemplate.DefaultClipTitle;

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
