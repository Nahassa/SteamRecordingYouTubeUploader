using System.Globalization;
using SteamClipRemuxer.Core.Highlights;
using SteamClipRemuxer.Core.Planning;
using SteamClipRemuxer.Core.Probing;
using SteamClipRemuxer.Core.Steam;
using SteamClipRemuxer.Core.Timelines;

namespace SteamClipRemuxer.Core.Execution;

/// <summary>What was found in one clip, and the pieces cut out of it.</summary>
public sealed record ClipHighlightResult
{
    /// <summary>The cut pieces, oldest first, ready to be joined. Empty when nothing qualified.</summary>
    public required IReadOnlyList<StitchInput> Parts { get; init; }

    public required IReadOnlyList<ChunkRun> Runs { get; init; }

    /// <summary>Why there is nothing to show, when there is nothing to show.</summary>
    public string? Nothing { get; init; }
}

/// <summary>
/// Cuts a Steam clip down to the fights in it, without re-encoding anything.
///
/// The cut works in whole chunks because that is the only unit a copy can cut in: Steam writes a
/// keyframe at the start of every chunk and nowhere else, so three seconds is the finest possible
/// grain and a chunk can be kept or dropped whole. Selecting chunks is a byte copy, and what
/// comes out is the same bitstream Steam recorded.
///
/// Kill times come from the clip's own timeline, in milliseconds. No video is analysed, so a kill
/// Steam did not record is a kill this cannot find.
/// </summary>
public sealed class ClipHighlightService
{
    private readonly IProcessRunner _runner;
    private readonly IMediaProbe _probe;
    private readonly IVideoStreamHasher _hasher;
    private readonly IPipelineLog _log;
    private readonly string _ffmpegPath;
    private readonly string _ffprobePath;

    /// <summary>Steam writes video as 0 and audio as 1; a separate microphone track would follow.</summary>
    private const int MaxStreams = 8;

    public ClipHighlightService(
        IProcessRunner runner,
        IMediaProbe probe,
        IVideoStreamHasher hasher,
        IPipelineLog? log = null,
        string ffmpegPath = "ffmpeg",
        string ffprobePath = "ffprobe")
    {
        _runner = runner;
        _probe = probe;
        _hasher = hasher;
        _log = log ?? NullPipelineLog.Instance;
        _ffmpegPath = ffmpegPath;
        _ffprobePath = ffprobePath;
    }

    /// <summary>
    /// Asks ffprobe only for keyframes, which is what makes reading the chunk boundaries cheap:
    /// 0.2 seconds on the sample, because nothing else is decoded.
    /// </summary>
    public static IReadOnlyList<string> BuildKeyframeArguments(string filePath) => new[]
    {
        "-v", "error",
        "-select_streams", "v:0",
        "-skip_frame", "nokey",
        "-show_entries", "frame=pts_time",
        "-of", "csv=p=0",
        "-i", filePath,
    };

    /// <summary>
    /// Reads the keyframe times out of ffprobe's output. Pure, so the parsing is tested rather
    /// than discovered during a run.
    /// </summary>
    public static IReadOnlyList<double> ParseKeyframes(string probeOutput)
    {
        var times = new List<double>();

        foreach (string raw in probeOutput.Split('\n'))
        {
            string line = raw.Trim().TrimEnd(',');
            if (line.Length == 0) continue;

            if (double.TryParse(line, NumberStyles.Float, CultureInfo.InvariantCulture, out double t))
                times.Add(t);
        }

        times.Sort();
        return times;
    }

    public async Task<ClipHighlightResult> CutAsync(
        ClipFolder clip,
        string workspace,
        RemuxOptions options,
        HighlightWindowOptions windowOptions,
        CancellationToken ct = default)
    {
        Directory.CreateDirectory(workspace);

        if (clip.TimelinePath is null)
            return Empty("the clip has no timeline, so its kills cannot be located");

        SessionTimeline timeline;
        try
        {
            timeline = SessionTimeline.Load(clip.TimelinePath);
        }
        catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException)
        {
            return Empty($"the timeline could not be read ({ex.Message})");
        }

        // Assembled once, to read where Steam put its keyframes. Those are the chunk boundaries,
        // read rather than assumed to be 3.0s apart: the measured gaps are 2.999 and 3.001.
        string whole = await DashSegments
            .AssembleAsync(clip.VideoPath, DashSegments.VideoStream, Path.Combine(workspace, "whole.mp4"), ct)
            .ConfigureAwait(false);

        SourceMedia source = await _probe.ProbeAsync(whole, ct).ConfigureAwait(false);
        IReadOnlyList<double> chunkStarts = await ReadKeyframesAsync(whole, ct).ConfigureAwait(false);

        if (chunkStarts.Count == 0)
            return Empty("no keyframes were found in the clip's video");

        double streamEnd = EndOf(chunkStarts, source.DurationSeconds);

        // The chunks reach a little either side of the clip itself, because the first one starts
        // at the keyframe before it. Search that span rather than the clip's own, so a kill in
        // the pre-roll is not missed.
        double offset = clip.Manifest.VideoSessionOffset.TotalSeconds;
        var spanStart = TimeSpan.FromSeconds(chunkStarts[0] + offset);
        var spanEnd = TimeSpan.FromSeconds(streamEnd + offset);

        IReadOnlyList<Engagement> fights = HighlightSelector.Engagements(timeline, spanStart, spanEnd);
        if (fights.Count == 0) return Empty("the timeline records no kills in this clip");

        IReadOnlyList<ChunkRun> runs = ChunkWindows.Plan(
            chunkStarts, streamEnd, offset, fights,
            timeline.RoundStarts(),
            windowOptions.StopBeforeDeaths
                ? HighlightSelector.Deaths(timeline, spanStart, spanEnd)
                : Array.Empty<TimeSpan>(),
            windowOptions);

        if (runs.Count == 0)
        {
            return Empty(
                $"no round in this clip reached {windowOptions.MinimumKillsPerRound} kills");
        }

        var parts = new List<StitchInput>();
        for (int i = 0; i < runs.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            parts.Add(await CutRunAsync(clip, runs[i], i, source, workspace, options, ct).ConfigureAwait(false));
        }

        return new ClipHighlightResult { Parts = parts, Runs = runs };
    }

    /// <summary>Byte-copies one run of chunks out of Steam's folder and muxes it into a part.</summary>
    private async Task<StitchInput> CutRunAsync(
        ClipFolder clip,
        ChunkRun run,
        int index,
        SourceMedia source,
        string workspace,
        RemuxOptions options,
        CancellationToken ct)
    {
        var assembled = new List<string>();

        for (int stream = 0; stream < MaxStreams; stream++)
        {
            if (!DashSegments.HasStream(clip.VideoPath, stream)) continue;

            assembled.Add(await DashSegments.AssembleRangeAsync(
                    clip.VideoPath, stream, run.FirstIndex, run.LastIndex,
                    Path.Combine(workspace, $"run{index:00}-stream{stream}.mp4"), ct)
                .ConfigureAwait(false));
        }

        if (assembled.Count == 0)
            throw new InvalidOperationException("The clip has no readable video segments.");

        string partPath = Path.Combine(workspace, $"run{index:00}.mp4");

        // No trim: the run is already exactly the chunks wanted, and asking FFmpeg to seek is
        // what hides unwanted footage behind an edit list the concat demuxer then discards.
        IReadOnlyList<string> args = ClipRemuxService.BuildArguments(
            source, assembled, partPath, options, trimStart: null, trimDuration: null);

        string before = await _hasher.HashAsync(assembled[0], ct).ConfigureAwait(false);

        ProcessResult result = await _runner.RunAsync(_ffmpegPath, args, ct).ConfigureAwait(false);
        if (!result.Succeeded)
        {
            throw new InvalidOperationException(
                $"FFmpeg failed ({result.ExitCode}): {Tail(result.StandardError)}");
        }

        if (!File.Exists(partPath) || new FileInfo(partPath).Length == 0)
            throw new InvalidOperationException("FFmpeg reported success but wrote no output.");

        string after = await _hasher.HashAsync(partPath, ct).ConfigureAwait(false);
        if (!string.Equals(before, after, StringComparison.OrdinalIgnoreCase))
        {
            throw new IntegrityCheckException(
                $"The video stream changed while cutting {run.Label}, so the output was discarded.");
        }

        _log.Info(
            $"  {run.Label}: chunks {run.FirstIndex}-{run.LastIndex} ({run.ChunkCount} x ~3s), video copied");

        return new StitchInput(partPath, run.Label);
    }

    private async Task<IReadOnlyList<double>> ReadKeyframesAsync(string filePath, CancellationToken ct)
    {
        ProcessResult result = await _runner
            .RunAsync(_ffprobePath, BuildKeyframeArguments(filePath), ct)
            .ConfigureAwait(false);

        if (!result.Succeeded)
        {
            throw new InvalidOperationException(
                $"Could not read the clip's keyframes: {Tail(result.StandardError)}");
        }

        return ParseKeyframes(result.StandardOutput);
    }

    /// <summary>
    /// Where the last chunk ends. ffprobe reports the format duration of these assembled streams
    /// as the end timestamp rather than a length, because they keep the session's clock, so it is
    /// only trusted when it lands past the last keyframe.
    /// </summary>
    public static double EndOf(IReadOnlyList<double> chunkStarts, double reportedDuration)
    {
        double last = chunkStarts[^1];
        if (reportedDuration > last) return reportedDuration;

        double gap = chunkStarts.Count > 1
            ? chunkStarts.Zip(chunkStarts.Skip(1), (a, b) => b - a).Max()
            : 3;

        return last + gap;
    }

    private static ClipHighlightResult Empty(string why) => new()
    {
        Parts = Array.Empty<StitchInput>(),
        Runs = Array.Empty<ChunkRun>(),
        Nothing = why,
    };

    private static string Tail(string text)
    {
        string[] lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        return lines.Length == 0 ? "no output" : lines[^1].Trim();
    }
}
