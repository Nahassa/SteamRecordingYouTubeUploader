using SteamRecUtility.Core.Planning;
using SteamRecUtility.Core.Probing;
using Xunit;

namespace SteamRecUtility.Core.Tests;

public class RemuxPlannerTests
{
    private static SourceMedia RealClip() => SourceMedia.Parse(
        File.ReadAllText(Path.Combine("Fixtures", "cs2_1280x960.json")), "C:/rec/Double_kill.mp4");

    private static SourceMedia Synthetic(
        int w, int h, string codec = "hevc", string sar = "1:1") =>
        SourceMedia.Parse($$"""
        {"streams":[{"index":0,"codec_type":"video","codec_name":"{{codec}}",
                     "sample_aspect_ratio":"{{sar}}","width":{{w}},"height":{{h}},
                     "pix_fmt":"yuv420p"}]}
        """, "C:/rec/in.mp4");

    private static string Joined(RemuxPlan p) => string.Join(" ", p.Arguments);

    [Fact]
    public void Native_4x3_steam_clip_is_stretched_to_widescreen()
    {
        RemuxPlan plan = RemuxPlanner.Plan(RealClip(), "C:/out/Double_kill.mp4");

        Assert.True(plan.AspectOverridden);
        Assert.Contains("-aspect", plan.Arguments);
        Assert.Equal("16:9", plan.Arguments[plan.Arguments.ToList().IndexOf("-aspect") + 1]);
        // 1280x960 pixels presented as 16:9 requires SAR 4:3.
        Assert.Equal(new AspectRatio(4, 3), plan.ResultingSampleAspect);
    }

    [Fact]
    public void Already_widescreen_clip_is_left_alone()
    {
        RemuxPlan plan = RemuxPlanner.Plan(Synthetic(1920, 1080), "C:/out/a.mp4");

        Assert.False(plan.AspectOverridden);
        Assert.DoesNotContain("-aspect", plan.Arguments);
    }

    [Fact]
    public void Already_anamorphic_widescreen_clip_is_left_alone()
    {
        // 1280x960 already tagged SAR 4:3 displays at 16:9 - re-tagging would be a no-op.
        RemuxPlan plan = RemuxPlanner.Plan(Synthetic(1280, 960, sar: "4:3"), "C:/out/a.mp4");
        Assert.False(plan.AspectOverridden);
    }

    [Fact]
    public void Video_is_always_copied_and_every_stream_is_mapped()
    {
        RemuxPlan plan = RemuxPlanner.Plan(RealClip(), "C:/out/a.mp4");
        string joined = Joined(plan);

        Assert.Contains("-c copy", joined);
        Assert.Contains("-map 0", joined);
        Assert.Contains("-map_metadata 0", joined);
        Assert.Contains("-map_chapters 0", joined);
    }

    [Theory]
    [InlineData("hevc", true)]
    [InlineData("h264", false)]
    [InlineData("av1", false)]
    public void Hvc1_tag_is_applied_only_to_hevc(string codec, bool expected)
    {
        RemuxPlan plan = RemuxPlanner.Plan(Synthetic(1280, 960, codec), "C:/out/a.mp4");
        Assert.Equal(expected, Joined(plan).Contains("-tag:v hvc1"));
    }

    [Fact]
    public void Mp4_only_flags_are_omitted_for_other_containers()
    {
        RemuxPlan plan = RemuxPlanner.Plan(Synthetic(1280, 960), "C:/out/a.mkv");
        string joined = Joined(plan);

        Assert.DoesNotContain("faststart", joined);
        Assert.DoesNotContain("hvc1", joined);
        Assert.Contains("-aspect 16:9", joined);   // the stretch still applies
    }

    [Fact]
    public void Faststart_can_be_disabled()
    {
        RemuxPlan plan = RemuxPlanner.Plan(
            RealClip(), "C:/out/a.mp4", new RemuxOptions { FastStart = false });
        Assert.DoesNotContain("faststart", Joined(plan));
    }

    [Fact]
    public void Target_display_aspect_is_configurable()
    {
        RemuxPlan plan = RemuxPlanner.Plan(
            RealClip(), "C:/out/a.mp4",
            new RemuxOptions { TargetDisplayAspect = new AspectRatio(21, 9) });

        // Ratios are stored in lowest terms, so "21:9" is emitted as the equivalent "7:3".
        Assert.Contains("-aspect 7:3", Joined(plan));
        Assert.Equal(new AspectRatio(21, 9), plan.ResultingDisplayAspect);
    }

    [Fact]
    public void Input_and_output_paths_are_separate_arguments_never_quoted()
    {
        // Paths go through ArgumentList, so spaces and quotes need no escaping and must not
        // arrive wrapped in quote characters.
        var src = SourceMedia.Parse($$"""
        {"streams":[{"index":0,"codec_type":"video","codec_name":"hevc","width":1280,
                     "height":960,"pix_fmt":"yuv420p"}]}
        """, """C:/rec/my "best" clip.mp4""");

        RemuxPlan plan = RemuxPlanner.Plan(src, "C:/out/my clip.mp4");

        Assert.Contains("""C:/rec/my "best" clip.mp4""", plan.Arguments);
        Assert.Equal("C:/out/my clip.mp4", plan.Arguments[^1]);
        Assert.DoesNotContain(plan.Arguments, a => a.StartsWith('"') && a.EndsWith('"') && a.Length > 1);
    }

    // The point of the rewrite: this pipeline must never grow an encoder.
    [Theory]
    [InlineData("-c:v")]
    [InlineData("libx265")]
    [InlineData("hevc_nvenc")]
    [InlineData("av1_nvenc")]
    [InlineData("libsvtav1")]
    [InlineData("-crf")]
    [InlineData("-cq")]
    [InlineData("-preset")]
    [InlineData("-pix_fmt")]
    [InlineData("-vf")]
    [InlineData("scale")]
    [InlineData("eq=")]
    public void Plan_never_contains_encoder_or_filter_flags(string forbidden)
    {
        foreach (SourceMedia src in new[] { RealClip(), Synthetic(1920, 1080), Synthetic(1280, 960, "h264") })
        {
            Assert.DoesNotContain(forbidden, Joined(RemuxPlanner.Plan(src, "C:/out/a.mp4")));
        }
    }

    [Fact]
    public void Audio_is_never_re_encoded()
    {
        // The old pipeline emitted no -c:a at all, so ffmpeg silently re-encoded to AAC ~128k.
        string joined = Joined(RemuxPlanner.Plan(RealClip(), "C:/out/a.mp4"));
        Assert.DoesNotContain("-c:a", joined);
        Assert.DoesNotContain("aac", joined);
        Assert.Contains("-c copy", joined);
    }
}
