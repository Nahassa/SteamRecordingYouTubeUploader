using SteamClipRemuxer.Core.Configuration;
using SteamClipRemuxer.Core.Highlights;
using Xunit;

namespace SteamClipRemuxer.Core.Tests;

public class ClipNamingTests
{
    private static readonly DateTimeOffset Recorded =
        new(2026, 8, 28, 21, 52, 42, TimeSpan.Zero);

    [Fact]
    public void Builds_the_default_name_from_a_highlight()
    {
        string name = ClipNaming.Expand(
            ClipNaming.DefaultTemplate, "Counter-Strike 2", Recorded,
            new Highlight { KillCount = 2, Weapon = "AK-47", Map = "Dust II" });

        Assert.Equal("Counter-Strike 2 - 2026-08-28 21-52-42 - Double kill", name);
    }

    [Fact]
    public void The_timestamp_keeps_two_similar_clips_apart()
    {
        // Two double kills on one evening would otherwise collide on a single filename.
        var highlight = new Highlight { KillCount = 2, Weapon = "AK-47" };

        string first = ClipNaming.Expand(ClipNaming.DefaultTemplate, "Counter-Strike 2", Recorded, highlight);
        string second = ClipNaming.Expand(
            ClipNaming.DefaultTemplate, "Counter-Strike 2", Recorded.AddMinutes(9), highlight);

        Assert.NotEqual(first, second);
    }

    [Fact]
    public void The_same_clip_names_itself_the_same_way_on_a_rerun()
    {
        // The timestamp is the clip's recorded moment, not the moment the batch ran, so
        // reprocessing does not produce a second file under a new name.
        var highlight = new Highlight { KillCount = 3, Weapon = "AK-47" };

        Assert.Equal(
            ClipNaming.Expand(ClipNaming.DefaultTemplate, "Counter-Strike 2", Recorded, highlight),
            ClipNaming.Expand(ClipNaming.DefaultTemplate, "Counter-Strike 2", Recorded, highlight));
    }

    [Fact]
    public void Colons_from_the_time_never_reach_the_filename()
    {
        string name = ClipNaming.Expand(
            ClipNaming.DefaultTemplate, "Counter-Strike 2", Recorded, new Highlight { KillCount = 1 });

        Assert.DoesNotContain(':', name);
        Assert.All(Path.GetInvalidFileNameChars(), c => Assert.DoesNotContain(c, name));
    }

    [Fact]
    public void A_blank_placeholder_does_not_leave_a_dangling_separator()
    {
        string name = ClipNaming.Expand(
            "{game} - {map} - {highlight}", "Counter-Strike 2", Recorded,
            new Highlight { KillCount = 2, Map = null });

        Assert.Equal("Counter-Strike 2 - Double kill", name);
    }

    [Fact]
    public void Falls_back_when_there_is_no_highlight()
    {
        string name = ClipNaming.Expand(
            ClipNaming.DefaultTemplate, "Counter-Strike 2", Recorded, highlight: null);

        Assert.Equal("Counter-Strike 2 - 2026-08-28 21-52-42 - Clip", name);
    }

    [Fact]
    public void The_full_form_is_available_for_templates_that_want_the_weapon()
    {
        string name = ClipNaming.Expand(
            "{highlight_full}", "Counter-Strike 2", Recorded,
            new Highlight { KillCount = 2, Weapon = "AK-47" });

        Assert.Equal("Double kill with the AK-47", name);
    }

    [Theory]
    [InlineData("a/b")]
    [InlineData("a\\b")]
    [InlineData("a?b")]
    [InlineData("a*b")]
    public void Strips_characters_a_filesystem_would_reject(string game)
    {
        string name = ClipNaming.Expand("{game}", game, Recorded, new Highlight { KillCount = 1 });
        Assert.All(Path.GetInvalidFileNameChars(), c => Assert.DoesNotContain(c, name));
    }

    [Fact]
    public void Never_returns_an_empty_name()
    {
        Assert.Equal("Clip", ClipNaming.Expand("{weapon}", "", Recorded, new Highlight { KillCount = 0 }));
    }

    [Theory]
    [InlineData('<')]
    [InlineData('>')]
    [InlineData(':')]
    [InlineData('"')]
    [InlineData('/')]
    [InlineData('\\')]
    [InlineData('|')]
    [InlineData('?')]
    [InlineData('*')]
    public void Every_character_windows_forbids_is_taken_out(char forbidden)
    {
        // Spelled out rather than asked of the running machine: Path.GetInvalidFileNameChars is
        // NUL and '/' on Linux, so a colon would pass the tests and then fail to write on the
        // Windows machine the app runs on.
        Assert.DoesNotContain(forbidden, ClipNaming.Sanitise($"Ace{forbidden}on Mirage"));
    }

    [Fact]
    public void A_control_character_is_taken_out_too() =>
        Assert.DoesNotContain('\t', ClipNaming.Sanitise("Ace\ton Mirage"));

    [Fact]
    public void A_name_that_sanitises_away_to_nothing_still_has_one() =>
        Assert.Equal("Clip", ClipNaming.Sanitise("///"));

    // The templates below became settings. These pin the defaults to what the code produced
    // when they were constants, so an install nobody has touched keeps naming files the same.

    [Fact]
    public void The_compilation_default_is_unchanged_by_becoming_a_setting()
    {
        string name = ClipNaming.Expand(
            new AppSettings().CompilationFileNameTemplate,
            "Counter-Strike 2", Recorded, highlight: null, count: 4);

        Assert.Equal("Counter-Strike 2 - Compilation - 2026-08-28 21-52-42 (4 clips)", name);
    }

    [Fact]
    public void A_highlights_reel_is_named_after_the_clip_it_came_from()
    {
        // Was hardcoded as SuggestedName + " - Highlights"; {clip_name} keeps that exactly, and
        // keeps it following the clip file name template rather than restating it.
        string name = ClipNaming.Expand(
            new AppSettings().HighlightsFileNameTemplate,
            "Counter-Strike 2", Recorded, new Highlight { KillCount = 3 },
            clipName: "Counter-Strike 2 - 2026-08-28 21-52-42 - Triple kill");

        Assert.Equal("Counter-Strike 2 - 2026-08-28 21-52-42 - Triple kill - Highlights", name);
    }

    [Fact]
    public void Clips_joined_and_fights_kept_are_counted_separately()
    {
        // Three clips can hold seven fights. One placeholder for both would have to lie about
        // one of them.
        string name = ClipNaming.Expand(
            "{count} clips, {fights} fights", "Counter-Strike 2", Recorded,
            highlight: null, count: 3, fights: 7);

        Assert.Equal("3 clips, 7 fights", name);
    }

    [Fact]
    public void A_placeholder_with_nothing_to_put_in_it_leaves_no_gap()
    {
        // A compilation spans several clips and so has no one map or round. The separators have
        // to close up, or every such name carries a " - - " in the middle.
        string name = ClipNaming.Expand(
            "{game} - {map} - {round} - Compilation", "Counter-Strike 2", Recorded,
            highlight: null);

        Assert.Equal("Counter-Strike 2 - Compilation", name);
    }

    [Fact]
    public void No_default_template_leans_on_a_placeholder_a_compilation_cannot_fill()
    {
        var settings = new AppSettings();

        foreach (string template in new[]
                 {
                     settings.CompilationFileNameTemplate,
                     settings.YouTubeCompilationTitleTemplate,
                 })
        {
            Assert.DoesNotContain("{map}", template);
            Assert.DoesNotContain("{round}", template);
            Assert.DoesNotContain("{highlight}", template);
        }
    }
}
