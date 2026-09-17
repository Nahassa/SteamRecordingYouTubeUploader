using SteamClipRemuxer.Core.Configuration;
using SteamClipRemuxer.Core.Execution;
using SteamClipRemuxer.Core.Planning;
using SteamClipRemuxer.Core.Probing;
using Xunit;

namespace SteamClipRemuxer.Core.Tests;

/// <summary>
/// The geometry, the command and the checks that replace the payload hash.
///
/// All three are pure, which is the point: the one re-encode in this pipeline should be settled by
/// tests rather than discovered during an upload to YouTube.
/// </summary>
public class UpscaleTests
{
    /// <summary>
    /// What the stitch actually hands the upscaler: 1280x960 stored, tagged to display 16:9, full
    /// range, 8-bit, one stereo AAC track.
    /// </summary>
    private static SourceMedia Source(
        int width = 1280,
        int height = 960,
        string sampleAspect = "4:3",
        string pixelFormat = "yuvj420p",
        string colorRange = "pc",
        string? primaries = "bt709",
        string? transfer = "bt709",
        string? space = "bt709",
        int? frames = 557,
        double duration = 9.28,
        string? audioCodec = "aac",
        int? audioChannels = 2,
        string path = "/out/clip.mp4") => new()
    {
        FilePath = path,
        Width = width,
        Height = height,
        VideoCodec = "hevc",
        SampleAspect = AspectRatio.TryParse(sampleAspect) ?? AspectRatio.Square,
        PixelFormat = pixelFormat,
        ColorRange = colorRange,
        ColorSpace = space,
        ColorPrimaries = primaries,
        ColorTransfer = transfer,
        ChromaLocation = "left",
        FrameCount = frames,
        DurationSeconds = duration,
        AudioCodec = audioCodec,
        AudioChannels = audioChannels,
        Streams = new[]
        {
            new MediaStream(0, "video", "hevc"),
            new MediaStream(1, "audio", "aac"),
        },
    };

    /// <summary>The scaled result, as it comes back from a correct encode.</summary>
    private static SourceMedia Scaled(
        int width = 1920,
        int height = 1080,
        string pixelFormat = "yuv420p10le",
        string colorRange = "pc",
        string? primaries = "bt709",
        string? transfer = "bt709",
        string? space = "bt709",
        int? frames = 557,
        double duration = 9.28,
        string? audioCodec = "aac",
        int? audioChannels = 2,
        int streams = 2) => new()
    {
        FilePath = "/tmp/clip 1080p.mp4",
        Width = width,
        Height = height,
        VideoCodec = "hevc",
        // Square pixels: the upscale resolves the stretch into real geometry, so 1920x1080
        // already displays 16:9 without anything being tagged.
        SampleAspect = AspectRatio.Square,
        PixelFormat = pixelFormat,
        ColorRange = colorRange,
        ColorSpace = space,
        ColorPrimaries = primaries,
        ColorTransfer = transfer,
        FrameCount = frames,
        DurationSeconds = duration,
        AudioCodec = audioCodec,
        AudioChannels = audioChannels,
        Streams = Enumerable.Range(0, streams)
            .Select(i => new MediaStream(i, i == 0 ? "video" : "audio", i == 0 ? "hevc" : "aac"))
            .ToList(),
    };

    private static UpscaleTarget Target(
        SourceMedia? source = null, YouTubeUpscale to = YouTubeUpscale.To1080p) =>
        UpscaleTarget.For(source ?? Source(), to);

    // -------------------------------------------------------------- geometry

    [Fact]
    public void A_stretched_steam_clip_reaches_the_1080_rung_without_distortion()
    {
        // 1280x960 at SAR 4:3 displays 16:9, so 1080 lines means 1920 columns. Taking the stored
        // 4:3 instead would give 1440x1080 and squash the picture.
        UpscaleTarget target = Target();
        Assert.True(target.ShouldScale);
        Assert.Equal(1920, target.Width);
        Assert.Equal(1080, target.Height);
    }

    [Fact]
    public void A_square_pixel_four_by_three_clip_keeps_its_own_shape()
    {
        UpscaleTarget target = Target(Source(sampleAspect: "1:1"));
        Assert.Equal(1440, target.Width);
        Assert.Equal(1080, target.Height);
    }

    [Fact]
    public void The_1440_setting_scales_to_1440_lines()
    {
        UpscaleTarget target = Target(to: YouTubeUpscale.To1440p);
        Assert.Equal(2560, target.Width);
        Assert.Equal(1440, target.Height);
    }

    [Theory]
    [InlineData("4:3")]
    [InlineData("1:1")]
    [InlineData("16:15")]
    [InlineData("64:45")]
    public void Both_dimensions_are_always_even(string sampleAspect)
    {
        // 4:2:0 subsamples chroma by two in each axis, so an odd dimension is rejected outright.
        UpscaleTarget target = Target(Source(sampleAspect: sampleAspect));
        Assert.Equal(0, target.Width % 2);
        Assert.Equal(0, target.Height % 2);
    }

    [Fact]
    public void Off_scales_nothing()
    {
        Assert.False(Target(to: YouTubeUpscale.Off).ShouldScale);
        Assert.Null(UpscaleTarget.LinesFor(YouTubeUpscale.Off));
    }

    [Fact]
    public void A_clip_already_at_the_target_is_left_alone()
    {
        // A resample is lossy. Performing one to reach the size the file already is would be
        // degradation with nothing bought.
        UpscaleTarget target = Target(Source(width: 1920, height: 1080, sampleAspect: "1:1"));
        Assert.False(target.ShouldScale);
        Assert.Contains("already", target.Skip);
    }

    [Fact]
    public void A_clip_above_the_target_is_left_alone_too()
    {
        Assert.False(Target(Source(width: 2560, height: 1440, sampleAspect: "1:1")).ShouldScale);
    }

    [Fact]
    public void Building_a_command_for_a_skipped_target_is_refused_rather_than_guessed() =>
        Assert.Throws<InvalidOperationException>(() => ClipUpscaleService.BuildArguments(
            Source(), Target(to: YouTubeUpscale.Off), UpscaleEncoder.Software, "/tmp/o.mp4"));

    // --------------------------------------------------------------- command

    private static IReadOnlyList<string> Args(
        UpscaleEncoder? encoder = null, SourceMedia? source = null, bool zscale = true)
    {
        SourceMedia media = source ?? Source();
        return ClipUpscaleService.BuildArguments(
            media, Target(media), encoder ?? UpscaleEncoder.Software, "/tmp/o.mp4", zscale);
    }

    private static string Filter(IReadOnlyList<string> args) =>
        args[args.ToList().IndexOf("-vf") + 1];

    private static string? After(IReadOnlyList<string> args, string flag)
    {
        int i = args.ToList().IndexOf(flag);
        return i >= 0 && i + 1 < args.Count ? args[i + 1] : null;
    }

    [Fact]
    public void Full_range_survives_in_both_vocabularies()
    {
        // zscale spells it full/limited and the output tag spells it pc/tv. Mixing the two is how
        // full-range footage comes out tagged limited, which crushes every black in the picture.
        IReadOnlyList<string> args = Args();
        Assert.Contains("rin=full:r=full", Filter(args));
        Assert.Equal("pc", After(args, "-color_range"));
    }

    [Fact]
    public void Limited_range_source_stays_limited()
    {
        IReadOnlyList<string> args = Args(source: Source(colorRange: "tv"));
        Assert.Contains("rin=limited:r=limited", Filter(args));
        Assert.Equal("tv", After(args, "-color_range"));
    }

    [Fact]
    public void Colour_tags_are_carried_from_the_probe_not_hardcoded()
    {
        IReadOnlyList<string> args = Args(source: Source(
            primaries: "bt2020", transfer: "smpte2084", space: "bt2020nc"));

        Assert.Equal("bt2020", After(args, "-color_primaries"));
        Assert.Equal("smpte2084", After(args, "-color_trc"));
        Assert.Equal("bt2020nc", After(args, "-colorspace"));
        Assert.Contains("pin=bt2020:p=bt2020", Filter(args));
        Assert.Contains("tin=smpte2084:t=smpte2084", Filter(args));
    }

    [Fact]
    public void An_unstated_colour_tag_falls_back_to_bt709_and_is_never_left_blank()
    {
        // Leaving the field off pushes the guess onto every player, which is how one file looks
        // different in two apps.
        SourceMedia vague = Source(primaries: null, transfer: "unknown", space: null);
        IReadOnlyList<string> args = Args(source: vague);

        Assert.Equal("bt709", After(args, "-color_primaries"));
        Assert.Equal("bt709", After(args, "-color_trc"));
        Assert.Equal("bt709", After(args, "-colorspace"));
        Assert.True(ClipUpscaleService.ColorWasAssumed(vague));
        Assert.False(ClipUpscaleService.ColorWasAssumed(Source()));
    }

    [Fact]
    public void Chroma_placement_is_carried_too() =>
        Assert.Equal("left", After(Args(), "-chroma_sample_location"));

    [Fact]
    public void The_software_path_ends_in_a_ten_bit_format_and_never_in_nv12()
    {
        IReadOnlyList<string> args = Args();
        Assert.EndsWith("format=yuv420p10le", Filter(args));
        Assert.DoesNotContain("nv12", Filter(args));
        Assert.Equal("yuv420p10le", After(args, "-pix_fmt"));
        Assert.Equal("main10", After(args, "-profile:v"));
    }

    [Fact]
    public void The_hardware_path_uses_nvencs_own_ten_bit_surface_format()
    {
        // p010le, never yuv420p10le, which NVENC rejects - and never nv12, which is 8-bit and
        // would throw away the precision this path exists to keep.
        IReadOnlyList<string> args = Args(UpscaleEncoder.Nvenc);
        Assert.EndsWith("format=p010le", Filter(args));
        Assert.Equal("p010le", After(args, "-pix_fmt"));
        Assert.DoesNotContain("nv12", string.Join(" ", args));
        Assert.DoesNotContain("yuv420p10le", string.Join(" ", args));
    }

    [Fact]
    public void Constant_quality_on_nvenc_always_carries_the_zero_bitrate()
    {
        // Without -b:v 0 some builds apply a default bitrate cap and ignore -cq entirely, which
        // is the usual reason NVENC gets called bad.
        List<string> args = Args(UpscaleEncoder.Nvenc).ToList();
        Assert.Contains("-cq", args);
        Assert.Equal("vbr", After(args, "-rc"));
        Assert.Equal("0", After(args, "-b:v"));
    }

    [Fact]
    public void The_resolved_encoder_is_the_only_thing_that_decides_the_command()
    {
        // The defect this guards against: a fallback was logged and then the argument builder
        // re-read a mode flag and emitted the other encoder anyway.
        Assert.Contains("libx265", Args(UpscaleEncoder.Software));
        Assert.DoesNotContain("hevc_nvenc", Args(UpscaleEncoder.Software));
        Assert.Contains("hevc_nvenc", Args(UpscaleEncoder.Nvenc));
        Assert.DoesNotContain("libx265", Args(UpscaleEncoder.Nvenc));
    }

    [Fact]
    public void Audio_is_copied_and_every_stream_is_mapped()
    {
        // No -c:a would let the container default re-encode audio on every upload, a generation
        // loss invisible in the log; no -map 0 would drop a microphone track entirely.
        List<string> args = Args().ToList();
        Assert.Equal("copy", After(args, "-c:a"));
        Assert.Equal("0", After(args, "-map"));
        Assert.Equal("0", After(args, "-map_metadata"));
        Assert.Equal("0", After(args, "-map_chapters"));
    }

    [Fact]
    public void Timing_is_passed_through_rather_than_forced()
    {
        // Steam's capture is variable frame rate; forcing constant duplicates frames and drifts
        // the audio against the picture.
        Assert.Equal("passthrough", After(Args(), "-fps_mode"));
    }

    [Fact]
    public void No_aspect_is_forced_because_the_scaled_pixels_are_already_the_right_shape()
    {
        List<string> args = Args().ToList();
        Assert.DoesNotContain("-aspect", args);
        Assert.DoesNotContain("setdar", Filter(args));
    }

    [Fact]
    public void Hevc_in_mp4_is_tagged_so_it_will_actually_play() =>
        Assert.Equal("hvc1", After(Args(), "-tag:v"));

    [Fact]
    public void No_tune_is_passed_to_x265()
    {
        // The psnr and ssim tunes actively degrade what a person sees, and grain is for film.
        Assert.DoesNotContain("-tune", Args(UpscaleEncoder.Software).ToList());
    }

    [Fact]
    public void The_swscale_fallback_still_scales_and_still_tags_colour()
    {
        // zscale needs libzimg, which is not universal. The fallback is a different input to the
        // same builder, never a second copy of it - the drifted-duplicate defect.
        IReadOnlyList<string> args = Args(zscale: false);
        Assert.StartsWith("scale=1920:1080:flags=lanczos", Filter(args));
        Assert.DoesNotContain("zscale", Filter(args));
        Assert.EndsWith("format=yuv420p10le", Filter(args));
        Assert.Equal("pc", After(args, "-color_range"));
    }

    [Fact]
    public void The_input_is_the_probed_file_and_the_output_is_last()
    {
        IReadOnlyList<string> args = Args();
        Assert.Equal("/out/clip.mp4", After(args, "-i"));
        Assert.Equal("/tmp/o.mp4", args[^1]);
    }

    // ----------------------------------------------------------- capability

    [Fact]
    public void An_encoder_trial_runs_the_exact_options_it_will_emit()
    {
        // Probing a bare -c:v hevc_nvenc would pass on a build that then rejects -b_ref_mode.
        IReadOnlyList<string> trial = EncoderCapability.BuildEncoderTrial(
            UpscaleEncoder.Nvenc.Name, UpscaleEncoder.Nvenc.Options);

        Assert.Contains("hevc_nvenc", trial);
        Assert.Contains("-b_ref_mode", trial);
        Assert.Contains("middle", trial);
        // Encoded into nothing, so a trial costs no disk and no real footage.
        Assert.Equal("-", trial[^1]);
        Assert.Equal("null", trial[^2]);
    }

    [Fact]
    public void A_filter_trial_is_fed_the_pixel_format_the_real_source_has()
    {
        // zscale refuses the deprecated full-range formats on some builds, and yuvj420p is
        // exactly what Steam records - so a trial against the default would answer the wrong
        // question.
        IReadOnlyList<string> trial = EncoderCapability.BuildFilterTrial("zscale=w=64:h=64", "yuvj420p");
        Assert.Contains(trial, a => a.Contains("format=yuvj420p"));
        Assert.Contains("zscale=w=64:h=64", trial);
    }

    // ---------------------------------------------------------- verification

    [Fact]
    public void A_correct_scale_passes_every_check() =>
        Assert.Null(ClipUpscaleService.Wrong(Source(), Scaled(), Target(), ssim: 0.995));

    [Fact]
    public void The_wrong_size_is_rejected() =>
        Assert.Contains("1280x720", ClipUpscaleService.Wrong(
            Source(), Scaled(width: 1280, height: 720), Target(), 0.995));

    [Fact]
    public void A_duration_drift_beyond_half_a_second_is_rejected()
    {
        Assert.Null(ClipUpscaleService.Wrong(Source(), Scaled(duration: 9.7), Target(), 0.995));
        Assert.Contains("away from the source", ClipUpscaleService.Wrong(
            Source(), Scaled(duration: 9.9), Target(), 0.995));
    }

    [Fact]
    public void A_single_missing_frame_is_rejected() =>
        Assert.Contains("556 frames", ClipUpscaleService.Wrong(
            Source(), Scaled(frames: 556), Target(), 0.995));

    [Fact]
    public void An_unknown_frame_count_on_either_side_is_not_treated_as_a_mismatch()
    {
        // Some containers do not count frames. That is a reason to stay quiet, not to reject a
        // file for having no answer.
        Assert.Null(ClipUpscaleService.Wrong(Source(frames: null), Scaled(), Target(), 0.995));
        Assert.Null(ClipUpscaleService.Wrong(Source(), Scaled(frames: null), Target(), 0.995));
    }

    [Fact]
    public void A_changed_display_aspect_is_rejected()
    {
        // 1440x1080 square-pixelled displays 4:3, where the source displayed 16:9. The size is
        // what was asked for and the picture is still squashed, which is why geometry is checked
        // as an aspect and not only as a resolution.
        var squashed = new UpscaleTarget { Width = 1440, Height = 1080 };

        Assert.Contains("displays at", ClipUpscaleService.Wrong(
            Source(), Scaled(width: 1440), squashed, 0.995));
    }

    [Fact]
    public void Nvenc_writing_limited_range_is_rejected_rather_than_accepted()
    {
        // NVENC has a long history of tagging tv whatever it was handed. A range flip crushes
        // every black in the picture, which matters far more than the 0.19 dB an encoder choice
        // is worth - so this is the check the hardware path exists to be caught by.
        string? wrong = ClipUpscaleService.Wrong(Source(), Scaled(colorRange: "tv"), Target(), 0.995);
        Assert.Contains("colour range is tv", wrong);
    }

    [Theory]
    [InlineData("bt2020", null, null, "primaries")]
    [InlineData(null, "smpte2084", null, "transfer")]
    [InlineData(null, null, "bt2020nc", "colour space")]
    public void A_changed_colour_tag_is_rejected(
        string? primaries, string? transfer, string? space, string expected)
    {
        SourceMedia scaled = Scaled(
            primaries: primaries ?? "bt709",
            transfer: transfer ?? "bt709",
            space: space ?? "bt709");

        Assert.Contains(expected, ClipUpscaleService.Wrong(Source(), scaled, Target(), 0.995));
    }

    [Fact]
    public void Losing_bit_depth_is_rejected() =>
        Assert.Contains("8-bit against", ClipUpscaleService.Wrong(
            Source(pixelFormat: "yuv420p10le"), Scaled(pixelFormat: "yuv420p"), Target(), 0.995));

    [Fact]
    public void Re_encoded_or_dropped_audio_is_rejected()
    {
        Assert.Contains("its audio is opus", ClipUpscaleService.Wrong(
            Source(), Scaled(audioCodec: "opus"), Target(), 0.995));

        Assert.Contains("1 channel", ClipUpscaleService.Wrong(
            Source(), Scaled(audioChannels: 1), Target(), 0.995));
    }

    [Fact]
    public void A_dropped_stream_is_rejected() =>
        Assert.Contains("1 stream(s)", ClipUpscaleService.Wrong(
            Source(), Scaled(streams: 1), Target(), 0.995));

    [Fact]
    public void Similarity_below_the_floor_is_rejected()
    {
        Assert.Null(ClipUpscaleService.Wrong(Source(), Scaled(), Target(), 0.98));
        Assert.Contains("under the 0.98 floor", ClipUpscaleService.Wrong(
            Source(), Scaled(), Target(), 0.97));
    }

    [Fact]
    public void Similarity_that_could_not_be_measured_is_not_a_pass() =>
        Assert.Contains("could not be measured", ClipUpscaleService.Wrong(
            Source(), Scaled(), Target(), ssim: null));

    // ------------------------------------------------------------ ssim parse

    [Fact]
    public void The_similarity_figure_is_read_out_of_ffmpegs_summary()
    {
        const string output =
            "[Parsed_ssim_2 @ 0x5] SSIM Y:0.993215 U:0.998001 V:0.997834 All:0.994712 (22.76)\n";

        Assert.Equal(0.994712, ClipUpscaleService.ParseSsim(output)!.Value, 6);
    }

    [Fact]
    public void The_summary_wins_over_the_per_frame_lines()
    {
        // A per-frame log carries the same key. The last one is the summary.
        const string output =
            "n:1 All:0.5 (3.0)\nn:2 All:0.6 (4.0)\nSSIM Y:0.99 U:0.99 V:0.99 All:0.99 (20.0)\n";

        Assert.Equal(0.99, ClipUpscaleService.ParseSsim(output)!.Value, 6);
    }

    [Fact]
    public void Output_with_no_figure_in_it_reads_as_unmeasured() =>
        Assert.Null(ClipUpscaleService.ParseSsim("Conversion failed!\n"));

    [Fact]
    public void The_similarity_pass_scales_the_output_back_to_the_sources_own_geometry()
    {
        // Comparing 1920x1080 against 1280x960 directly would measure the resize, not the damage.
        IReadOnlyList<string> args = ClipUpscaleService.BuildSsimArguments("/tmp/s.mp4", Source());
        string graph = args[args.ToList().IndexOf("-lavfi") + 1];

        Assert.Contains("scale=1280:960", graph);
        Assert.Contains("ssim", graph);
        // Distorted first, reference second, both anchored to zero so a start-time offset in the
        // concatenated source cannot slide the comparison.
        Assert.Contains("[0:v]", graph);
        Assert.Contains("setpts=PTS-STARTPTS", graph);
    }
}
