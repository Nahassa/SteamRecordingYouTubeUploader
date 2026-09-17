namespace SteamClipRemuxer.Core.Execution;

/// <summary>
/// Which file goes to YouTube, which file is kept, and which one has to be cleaned up.
///
/// Three separate paths rather than one, because with upscaling on they genuinely differ and
/// conflating them would destroy the recording. The scaled copy is the only thing uploaded; the
/// lossless file is the only thing archived. A single "the file" variable serving both is exactly
/// how the archived copy would quietly become the re-encode.
/// </summary>
public sealed record UploadPayload
{
    /// <summary>The file sent to YouTube.</summary>
    public required string UploadPath { get; init; }

    /// <summary>
    /// The file moved into "uploaded" afterwards. Always the lossless one - this pipeline's whole
    /// promise is that what it leaves on disk is bit-identical to the recording, and an upscale
    /// for YouTube's benefit does not get to change that.
    /// </summary>
    public required string ArchivePath { get; init; }

    /// <summary>The temporary to delete when the upload is over, however it went. Null when there is none.</summary>
    public string? Temporary { get; init; }

    public bool WasScaled => Temporary is not null;

    /// <summary>Upload the file as built. The only shape that existed before upscaling.</summary>
    public static UploadPayload Lossless(string path) =>
        new() { UploadPath = path, ArchivePath = path };

    /// <summary>Upload the scaled copy, keep the lossless original, delete the copy afterwards.</summary>
    public static UploadPayload Scaled(string losslessPath, string scaledPath) =>
        new() { UploadPath = scaledPath, ArchivePath = losslessPath, Temporary = scaledPath };
}
