using SteamClipRemuxer.Core.Configuration;
using SteamClipRemuxer.Core.Probing;

namespace SteamClipRemuxer.Core.Planning;

/// <summary>
/// The pixel dimensions an upscale should produce, or the reason there is nothing to do.
///
/// Pure and separate from the encode so the geometry is covered by tests rather than discovered
/// in an upload. Every number here comes from the probe; nothing assumes 16:9, and nothing
/// computes a ratio from a resolution the file does not have - that mistake once produced a
/// reassuring log line naming a size the source had never been.
/// </summary>
public sealed record UpscaleTarget
{
    public required int Width { get; init; }
    public required int Height { get; init; }

    /// <summary>Why no scaling is wanted, or null when <see cref="Width"/> and <see cref="Height"/> apply.</summary>
    public string? Skip { get; init; }

    public bool ShouldScale => Skip is null;

    private static UpscaleTarget Nothing(string why) =>
        new() { Width = 0, Height = 0, Skip = why };

    /// <summary>Lines each setting aims for, or null when it aims for nothing.</summary>
    public static int? LinesFor(YouTubeUpscale upscale) => upscale switch
    {
        YouTubeUpscale.To1080p => 1080,
        YouTubeUpscale.To1440p => 1440,
        _ => null,
    };

    /// <summary>
    /// Works out the target for a source, in the source's own display aspect.
    ///
    /// The width follows from the height and the measured display aspect, so a 16:9 file reaches
    /// 1920x1080 and a 4:3 one reaches 1440x1080 - both correct, neither distorted. The result is
    /// square-pixelled, which is why nothing downstream needs to tag an aspect afterwards: the
    /// pixels genuinely are the shape the picture is.
    /// </summary>
    public static UpscaleTarget For(SourceMedia source, YouTubeUpscale upscale)
    {
        if (LinesFor(upscale) is not { } lines) return Nothing("upscaling is off");

        int height = Even(lines);
        int width = Even((int)Math.Round(height * source.DisplayAspect.Value));

        // A resample is lossy, so resampling to reach a size the file already has or exceeds is
        // pure degradation. Both axes have to grow for this to be worth doing.
        if (width <= source.Width || height <= source.Height)
        {
            return Nothing(
                $"{source.Width}x{source.Height} is already at or above {width}x{height}");
        }

        return new UpscaleTarget { Width = width, Height = height };
    }

    /// <summary>
    /// Rounded up to an even number. 4:2:0 chroma is subsampled by two in each axis, so an odd
    /// dimension is rejected outright by both encoders.
    /// </summary>
    private static int Even(int value) => value % 2 == 0 ? value : value + 1;

    public override string ToString() => ShouldScale ? $"{Width}x{Height}" : $"(none: {Skip})";
}
