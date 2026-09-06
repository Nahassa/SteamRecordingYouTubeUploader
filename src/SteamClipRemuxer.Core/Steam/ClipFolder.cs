using SteamClipRemuxer.Core.Execution;

namespace SteamClipRemuxer.Core.Steam;

/// <summary>
/// One of Steam's own clip folders, with everything needed to turn it into a file.
///
/// The layout Steam writes:
///
///   clip_730_20260828_221805/
///     clip.pb                       where this clip sits in the session
///     thumbnail.jpg
///     timelines/timeline_...json    the whole session's events
///     video/bg_730_.../             DASH segments plus session.mpd
/// </summary>
public sealed record ClipFolder
{
    public required string Path { get; init; }
    public required ClipManifest Manifest { get; init; }

    /// <summary>The session timeline this clip indexes into, when it is present.</summary>
    public string? TimelinePath { get; init; }

    /// <summary>Folder holding the DASH segments.</summary>
    public required string VideoPath { get; init; }

    public string? ThumbnailPath { get; init; }

    public string FolderName => System.IO.Path.GetFileName(Path.TrimEnd(
        System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar));

    /// <summary>Steam keeps unexported clips here, inside the folder exports are written to.</summary>
    public const string ClipsFolderName = "clips";

    /// <summary>
    /// Finds the folder actually holding clip_* directories.
    ///
    /// Accepts either the recording folder, which has a "clips" subfolder, or that subfolder
    /// itself. Pointing the app straight at "clips" is the obvious thing to try, and silently
    /// finding nothing gives no clue which of the two was wanted.
    /// </summary>
    public static string? ResolveClipsRoot(string inputFolder)
    {
        if (string.IsNullOrWhiteSpace(inputFolder) || !Directory.Exists(inputFolder)) return null;

        string nested = System.IO.Path.Combine(inputFolder, ClipsFolderName);
        if (Directory.Exists(nested) && HasClipFolders(nested)) return nested;

        return HasClipFolders(inputFolder) ? inputFolder : null;
    }

    private static bool HasClipFolders(string folder) =>
        Directory.EnumerateDirectories(folder, "clip_*").Any();

    /// <summary>
    /// Every readable clip folder under <paramref name="inputFolder"/>, newest first.
    /// A folder whose clip.pb cannot be parsed is reported and skipped rather than failing
    /// the whole scan, since one unreadable clip should not hide the rest.
    /// </summary>
    public static IReadOnlyList<ClipFolder> Discover(string inputFolder, IPipelineLog? log = null)
    {
        log ??= NullPipelineLog.Instance;

        string? root = ResolveClipsRoot(inputFolder);
        if (root is null) return Array.Empty<ClipFolder>();

        var found = new List<ClipFolder>();

        foreach (string directory in Directory.EnumerateDirectories(root, "clip_*")
                     .OrderBy(d => d, StringComparer.OrdinalIgnoreCase))
        {
            ClipFolder? clip = TryRead(directory, log);
            if (clip is not null) found.Add(clip);
        }

        // Newest first: the clip just saved is the one most likely to be wanted.
        return found
            .OrderByDescending(c => c.Manifest.SessionStart + c.Manifest.StartInSession)
            .ToList();
    }

    public static ClipFolder? TryRead(string directory, IPipelineLog? log = null)
    {
        log ??= NullPipelineLog.Instance;
        string name = System.IO.Path.GetFileName(directory);

        string manifestPath = System.IO.Path.Combine(directory, "clip.pb");
        if (!File.Exists(manifestPath))
        {
            log.Warning($"  {name}: no clip.pb, skipped");
            return null;
        }

        ClipManifest? manifest = ClipManifest.Load(manifestPath);
        if (manifest is null)
        {
            log.Warning($"  {name}: clip.pb could not be read, skipped");
            return null;
        }

        string? video = ResolveVideoFolder(directory, manifest);
        if (video is null)
        {
            log.Warning($"  {name}: no video segments found, skipped");
            return null;
        }

        return new ClipFolder
        {
            Path = directory,
            Manifest = manifest,
            VideoPath = video,
            TimelinePath = ResolveTimeline(directory, manifest),
            ThumbnailPath = Exists(System.IO.Path.Combine(directory, "thumbnail.jpg")),
        };
    }

    /// <summary>
    /// The timeline named by clip.pb. Steam writes it without an extension there, and a clip
    /// carries its session's copy, so the named file is looked for first and any single
    /// timeline in the folder accepted as a fallback.
    /// </summary>
    private static string? ResolveTimeline(string directory, ClipManifest manifest)
    {
        string folder = System.IO.Path.Combine(directory, "timelines");
        if (!Directory.Exists(folder)) return null;

        string named = System.IO.Path.Combine(folder, manifest.TimelineFile + ".json");
        if (File.Exists(named)) return named;

        string[] all = Directory.GetFiles(folder, "*.json");
        return all.Length == 1 ? all[0] : null;
    }

    /// <summary>
    /// The folder of DASH segments. clip.pb names it, but a clip with only one video folder is
    /// unambiguous, so a rename or a missing name does not make the clip unusable.
    /// </summary>
    private static string? ResolveVideoFolder(string directory, ClipManifest manifest)
    {
        string folder = System.IO.Path.Combine(directory, "video");
        if (!Directory.Exists(folder)) return null;

        if (manifest.VideoSessionFolder.Length > 0)
        {
            string named = System.IO.Path.Combine(folder, manifest.VideoSessionFolder);
            if (Directory.Exists(named)) return named;
        }

        string[] all = Directory.GetDirectories(folder);
        return all.Length == 1 ? all[0] : null;
    }

    private static string? Exists(string path) => File.Exists(path) ? path : null;
}
