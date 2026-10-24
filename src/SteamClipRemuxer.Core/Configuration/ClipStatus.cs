namespace SteamClipRemuxer.Core.Configuration;

/// <summary>
/// What has already happened to a clip, as the list's columns show it.
///
/// Derived rather than stored, so the answer cannot drift from the log. Pure apart from the
/// file-existence check, which is injectable for the same reason.
/// </summary>
public sealed record ClipStatus
{
    /// <summary>An output file was written.</summary>
    public required bool Remuxed { get; init; }

    /// <summary>It reached YouTube.</summary>
    public required bool Uploaded { get; init; }

    /// <summary>Written but never uploaded: the work a later upload-only run would pick up.</summary>
    public required bool PendingUpload { get; init; }

    /// <summary>Written, still waiting to be uploaded, and no longer where the log says it is.</summary>
    public required bool OutputMissing { get; init; }

    /// <summary>The output was cut down to the clip's fights rather than being the whole clip.</summary>
    public required bool CutToHighlights { get; init; }

    /// <summary>Nothing has been done with this clip yet.</summary>
    public bool Untouched => !Remuxed && !Uploaded;

    public static readonly ClipStatus New = new()
    {
        Remuxed = false,
        Uploaded = false,
        PendingUpload = false,
        OutputMissing = false,
        CutToHighlights = false,
    };

    /// <param name="exists">
    /// How to test for the output file. Defaults to the filesystem; supplied by tests so the
    /// rules can be checked without writing anything.
    /// </param>
    public static ClipStatus For(ProcessedClip? entry, Func<string, bool>? exists = null)
    {
        if (entry is null) return New;

        exists ??= File.Exists;

        bool remuxed = entry.RemuxedAt is not null;
        bool uploaded = entry.UploadedAt is not null;

        return new ClipStatus
        {
            Remuxed = remuxed,
            Uploaded = uploaded,
            // Uploading is the finish line, so an uploaded clip is never also pending.
            PendingUpload = remuxed && !uploaded,
            // Only asked about a clip still waiting to be uploaded. A successful upload files the
            // output under uploaded/, which leaves the recorded path empty by design - reporting
            // that as a missing file would flag every finished clip.
            OutputMissing = remuxed
                && !uploaded
                && entry.OutputPath.Length > 0
                && !exists(entry.OutputPath),
            CutToHighlights = entry.CutToHighlights,
        };
    }
}
