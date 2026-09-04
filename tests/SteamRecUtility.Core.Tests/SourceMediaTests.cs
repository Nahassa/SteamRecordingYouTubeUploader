using SteamRecUtility.Core.Probing;
using Xunit;

namespace SteamRecUtility.Core.Tests;

public class SourceMediaTests
{
    // Real `ffprobe -print_format json -show_format -show_streams` output from a Steam
    // Counter-Strike 2 recording. Only the filename was substituted.
    private static SourceMedia RealClip() => SourceMedia.Parse(
        File.ReadAllText(Path.Combine("Fixtures", "cs2_1280x960.json")), "C:/rec/Double_kill.mp4");

    [Fact]
    public void Reads_geometry_from_real_steam_recording()
    {
        SourceMedia m = RealClip();
        Assert.Equal(1280, m.Width);
        Assert.Equal(960, m.Height);
        Assert.Equal(AspectRatio.Square, m.SampleAspect);
        Assert.Equal(new AspectRatio(4, 3), m.DisplayAspect);
    }

    [Fact]
    public void Reads_codec_and_colour_from_real_steam_recording()
    {
        SourceMedia m = RealClip();
        Assert.Equal("hevc", m.VideoCodec);
        Assert.True(m.IsHevc);
        Assert.Equal("yuvj420p", m.PixelFormat);
        // Steam records full range. Anything that re-tags this as limited crushes blacks.
        Assert.Equal("pc", m.ColorRange);
        Assert.True(m.IsFullRange);
    }

    [Fact]
    public void Reads_all_streams_not_just_video()
    {
        SourceMedia m = RealClip();
        Assert.Equal(2, m.Streams.Count);
        Assert.Equal(1, m.AudioStreamCount);
        Assert.Contains(m.Streams, s => s.CodecType == "audio" && s.CodecName == "aac");
    }

    [Fact]
    public void Absent_sample_aspect_ratio_means_square_pixels()
    {
        // ffprobe omits the field entirely for square pixels.
        const string json = """
        {"streams":[{"index":0,"codec_type":"video","codec_name":"h264",
                     "width":1920,"height":1080,"pix_fmt":"yuv420p"}]}
        """;
        SourceMedia m = SourceMedia.Parse(json, "x.mp4");
        Assert.Equal(AspectRatio.Square, m.SampleAspect);
        Assert.Equal(AspectRatio.Widescreen, m.DisplayAspect);
    }

    [Fact]
    public void Unknown_sample_aspect_ratio_means_square_pixels()
    {
        const string json = """
        {"streams":[{"index":0,"codec_type":"video","codec_name":"h264","sample_aspect_ratio":"0:1",
                     "width":1920,"height":1080,"pix_fmt":"yuv420p"}]}
        """;
        Assert.Equal(AspectRatio.Square, SourceMedia.Parse(json, "x.mp4").SampleAspect);
    }

    [Fact]
    public void Anamorphic_source_reports_its_real_display_aspect()
    {
        const string json = """
        {"streams":[{"index":0,"codec_type":"video","codec_name":"hevc","sample_aspect_ratio":"4:3",
                     "width":1280,"height":960,"pix_fmt":"yuv420p"}]}
        """;
        SourceMedia m = SourceMedia.Parse(json, "x.mp4");
        Assert.Equal(AspectRatio.Widescreen, m.DisplayAspect);
    }

    [Fact]
    public void Rejects_a_file_with_no_video_stream()
    {
        const string json = """{"streams":[{"index":0,"codec_type":"audio","codec_name":"aac"}]}""";
        Assert.Throws<InvalidMediaException>(() => SourceMedia.Parse(json, "audio-only.m4a"));
    }

    [Fact]
    public void Probe_arguments_request_streams_and_format_as_json()
    {
        IReadOnlyList<string> args = MediaProbe.BuildArguments("C:/a b/clip.mp4");
        Assert.Contains("-show_streams", args);
        Assert.Contains("-show_format", args);
        Assert.Equal("C:/a b/clip.mp4", args[^1]);
    }
}
