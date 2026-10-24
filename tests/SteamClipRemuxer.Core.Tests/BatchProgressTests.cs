using SteamClipRemuxer.Core.Execution;
using Xunit;

namespace SteamClipRemuxer.Core.Tests;

/// <summary>
/// What the status bar is told.
///
/// The bar shows the clip in hand and restarts for each one; which clip it is, is a count beside
/// it. A bar that only moved between clips sat still for whole minutes, because one clip can be
/// several minutes of work.
/// </summary>
public class BatchProgressTests
{
    [Fact]
    public void A_clip_with_known_work_fills_its_own_bar()
    {
        // Three of seven fights cut: the bar is three sevenths along, not three sevenths of the
        // whole batch.
        var p = new BatchProgress(2, 5, "Dust II ace", "cutting fight 4 of 7", 3, 7);

        Assert.True(p.ItemIsMeasured);
        Assert.Equal(3d / 7, p.ItemFraction, 6);
    }

    [Fact]
    public void Work_that_cannot_be_counted_says_so_rather_than_reading_as_zero()
    {
        // A remux, an upload or an encode has no countable steps. The caller marquees on this
        // rather than leaving a bar at zero, which would look like nothing was happening.
        var p = new BatchProgress(0, 3, "Mirage double kill");

        Assert.False(p.ItemIsMeasured);
        Assert.Equal(0, p.ItemFraction);
    }

    [Theory]
    [InlineData(0, 7, "1/7 clips")]
    [InlineData(3, 7, "4/7 clips")]
    [InlineData(6, 7, "7/7 clips")]
    public void The_count_is_one_based_because_it_names_the_clip_being_worked_on(
        int completed, int total, string expected) =>
        Assert.Equal(expected, new BatchProgress(completed, total, "a clip").Counted());

    [Fact]
    public void The_count_cannot_run_past_the_total() =>
        // A last report can arrive with Completed already at Total; "8/7 clips" would be nonsense.
        Assert.Equal("7/7 clips", new BatchProgress(7, 7, "a clip").Counted());

    [Fact]
    public void Finishing_is_said_differently_from_starting_the_last_clip()
    {
        // An empty CurrentFile is how the services signal the end of a run.
        var done = new BatchProgress(5, 5, string.Empty);

        Assert.Equal("5/5 clips done", done.Counted());
        Assert.Equal(string.Empty, done.Describe());
    }

    [Fact]
    public void The_stage_is_shown_beside_the_clip_when_there_is_one()
    {
        Assert.Equal(
            "Dust II ace  -  cutting fight 2 of 4",
            new BatchProgress(0, 1, "Dust II ace", "cutting fight 2 of 4", 1, 4).Describe());

        Assert.Equal("Dust II ace", new BatchProgress(0, 1, "Dust II ace").Describe());
    }

    [Fact]
    public void A_batch_of_nothing_does_not_divide_by_zero() =>
        Assert.Equal(0, new BatchProgress(0, 0, string.Empty).ItemFraction);

    [Fact]
    public void A_step_count_past_its_total_is_clamped_rather_than_overflowing() =>
        Assert.Equal(1, new BatchProgress(0, 1, "a clip", "joining", 9, 4).ItemFraction);
}
