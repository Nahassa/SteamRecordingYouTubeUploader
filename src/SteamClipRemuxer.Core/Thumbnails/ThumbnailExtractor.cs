using System.Globalization;
using SteamClipRemuxer.Core.Execution;
using SteamClipRemuxer.Core.Probing;

namespace SteamClipRemuxer.Core.Thumbnails;

public interface IThumbnailExtractor
{
    Task<byte[]> ExtractAsync(SourceMedia media, double atFraction = 0.5, CancellationToken ct = default);
}

/// <summary>
/// Grabs a single frame rendered at the clip's DISPLAY aspect, so the preview shows the
/// stretched result rather than the stored 4:3 pixels.
/// </summary>
/// <remarks>
/// Returns the encoded bytes rather than a path or an Image. The previous implementation
/// handed back a System.Drawing.Image created with Image.FromFile, which held a lock on the
/// temp file and defeated its own delayed cleanup, leaking a JPEG per preview.
/// </remarks>
public sealed class ThumbnailExtractor : IThumbnailExtractor
{
    private readonly IProcessRunner _runner;
    private readonly string _ffmpegPath;

    public ThumbnailExtractor(IProcessRunner runner, string ffmpegPath = "ffmpeg")
    {
        _runner = runner;
        _ffmpegPath = ffmpegPath;
    }

    /// <summary>
    /// Renders at display aspect. For 1280x960 with SAR 4:3 that is 1706x960, which is what
    /// the clip actually looks like in a player.
    /// </summary>
    public static (int width, int height) DisplayDimensions(SourceMedia media)
    {
        // Integer maths, and truncating rather than rounding, so this matches what ffmpeg's
        // own `scale=iw*sar:ih` produces: 1280 * 4/3 gives 1706, not 1707.
        long width = (long)media.Width * media.SampleAspect.Numerator / media.SampleAspect.Denominator;
        if (width % 2 != 0) width--;                     // scalers want even dimensions
        return ((int)Math.Max(width, 2), media.Height);
    }

    public static IReadOnlyList<string> BuildArguments(SourceMedia media, double seekSeconds, string outputPath)
    {
        (int w, int h) = DisplayDimensions(media);
        return new[]
        {
            "-y",
            "-ss", seekSeconds.ToString("0.000", CultureInfo.InvariantCulture),
            "-i", media.FilePath,
            "-frames:v", "1",
            "-vf", $"scale={w}:{h}:flags=lanczos",
            "-q:v", "3",
            outputPath,
        };
    }

    public async Task<byte[]> ExtractAsync(
        SourceMedia media, double atFraction = 0.5, CancellationToken ct = default)
    {
        double seek = media.DurationSeconds > 0
            ? Math.Clamp(media.DurationSeconds * atFraction, 0, Math.Max(0, media.DurationSeconds - 0.1))
            : 0;

        string temp = Path.Combine(Path.GetTempPath(), $"sclip-thumb-{Guid.NewGuid():N}.jpg");
        try
        {
            ProcessResult result = await _runner
                .RunAsync(_ffmpegPath, BuildArguments(media, seek, temp), ct)
                .ConfigureAwait(false);

            if (!result.Succeeded || !File.Exists(temp))
                throw new ThumbnailException($"Could not extract a frame: {result.StandardError.Trim()}");

            return await File.ReadAllBytesAsync(temp, ct).ConfigureAwait(false);
        }
        finally
        {
            // Nothing holds the file open, so this always succeeds.
            try { if (File.Exists(temp)) File.Delete(temp); } catch { /* best effort */ }
        }
    }
}

public sealed class ThumbnailException : Exception
{
    public ThumbnailException(string message) : base(message) { }
}
