using SteamClipRemuxer.Core.Probing;
using Xunit;

namespace SteamClipRemuxer.Core.Tests;

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

    [Fact]
    public void Reads_every_colour_tag_a_reencode_has_to_carry()
    {
        // Filters do not reliably carry these across a format conversion, so anything that
        // re-encodes has to read them here and tag them on the output explicitly.
        SourceMedia m = RealClip();
        Assert.Equal("bt709", m.ColorPrimaries);
        Assert.Equal("bt709", m.ColorTransfer);
        Assert.Equal("left", m.ChromaLocation);
    }

    [Fact]
    public void Bit_depth_comes_from_the_pixel_format_when_the_container_omits_it()
    {
        // This is the case that matters: Steam's recordings carry no bits_per_raw_sample at all,
        // so an implementation reading only that key reports nothing for every file this tool
        // handles.
        SourceMedia m = RealClip();
        Assert.Null(m.BitsPerRawSample);
        Assert.Equal(8, m.BitDepth);
        Assert.Equal("Main", m.Profile);
    }

    [Theory]
    [InlineData("yuv420p", 8)]
    [InlineData("yuvj420p", 8)]
    [InlineData("nv12", 8)]
    [InlineData("yuv420p10le", 10)]
    [InlineData("p010le", 10)]
    [InlineData("yuv420p12le", 12)]
    public void Bit_depth_is_inferred_for_each_pixel_format(string pixelFormat, int expected)
    {
        string json = "{\"streams\":[{\"index\":0,\"codec_type\":\"video\",\"codec_name\":\"hevc\","
            + "\"width\":1280,\"height\":960,\"pix_fmt\":\"" + pixelFormat + "\"}],"
            + "\"format\":{\"duration\":\"9.0\"}}";

        Assert.Equal(expected, SourceMedia.Parse(json, "/c.mp4").BitDepth);
    }

    [Fact]
    public void A_stated_bit_depth_wins_over_the_pixel_format()
    {
        string json = "{\"streams\":[{\"index\":0,\"codec_type\":\"video\",\"codec_name\":\"hevc\","
            + "\"width\":1280,\"height\":960,\"pix_fmt\":\"yuv420p\",\"bits_per_raw_sample\":\"10\"}],"
            + "\"format\":{\"duration\":\"9.0\"}}";

        // Quoted, because ffprobe emits this one as a string in some builds and a number in
        // others; reading only the number would drop it.
        Assert.Equal(10, SourceMedia.Parse(json, "/c.mp4").BitDepth);
    }

    [Fact]
    public void Reads_the_frame_count_and_the_audio_layout()
    {
        // All three feed the checks that replace the payload hash once a file is re-encoded.
        SourceMedia m = RealClip();
        Assert.Equal(557, m.FrameCount);
        Assert.Equal("aac", m.AudioCodec);
        Assert.Equal(2, m.AudioChannels);
        Assert.Equal("60/1", m.RFrameRate);
    }

    [Fact]
    public void A_file_with_no_audio_reports_none_rather_than_guessing()
    {
        string json = "{\"streams\":[{\"index\":0,\"codec_type\":\"video\",\"codec_name\":\"hevc\","
            + "\"width\":1280,\"height\":960,\"pix_fmt\":\"yuv420p\"}],"
            + "\"format\":{\"duration\":\"9.0\"}}";

        SourceMedia m = SourceMedia.Parse(json, "/c.mp4");
        Assert.Null(m.AudioCodec);
        Assert.Null(m.AudioChannels);
        Assert.Equal(0, m.AudioStreamCount);
    }
}
