using System.Globalization;
using SteamClipRemuxer.Core.Configuration;
using SteamClipRemuxer.Core.Execution;
using SteamClipRemuxer.Core.Probing;
using SteamClipRemuxer.Core.Steam;
using SteamClipRemuxer.Core.Youtube;
using Xunit;

namespace SteamClipRemuxer.Core.Tests;

/// <summary>
/// A whole highlights run, from a clip folder on disk to the file written and the title sent to
/// YouTube, so the naming settings are checked where they are actually used rather than only
/// where the templates are expanded.
/// </summary>
public class HighlightNamingTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "sclip-hlname-" + Guid.NewGuid().ToString("N"));

    private readonly string _output;

    /// <summary>Where the fake ffprobe says the chunks start, in the video session's clock.</summary>
    private readonly double[] _chunkStarts;

    public HighlightNamingTests()
    {
        _output = Path.Combine(_root, "out");
        string clip = Path.Combine(_root, ClipFolder.ClipsFolderName, "clip_730_20260828_221805");
        string video = Path.Combine(clip, "video", "bg_730_20260828_204356");
        string timelines = Path.Combine(clip, "timelines");
        Directory.CreateDirectory(video);
        Directory.CreateDirectory(timelines);
        Directory.CreateDirectory(_output);

        byte[] pb = File.ReadAllBytes(Path.Combine("Fixtures", "clip_cropped_7435ms.pb"));
        File.WriteAllBytes(Path.Combine(clip, "clip.pb"), pb);
        ClipManifest manifest = ClipManifest.Parse(pb)!;

        File.WriteAllBytes(Path.Combine(video, "init-stream0.m4s"), new byte[] { 1 });
        for (int i = 1; i <= 4; i++)
            File.WriteAllBytes(Path.Combine(video, $"chunk-stream0-{i:00000}.m4s"), new byte[] { 2 });

        // Four three-second chunks starting a little before the clip, as Steam's do.
        double clipStart = (manifest.StartInSession - manifest.VideoSessionOffset).TotalSeconds;
        double first = Math.Floor(clipStart / 3) * 3;
        _chunkStarts = new[] { first, first + 3, first + 6, first + 9 };

        // A match on Inferno covering the whole clip, and one kill inside it.
        double offset = manifest.VideoSessionOffset.TotalSeconds;
        long phase = (long)((first + offset - 60) * 1000);
        long kill = (long)((first + 4 + offset) * 1000);
        File.WriteAllText(Path.Combine(timelines, manifest.TimelineFile + ".json"), $$"""
            { "entries": [
              { "time": "{{phase}}", "type": "phase", "duration": "3600000",
                "tags": [ { "name": "Competitive", "group": "Mode" }, { "name": "Inferno", "group": "Map" } ] },
              { "time": "{{kill}}", "type": "event", "title": "You killed Bot",
                "description": "with the AK-47", "icon": "cs2_gun_kill" }
            ] }
            """);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
        GC.SuppressFinalize(this);
    }

    private sealed class Ffmpeg : IProcessRunner
    {
        private readonly double[] _keyframes;

        public Ffmpeg(double[] keyframes) => _keyframes = keyframes;

        public Task<ProcessResult> RunAsync(
            string fileName, IReadOnlyList<string> arguments, CancellationToken ct = default)
        {
            var args = arguments.ToList();

            if (args.Contains("-skip_frame"))
            {
                return Ok(string.Join("\n",
                    _keyframes.Select(k => k.ToString("0.000", CultureInfo.InvariantCulture))) + "\n");
            }

            if (args.Contains("streamhash")) return Ok("0,v,md5=abc\n");

            string destination = args[^1];
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.WriteAllBytes(destination, new byte[] { 3 });
            return Ok();
        }

        private static Task<ProcessResult> Ok(string stdout = "") =>
            Task.FromResult(new ProcessResult(0, stdout, ""));
    }

    private sealed class Probe : IMediaProbe
    {
        public Task<SourceMedia> ProbeAsync(string filePath, CancellationToken ct = default) =>
            Task.FromResult(new SourceMedia
            {
                FilePath = filePath,
                Width = 1280,
                Height = 960,
                VideoCodec = "hevc",
                SampleAspect = new AspectRatio(4, 3),
                PixelFormat = "yuvj420p",
                DurationSeconds = 12,
                Streams = new[] { new MediaStream(0, "video", "hevc") },
            });
    }

    private sealed class Hasher : IVideoStreamHasher
    {
        public Task<string> HashAsync(string filePath, CancellationToken ct = default) =>
            Task.FromResult("same");
    }

    private sealed class Uploader : IYouTubeUploader
    {
        public bool IsAuthenticated => true;

        public UploadRequest? Request { get; private set; }

        public Task<UploadResult> UploadAsync(
            UploadRequest request, IProgress<int>? progress = null, CancellationToken ct = default)
        {
            Request = request;
            return Task.FromResult(UploadResult.Ok("abc123"));
        }
    }

    private async Task<(ClipOutcome Outcome, Uploader Uploader, ClipListing Listing)> RunAsync(
        Action<AppSettings>? arrange = null, bool join = false)
    {
        var ffmpeg = new Ffmpeg(_chunkStarts);
        var probe = new Probe();
        var batch = new ClipBatchService(
            new ClipRemuxService(ffmpeg, probe, new Hasher()),
            new ClipStitchService(ffmpeg, probe),
            new ClipHighlightService(ffmpeg, probe, new Hasher()));

        var settings = new AppSettings
        {
            InputFolder = _root,
            OutputFolder = _output,
            ClipSource = ClipSource.SteamClips,
            MoveProcessedFiles = false,
            SkipAlreadyProcessed = false,
            EnableYouTubeUpload = true,
            GameNames = new Dictionary<string, string> { ["730"] = "CS2" },
        };
        arrange?.Invoke(settings);

        var processed = new ProcessedClipLog(Array.Empty<ProcessedClip>(), Path.Combine(_root, "log.json"));
        ClipListing listing = Assert.Single(ClipBatchService.FindClips(settings, processed));

        var uploader = new Uploader();
        IReadOnlyList<ClipOutcome> outcomes = await batch.RunHighlightsAsync(
            new[] { listing }, settings, processed,
            new HighlightWindowOptions { Lead = TimeSpan.Zero, Tail = TimeSpan.Zero },
            joinAcrossClips: join, youtube: uploader);

        return (Assert.Single(outcomes), uploader, listing);
    }

    [Fact]
    public async Task A_reel_is_named_and_titled_by_the_highlights_templates()
    {
        (ClipOutcome outcome, Uploader uploader, ClipListing listing) = await RunAsync();

        Assert.Null(outcome.Error);
        string date = listing.RecordedAt.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        string time = listing.RecordedAt.ToString("HH-mm-ss", CultureInfo.InvariantCulture);

        Assert.Equal($"CS2 - {date} {time} - Inferno - Highlights.mp4", Path.GetFileName(outcome.OutputPath));
        Assert.NotNull(uploader.Request);
        Assert.Equal($"CS2 - {date} - Inferno highlights", uploader.Request!.Title);
    }

    [Fact]
    public async Task Edited_highlights_templates_are_the_ones_used()
    {
        (ClipOutcome outcome, Uploader uploader, _) = await RunAsync(s =>
        {
            s.HighlightsFileNameTemplate = "{map} reel";
            s.YouTubeHighlightsTitleTemplate = "{game} on {map}";
        });

        Assert.Equal("Inferno reel.mp4", Path.GetFileName(outcome.OutputPath));
        Assert.Equal("CS2 on Inferno", uploader.Request!.Title);
    }

    [Fact]
    public async Task Joining_a_single_clip_names_it_as_a_reel_not_a_compilation()
    {
        // "Stitch into one video" with one clip used to give "CS2 - 1 clip compilation".
        (ClipOutcome outcome, Uploader uploader, ClipListing listing) = await RunAsync(join: true);

        string date = listing.RecordedAt.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        Assert.EndsWith(" - Inferno - Highlights.mp4", outcome.OutputPath);
        Assert.Equal($"CS2 - {date} - Inferno highlights", uploader.Request!.Title);
    }

    [Fact]
    public async Task The_name_the_list_shows_is_the_name_the_reel_is_written_under()
    {
        AppSettings settings = new()
        {
            GameNames = new Dictionary<string, string> { ["730"] = "CS2" },
        };

        (ClipOutcome outcome, Uploader uploader, ClipListing listing) = await RunAsync();

        Assert.Equal(
            ClipBatchService.HighlightsNameFor(listing, settings) + ".mp4",
            Path.GetFileName(outcome.OutputPath));
        Assert.Equal(
            ClipBatchService.HighlightsTitleFor(listing, settings, outcome.OutputPath!),
            uploader.Request!.Title);
    }

    [Fact]
    public async Task A_name_set_on_the_clip_beats_the_highlights_templates()
    {
        // Deliberate, and the reason the list must show the reel's name: a rename made to
        // correct a wrong-looking row then sticks for the file and the title alike.
        (_, _, ClipListing generated) = await RunAsync();
        ClipListing listing = generated with { CustomName = "my name" };

        Assert.Equal("my name", ClipBatchService.HighlightsNameFor(listing, new AppSettings()));
        Assert.Equal("my name", ClipBatchService.HighlightsTitleFor(listing, new AppSettings(), "x.mp4"));
    }
}
