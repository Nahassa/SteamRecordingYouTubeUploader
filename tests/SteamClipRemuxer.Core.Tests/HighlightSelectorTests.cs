using SteamClipRemuxer.Core.Highlights;
using SteamClipRemuxer.Core.Steam;
using SteamClipRemuxer.Core.Timelines;
using Xunit;

namespace SteamClipRemuxer.Core.Tests;

/// <summary>
/// Driven by a real session timeline and the real clip.pb that indexes into it, so the window
/// under test is the one Steam actually recorded rather than a constructed range.
/// </summary>
public class HighlightSelectorTests
{
    private static SessionTimeline Mirage() =>
        SessionTimeline.Load(Path.Combine("Fixtures", "timeline_session_mirage.json"));

    private static ClipManifest Manifest(string fixture) =>
        ClipManifest.Parse(File.ReadAllBytes(Path.Combine("Fixtures", fixture)))!;

    [Fact]
    public void Reads_map_and_mode_from_the_session()
    {
        SessionTimeline t = Mirage();
        Assert.Equal("Mirage", t.MapAt(TimeSpan.FromMilliseconds(1029109)));
        Assert.Equal("Competitive", t.ModeAt(TimeSpan.FromMilliseconds(1029109)));
    }

    [Fact]
    public void Reads_the_round_in_force_at_a_moment()
    {
        Assert.Equal(7, Mirage().RoundAt(TimeSpan.FromMilliseconds(1029109)));
    }

    [Fact]
    public void Picks_the_double_kill_out_of_a_real_cropped_clip()
    {
        // The 41.7 second clip holds a kill, a bomb plant and a Double kill. The engagement
        // worth naming is the Double kill, not the lone kill 30 seconds earlier.
        ClipManifest clip = Manifest("clip_cropped_41707ms.pb");
        Highlight? h = HighlightSelector.Select(Mirage(), clip.StartInSession, clip.EndInSession);

        Assert.NotNull(h);
        Assert.Equal(2, h!.KillCount);
        Assert.Equal("Double kill", h.Label);
        Assert.Equal("AK-47", h.Weapon);
        Assert.Equal("Mirage", h.Map);
    }

    [Fact]
    public void Notices_that_the_player_planted_the_bomb()
    {
        ClipManifest clip = Manifest("clip_cropped_41707ms.pb");
        Highlight h = HighlightSelector.Select(Mirage(), clip.StartInSession, clip.EndInSession)!;

        // "You planted the bomb" is the player; "Karotten planted the bomb" is not.
        Assert.True(h.PlantedBomb);
        Assert.False(h.DefusedBomb);
    }

    [Fact]
    public void The_title_never_contains_a_player_name()
    {
        ClipManifest clip = Manifest("clip_cropped_41707ms.pb");
        Highlight h = HighlightSelector.Select(Mirage(), clip.StartInSession, clip.EndInSession)!;

        // The underlying event reads "You killed Stormb3ast and powerage with the AK-47".
        Assert.Equal("Double kill with the AK-47", h.Describe());
        Assert.DoesNotContain("Stormb3ast", h.Describe(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("powerage", h.Describe(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void An_empty_window_yields_nothing_rather_than_a_vague_title()
    {
        // Better to fall back to the existing naming than publish "Highlight".
        Assert.Null(HighlightSelector.Select(Mirage(), TimeSpan.FromMilliseconds(700000), TimeSpan.FromMilliseconds(705000)));
    }

    [Theory]
    [InlineData(1, "Kill")]
    [InlineData(2, "Double kill")]
    [InlineData(3, "Triple kill")]
    [InlineData(4, "Quad kill")]
    [InlineData(5, "Ace")]
    [InlineData(6, "6 kills")]
    public void Uses_the_games_own_names_and_degrades_past_an_ace(int kills, string expected)
    {
        Assert.Equal(expected, new Highlight { KillCount = kills }.Label);
    }

    [Fact]
    public void Omits_the_weapon_when_steam_recorded_none()
    {
        // One real event reads "You killed rokita4 and Kopala2" with no weapon at all. A
        // dangling "with the" would be worse than no weapon.
        Assert.Equal("Double kill", new Highlight { KillCount = 2, Weapon = null }.Describe());
        Assert.Equal("Double kill", new Highlight { KillCount = 2, Weapon = "" }.Describe());
    }

    [Fact]
    public void Omits_the_weapon_when_an_engagement_mixed_them()
    {
        // "Double kill with the AK-47" is wrong if half of it was a grenade.
        var timeline = new SessionTimeline(new[]
        {
            Event(1000, "cs2_gun_kill", "You killed A", "with the AK-47"),
            Event(3000, "cs2_grenade_kill", "You killed B", "with the High Explosive Grenade"),
        });

        Highlight h = HighlightSelector.Select(timeline, TimeSpan.Zero, TimeSpan.FromSeconds(10))!;
        Assert.Equal(2, h.KillCount);
        Assert.Null(h.Weapon);
    }

    [Fact]
    public void Recovers_a_triple_that_steam_split_into_two_labels()
    {
        // Measured on a real session: a P250 kill 3.4 seconds before a labelled "Double kill".
        // Steam reported two things; it was one triple.
        var timeline = new SessionTimeline(new[]
        {
            Event(3140097, "cs2_gun_kill", "You killed IAABI", "with the P250"),
            Event(3143526, "cs2_double_kill", "Double kill",
                "You killed borsi7 and BySMO with the AK-47", durationMs: 5687),
        });

        Highlight h = HighlightSelector.Select(
            timeline, TimeSpan.FromMilliseconds(3138000), TimeSpan.FromMilliseconds(3152000))!;

        Assert.Equal(3, h.KillCount);
        Assert.Equal("Triple kill", h.Label);
    }

    [Fact]
    public void Separate_engagements_are_not_merged_into_one_multi_kill()
    {
        // Two kills thirty seconds apart are two fights, not a double kill.
        var timeline = new SessionTimeline(new[]
        {
            Event(1000, "cs2_gun_kill", "You killed A", "with the AK-47"),
            Event(31000, "cs2_gun_kill", "You killed B", "with the AK-47"),
        });

        Highlight h = HighlightSelector.Select(timeline, TimeSpan.Zero, TimeSpan.FromSeconds(40))!;
        Assert.Equal(1, h.KillCount);
    }

    [Fact]
    public void Counts_victims_in_a_multi_kill_without_keeping_their_names()
    {
        var timeline = new SessionTimeline(new[]
        {
            Event(1000, "cs2_multi_kill", "Multi kill",
                "You killed Wolf, McCoy, and Osiris with the AK-47", durationMs: 6000),
        });

        Highlight h = HighlightSelector.Select(timeline, TimeSpan.Zero, TimeSpan.FromSeconds(10))!;
        Assert.Equal(3, h.KillCount);
        Assert.Equal("Triple kill with the AK-47", h.Describe());
    }

    [Fact]
    public void Fire_and_world_are_not_reported_as_weapons()
    {
        // Steam records burn damage as "with fire" and falling as "with the world".
        var timeline = new SessionTimeline(new[]
        {
            Event(1000, "cs2_inferno_kill", "You killed Stormb3ast", "with fire"),
        });

        Highlight h = HighlightSelector.Select(timeline, TimeSpan.Zero, TimeSpan.FromSeconds(5))!;
        Assert.Null(h.Weapon);
        Assert.Equal("Kill", h.Describe());
    }

    [Fact]
    public void Tolerates_a_timeline_whose_entries_steam_wrote_as_an_object()
    {
        // Keys are indices, so an ordinal sort would give 0, 1, 10, 2 and scramble the session.
        const string json = """
        {"entries":{"0":{"time":"1000","type":"event","title":"Start of round 1"},
                    "10":{"time":"11000","type":"event","title":"Start of round 3"},
                    "2":{"time":"5000","type":"event","title":"Start of round 2"}}}
        """;

        SessionTimeline t = SessionTimeline.Parse(json);
        Assert.Equal(3, t.Entries.Count);
        Assert.Equal(2, t.RoundAt(TimeSpan.FromMilliseconds(6000)));
        Assert.Equal(3, t.RoundAt(TimeSpan.FromMilliseconds(12000)));
    }

    private static TimelineEntry Event(
        long timeMs, string icon, string title, string description, long durationMs = 0) =>
        new()
        {
            Time = TimeSpan.FromMilliseconds(timeMs),
            Duration = TimeSpan.FromMilliseconds(durationMs),
            Type = "event",
            Icon = icon,
            Title = title,
            Description = description,
        };
}
