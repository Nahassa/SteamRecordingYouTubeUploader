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
}
