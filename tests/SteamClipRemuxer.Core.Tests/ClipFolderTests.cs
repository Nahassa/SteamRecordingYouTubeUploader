using SteamClipRemuxer.Core.Execution;
using SteamClipRemuxer.Core.Steam;
using Xunit;

namespace SteamClipRemuxer.Core.Tests;

public class ClipFolderTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "sclip-folders-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); } catch (IOException) { }
        GC.SuppressFinalize(this);
    }

    /// <summary>Builds a clip folder carrying a real clip.pb, so the manifest is genuine.</summary>
    private string MakeClip(string parent, string name = "clip_730_20260828_221805")
    {
        string clip = Path.Combine(parent, name);
        Directory.CreateDirectory(Path.Combine(clip, "timelines"));

        string video = Path.Combine(clip, "video", "bg_730_20260828_204356");
        Directory.CreateDirectory(video);

        File.Copy(Path.Combine("Fixtures", "clip_cropped_7435ms.pb"), Path.Combine(clip, "clip.pb"));
        File.WriteAllBytes(Path.Combine(video, "init-stream0.m4s"), new byte[] { 1 });
        File.WriteAllBytes(Path.Combine(video, "chunk-stream0-00001.m4s"), new byte[] { 2 });
        return clip;
    }

    [Fact]
    public void Finds_clips_when_pointed_at_the_recording_folder()
    {
        string clips = Path.Combine(_root, ClipFolder.ClipsFolderName);
        Directory.CreateDirectory(clips);
        MakeClip(clips);

        Assert.Equal(clips, ClipFolder.ResolveClipsRoot(_root));
        Assert.Single(ClipFolder.Discover(_root));
    }

    [Fact]
    public void Finds_clips_when_pointed_straight_at_the_clips_folder()
    {
        // Pointing the app at "clips" is the obvious thing to try, and finding nothing gives no
        // clue which of the two folders was wanted.
        string clips = Path.Combine(_root, ClipFolder.ClipsFolderName);
        Directory.CreateDirectory(clips);
        MakeClip(clips);

        Assert.Equal(clips, ClipFolder.ResolveClipsRoot(clips));
        Assert.Single(ClipFolder.Discover(clips));
    }

    [Fact]
    public void A_folder_with_no_clips_resolves_to_nothing()
    {
        Directory.CreateDirectory(_root);
        Assert.Null(ClipFolder.ResolveClipsRoot(_root));
        Assert.Empty(ClipFolder.Discover(_root));
    }

    [Fact]
    public void A_missing_folder_is_not_an_error()
    {
        Assert.Null(ClipFolder.ResolveClipsRoot(Path.Combine(_root, "nope")));
        Assert.Empty(ClipFolder.Discover(Path.Combine(_root, "nope")));
    }

    [Fact]
    public void Reads_the_manifest_timeline_and_video_folder()
    {
        Directory.CreateDirectory(_root);
        string clip = MakeClip(_root);
        File.WriteAllText(
            Path.Combine(clip, "timelines", "timeline_73020260828_204331.json"), "{\"entries\":[]}");

        ClipFolder? read = ClipFolder.TryRead(clip);

        Assert.NotNull(read);
        Assert.Equal("bg_730_20260828_204356", Path.GetFileName(read!.VideoPath));
        Assert.NotNull(read.TimelinePath);
        Assert.Equal(TimeSpan.FromMilliseconds(7435), read.Manifest.Duration);
    }

    [Fact]
    public void A_clip_without_video_segments_is_skipped_rather_than_failing_the_scan()
    {
        Directory.CreateDirectory(_root);
        string clip = Path.Combine(_root, "clip_730_broken");
        Directory.CreateDirectory(clip);
        File.Copy(Path.Combine("Fixtures", "clip_cropped_7435ms.pb"), Path.Combine(clip, "clip.pb"));

        Assert.Null(ClipFolder.TryRead(clip));
    }

    [Fact]
    public void One_unreadable_clip_does_not_hide_the_others()
    {
        Directory.CreateDirectory(_root);
        MakeClip(_root, "clip_730_20260828_221805");

        string broken = Path.Combine(_root, "clip_730_broken");
        Directory.CreateDirectory(broken);
        File.WriteAllText(Path.Combine(broken, "clip.pb"), "not a protobuf");

        Assert.Single(ClipFolder.Discover(_root));
    }
}

public class DashSegmentsTests : IDisposable
{
    private readonly string _folder = Path.Combine(
        Path.GetTempPath(), "sclip-dash-" + Guid.NewGuid().ToString("N"));

    public DashSegmentsTests() => Directory.CreateDirectory(_folder);

    public void Dispose()
    {
        try { Directory.Delete(_folder, recursive: true); } catch (IOException) { }
        GC.SuppressFinalize(this);
    }

    private void Write(string name, byte content) =>
        File.WriteAllBytes(Path.Combine(_folder, name), new[] { content });

    [Fact]
    public void Orders_segments_numerically_rather_than_as_text()
    {
        // Steam zero-pads today, so an ordinal sort happens to work, but the same assumption
        // about Steam's numbering is what scrambled timeline entries into 0, 1, 10, 2.
        Write("chunk-stream0-00002.m4s", 2);
        Write("chunk-stream0-00010.m4s", 10);
        Write("chunk-stream0-1.m4s", 1);

        string[] names = DashSegments.Chunks(_folder, 0).Select(Path.GetFileName).ToArray()!;

        Assert.Equal(new[] { "chunk-stream0-1.m4s", "chunk-stream0-00002.m4s", "chunk-stream0-00010.m4s" }, names);
    }

    [Fact]
    public void Only_reports_a_stream_when_both_parts_are_present()
    {
        Write("chunk-stream1-00001.m4s", 1);
        Assert.False(DashSegments.HasStream(_folder, 1));

        Write("init-stream1.m4s", 9);
        Assert.True(DashSegments.HasStream(_folder, 1));
    }

    [Fact]
    public async Task Assembles_the_init_segment_followed_by_the_media_segments()
    {
        Write("init-stream0.m4s", 9);
        Write("chunk-stream0-00001.m4s", 1);
        Write("chunk-stream0-00002.m4s", 2);

        string destination = Path.Combine(_folder, "out", "stream0.mp4");
        await DashSegments.AssembleAsync(_folder, 0, destination);

        // Byte concatenation and nothing else: this is what keeps the pipeline lossless.
        Assert.Equal(new byte[] { 9, 1, 2 }, await File.ReadAllBytesAsync(destination));
    }
}

public class ClipTrimTests
{
    private static ClipManifest Manifest() =>
        ClipManifest.Parse(File.ReadAllBytes(Path.Combine("Fixtures", "clip_cropped_7435ms.pb")))!;

    [Fact]
    public void Trim_offset_is_measured_from_where_the_assembled_segments_begin()
    {
        // Measured on the real clip: the concatenated segments start at 4125.010s and the clip
        // itself at 4126.140s in the video session's clock, so 1.130s is dropped from the head.
        ClipManifest m = Manifest();

        TimeSpan? offset = ClipRemuxService.TrimOffsetFor(m, 4125.010492);

        Assert.NotNull(offset);
        Assert.Equal(1.130, offset!.Value.TotalSeconds, precision: 2);
    }

    [Fact]
    public void The_video_session_offset_is_taken_back_out_of_the_clip_start()
    {
        // clip.pb's start is in the timeline's clock; the segments are in the video session's.
        // Forgetting the difference misplaces the trim by the offset, 25.4s on this clip.
        ClipManifest m = Manifest();

        TimeSpan withOffset = ClipRemuxService.TrimOffsetFor(m, 4125.010492)!.Value;
        double naive = (m.StartInSession - TimeSpan.FromSeconds(4125.010492)).TotalSeconds;

        Assert.Equal(25.388, naive - withOffset.TotalSeconds, precision: 2);
    }

    [Fact]
    public void No_trim_is_reported_when_the_clip_starts_at_the_segment_boundary()
    {
        ClipManifest m = Manifest();
        double exact = (m.StartInSession - m.VideoSessionOffset).TotalSeconds;

        Assert.Null(ClipRemuxService.TrimOffsetFor(m, exact));
    }
}
