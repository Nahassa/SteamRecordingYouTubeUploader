using SteamClipRemuxer.Core.Execution;
using SteamClipRemuxer.Core.Highlights;
using SteamClipRemuxer.Core.Steam;
using Xunit;

namespace SteamClipRemuxer.Core.Tests;

/// <summary>
/// A round cut into two pieces is still one round.
///
/// Before this, a round holding a double kill and a triple kill far enough apart to be cut
/// separately published two timestamps for one round:
///
///   0:00  Round 19 - Double kill with the Tec-9
///   0:14  Round 19 - Triple kill with the AWP
///
/// which counts the round twice and names neither thing that happened. Five kills in a round is
/// an ace, and it gets one line.
/// </summary>
public class RoundChapterTests
{
    private static Engagement Fight(int kills, string? weapon, int? round, double at) => new()
    {
        FirstKill = TimeSpan.FromSeconds(at),
        LastKill = TimeSpan.FromSeconds(at),
        KillCount = kills,
        Weapon = weapon,
        Round = round,
    };

    private static ChunkRun Run(params Engagement[] fights) => new()
    {
        FirstIndex = 1,
        LastIndex = 2,
        Round = fights[0].Round,
        Engagements = fights,
    };

    private static StitchPart Part(string label, string? group, double startsAt, double duration = 12) =>
        new()
        {
            Path = $"/parts/{label}.mp4",
            Label = label,
            Group = group,
            StartsAt = TimeSpan.FromSeconds(startsAt),
            Duration = TimeSpan.FromSeconds(duration),
        };

    [Fact]
    public void A_round_is_named_for_its_whole_tally_not_its_biggest_fight()
    {
        string label = ChunkWindows.RoundLabel(new[]
        {
            Run(Fight(2, "Tec-9", 19, at: 100)),
            Run(Fight(3, "AWP", 19, at: 114)),
        });

        Assert.Equal("Round 19 - Ace", label);
    }

    [Fact]
    public void A_round_fought_with_one_weapon_keeps_it()
    {
        string label = ChunkWindows.RoundLabel(new[]
        {
            Run(Fight(2, "AWP", 19, at: 100)),
            Run(Fight(3, "AWP", 19, at: 114)),
        });

        Assert.Equal("Round 19 - Ace with the AWP", label);
    }

    [Fact]
    public void A_round_Steam_recorded_no_weapon_for_claims_none()
    {
        // Steam writes some multi-kills with no weapon at all. Half a round's weapon is not the
        // round's weapon, and it must not throw on the null either.
        string label = ChunkWindows.RoundLabel(new[]
        {
            Run(Fight(2, null, 19, at: 100)),
            Run(Fight(3, "AWP", 19, at: 114)),
        });

        Assert.Equal("Round 19 - Ace", label);
    }

    [Fact]
    public void A_game_without_rounds_is_named_without_one()
    {
        Assert.Equal("Double kill with the AK-47", ChunkWindows.RoundLabel(new[]
        {
            Run(Fight(2, "AK-47", round: null, at: 100)),
        }));
    }

    [Fact]
    public void A_run_still_names_only_the_fight_it_holds()
    {
        // The per-run label is what the log prints, and it must not start summing: two fights an
        // engagement window apart are not one multi-kill.
        ChunkRun run = Run(Fight(2, "Tec-9", 19, at: 100), Fight(3, "AWP", 19, at: 114));

        Assert.Equal("Round 19 - Triple kill with the AWP", run.Label);
    }

    [Fact]
    public void Two_pieces_of_one_round_are_one_chapter()
    {
        IReadOnlyList<Chapter> chapters = StitchChapters.Chapters(new[]
        {
            Part("Round 19 - Ace", "19", startsAt: 0),
            Part("Round 19 - Ace", "19", startsAt: 12),
        });

        Chapter only = Assert.Single(chapters);
        Assert.Equal(TimeSpan.Zero, only.StartsAt);
        Assert.Equal(TimeSpan.FromSeconds(24), only.Duration);
        Assert.Equal("Round 19 - Ace", only.Label);
    }

    [Fact]
    public void Different_rounds_stay_separate()
    {
        IReadOnlyList<Chapter> chapters = StitchChapters.Chapters(new[]
        {
            Part("Round 19 - Ace", "19", startsAt: 0),
            Part("Round 20 - Double kill", "20", startsAt: 12),
        });

        Assert.Equal(
            new[] { "Round 19 - Ace", "Round 20 - Double kill" },
            chapters.Select(c => c.Label));
    }

    [Fact]
    public void A_round_that_comes_back_later_is_not_folded_into_the_earlier_one()
    {
        // Only neighbours collapse. Reaching back to the open chapter instead would swallow
        // whatever sat between.
        IReadOnlyList<Chapter> chapters = StitchChapters.Chapters(new[]
        {
            Part("Round 19 - Kill", "19", startsAt: 0),
            Part("Round 20 - Kill", "20", startsAt: 12),
            Part("Round 19 - Kill", "19", startsAt: 24),
        });

        Assert.Equal(3, chapters.Count);
    }

    [Fact]
    public void A_compilation_still_lists_every_clip()
    {
        // The compilation path carries no group, so nothing there collapses.
        IReadOnlyList<Chapter> chapters = StitchChapters.Chapters(new[]
        {
            Part("Ace", null, startsAt: 0),
            Part("Ace", null, startsAt: 12),
        });

        Assert.Equal(2, chapters.Count);
    }

    [Fact]
    public void The_description_prints_one_line_per_chapter()
    {
        string described = StitchChapters.Describe(new[]
        {
            Part("Round 19 - Ace", "19", startsAt: 0),
            Part("Round 19 - Ace", "19", startsAt: 12),
            Part("Round 20 - Double kill", "20", startsAt: 24),
        });

        Assert.Equal(
            "0:00  Round 19 - Ace" + Environment.NewLine + "0:24  Round 20 - Double kill",
            described);
    }

    [Fact]
    public void Youtube_chapters_are_counted_after_the_rounds_collapse()
    {
        // Four parts, but the list printed has two lines, which is under YouTube's minimum of
        // three. Counting parts would have claimed it qualified.
        StitchPart[] parts =
        {
            Part("Round 19 - Ace", "19", startsAt: 0),
            Part("Round 19 - Ace", "19", startsAt: 12),
            Part("Round 20 - Ace", "20", startsAt: 24),
            Part("Round 20 - Ace", "20", startsAt: 36),
        };

        Assert.False(StitchChapters.QualifyAsYouTubeChapters(parts));
    }

    [Fact]
    public void Three_separate_rounds_do_qualify()
    {
        StitchPart[] parts =
        {
            Part("Round 19 - Ace", "19", startsAt: 0),
            Part("Round 20 - Ace", "20", startsAt: 12),
            Part("Round 21 - Ace", "21", startsAt: 24),
        };

        Assert.True(StitchChapters.QualifyAsYouTubeChapters(parts));
    }
}
