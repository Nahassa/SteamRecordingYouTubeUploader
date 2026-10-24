namespace SteamClipRemuxer.Core.Execution;

/// <summary>
/// One encoder, with the exact options it is to be run with.
///
/// Bundled rather than left as a name plus a branch, because the resolved encoder has to be the
/// single input to argument construction. This codebase once logged "falling back to hevc_nvenc"
/// and then emitted av1_nvenc anyway, because the argument builder re-read a mode flag instead of
/// the decision. A caller here cannot make that mistake: there is nothing else to read.
/// </summary>
public sealed record UpscaleEncoder
{
    public required string Name { get; init; }

    /// <summary>Everything after -c:v, in order.</summary>
    public required IReadOnlyList<string> Options { get; init; }

    /// <summary>The pixel format the filter chain must end in for this encoder.</summary>
    public required string PixelFormat { get; init; }

    public required bool IsHardware { get; init; }

    /// <summary>
    /// The default and the fallback.
    ///
    /// -preset fast rather than slow on purpose: the whole encoder field measured 0.19 dB apart
    /// across a 30x range of encode times on this content, so paying for a slower preset buys
    /// nothing that can be measured. The -x265-params are free at any preset and are worth having
    /// anyway - no-sao especially, because SAO smears the UI text and crosshair that fill a game
    /// capture. No -tune: the psnr and ssim tunes actively degrade what a person sees.
    /// </summary>
    public static readonly UpscaleEncoder Software = new()
    {
        Name = "libx265",
        PixelFormat = "yuv420p10le",
        IsHardware = false,
        Options = new[]
        {
            "-preset", "fast",
            "-crf", "18",
            "-profile:v", "main10",
            "-pix_fmt", "yuv420p10le",
            "-x265-params",
            "aq-mode=3:aq-strength=1.0:psy-rd=2.0:psy-rdoq=1.0:rdoq-level=2"
            + ":no-sao=1:strong-intra-smoothing=0",
        },
    };

    /// <summary>
    /// The opt-in hardware path. One fixed target - HEVC Main10 - so a 5080 and a 3080 emit the
    /// same format and differ only in speed. Choosing AV1 on the cards that have it would rebuild
    /// exactly the per-generation option matrix this project exists without, to buy 0.19 dB.
    ///
    /// -b:v 0 is required, not cosmetic: with -rc vbr and no explicit zero, some builds apply a
    /// default bitrate cap and ignore -cq entirely. That is the usual reason NVENC gets called
    /// bad. p010le is NVENC's 10-bit surface format; passing yuv420p10le here is an error, and
    /// nv12 would be 8-bit and silently throw away the precision this path exists to keep.
    /// </summary>
    public static readonly UpscaleEncoder Nvenc = new()
    {
        Name = "hevc_nvenc",
        PixelFormat = "p010le",
        IsHardware = true,
        Options = new[]
        {
            "-preset", "p7",
            "-tune", "hq",
            "-rc", "vbr",
            "-cq", "20",
            "-b:v", "0",
            "-multipass", "fullres",
            "-rc-lookahead", "32",
            "-spatial-aq", "1",
            "-aq-strength", "8",
            "-temporal-aq", "1",
            "-bf", "4",
            "-b_ref_mode", "middle",
            "-g", "250",
            "-profile:v", "main10",
            "-pix_fmt", "p010le",
        },
    };
}
