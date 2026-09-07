using System.Diagnostics;
using System.Globalization;
using SteamClipRemuxer.Core.Planning;
using SteamClipRemuxer.Core.Probing;
using SteamClipRemuxer.Core.Steam;

namespace SteamClipRemuxer.Core.Execution;

public sealed record ClipRemuxResult
{
    public required string OutputPath { get; init; }
    public required bool AspectOverridden { get; init; }
    public required bool Trimmed { get; init; }
    public TimeSpan Elapsed { get; init; }
}

/// <summary>
/// Turns one of Steam's clip folders into a finished file, replacing the export step.
///
/// The video is never re-encoded. The DASH segments are concatenated, which is a byte copy, and
/// FFmpeg then copies the streams into an MP4 with the display aspect corrected.
/// </summary>
public sealed class ClipRemuxService
{
    private readonly IProcessRunner _runner;
    private readonly IMediaProbe _probe;
    private readonly IVideoStreamHasher _hasher;
    private readonly IPipelineLog _log;
    private readonly string _ffmpegPath;

    /// <summary>Streams to look for. Steam writes video as 0 and audio as 1; a separate microphone track would follow.</summary>
    private const int MaxStreams = 8;

    public ClipRemuxService(
        IProcessRunner runner,
        IMediaProbe probe,
        IVideoStreamHasher hasher,
        IPipelineLog? log = null,
        string ffmpegPath = "ffmpeg")
    {
        _runner = runner;
        _probe = probe;
        _hasher = hasher;
        _log = log ?? NullPipelineLog.Instance;
        _ffmpegPath = ffmpegPath;
    }

    /// <summary>
    /// Builds the FFmpeg arguments. Separate and pure so the command is covered by tests rather
    /// than discovered during a run, and built as a list so a path containing a quote or a
    /// percent sign is not a hazard.
    /// </summary>
    public static IReadOnlyList<string> BuildArguments(
        SourceMedia source,
        IReadOnlyList<string> assembledStreams,
        string outputPath,
        RemuxOptions options,
        TimeSpan? trimStart,
        TimeSpan? trimDuration)
    {
        AspectRatio targetDar = options.TargetDisplayAspect;
        bool needsAspect = source.DisplayAspect != targetDar;

        var args = new List<string> { "-y" };

        foreach (string stream in assembledStreams)
        {
            // Seeking before -i seeks the input, which lands on the nearest earlier keyframe and
            // copies from there. Applied per input so the streams stay aligned.
            if (trimStart is { } start)
            {
                args.Add("-ss");
                args.Add(start.TotalSeconds.ToString("0.######", CultureInfo.InvariantCulture));
            }

            if (trimDuration is { } duration)
            {
                args.Add("-t");
                args.Add(duration.TotalSeconds.ToString("0.######", CultureInfo.InvariantCulture));
            }

            args.Add("-i");
            args.Add(stream);
        }

        // Every assembled file holds exactly one stream, so mapping each input whole keeps all of
        // them - including a microphone track, if Steam recorded one.
        for (int i = 0; i < assembledStreams.Count; i++)
        {
            args.Add("-map");
            args.Add(i.ToString(CultureInfo.InvariantCulture));
        }

        args.AddRange(new[] { "-c", "copy", "-map_metadata", "0", "-map_chapters", "0" });

        if (needsAspect)
        {
            args.Add("-aspect");
            args.Add($"{targetDar.Numerator}:{targetDar.Denominator}");
        }

        if (source.IsHevc && outputPath.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase))
        {
            // Without this tag QuickTime and several browsers refuse to play HEVC in MP4.
            args.Add("-tag:v");
            args.Add("hvc1");
        }

        // Steam's capture is variable frame rate. Forcing constant rate duplicates frames and
        // drifts the audio.
        args.AddRange(new[] { "-fps_mode", "passthrough" });

        if (options.FastStart && outputPath.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase))
        {
            args.Add("-movflags");
            args.Add("+faststart");
        }

        args.Add(outputPath);
        return args;
    }

    /// <summary>
    /// Where the trim starts within the assembled stream, or null when the whole thing is kept.
    ///
    /// The assembled segments begin at a segment boundary at or before the clip, so the clip
    /// starts some way into them. session.mpd's Period@start is measured from the video session,
    /// which is why the manifest's own offset between the two clocks has to come out again here.
    /// </summary>
    public static TimeSpan? TrimOffsetFor(ClipManifest manifest, double assembledStartSeconds)
    {
        double clipStartInVideoSession =
            (manifest.StartInSession - manifest.VideoSessionOffset).TotalSeconds;

        double offset = clipStartInVideoSession - assembledStartSeconds;
        return offset > 0.001 ? TimeSpan.FromSeconds(offset) : null;
    }

    public async Task<ClipRemuxResult> RemuxAsync(
        ClipFolder clip,
        string outputDirectory,
        string outputFileName,
        RemuxOptions? options = null,
        bool respectSteamCrop = true,
        CancellationToken ct = default)
    {
        options ??= new RemuxOptions();
        var stopwatch = Stopwatch.StartNew();

        Directory.CreateDirectory(outputDirectory);

        string outputPath = Path.Combine(outputDirectory, outputFileName + ".mp4");
        string partialPath = Path.Combine(outputDirectory, outputFileName + ".partial.mp4");

        // Steam owns the clip folder, so nothing is ever written back into it.
        string workspace = Path.Combine(
            Path.GetTempPath(), "sclip-dash-" + Guid.NewGuid().ToString("N"));

        try
        {
            List<string> assembled = await AssembleStreamsAsync(clip, workspace, ct).ConfigureAwait(false);
            if (assembled.Count == 0)
                throw new InvalidOperationException("The clip has no readable video segments.");

            SourceMedia source = await _probe.ProbeAsync(assembled[0], ct).ConfigureAwait(false);

            TimeSpan? trimStart = null;
            TimeSpan? trimDuration = null;
            if (respectSteamCrop)
            {
                trimStart = TrimOffsetFor(clip.Manifest, source.StartTimeSeconds);
                trimDuration = clip.Manifest.Duration;
            }

            IReadOnlyList<string> args = BuildArguments(
                source, assembled, partialPath, options, trimStart, trimDuration);

            // Hash the source over exactly the range being kept. Comparing the whole stream
            // against a trimmed output would always differ, which would report a lossless copy
            // as corruption.
            string before = await HashAsync(assembled[0], trimStart, trimDuration, ct).ConfigureAwait(false);

            ProcessResult result = await _runner
                .RunAsync(_ffmpegPath, args, ct: ct)
                .ConfigureAwait(false);

            if (!result.Succeeded)
            {
                throw new InvalidOperationException(
                    $"FFmpeg failed ({result.ExitCode}): {Tail(result.StandardError)}");
            }

            if (!File.Exists(partialPath) || new FileInfo(partialPath).Length == 0)
                throw new InvalidOperationException("FFmpeg reported success but wrote no output.");

            string after = await _hasher.HashAsync(partialPath, ct).ConfigureAwait(false);
            if (!string.Equals(before, after, StringComparison.OrdinalIgnoreCase))
            {
                throw new IntegrityCheckException(
                    "The video stream changed during the remux, so the output was discarded.");
            }

            File.Move(partialPath, outputPath, overwrite: true);

            return new ClipRemuxResult
            {
                OutputPath = outputPath,
                AspectOverridden = source.DisplayAspect != options.TargetDisplayAspect,
                Trimmed = trimStart is not null || trimDuration is not null,
                Elapsed = stopwatch.Elapsed,
            };
        }
        catch
        {
            // Never leave a partial file that could be mistaken for a finished clip.
            TryDelete(partialPath);
            throw;
        }
        finally
        {
            TryDeleteDirectory(workspace);
        }
    }

    private async Task<List<string>> AssembleStreamsAsync(
        ClipFolder clip, string workspace, CancellationToken ct)
    {
        var assembled = new List<string>();

        for (int stream = 0; stream < MaxStreams; stream++)
        {
            if (!DashSegments.HasStream(clip.VideoPath, stream)) continue;

            string destination = Path.Combine(workspace, $"stream{stream}.mp4");
            assembled.Add(await DashSegments
                .AssembleAsync(clip.VideoPath, stream, destination, ct)
                .ConfigureAwait(false));
        }

        return assembled;
    }

    private async Task<string> HashAsync(
        string path, TimeSpan? trimStart, TimeSpan? trimDuration, CancellationToken ct)
    {
        if (trimStart is null && trimDuration is null)
            return await _hasher.HashAsync(path, ct).ConfigureAwait(false);

        var args = new List<string> { "-v", "error" };

        if (trimStart is { } start)
        {
            args.Add("-ss");
            args.Add(start.TotalSeconds.ToString("0.######", CultureInfo.InvariantCulture));
        }

        if (trimDuration is { } duration)
        {
            args.Add("-t");
            args.Add(duration.TotalSeconds.ToString("0.######", CultureInfo.InvariantCulture));
        }

        args.AddRange(new[] { "-i", path, "-map", "0:v", "-c", "copy", "-f", "streamhash", "-hash", "md5", "-" });

        ProcessResult result = await _runner.RunAsync(_ffmpegPath, args, ct: ct).ConfigureAwait(false);
        return VideoStreamHasher.ParseHash(result.StandardOutput);
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
