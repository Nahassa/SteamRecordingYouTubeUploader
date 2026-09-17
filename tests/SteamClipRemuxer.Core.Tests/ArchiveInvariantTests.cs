using SteamClipRemuxer.Core.Configuration;
using SteamClipRemuxer.Core.Execution;
using SteamClipRemuxer.Core.Files;
using SteamClipRemuxer.Core.Probing;
using SteamClipRemuxer.Core.Steam;
using SteamClipRemuxer.Core.Youtube;
using Xunit;

namespace SteamClipRemuxer.Core.Tests;

/// <summary>
/// The one thing worth proving about the upscale: what is left on disk is the lossless file.
///
/// Upscaling is the only re-encode in a pipeline whose whole promise is that it never re-encodes.
/// If the archived copy ever became the re-encode, the tool would have destroyed the thing it
/// exists to preserve, and nothing else would notice - the upload would still succeed and the log
/// would still read correctly. So this runs the real batch service end to end with FFmpeg and
/// YouTube stood in for, and then looks at the bytes.
/// </summary>
public class ArchiveInvariantTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "sclip-archive-" + Guid.NewGuid().ToString("N"));

    private readonly string _output;
    private readonly string _logFile;

    /// <summary>What a lossless remux writes, and what therefore has to reach the archive.</summary>
    private static readonly byte[] Lossless = { 0x10, 0x05, 0x51, 0xE5, 0x55 };

    /// <summary>What the upscale writes. Deliberately different, so a mix-up cannot pass.</summary>
    private static readonly byte[] Scaled = { 0x5C, 0xA1, 0xED };

    public ArchiveInvariantTests()
    {
        _output = Path.Combine(_root, "out");
        _logFile = Path.Combine(_root, "processed.json");

        string clip = Path.Combine(_root, ClipFolder.ClipsFolderName, "clip_730_20260828_221805");
        string video = Path.Combine(clip, "video", "bg_730_20260828_204356");
        Directory.CreateDirectory(video);
        Directory.CreateDirectory(_output);

        File.Copy(Path.Combine("Fixtures", "clip_cropped_7435ms.pb"), Path.Combine(clip, "clip.pb"));
        File.WriteAllBytes(Path.Combine(video, "init-stream0.m4s"), new byte[] { 1 });
        File.WriteAllBytes(Path.Combine(video, "chunk-stream0-00001.m4s"), new byte[] { 2 });
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
        GC.SuppressFinalize(this);
    }

    // ----------------------------------------------------------------- fakes

    /// <summary>
    /// Stands in for FFmpeg. Which call is which is read off the arguments, so the fake cannot
    /// drift from the real command shape without the tests noticing.
    /// </summary>
    private sealed class Ffmpeg : IProcessRunner
    {
        public List<IReadOnlyList<string>> Calls { get; } = new();

        /// <summary>Where the scaled copy was written, so a test can check it was cleaned up.</summary>
        public string? ScaledPath { get; private set; }

        /// <summary>What the similarity pass reports. Below the floor makes the scale unusable.</summary>
        public double Ssim { get; set; } = 0.994;

        /// <summary>Set to fail every encode, as a machine without a working encoder would.</summary>
        public bool EncodeFails { get; set; }

        public Task<ProcessResult> RunAsync(
            string fileName, IReadOnlyList<string> arguments, CancellationToken ct = default)
        {
            Calls.Add(arguments);
            var args = arguments.ToList();

            if (args.Contains("-version"))
                return Ok("ffmpeg version 6.1.1-test");

            // The similarity pass. Reports on stderr, as the ssim filter does.
            if (args.Contains("-lavfi"))
            {
                return Task.FromResult(new ProcessResult(
                    0, "", $"[Parsed_ssim_0 @ 0x1] SSIM Y:0.99 U:0.99 V:0.99 All:{Ssim} (22.7)\n"));
            }

            // A capability trial: encodes nothing into nothing.
            if (args[^1] == "-" && args.Contains("null")) return Ok();

            // The hash of the source range, also written to stdout rather than to a file.
            if (args.Contains("streamhash")) return Ok("0,v,md5=abc\n");

            string destination = args[^1];
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);

            // The upscale: the only call that names a video encoder.
            if (args.Contains("-c:v"))
            {
                if (EncodeFails) return Task.FromResult(new ProcessResult(1, "", "no encoder\n"));

                File.WriteAllBytes(destination, Scaled);
                ScaledPath = destination;
                return Ok();
            }

            // Everything else is a stream copy, which is what a remux is.
            File.WriteAllBytes(destination, Lossless);
            return Ok();
        }

        private static Task<ProcessResult> Ok(string stdout = "") =>
            Task.FromResult(new ProcessResult(0, stdout, ""));
    }

    /// <summary>Describes a file by what is in it, so the probe cannot disagree with the runner.</summary>
    private sealed class Probe : IMediaProbe
    {
        public Task<SourceMedia> ProbeAsync(string filePath, CancellationToken ct = default)
        {
            bool scaled = File.Exists(filePath)
                && File.ReadAllBytes(filePath).SequenceEqual(Scaled);

            return Task.FromResult(scaled ? Scaled1080(filePath) : Source(filePath));
        }

        private static SourceMedia Source(string path) => new()
        {
            FilePath = path,
            Width = 1280,
            Height = 960,
            VideoCodec = "hevc",
            SampleAspect = new AspectRatio(4, 3),
            PixelFormat = "yuvj420p",
            ColorRange = "pc",
            ColorSpace = "bt709",
            ColorPrimaries = "bt709",
            ColorTransfer = "bt709",
            FrameCount = 446,
            DurationSeconds = 7.435,
            AudioCodec = "aac",
            AudioChannels = 2,
            Streams = new[]
            {
                new MediaStream(0, "video", "hevc"),
                new MediaStream(1, "audio", "aac"),
            },
        };

        private static SourceMedia Scaled1080(string path) => Source(path) with
        {
            Width = 1920,
            Height = 1080,
            SampleAspect = AspectRatio.Square,
            PixelFormat = "yuv420p10le",
        };
    }

    private sealed class Hasher : IVideoStreamHasher
    {
        public Task<string> HashAsync(string filePath, CancellationToken ct = default) =>
            Task.FromResult("same");
    }

    private sealed class Uploader : IYouTubeUploader
    {
        public bool IsAuthenticated => true;

        public bool Fail { get; set; }

        /// <summary>The file YouTube was actually handed, and what was in it at the time.</summary>
        public string? UploadedPath { get; private set; }

        public byte[]? UploadedBytes { get; private set; }

        public Task<UploadResult> UploadAsync(
            UploadRequest request, IProgress<int>? progress = null, CancellationToken ct = default)
        {
            UploadedPath = request.FilePath;
            UploadedBytes = File.Exists(request.FilePath) ? File.ReadAllBytes(request.FilePath) : null;

            return Task.FromResult(Fail
                ? UploadResult.Failed("nope")
                : UploadResult.Ok("abc123"));
        }
    }

    // --------------------------------------------------------------- harness

    private AppSettings Settings(YouTubeUpscale upscale) => new()
    {
        InputFolder = _root,
        OutputFolder = _output,
        // The fake FFmpeg copies rather than trims, so the crop is left off to keep the
        // integrity hash comparing like with like.
        RespectSteamCrop = false,
        MoveProcessedFiles = false,
        SkipAlreadyProcessed = false,
        EnableYouTubeUpload = true,
        YouTubeUpscale = upscale,
    };

    private async Task<(Ffmpeg Ffmpeg, Uploader Uploader)> RunAsync(
        YouTubeUpscale upscale,
        Action<Ffmpeg>? arrange = null,
        Action<Uploader>? arrangeUpload = null)
    {
        var ffmpeg = new Ffmpeg();
        var uploader = new Uploader();
        arrange?.Invoke(ffmpeg);
        arrangeUpload?.Invoke(uploader);

        var probe = new Probe();
        var capability = new EncoderCapability(ffmpeg);

        var batch = new ClipBatchService(
            new ClipRemuxService(ffmpeg, probe, new Hasher()),
            new ClipStitchService(ffmpeg, probe),
            new ClipHighlightService(ffmpeg, probe, new Hasher()),
            log: null,
            upscale: new ClipUpscaleService(ffmpeg, probe, capability));

        AppSettings settings = Settings(upscale);
        var processed = new ProcessedClipLog(Array.Empty<ProcessedClip>(), _logFile);

        IReadOnlyList<ClipListing> clips = ClipBatchService.FindClips(settings, processed);
        Assert.Single(clips);

        await batch.RunAsync(clips, settings, processed, uploader);
        return (ffmpeg, uploader);
    }

    private string[] Archived() =>
        Directory.Exists(Path.Combine(_output, FileOrganizer.UploadedFolderName))
            ? Directory.GetFiles(Path.Combine(_output, FileOrganizer.UploadedFolderName))
            : Array.Empty<string>();

    // ----------------------------------------------------------------- tests

    [Fact]
    public async Task The_scaled_copy_is_uploaded_and_the_lossless_file_is_the_one_kept()
    {
        (Ffmpeg ffmpeg, Uploader uploader) = await RunAsync(YouTubeUpscale.To1080p);

        // YouTube got the re-encode...
        Assert.Equal(Scaled, uploader.UploadedBytes);

        // ...and the file left on disk is the untouched one. This is the whole point.
        string archived = Assert.Single(Archived());
        Assert.Equal(Lossless, File.ReadAllBytes(archived));
        Assert.EndsWith(".mp4", archived);

        // The scaled copy was a temporary and is gone.
        Assert.NotNull(ffmpeg.ScaledPath);
        Assert.False(File.Exists(ffmpeg.ScaledPath));
        Assert.NotEqual(archived, ffmpeg.ScaledPath);
    }

    [Fact]
    public async Task The_scaled_copy_is_never_written_into_the_output_folder()
    {
        // If it were, the clip list and the archiver could both pick it up as an output.
        (Ffmpeg ffmpeg, _) = await RunAsync(YouTubeUpscale.To1080p);

        Assert.NotNull(ffmpeg.ScaledPath);
        Assert.DoesNotContain(
            Path.GetFullPath(_output),
            Path.GetFullPath(ffmpeg.ScaledPath!));
    }

    [Fact]
    public async Task With_upscaling_off_the_lossless_file_is_uploaded_and_kept()
    {
        (Ffmpeg ffmpeg, Uploader uploader) = await RunAsync(YouTubeUpscale.Off);

        Assert.Equal(Lossless, uploader.UploadedBytes);
        Assert.Null(ffmpeg.ScaledPath);
        Assert.Equal(Lossless, File.ReadAllBytes(Assert.Single(Archived())));
    }

    [Fact]
    public async Task A_scale_that_fails_its_similarity_check_uploads_the_original_instead()
    {
        // A failed upscale costs the upscale, not the upload.
        (Ffmpeg ffmpeg, Uploader uploader) = await RunAsync(
            YouTubeUpscale.To1080p, arrange: f => f.Ssim = 0.90);

        Assert.Equal(Lossless, uploader.UploadedBytes);
        Assert.Equal(Lossless, File.ReadAllBytes(Assert.Single(Archived())));
        Assert.False(File.Exists(ffmpeg.ScaledPath));
    }

    [Fact]
    public async Task A_scale_that_cannot_encode_at_all_uploads_the_original_instead()
    {
        (_, Uploader uploader) = await RunAsync(
            YouTubeUpscale.To1080p, arrange: f => f.EncodeFails = true);

        Assert.Equal(Lossless, uploader.UploadedBytes);
        Assert.Equal(Lossless, File.ReadAllBytes(Assert.Single(Archived())));
    }

    [Fact]
    public async Task A_failed_upload_keeps_the_lossless_file_where_it_was_and_drops_the_copy()
    {
        // Nothing is archived, because nothing reached YouTube - but the remuxed file has to
        // still be there for a later run to upload, and the temporary must not be.
        (Ffmpeg ffmpeg, _) = await RunAsync(
            YouTubeUpscale.To1080p, arrangeUpload: u => u.Fail = true);

        Assert.Empty(Archived());
        Assert.False(File.Exists(ffmpeg.ScaledPath));

        string kept = Assert.Single(Directory.GetFiles(_output, "*.mp4"));
        Assert.Equal(Lossless, File.ReadAllBytes(kept));
    }
}
