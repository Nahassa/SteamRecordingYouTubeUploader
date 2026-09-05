using SteamClipRemuxer.Core.Execution;
using SteamClipRemuxer.Core.Probing;
using Xunit;

namespace SteamClipRemuxer.Core.Tests;

public class VideoStreamHasherTests
{
    // Real `ffmpeg -f streamhash -hash md5` output.
    private const string RealOutput = """
        #format: stream checksums
        #version: 2
        #hash: MD5
        #extradata 0,                             133, 7dac0d5de684c79e5b5703973423fa4e
        #software: Lavf63.6.100
        #media_type 0: video
        #codec_id 0: hevc
        #dimensions 0: 1280x960
        #sar 0: 1/1
        #stream#, size, hash
        0,    13519795, 00548921ea4632348da6bec560060a34
        """;

    [Fact]
    public void Parses_hash_from_real_ffmpeg_output() =>
        Assert.Equal("00548921ea4632348da6bec560060a34", VideoStreamHasher.ParseHash(RealOutput));

    [Fact]
    public void Ignores_the_extradata_comment_line_which_also_contains_a_hash()
    {
        // '#extradata' carries its own md5 and would be picked up by a naive parser.
        Assert.NotEqual("7dac0d5de684c79e5b5703973423fa4e", VideoStreamHasher.ParseHash(RealOutput));
    }

    [Fact]
    public void Throws_when_ffmpeg_produced_no_data_line() =>
        Assert.Throws<IntegrityCheckException>(() => VideoStreamHasher.ParseHash("#only comments\n"));

    [Fact]
    public void Hashes_only_the_video_stream_by_stream_copy()
    {
        IReadOnlyList<string> args = VideoStreamHasher.BuildArguments("C:/a.mp4");
        Assert.Contains("0:v", args);
        Assert.Contains("copy", args);
        Assert.Contains("streamhash", args);
    }
}

public class RemuxServicePathTests
{
    [Fact]
    public void Partial_file_keeps_the_real_extension_so_ffmpeg_picks_the_right_muxer()
    {
        (string output, string partial) = RemuxService.ResolvePaths("C:/rec/clip.mp4", "C:/out");

        Assert.EndsWith(".mp4", partial);
        Assert.Contains("partial", partial);
        Assert.NotEqual(output, partial);
    }

    [Fact]
    public void Partial_sits_beside_the_final_file_so_committing_is_a_rename()
    {
        (string output, string partial) = RemuxService.ResolvePaths("C:/rec/clip.mp4", "C:/out");
        Assert.Equal(Path.GetDirectoryName(output), Path.GetDirectoryName(partial));
    }

    [Fact]
    public void Output_keeps_the_original_filename()
    {
        (string output, _) = RemuxService.ResolvePaths("C:/rec/Double_kill.mp4", "C:/out");
        Assert.Equal("Double_kill.mp4", Path.GetFileName(output));
    }

    [Theory]
    [InlineData("/a/b/c.mp4", "/a/b/c.mp4", true)]
    [InlineData("/a/b/../b/c.mp4", "/a/b/c.mp4", true)]
    [InlineData("/a/b/c.mp4", "/a/b/d.mp4", false)]
    public void Detects_when_output_would_overwrite_the_source(string a, string b, bool same) =>
        Assert.Equal(same, RemuxService.IsSameFile(a, b));
}

public class RemuxServiceBehaviourTests
{
    private sealed class FakeProbe : IMediaProbe
    {
        private readonly string _json;
        public FakeProbe(string json) => _json = json;
        public Task<SourceMedia> ProbeAsync(string filePath, CancellationToken ct = default) =>
            Task.FromResult(SourceMedia.Parse(_json, filePath));
    }

    private sealed class FakeRunner : IProcessRunner
    {
        public List<IReadOnlyList<string>> Calls { get; } = new();
        public ProcessResult Result { get; set; } = new(0, "", "");
        public Func<string, IReadOnlyList<string>, ProcessResult>? Handler { get; set; }

        public Task<ProcessResult> RunAsync(string fileName, IReadOnlyList<string> arguments, CancellationToken ct = default)
        {
            Calls.Add(arguments);
            return Task.FromResult(Handler?.Invoke(fileName, arguments) ?? Result);
        }
    }

    private sealed class FakeHasher : IVideoStreamHasher
    {
        private readonly Queue<string> _hashes;
        public FakeHasher(params string[] hashes) => _hashes = new Queue<string>(hashes);
        public Task<string> HashAsync(string filePath, CancellationToken ct = default) =>
            Task.FromResult(_hashes.Count > 0 ? _hashes.Dequeue() : "same");
    }

    private const string Clip4x3 = """
        {"streams":[{"index":0,"codec_type":"video","codec_name":"hevc","width":1280,
                     "height":960,"pix_fmt":"yuvj420p","color_range":"pc"}]}
        """;

    private static string TempDir()
    {
        string d = Path.Combine(Path.GetTempPath(), "srec-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(d);
        return d;
    }

    [Fact]
    public async Task Refuses_to_write_output_over_the_source()
    {
        string dir = TempDir();
        string input = Path.Combine(dir, "clip.mp4");
        await File.WriteAllTextAsync(input, "x");

        var service = new RemuxService(new FakeProbe(Clip4x3), new FakeRunner(), new FakeHasher());

        // Same folder for input and output means the output path equals the input path.
        RemuxException ex = await Assert.ThrowsAsync<RemuxException>(
            () => service.RemuxAsync(input, dir));
        Assert.Contains("overwrite", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Discards_the_output_when_the_video_stream_changed()
    {
        string inDir = TempDir(), outDir = TempDir();
        string input = Path.Combine(inDir, "clip.mp4");
        await File.WriteAllTextAsync(input, "x");

        var runner = new FakeRunner
        {
            Handler = (_, args) =>
            {
                // Simulate ffmpeg producing the partial file.
                File.WriteAllText(args[^1], "output");
                return new ProcessResult(0, "", "");
            },
        };
        var service = new RemuxService(
            new FakeProbe(Clip4x3), runner, new FakeHasher("hash-before", "hash-AFTER"));

        await Assert.ThrowsAsync<IntegrityCheckException>(() => service.RemuxAsync(input, outDir));

        Assert.Empty(Directory.GetFiles(outDir));   // nothing left behind, not even a partial
    }

    [Fact]
    public async Task Leaves_no_partial_file_when_ffmpeg_fails()
    {
        string inDir = TempDir(), outDir = TempDir();
        string input = Path.Combine(inDir, "clip.mp4");
        await File.WriteAllTextAsync(input, "x");

        var runner = new FakeRunner
        {
            Handler = (_, args) =>
            {
                File.WriteAllText(args[^1], "half a file");
                return new ProcessResult(1, "", "Invalid data found when processing input");
            },
        };
        var service = new RemuxService(new FakeProbe(Clip4x3), runner, new FakeHasher());

        await Assert.ThrowsAsync<RemuxException>(() => service.RemuxAsync(input, outDir));
        Assert.Empty(Directory.GetFiles(outDir));
    }

    [Fact]
    public async Task Successful_remux_commits_the_output_and_reports_the_stretch()
    {
        string inDir = TempDir(), outDir = TempDir();
        string input = Path.Combine(inDir, "Double_kill.mp4");
        await File.WriteAllTextAsync(input, "x");

        var runner = new FakeRunner
        {
            Handler = (_, args) => { File.WriteAllText(args[^1], "out"); return new ProcessResult(0, "", ""); },
        };
        var service = new RemuxService(
            new FakeProbe(Clip4x3), runner, new FakeHasher("same", "same"));

        RemuxResult result = await service.RemuxAsync(input, outDir);

        Assert.True(result.IntegrityVerified);
        Assert.True(result.AspectOverridden);
        Assert.Equal(new AspectRatio(4, 3), result.ResultingSampleAspect);
        Assert.Equal(AspectRatio.Widescreen, result.ResultingDisplayAspect);
        Assert.True(File.Exists(Path.Combine(outDir, "Double_kill.mp4")));
        Assert.Empty(Directory.GetFiles(outDir, "*partial*"));
    }
}
