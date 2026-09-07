using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using SteamClipRemuxer.Core.Planning;
using SteamClipRemuxer.Core.Probing;

namespace SteamClipRemuxer.Core.Execution;

/// <summary>One finished file going into a compilation, with the name its chapter should carry.</summary>
public sealed record StitchInput(string Path, string Label);

/// <summary>Where a part ended up inside the finished compilation.</summary>
public sealed record StitchPart
{
    public required string Path { get; init; }
    public required string Label { get; init; }
    public required TimeSpan StartsAt { get; init; }
    public required TimeSpan Duration { get; init; }
}

public sealed record ClipStitchResult
{
    public required string OutputPath { get; init; }
    public required IReadOnlyList<StitchPart> Parts { get; init; }

    /// <summary>Inputs left out because their stream parameters did not match, each with its reason.</summary>
    public required IReadOnlyList<string> Excluded { get; init; }

    public required bool AspectOverridden { get; init; }
    public TimeSpan Elapsed { get; init; }
}

/// <summary>
/// Turns the parts of a compilation into the timestamp list YouTube reads as chapters.
/// </summary>
public static class StitchChapters
{
    /// <summary>YouTube ignores a chapter list unless there are at least this many marks.</summary>
    public const int MinimumChapters = 3;

    /// <summary>...and unless every chapter is at least this long.</summary>
    public static readonly TimeSpan MinimumChapterLength = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Whether YouTube will actually render the list as chapters. Short clips are exactly what
    /// this tool produces, so failing the test is normal rather than an error; the list is still
    /// worth publishing as plain timestamps.
    /// </summary>
    public static bool QualifyAsYouTubeChapters(IReadOnlyList<StitchPart> parts) =>
        parts.Count >= MinimumChapters
        && parts[0].StartsAt == TimeSpan.Zero
        && parts.All(p => p.Duration >= MinimumChapterLength);

    /// <summary>One "0:00  Double kill with the AK-47" line per part.</summary>
    public static string Describe(IReadOnlyList<StitchPart> parts) =>
        string.Join(Environment.NewLine, parts.Select(p => $"{Stamp(p.StartsAt)}  {p.Label}"));

    /// <summary>
    /// YouTube requires "0:00" rather than "00:00" for the opening chapter, and only shows the
    /// hour component once there is one.
    /// </summary>
    public static string Stamp(TimeSpan at) =>
        at.TotalHours >= 1
            ? string.Format(CultureInfo.InvariantCulture, "{0}:{1:00}:{2:00}", (int)at.TotalHours, at.Minutes, at.Seconds)
            : string.Format(CultureInfo.InvariantCulture, "{0}:{1:00}", at.Minutes, at.Seconds);
}

/// <summary>
/// Joins finished clips into one file without re-encoding anything.
///
/// Takes MP4s rather than clip folders on purpose, so the same class serves both sources: clips
/// read from Steam are remuxed into parts first, exported files go in as they are.
///
/// The DASH chunks themselves cannot simply be concatenated across clips. Each clip's init
/// segment carries its own moov/trak/stsd - the decoder configuration - so its media fragments
/// mean nothing against another clip's. FFmpeg's concat demuxer with -c copy is what works, and
/// it is a genuine stream copy: on the sample this was built against, the joined file's video
/// payload is byte-identical to the parts' payloads concatenated.
/// </summary>
public sealed class ClipStitchService
{
    private readonly IProcessRunner _runner;
    private readonly IMediaProbe _probe;
    private readonly IPipelineLog _log;
    private readonly string _ffmpegPath;

    /// <summary>How far the joined duration may drift from the sum of the parts before it is worth saying so.</summary>
    private static readonly TimeSpan DurationTolerance = TimeSpan.FromSeconds(0.5);

    public ClipStitchService(
        IProcessRunner runner,
        IMediaProbe probe,
        IPipelineLog? log = null,
        string ffmpegPath = "ffmpeg")
    {
        _runner = runner;
        _probe = probe;
        _log = log ?? NullPipelineLog.Instance;
        _ffmpegPath = ffmpegPath;
    }

    /// <summary>
    /// Builds the join command. Separate and pure so the command is covered by tests rather than
    /// discovered during a run, and built as a list so a path containing a quote or a percent
    /// sign is not a hazard.
    /// </summary>
    public static IReadOnlyList<string> BuildArguments(
        SourceMedia first, string listPath, string outputPath, RemuxOptions options)
    {
        AspectRatio targetDar = options.TargetDisplayAspect;
        bool needsAspect = first.DisplayAspect != targetDar;

        var args = new List<string>
        {
            "-y",
            // The list holds absolute paths, which the demuxer refuses to open without this.
            "-f", "concat",
            "-safe", "0",
            "-i", listPath,
            // Every stream of every part: video, all audio tracks, and a microphone track if
            // Steam recorded one.
            "-map", "0",
            "-c", "copy",
            "-map_metadata", "0",
            "-map_chapters", "0",
        };

        if (needsAspect)
        {
            // The concat demuxer takes its stream parameters from the first part, so the aspect
            // each part carried is re-asserted here rather than assumed to survive.
            args.Add("-aspect");
            args.Add($"{targetDar.Numerator}:{targetDar.Denominator}");
        }

        if (first.IsHevc && IsMp4(outputPath))
        {
            // Without this tag QuickTime and several browsers refuse to play HEVC in MP4.
            args.Add("-tag:v");
            args.Add("hvc1");
        }

        // Steam's capture is variable frame rate, and the joins are where forcing a constant
        // rate would drift the audio worst.
        args.AddRange(new[] { "-fps_mode", "passthrough" });

        if (options.FastStart && IsMp4(outputPath))
        {
            args.Add("-movflags");
            args.Add("+faststart");
        }

        args.Add(outputPath);
        return args;
    }

    /// <summary>
    /// Writes the concat demuxer's list file. Paths are single-quoted, and a literal quote
    /// inside one is written as '\'' - the only escape the demuxer understands.
    /// </summary>
    public static string BuildConcatList(IEnumerable<string> paths) =>
        string.Join("\n", paths.Select(p => $"file '{p.Replace("'", @"'\''", StringComparison.Ordinal)}'")) + "\n";

    /// <summary>
    /// Why two parts cannot be joined by copying, or null when they can. A stream copy carries
    /// no filter chain, so anything the decoder configuration depends on has to match exactly.
    /// </summary>
    public static string? Mismatch(SourceMedia first, SourceMedia other)
    {
        if (!string.Equals(first.VideoCodec, other.VideoCodec, StringComparison.OrdinalIgnoreCase))
            return $"{other.VideoCodec} does not match the compilation's {first.VideoCodec}";

        if (first.Width != other.Width || first.Height != other.Height)
            return $"{other.Width}x{other.Height} does not match the compilation's {first.Width}x{first.Height}";

        if (!string.Equals(first.PixelFormat, other.PixelFormat, StringComparison.OrdinalIgnoreCase))
            return $"pixel format {other.PixelFormat} does not match the compilation's {first.PixelFormat}";

        if (first.SampleAspect != other.SampleAspect)
            return $"pixel aspect {other.SampleAspect} does not match the compilation's {first.SampleAspect}";

        if (!SameTag(first.ColorRange, other.ColorRange))
            return $"colour range {Show(other.ColorRange)} does not match the compilation's {Show(first.ColorRange)}";

        if (!SameTag(first.ColorSpace, other.ColorSpace))
            return $"colour space {Show(other.ColorSpace)} does not match the compilation's {Show(first.ColorSpace)}";

        if (first.AudioStreamCount != other.AudioStreamCount)
            return $"{other.AudioStreamCount} audio track(s) do not match the compilation's {first.AudioStreamCount}";

        return null;
    }

    public async Task<ClipStitchResult> StitchAsync(
        IReadOnlyList<StitchInput> inputs,
        string outputDirectory,
        Func<int, string> outputFileName,
        RemuxOptions? options = null,
        CancellationToken ct = default)
    {
        options ??= new RemuxOptions();

        if (inputs.Count < 2)
            throw new InvalidOperationException("A compilation needs at least two clips.");

        var stopwatch = Stopwatch.StartNew();
        Directory.CreateDirectory(outputDirectory);

        string workspace = Path.Combine(
            Path.GetTempPath(), "sclip-stitch-" + Guid.NewGuid().ToString("N"));
        string partialPath = string.Empty;

        try
        {
            Directory.CreateDirectory(workspace);

            // Probe first, decide after. Whether these files can be joined at all is a property
            // of the files, not something to assume because they came from the same recorder.
            var probed = new List<(StitchInput Input, SourceMedia Media)>();
            foreach (StitchInput input in inputs)
                probed.Add((input, await _probe.ProbeAsync(input.Path, ct).ConfigureAwait(false)));

            (IReadOnlyList<(StitchInput Input, SourceMedia Media)> kept, IReadOnlyList<string> excluded) =
                Partition(probed);

            SourceMedia first = kept[0].Media;

            // Named only once the incompatible parts are out, so a compilation of four never
            // calls itself a compilation of five.
            string name = outputFileName(kept.Count);
            string outputPath = Path.Combine(outputDirectory, name + ".mp4");

            // Exported files can be joined in place, so the output folder may be the input
            // folder; writing the compilation over one of its own parts would destroy it.
            foreach ((StitchInput input, _) in kept)
            {
                if (RemuxService.IsSameFile(input.Path, outputPath))
                {
                    throw new InvalidOperationException(
                        $"The compilation would be written over '{Path.GetFileName(input.Path)}'. "
                        + "Choose a different output folder.");
                }
            }

            partialPath = Path.Combine(outputDirectory, name + ".partial.mp4");

            string listPath = Path.Combine(workspace, "parts.txt");
            await File.WriteAllTextAsync(
                listPath, BuildConcatList(kept.Select(k => k.Input.Path)), ct).ConfigureAwait(false);

            IReadOnlyList<string> args = BuildArguments(first, listPath, partialPath, options);

            ProcessResult result = await _runner.RunAsync(_ffmpegPath, args, ct).ConfigureAwait(false);
            if (!result.Succeeded)
            {
                throw new InvalidOperationException(
                    $"FFmpeg failed ({result.ExitCode}): {Tail(result.StandardError)}");
            }

            if (!File.Exists(partialPath) || new FileInfo(partialPath).Length == 0)
                throw new InvalidOperationException("FFmpeg reported success but wrote no output.");

            IReadOnlyList<StitchPart> parts = LayOut(kept);

            await VerifyAsync(kept.Select(k => k.Input.Path).ToList(), partialPath, first, workspace, ct)
                .ConfigureAwait(false);
            await CheckDurationAsync(partialPath, parts, ct).ConfigureAwait(false);

            File.Move(partialPath, outputPath, overwrite: true);

            return new ClipStitchResult
            {
                OutputPath = outputPath,
                Parts = parts,
                Excluded = excluded,
                AspectOverridden = first.DisplayAspect != options.TargetDisplayAspect,
                Elapsed = stopwatch.Elapsed,
            };
        }
        catch
        {
            // Never leave a partial file that could be mistaken for a finished compilation.
            if (partialPath.Length > 0) TryDelete(partialPath);
            throw;
        }
        finally
        {
            TryDeleteDirectory(workspace);
        }
    }

    /// <summary>
    /// Splits the probed inputs into the ones that can be joined and the ones that cannot. The
    /// first input defines the compilation; dropping the odd one out beats failing the whole run
    /// or quietly re-encoding it to fit.
    /// </summary>
    private (IReadOnlyList<(StitchInput Input, SourceMedia Media)> Kept, IReadOnlyList<string> Excluded)
        Partition(IReadOnlyList<(StitchInput Input, SourceMedia Media)> probed)
    {
        var kept = new List<(StitchInput, SourceMedia)> { probed[0] };
        var excluded = new List<string>();

        foreach ((StitchInput input, SourceMedia media) in probed.Skip(1))
        {
            string? reason = Mismatch(probed[0].Media, media);
            if (reason is null)
            {
                kept.Add((input, media));
                continue;
            }

            excluded.Add($"{input.Label}: {reason}");
            _log.Warning($"  {input.Label}: {reason}; left out of the compilation.");
        }

        if (kept.Count < 2)
        {
            throw new InvalidOperationException(
                "Only one of the selected clips matches the first one's format, so there is "
                + "nothing to join. Remux them individually instead.");
        }

        return (kept, excluded);
    }

    /// <summary>Where each part starts in the finished file, for the chapter list.</summary>
    private static IReadOnlyList<StitchPart> LayOut(
        IReadOnlyList<(StitchInput Input, SourceMedia Media)> kept)
    {
        var parts = new List<StitchPart>(kept.Count);
        TimeSpan at = TimeSpan.Zero;

        foreach ((StitchInput input, SourceMedia media) in kept)
        {
            var duration = TimeSpan.FromSeconds(media.DurationSeconds);
            parts.Add(new StitchPart
            {
                Path = input.Path,
                Label = input.Label,
                StartsAt = at,
                Duration = duration,
            });

            at += duration;
        }

        return parts;
    }

    /// <summary>
    /// Proves the join copied the video rather than re-encoding it, by comparing the joined
    /// file's raw bitstream against the parts' bitstreams concatenated.
    ///
    /// The existing per-file stream hash cannot do this: a hash of each part does not compose
    /// into a hash of the whole, so the bytes themselves have to be run through one digest in
    /// order.
    /// </summary>
    private async Task VerifyAsync(
        IReadOnlyList<string> parts,
        string joined,
        SourceMedia first,
        string workspace,
        CancellationToken ct)
    {
        string? format = ElementaryStreamFormat(first.VideoCodec);
        if (format is null)
        {
            // Saying so matters: a check that quietly did not run is indistinguishable from one
            // that passed.
            _log.Warning(
                $"  {first.VideoCodec} has no raw bitstream form here, so the join was checked "
                + "by duration only.");
            return;
        }

        var extracted = new List<string>(parts.Count);
        for (int i = 0; i < parts.Count; i++)
        {
            extracted.Add(await ExtractVideoAsync(
                parts[i], Path.Combine(workspace, $"part{i:00}.{format}"), format, ct).ConfigureAwait(false));
        }

        string joinedBits = await ExtractVideoAsync(
            joined, Path.Combine(workspace, $"joined.{format}"), format, ct).ConfigureAwait(false);

        if (!string.Equals(Hash(extracted), Hash(new[] { joinedBits }), StringComparison.OrdinalIgnoreCase))
        {
            throw new IntegrityCheckException(
                "The video stream changed while the clips were joined, so the output was discarded.");
        }
    }

    /// <summary>Writes one file's video stream out as a raw bitstream, copied packet for packet.</summary>
    private async Task<string> ExtractVideoAsync(
        string source, string destination, string format, CancellationToken ct)
    {
        string[] args =
        {
            "-y", "-v", "error", "-i", source,
            "-map", "0:v", "-c", "copy", "-f", format, destination,
        };

        ProcessResult result = await _runner.RunAsync(_ffmpegPath, args, ct).ConfigureAwait(false);
        if (!result.Succeeded)
        {
            throw new IntegrityCheckException(
                $"Could not read the video stream of '{Path.GetFileName(source)}': {Tail(result.StandardError)}");
        }

        return destination;
    }

    /// <summary>
    /// Warns when the joined file is not as long as its parts. The concat demuxer discards edit
    /// lists, so a small difference is expected where a part was trimmed; a large one means
    /// something was dropped.
    /// </summary>
    private async Task CheckDurationAsync(
        string joined, IReadOnlyList<StitchPart> parts, CancellationToken ct)
    {
        TimeSpan expected = parts[^1].StartsAt + parts[^1].Duration;

        SourceMedia media = await _probe.ProbeAsync(joined, ct).ConfigureAwait(false);
        var actual = TimeSpan.FromSeconds(media.DurationSeconds);

        if ((actual - expected).Duration() > DurationTolerance)
        {
            _log.Warning(
                $"  the compilation runs {actual.TotalSeconds:0.##}s but its parts total "
                + $"{expected.TotalSeconds:0.##}s; the timestamps below may be off.");
        }
    }

    /// <summary>MD5 over the given files' bytes, in order, as one digest.</summary>
    private static string Hash(IReadOnlyList<string> files)
    {
        using IncrementalHash digest = IncrementalHash.CreateHash(HashAlgorithmName.MD5);
        byte[] buffer = new byte[81920];

        foreach (string file in files)
        {
            using FileStream stream = File.OpenRead(file);
            int read;
            while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
                digest.AppendData(buffer, 0, read);
        }

        return Convert.ToHexString(digest.GetHashAndReset()).ToLowerInvariant();
    }

    /// <summary>The raw bitstream container for a codec, or null when there is no such thing here.</summary>
    private static string? ElementaryStreamFormat(string codec) => codec.ToLowerInvariant() switch
    {
        "hevc" or "h265" => "hevc",
        "h264" or "avc" => "h264",
        _ => null,
    };

    private static bool SameTag(string? a, string? b) =>
        string.Equals(a ?? "", b ?? "", StringComparison.OrdinalIgnoreCase);

    private static string Show(string? tag) => string.IsNullOrEmpty(tag) ? "unset" : tag;

    private static bool IsMp4(string path)
    {
        string extension = Path.GetExtension(path);
        return extension.Equals(".mp4", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".m4v", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".mov", StringComparison.OrdinalIgnoreCase);
    }

    private static string Tail(string text)
    {
        string[] lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        return lines.Length == 0 ? "no output" : lines[^1].Trim();
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private static void TryDeleteDirectory(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); }
        catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
