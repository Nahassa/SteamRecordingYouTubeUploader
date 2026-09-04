using SteamRecUtility.Core.Probing;
using Xunit;

namespace SteamRecUtility.Core.Tests;

public class AspectRatioTests
{
    [Theory]
    [InlineData(16, 9, 16, 9)]
    [InlineData(32, 18, 16, 9)]
    [InlineData(1920, 1080, 16, 9)]
    [InlineData(1280, 960, 4, 3)]
    public void Reduces_to_lowest_terms(int n, int d, int en, int ed)
    {
        var r = new AspectRatio(n, d);
        Assert.Equal(en, r.Numerator);
        Assert.Equal(ed, r.Denominator);
    }

    [Theory]
    [InlineData("4:3", 4, 3)]
    [InlineData("1:1", 1, 1)]
    [InlineData("16/9", 16, 9)]
    public void Parses_ffprobe_forms(string text, int n, int d)
    {
        AspectRatio? r = AspectRatio.TryParse(text);
        Assert.NotNull(r);
        Assert.Equal(n, r!.Value.Numerator);
        Assert.Equal(d, r.Value.Denominator);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("0:1")]      // ffprobe's "unknown"
    [InlineData("garbage")]
    public void Rejects_absent_or_degenerate_values(string? text) =>
        Assert.Null(AspectRatio.TryParse(text));

    [Fact]
    public void Square_pixels_at_1280x960_display_as_4_3()
    {
        Assert.Equal(new AspectRatio(4, 3), AspectRatio.Square.DisplayAspectFor(1280, 960));
    }

    [Fact]
    public void Sar_4_3_at_1280x960_displays_as_16_9()
    {
        // The whole premise of the stretch: keep 1280x960 pixels, present them as 16:9.
        Assert.Equal(AspectRatio.Widescreen, new AspectRatio(4, 3).DisplayAspectFor(1280, 960));
    }

    [Fact]
    public void Computes_sar_needed_to_stretch_1280x960_to_widescreen()
    {
        AspectRatio sar = AspectRatio.SarForTargetDisplay(1280, 960, AspectRatio.Widescreen);
        Assert.Equal(new AspectRatio(4, 3), sar);
    }

    [Fact]
    public void Regression_sar_must_be_computed_from_source_not_target_resolution()
    {
        // The original defect: ComputeSarFor16by9 was handed the configured OUTPUT resolution
        // (1920x1080) instead of the source's own. That yields 1:1, which the caller then
        // discarded as "no change needed" - so the stretch silently never happened.
        AspectRatio fromTarget = AspectRatio.SarForTargetDisplay(1920, 1080, AspectRatio.Widescreen);
        Assert.Equal(AspectRatio.Square, fromTarget);

        AspectRatio fromSource = AspectRatio.SarForTargetDisplay(1280, 960, AspectRatio.Widescreen);
        Assert.NotEqual(fromTarget, fromSource);
        Assert.Equal(new AspectRatio(4, 3), fromSource);
    }

    [Fact]
    public void Rejects_zero_dimensions()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => AspectRatio.SarForTargetDisplay(0, 960, AspectRatio.Widescreen));
    }
}
