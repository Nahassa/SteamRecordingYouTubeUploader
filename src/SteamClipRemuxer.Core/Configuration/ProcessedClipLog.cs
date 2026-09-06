using System.Text.Json;
using System.Text.Json.Serialization;

namespace SteamClipRemuxer.Core.Configuration;

/// <summary>One clip the tool has already handled.</summary>
public sealed record ProcessedClip
{
    /// <summary>The clip's content identity. See ClipManifest.Id.</summary>
    public required string Id { get; init; }

    /// <summary>Folder it came from, recorded so the log is legible to a human reading it.</summary>
    public string ClipFolder { get; init; } = string.Empty;

    /// <summary>Title used, so a duplicate report can say what the clip was.</summary>
    public string Title { get; init; } = string.Empty;

    public DateTimeOffset? RemuxedAt { get; init; }
    public DateTimeOffset? UploadedAt { get; init; }

    /// <summary>Set once the clip reached YouTube, so the log doubles as a record of what is up there.</summary>
    public string? YouTubeVideoId { get; init; }
}

/// <summary>
/// Remembers which clips have already been processed, so Steam's own clip list can be left
/// alone. Without this the only way to avoid uploading the same highlight twice is to delete
/// clips inside Steam, which throws away the source to protect the destination.
///
/// Remux and upload are recorded separately on purpose. A clip that was remuxed while upload
/// was switched off is not "done" for a later run that does upload, and treating it as done
/// would silently skip a video the user does want on YouTube.
/// </summary>
public sealed class ProcessedClipLog
{
    private readonly Dictionary<string, ProcessedClip> _entries;

    public ProcessedClipLog() : this(Array.Empty<ProcessedClip>()) { }

    public ProcessedClipLog(IEnumerable<ProcessedClip> entries)
    {
        _entries = entries
            .Where(e => !string.IsNullOrWhiteSpace(e.Id))
            .GroupBy(e => e.Id, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Last(), StringComparer.Ordinal);
    }

    public int Count => _entries.Count;

    public IReadOnlyCollection<ProcessedClip> Entries => _entries.Values;

    public ProcessedClip? Find(string id) =>
        _entries.TryGetValue(id, out ProcessedClip? entry) ? entry : null;

    /// <summary>
    /// Whether a clip can be skipped this run. When uploading, only a clip that actually
    /// reached YouTube counts; otherwise a successful remux is enough.
    /// </summary>
    public bool ShouldSkip(string id, bool uploadEnabled)
    {
        ProcessedClip? entry = Find(id);
        if (entry is null) return false;

        return uploadEnabled ? entry.UploadedAt is not null : entry.RemuxedAt is not null;
    }

    public void MarkRemuxed(string id, string clipFolder, string title)
    {
        ProcessedClip existing = Find(id) ?? new ProcessedClip { Id = id };
        _entries[id] = existing with
        {
            ClipFolder = clipFolder,
            Title = title,
            RemuxedAt = DateTimeOffset.UtcNow,
        };
    }

    public void MarkUploaded(string id, string? youTubeVideoId)
    {
        ProcessedClip existing = Find(id) ?? new ProcessedClip { Id = id };
        _entries[id] = existing with
        {
            UploadedAt = DateTimeOffset.UtcNow,
            YouTubeVideoId = youTubeVideoId,
        };
    }

    /// <summary>Drops an entry so the clip is processed again. Returns false if it was not present.</summary>
    public bool Forget(string id) => _entries.Remove(id);

    public void Clear() => _entries.Clear();

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>
    /// Loads the log, falling back to an empty one. Never throws: a log that cannot be read is
    /// a reason to redo work, not to refuse to run.
    /// </summary>
    public static ProcessedClipLog Load(string? path = null, Action<string>? onError = null)
    {
        path ??= AppPaths.ProcessedClipsFile;

        try
        {
            if (!File.Exists(path)) return new ProcessedClipLog();

            List<ProcessedClip>? entries =
                JsonSerializer.Deserialize<List<ProcessedClip>>(File.ReadAllText(path), Options);
            return new ProcessedClipLog(entries ?? new List<ProcessedClip>());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            onError?.Invoke($"Could not read the processed-clip log ({ex.Message}); starting empty.");
            return new ProcessedClipLog();
        }
    }

    /// <summary>
    /// Writes the log through a temporary file and renames over the original. A crash midway
    /// through a direct write would leave truncated JSON, and a log that fails to parse means
    /// every clip already uploaded looks new again.
    /// </summary>
    public void Save(string? path = null, Action<string>? onError = null)
    {
        path ??= AppPaths.ProcessedClipsFile;

        try
        {
            AppPaths.EnsureCreated();

            string temporary = path + ".tmp";
            List<ProcessedClip> ordered = _entries.Values
                .OrderBy(e => e.RemuxedAt ?? e.UploadedAt ?? DateTimeOffset.MinValue)
                .ToList();

            File.WriteAllText(temporary, JsonSerializer.Serialize(ordered, Options));
            File.Move(temporary, path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            onError?.Invoke($"Could not save the processed-clip log: {ex.Message}");
        }
    }
}
