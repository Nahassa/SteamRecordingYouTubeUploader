using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using SteamClipRemuxer.Core.Configuration;
using SteamClipRemuxer.Core.Planning;
using SteamClipRemuxer.Core.Probing;

namespace SteamClipRemuxer.Core.Execution;

/// <summary>What an upscale produced, or why it produced nothing.</summary>
public sealed record UpscaleResult
{
    /// <summary>The scaled file, or null when the lossless one should be uploaded instead.</summary>
    public string? OutputPath { get; init; }

    /// <summary>Why nothing was produced, when nothing was. Never an error the caller should throw on.</summary>
    public string? Skipped { get; init; }

    public TimeSpan Elapsed { get; init; }

    /// <summary>Structural similarity against the source, measured after scaling back down.</summary>
    public double? Ssim { get; init; }

    public static UpscaleResult Nothing(string why) => new() { Skipped = why };
}

/// <summary>
/// Scales a finished clip up so YouTube will build a higher ladder rung for it.
///
/// This is the only re-encode in a pipeline whose whole character is that it never re-encodes, and
/// it is confined accordingly: the result is a temporary file, uploaded and then deleted, and the
/// file kept on disk stays the lossless one. Nothing here writes into the output folder.
///
/// Every value in the command comes from the probe. Nothing assumes 16:9, 8-bit, one audio track,
/// limited range, or that NVENC exists - each of those assumptions has been a real defect here.
/// </summary>
public sealed class ClipUpscaleService
{
    private readonly IProcessRunner _runner;
    private readonly IMediaProbe _probe;
    private readonly EncoderCapability _capability;
    private readonly IPipelineLog _log;
    private readonly string _ffmpegPath;

    /// <summary>How far the scaled duration may drift from the source's before it is a failure.</summary>
    public static readonly TimeSpan DurationTolerance = TimeSpan.FromSeconds(0.5);

    /// <summary>
    /// The structural-similarity floor, measured after scaling the output back to the source's own
    /// geometry. Chosen against a measured round-trip of 47.2 dB, which is far above this; a result
    /// below it means the resample or the encode did something other than what was asked.
    /// </summary>
    public const double MinimumSsim = 0.98;

    public ClipUpscaleService(
        IProcessRunner runner,
        IMediaProbe probe,
        EncoderCapability capability,
        IPipelineLog? log = null,
        string ffmpegPath = "ffmpeg")
    {
        _runner = runner;
        _probe = probe;
        _capability = capability;
        _log = log ?? NullPipelineLog.Instance;
        _ffmpegPath = ffmpegPath;
    }

    // ---------------------------------------------------------------- colour

    /// <summary>
    /// How zscale spells a range, which is not how the output tag spells it.
    ///
    /// zscale takes full/limited and -color_range takes pc/tv. Mixing the two vocabularies is
    /// precisely how full-range footage comes out tagged limited, which crushes every black in
    /// the picture - a far bigger loss than anything the encoder choice is worth.
    /// </summary>
    public static string ZscaleRange(string? colorRange) =>
        IsFullRange(colorRange) ? "full" : "limited";

    /// <summary>How -color_range spells the same thing.</summary>
    public static string TagRange(string? colorRange) => IsFullRange(colorRange) ? "pc" : "tv";

    private static bool IsFullRange(string? colorRange) =>
        string.Equals(colorRange, "pc", StringComparison.OrdinalIgnoreCase)
        || string.Equals(colorRange, "full", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// A colour tag from the probe, falling back to bt709 when ffprobe reported nothing usable.
    ///
    /// Leaving the field unspecified is not an option: it pushes the guess onto every player, and
    /// the same file then looks different in two apps. The assumption is logged by the caller
    /// rather than made quietly.
    /// </summary>
    public static string ColorTag(string? probed) =>
        string.IsNullOrWhiteSpace(probed)
        || probed.Equals("unknown", StringComparison.OrdinalIgnoreCase)
        || probed.Equals("reserved", StringComparison.OrdinalIgnoreCase)
            ? "bt709"
            : probed;

    /// <summary>True when any colour tag had to be assumed rather than read.</summary>
    public static bool ColorWasAssumed(SourceMedia source) =>
        ColorTag(source.ColorPrimaries) != source.ColorPrimaries
        || ColorTag(source.ColorTransfer) != source.ColorTransfer
        || ColorTag(source.ColorSpace) != source.ColorSpace;

    // ---------------------------------------------------------------- filter

    /// <summary>
    /// The scaling filter.
    ///
    /// zscale is preferred: it is colour-aware and works at higher internal precision than
    /// swscale. The in and out colour properties are both stated, so the resample happens in a
    /// known space instead of whatever the filter guesses. The chain ends in the encoder's own
    /// pixel format - and never in nv12, which is 8-bit and would throw away the precision the
    /// 10-bit output exists for.
    /// </summary>
    public static string BuildFilter(
        SourceMedia source, UpscaleTarget target, UpscaleEncoder encoder, bool zscaleAvailable)
    {
        string range = ZscaleRange(source.ColorRange);
        string primaries = ColorTag(source.ColorPrimaries);
        string transfer = ColorTag(source.ColorTransfer);
        string matrix = ColorTag(source.ColorSpace);

        if (!zscaleAvailable)
        {
            // swscale fallback. Lower precision and not colour-aware, so the tags on the output
            // carry the whole burden of describing the result - which is why they are emitted
            // unconditionally in BuildArguments rather than only on the zscale path.
            return $"scale={target.Width}:{target.Height}"
                + ":flags=lanczos+accurate_rnd+full_chroma_int"
                + $",format={encoder.PixelFormat}";
        }

        return $"zscale=w={target.Width}:h={target.Height}"
            + ":f=lanczos:dither=error_diffusion"
            + $":rin={range}:r={range}"
            + $":pin={primaries}:p={primaries}"
            + $":tin={transfer}:t={transfer}"
            + $":min={matrix}:m={matrix}"
            + $",format={encoder.PixelFormat}";
    }

    // ------------------------------------------------------------- arguments

    /// <summary>
    /// The whole command. Static and pure, like the other services' builders, so what runs is
    /// covered by tests instead of discovered during an upload - and built as a list, so a path
    /// containing a quote or a percent sign is not a hazard.
    /// </summary>
    public static IReadOnlyList<string> BuildArguments(
        SourceMedia source,
        UpscaleTarget target,
        UpscaleEncoder encoder,
        string outputPath,
        bool zscaleAvailable = true)
    {
        if (!target.ShouldScale)
            throw new InvalidOperationException($"Nothing to scale: {target.Skip}.");

        var args = new List<string>
        {
            "-hide_banner", "-loglevel", "error", "-nostats", "-y",
            "-i", source.FilePath,
            "-vf", BuildFilter(source, target, encoder, zscaleAvailable),

            // Every stream, and the metadata with them. Steam captures routinely carry a separate
            // microphone track, and a missing -map would drop it without a word.
            "-map", "0", "-map_metadata", "0", "-map_chapters", "0",

            "-c:v", encoder.Name,
        };

        args.AddRange(encoder.Options);

        // Tagged explicitly and always. Filters do not reliably carry colour across a format
        // conversion, so the only reliable statement of what the output is, is this one.
        args.AddRange(new[]
        {
            "-color_primaries", ColorTag(source.ColorPrimaries),
            "-color_trc", ColorTag(source.ColorTransfer),
            "-colorspace", ColorTag(source.ColorSpace),
            "-color_range", TagRange(source.ColorRange),
        });

        if (source.ChromaLocation is { Length: > 0 } chroma
            && !chroma.Equals("unspecified", StringComparison.OrdinalIgnoreCase))
        {
            args.Add("-chroma_sample_location");
            args.Add(chroma);
        }

        // Both encoders here produce HEVC, and without this tag QuickTime and several browsers
        // refuse to play it in MP4.
        args.AddRange(new[] { "-tag:v", "hvc1" });

        // Audio is copied, never re-encoded. Leaving -c:a off would let the container default
        // re-encode it to AAC on every upload, a generation loss invisible in the log.
        args.AddRange(new[] { "-c:a", "copy", "-c:s", "copy" });

        // Steam's capture is variable frame rate; forcing constant duplicates frames and drifts
        // the audio against the picture.
        args.AddRange(new[] { "-fps_mode", "passthrough" });

        args.AddRange(new[] { "-movflags", "+faststart" });

        args.Add(outputPath);
        return args;
    }

    // ------------------------------------------------------------------ SSIM

    /// <summary>
    /// Compares the scaled output against the source, having scaled it back down to the source's
    /// own geometry so the two are the same shape.
    ///
    /// VMAF would be the better metric and is not available here: it is absent from this FFmpeg
    /// build, from apt and from PyPI, and the download hosts are blocked. SSIM is what can
    /// actually be measured, and the floor was set against a measured round-trip rather than
    /// guessed.
    /// </summary>
    public static IReadOnlyList<string> BuildSsimArguments(
        string scaledPath, SourceMedia source) => new[]
    {
        "-hide_banner", "-loglevel", "info", "-nostats",
        "-i", scaledPath,
        "-i", source.FilePath,
        "-lavfi",
        $"[0:v]scale={source.Width}:{source.Height}:flags=lanczos,setpts=PTS-STARTPTS[d];"
        + "[1:v]setpts=PTS-STARTPTS[r];[d][r]ssim",
        "-f", "null", "-",
    };

    private static readonly Regex SsimAll = new(
        @"All:\s*(?<value>[0-9]*\.?[0-9]+)", RegexOptions.Compiled);

    /// <summary>
    /// Reads the overall figure out of the ssim filter's summary line, which it writes to stderr
    /// as "SSIM Y:0.99 U:0.99 V:0.99 All:0.99 (20.1)". The last match wins, so a per-frame log
    /// cannot be mistaken for the summary.
    /// </summary>
    public static double? ParseSsim(string ffmpegOutput)
    {
        MatchCollection matches = SsimAll.Matches(ffmpegOutput);
        if (matches.Count == 0) return null;

        return double.TryParse(
            matches[^1].Groups["value"].Value,
            NumberStyles.Float, CultureInfo.InvariantCulture, out double value)
            ? value
            : null;
    }

    // ---------------------------------------------------------- verification

    /// <summary>
    /// Why the scaled file cannot be trusted, or null when it can.
    ///
    /// The lossless paths verify by comparing the video payload's md5, which is meaningless across
    /// a re-encode - so it is replaced here rather than dropped. Exit code zero on its own has
    /// never been evidence in this codebase.
    /// </summary>
    public static string? Wrong(SourceMedia source, SourceMedia scaled, UpscaleTarget target, double? ssim)
    {
        if (scaled.Width != target.Width || scaled.Height != target.Height)
            return $"it came out {scaled.Width}x{scaled.Height} rather than {target.Width}x{target.Height}";

        if (scaled.Width <= source.Width || scaled.Height <= source.Height)
            return "it is no larger than the source, so the re-encode bought nothing";

        var drift = TimeSpan.FromSeconds(Math.Abs(scaled.DurationSeconds - source.DurationSeconds));
        if (drift > DurationTolerance)
            return $"it runs {drift.TotalSeconds:0.##}s away from the source";

        if (source.FrameCount is { } wanted && scaled.FrameCount is { } got && wanted != got)
            return $"it holds {got} frames against the source's {wanted}";

        if (scaled.DisplayAspect != source.DisplayAspect)
            return $"it displays at {scaled.DisplayAspect} rather than {source.DisplayAspect}";

        // The one most likely to go wrong, and the one that matters most. NVENC has a long
        // history of tagging limited range whatever it was handed, and a range flip crushes every
        // black in the picture.
        if (!SameTag(TagRange(source.ColorRange), TagRange(scaled.ColorRange)))
            return $"its colour range is {Show(scaled.ColorRange)}, not the source's {Show(source.ColorRange)}";

        if (!SameTag(ColorTag(source.ColorPrimaries), ColorTag(scaled.ColorPrimaries)))
            return $"its primaries are {Show(scaled.ColorPrimaries)}, not {Show(source.ColorPrimaries)}";

        if (!SameTag(ColorTag(source.ColorTransfer), ColorTag(scaled.ColorTransfer)))
            return $"its transfer is {Show(scaled.ColorTransfer)}, not {Show(source.ColorTransfer)}";

        if (!SameTag(ColorTag(source.ColorSpace), ColorTag(scaled.ColorSpace)))
            return $"its colour space is {Show(scaled.ColorSpace)}, not {Show(source.ColorSpace)}";

        if (scaled.BitDepth < source.BitDepth)
            return $"it is {scaled.BitDepth}-bit against the source's {source.BitDepth}";

        if (!SameTag(source.AudioCodec, scaled.AudioCodec))
            return $"its audio is {Show(scaled.AudioCodec)}, not the source's {Show(source.AudioCodec)}";

        if (source.AudioChannels != scaled.AudioChannels)
            return $"its audio has {scaled.AudioChannels} channel(s) against {source.AudioChannels}";

        if (scaled.Streams.Count != source.Streams.Count)
            return $"it carries {scaled.Streams.Count} stream(s) against the source's {source.Streams.Count}";

        if (ssim is null) return "its similarity to the source could not be measured";

        if (ssim < MinimumSsim)
            return $"it only matches the source to {ssim:0.####} SSIM, under the {MinimumSsim} floor";

        return null;
    }

    private static bool SameTag(string? a, string? b) =>
        string.Equals(a ?? "", b ?? "", StringComparison.OrdinalIgnoreCase);

    private static string Show(string? tag) => string.IsNullOrEmpty(tag) ? "unspecified" : tag;

    // --------------------------------------------------------------- running

    /// <summary>
    /// Scales one finished file into <paramref name="workspace"/>, or returns a reason it did not.
    ///
    /// Never throws for a bad outcome. A failed upscale must cost the upscale, not the upload: the
    /// caller falls back to the lossless file, which is the one that was going to be kept anyway.
    /// </summary>
    public async Task<UpscaleResult> UpscaleAsync(
        string filePath,
        string workspace,
        AppSettings settings,
        CancellationToken ct = default)
    {
        if (settings.YouTubeUpscale == YouTubeUpscale.Off) return UpscaleResult.Nothing("upscaling is off");

        try
        {
            SourceMedia source = await _probe.ProbeAsync(filePath, ct).ConfigureAwait(false);

            UpscaleTarget target = UpscaleTarget.For(source, settings.YouTubeUpscale);
            if (!target.ShouldScale) return UpscaleResult.Nothing(target.Skip!);

            bool zscale = await _capability
                .CanFilterAsync(
                    $"zscale=w={target.Width}:h={target.Height}:f=lanczos:dither=error_diffusion",
                    source.PixelFormat, ct)
                .ConfigureAwait(false);

            if (!zscale)
            {
                _log.Warning(
                    $"  zscale cannot take {source.PixelFormat} on this FFmpeg; "
                    + "scaling with swscale instead.");
            }

            if (ColorWasAssumed(source))
            {
                _log.Warning(
                    "  the source does not state all of its colour properties; "
                    + "assuming bt709 for the ones it leaves out.");
            }

            UpscaleEncoder encoder = await ResolveEncoderAsync(settings, ct).ConfigureAwait(false);

            UpscaleResult attempt = await AttemptAsync(
                    source, target, encoder, workspace, zscale, ct)
                .ConfigureAwait(false);

            // A hardware result that was rejected is worth one go in software. NVENC's usual
            // failure is a mis-tagged colour range, which the software path does not have - and
            // retrying with the encoder rather than with an edited settings object keeps the
            // resolved encoder the single input to the command.
            if (attempt.OutputPath is null && encoder.IsHardware)
            {
                _log.Warning(
                    $"  the GPU-scaled file was not used because {attempt.Skipped}; "
                    + $"retrying with {UpscaleEncoder.Software.Name}.");

                attempt = await AttemptAsync(
                        source, target, UpscaleEncoder.Software, workspace, zscale, ct)
                    .ConfigureAwait(false);
            }

            return attempt;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                       or InvalidMediaException or ProcessLaunchException)
        {
            return UpscaleResult.Nothing(ex.Message);
        }
    }

    /// <summary>One encode with one resolved encoder, verified before it is handed back.</summary>
    private async Task<UpscaleResult> AttemptAsync(
        SourceMedia source,
        UpscaleTarget target,
        UpscaleEncoder encoder,
        string workspace,
        bool zscaleAvailable,
        CancellationToken ct)
    {
        var stopwatch = Stopwatch.StartNew();

        Directory.CreateDirectory(workspace);
        string outputPath = Path.Combine(
            workspace,
            Path.GetFileNameWithoutExtension(source.FilePath)
            + $" {target.Height}p-{encoder.Name}.mp4");

        // Never write output over input. These paths are built to differ, but the check is free
        // and the alternative is destroying the recording.
        if (SamePath(source.FilePath, outputPath))
            return UpscaleResult.Nothing("the scaled file would overwrite the original");

        _log.Info(
            $"  scaling {source.Width}x{source.Height} to {target} with {encoder.Name}"
            + $" ({source.BitDepth}-bit in, 10-bit out)...");

        IReadOnlyList<string> args = BuildArguments(source, target, encoder, outputPath, zscaleAvailable);
        ProcessResult result = await _runner.RunAsync(_ffmpegPath, args, ct).ConfigureAwait(false);

        if (!result.Succeeded)
        {
            TryDelete(outputPath);
            return UpscaleResult.Nothing(
                $"FFmpeg failed ({result.ExitCode}): {Tail(result.StandardError)}");
        }

        if (!File.Exists(outputPath) || new FileInfo(outputPath).Length == 0)
            return UpscaleResult.Nothing("FFmpeg reported success but wrote no output");

        SourceMedia scaled = await _probe.ProbeAsync(outputPath, ct).ConfigureAwait(false);
        double? ssim = await MeasureSsimAsync(outputPath, source, ct).ConfigureAwait(false);

        if (Wrong(source, scaled, target, ssim) is { } wrong)
        {
            TryDelete(outputPath);
            return UpscaleResult.Nothing($"it was rejected: {wrong}");
        }

        _log.Success(
            $"  scaled to {target} ({ssim:0.####} SSIM, {stopwatch.Elapsed.TotalSeconds:0.#}s)");

        return new UpscaleResult
        {
            OutputPath = outputPath,
            Elapsed = stopwatch.Elapsed,
            Ssim = ssim,
        };
    }

    /// <summary>
    /// Which encoder will actually run. The hardware path is taken only after a trial encode of
    /// the exact option set succeeds, and a substitution is always logged - a fallback nobody can
    /// see is indistinguishable from a bug.
    /// </summary>
    private async Task<UpscaleEncoder> ResolveEncoderAsync(AppSettings settings, CancellationToken ct)
    {
        if (!settings.UseHardwareEncoder) return UpscaleEncoder.Software;

        bool can = await _capability
            .CanEncodeAsync(UpscaleEncoder.Nvenc.Name, UpscaleEncoder.Nvenc.Options, ct)
            .ConfigureAwait(false);

        if (can) return UpscaleEncoder.Nvenc;

        _log.Warning(
            $"  {UpscaleEncoder.Nvenc.Name} cannot run these options on this machine; "
            + $"scaling with {UpscaleEncoder.Software.Name} instead.");

        return UpscaleEncoder.Software;
    }

    private async Task<double?> MeasureSsimAsync(
        string scaledPath, SourceMedia source, CancellationToken ct)
    {
        ProcessResult result = await _runner
            .RunAsync(_ffmpegPath, BuildSsimArguments(scaledPath, source), ct)
            .ConfigureAwait(false);

        // The filter writes its summary to stderr; stdout is checked too so a build that routes
        // it differently is not reported as unmeasurable.
        return ParseSsim(result.StandardError) ?? ParseSsim(result.StandardOutput);
    }

    private static bool SamePath(string a, string b) =>
        string.Equals(
            Path.GetFullPath(a), Path.GetFullPath(b),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private static string Tail(string text)
    {
        string[] lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        return lines.Length == 0 ? "no output" : lines[^1].Trim();
    }
}
