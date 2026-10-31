using SteamClipRemuxer.Core.Configuration;
using SteamClipRemuxer.Core.Highlights;
using SteamClipRemuxer.Core.Steam;
using SteamClipRemuxer.Core.Timelines;
using Xunit;

namespace SteamClipRemuxer.Core.Tests;

/// <summary>Which clips start out ticked.</summary>
public class LaunchSelectionTests
{
    private static readonly TimeZoneInfo Copenhagen =
        TimeZoneInfo.CreateCustomTimeZone("test+2", TimeSpan.FromHours(2), "test+2", "test+2");

    private static readonly DateTimeOffset Now = new(2026, 10, 31, 20, 0, 0, TimeSpan.FromHours(2));
    private static readonly DateTimeOffset LastClosed = new(2026, 10, 30, 23, 0, 0, TimeSpan.FromHours(2));

    [Theory]
    [InlineData(LaunchSelection.None)]
    [InlineData(LaunchSelection.RecordedToday)]
    [InlineData(LaunchSelection.SinceLastSession)]
    public void A_processed_clip_is_never_ticked(LaunchSelection mode) =>
        Assert.False(LaunchSelectionRule.ShouldTick(
            mode, unprocessed: false, Now.AddMinutes(-5), Now, LastClosed, Copenhagen));

    [Fact]
    public void None_ticks_nothing() =>
        Assert.False(LaunchSelectionRule.ShouldTick(
            LaunchSelection.None, unprocessed: true, Now.AddMinutes(-5), Now, LastClosed, Copenhagen));

    [Fact]
    public void Since_last_session_ticks_only_what_was_recorded_after_the_app_closed()
    {
        Assert.True(LaunchSelectionRule.ShouldTick(
            LaunchSelection.SinceLastSession, true, LastClosed.AddMinutes(1), Now, LastClosed, Copenhagen));
        Assert.False(LaunchSelectionRule.ShouldTick(
            LaunchSelection.SinceLastSession, true, LastClosed.AddMinutes(-1), Now, LastClosed, Copenhagen));
    }

    [Fact]
    public void Since_last_session_ticks_nothing_on_a_first_run()
    {
        // Nothing to measure from. Ticking everything is the behaviour this replaced.
        Assert.False(LaunchSelectionRule.ShouldTick(
            LaunchSelection.SinceLastSession, true, Now.AddMinutes(-1), Now, lastSessionEnded: null, Copenhagen));
    }

    [Fact]
    public void Today_is_the_local_calendar_day_not_the_last_24_hours()
    {
        // 00:30 local today is "today"; 23:30 local yesterday is not, though both are recent.
        var justAfterMidnight = new DateTimeOffset(2026, 10, 30, 22, 30, 0, TimeSpan.Zero);
        var justBeforeMidnight = new DateTimeOffset(2026, 10, 30, 21, 30, 0, TimeSpan.Zero);

        Assert.True(LaunchSelectionRule.ShouldTick(
            LaunchSelection.RecordedToday, true, justAfterMidnight, Now, null, Copenhagen));
        Assert.False(LaunchSelectionRule.ShouldTick(
            LaunchSelection.RecordedToday, true, justBeforeMidnight, Now, null, Copenhagen));
    }
}

/// <summary>Intro and outro around the fights, and fights that cannot be shown.</summary>
public class IntroOutroTests
{
    /// <summary>Ten three-second chunks.</summary>
    private static readonly double[] Chunks = { 0, 3, 6, 9, 12, 15, 18, 21, 24, 27 };
    private const double End = 30;

    private static Engagement Fight(double at) => new()
    {
        FirstKill = TimeSpan.FromSeconds(at),
        LastKill = TimeSpan.FromSeconds(at),
        KillCount = 1,
    };

    private static IReadOnlyList<ChunkRun> Plan(
        IEnumerable<Engagement> fights, double intro = 0, double outro = 0, double offset = 0) =>
        ChunkWindows.Plan(
            Chunks, End, offset, fights.ToList(),
            Array.Empty<RoundStart>(), Array.Empty<TimeSpan>(),
            new HighlightWindowOptions
            {
                Lead = TimeSpan.Zero,
                Tail = TimeSpan.Zero,
                Intro = TimeSpan.FromSeconds(intro),
                Outro = TimeSpan.FromSeconds(outro),
            });

    [Fact]
    public void Off_by_default_there_is_no_intro_or_outro()
    {
        ChunkRun run = Assert.Single(Plan(new[] { Fight(13) }));
        Assert.Equal(RunKind.Fight, run.Kind);
    }

    [Fact]
    public void An_intro_keeps_the_opening_chunks_ahead_of_the_first_fight()
    {
        // Four seconds reaches into the second chunk, so two chunks: the cut snaps outward.
        IReadOnlyList<ChunkRun> runs = Plan(new[] { Fight(13) }, intro: 4);

        Assert.Equal(2, runs.Count);
        Assert.Equal(RunKind.Intro, runs[0].Kind);
        Assert.Equal("Intro", runs[0].Label);
        Assert.Equal((1, 2), (runs[0].FirstIndex, runs[0].LastIndex));
        Assert.Equal(0, runs[0].Kills);
    }

    [Fact]
    public void An_intro_of_exactly_one_chunk_keeps_one_chunk() =>
        Assert.Equal(1, Plan(new[] { Fight(13) }, intro: 3)[0].LastIndex);

    [Fact]
    public void An_intro_stops_where_the_first_fight_begins_rather_than_repeating_it()
    {
        // The fight is in chunk 3; a 15-second intro would reach chunk 5.
        IReadOnlyList<ChunkRun> runs = Plan(new[] { Fight(7) }, intro: 15);

        Assert.Equal((1, 2), (runs[0].FirstIndex, runs[0].LastIndex));
        Assert.Equal((3, 3), (runs[1].FirstIndex, runs[1].LastIndex));
    }

    [Fact]
    public void A_fight_in_the_first_chunk_leaves_no_room_for_an_intro()
    {
        ChunkRun run = Assert.Single(Plan(new[] { Fight(1) }, intro: 6));
        Assert.Equal(RunKind.Fight, run.Kind);
    }

    [Fact]
    public void An_outro_keeps_the_closing_chunks_after_the_last_fight()
    {
        IReadOnlyList<ChunkRun> runs = Plan(new[] { Fight(7) }, outro: 6);

        Assert.Equal(RunKind.Outro, runs[^1].Kind);
        Assert.Equal((9, 10), (runs[^1].FirstIndex, runs[^1].LastIndex));
    }

    [Fact]
    public void An_outro_never_repeats_the_last_fight()
    {
        IReadOnlyList<ChunkRun> runs = Plan(new[] { Fight(22) }, outro: 15);

        Assert.Equal((8, 8), (runs[0].FirstIndex, runs[0].LastIndex));
        Assert.Equal((9, 10), (runs[1].FirstIndex, runs[1].LastIndex));
    }

    [Fact]
    public void With_no_fights_there_is_nothing_to_frame()
    {
        // An intro and outro alone are not highlights; the clip is reported as having none.
        Assert.Empty(Plan(Array.Empty<Engagement>(), intro: 6, outro: 6));
    }

    [Fact]
    public void A_fight_that_ended_before_the_footage_began_is_dropped_not_clamped_to_chunk_one()
    {
        // The timeline is searched a little beyond the clip. A fight there used to be clamped
        // onto chunk 1, which put the clip's opening seconds into the reel as an unasked-for intro.
        IReadOnlyList<ChunkRun> runs = Plan(new[] { Fight(95), Fight(113) }, offset: 100);

        ChunkRun run = Assert.Single(runs);
        Assert.Equal(5, run.FirstIndex);
    }
}

/// <summary>Warmup, and the map of a clip that starts before the match does.</summary>
public class WarmupTests
{
    private static SessionTimeline Mirage() =>
        SessionTimeline.Load(Path.Combine("Fixtures", "timeline_session_mirage.json"));

    // The sample: a warmup kill at 3:44, the match phase opening at 4:03, round 1 at 4:08.
    private static readonly TimeSpan WarmupKill = TimeSpan.FromMilliseconds(224174);
    private static readonly TimeSpan PhaseOpens = TimeSpan.FromMilliseconds(243921);
    private static readonly TimeSpan WholeMatchStart = TimeSpan.FromSeconds(200);
    private static readonly TimeSpan WholeMatchEnd = TimeSpan.FromSeconds(2400);

    [Fact]
    public void The_sample_has_a_kill_before_the_match_begins()
    {
        SessionTimeline timeline = Mirage();

        Assert.False(timeline.InMatch(WarmupKill));
        Assert.True(timeline.InMatch(PhaseOpens + TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public void Warmup_kills_are_left_out_of_a_whole_match_reel_by_default()
    {
        IReadOnlyList<Engagement> fights =
            HighlightSelector.Engagements(Mirage(), WholeMatchStart, WholeMatchEnd);

        Assert.NotEmpty(fights);
        Assert.All(fights, f => Assert.True(f.FirstKill > PhaseOpens));
    }

    [Fact]
    public void Warmup_kills_come_back_when_asked_for()
    {
        IReadOnlyList<Engagement> fights =
            HighlightSelector.Engagements(Mirage(), WholeMatchStart, WholeMatchEnd, includeWarmup: true);

        Assert.Equal(WarmupKill, fights[0].FirstKill);
    }

    [Fact]
    public void A_clip_starting_in_warmup_still_knows_its_map()
    {
        // The map is only tagged from the phase onward. Asking about the clip's first moment
        // alone gave no map, so {map} in the highlights title came out empty.
        Highlight? highlight = HighlightSelector.Select(Mirage(), WholeMatchStart, WholeMatchEnd);

        Assert.NotNull(highlight);
        Assert.Equal("Mirage", highlight.Map);
        Assert.Equal("Competitive", highlight.Mode);
    }

    [Fact]
    public void A_timeline_with_no_phases_keeps_every_kill()
    {
        // A game that writes no phases must not lose all of its kills to the warmup rule.
        var timeline = new SessionTimeline(new[]
        {
            new TimelineEntry
            {
                Time = TimeSpan.FromSeconds(10), Type = "event",
                Title = "You killed Bot", Description = "with the AK-47", Icon = "cs2_gun_kill",
            },
        });

        Assert.Single(HighlightSelector.Engagements(timeline, TimeSpan.Zero, TimeSpan.FromSeconds(60)));
    }
}

/// <summary>Moving a settings file written with the old highlight defaults forward.</summary>
public class HighlightDefaultsMigrationTests
{
    [Fact]
    public void Old_defaults_are_moved_to_the_new_ones_but_edited_templates_are_kept()
    {
        string dir = Path.Combine(Path.GetTempPath(), "sclip-settings-" + Guid.NewGuid().ToString("N"));
        string path = Path.Combine(dir, "settings.json");

        try
        {
            new AppSettings
            {
                HighlightsFileNameTemplate = ClipNaming.LegacyHighlightsTemplate,
                YouTubeHighlightsTitleTemplate = "{game} - my own title",
            }.Save(path);

            AppSettings loaded = AppSettings.Load(path);

            Assert.Equal(ClipNaming.DefaultHighlightsTemplate, loaded.HighlightsFileNameTemplate);
            Assert.Equal("{game} - my own title", loaded.YouTubeHighlightsTitleTemplate);
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void New_settings_round_trip()
    {
        string dir = Path.Combine(Path.GetTempPath(), "sclip-settings-" + Guid.NewGuid().ToString("N"));
        string path = Path.Combine(dir, "settings.json");
        var closed = new DateTimeOffset(2026, 10, 30, 23, 0, 0, TimeSpan.FromHours(1));

        try
        {
            new AppSettings
            {
                LaunchSelection = LaunchSelection.RecordedToday,
                LastSessionEndedAt = closed,
                HighlightMinKillsPerRound = 3,
                HighlightIntroSeconds = 6,
                HighlightOutroSeconds = 9,
                KillHighlightsOnly = true,
                StopBeforeDeaths = true,
            }.Save(path);

            AppSettings loaded = AppSettings.Load(path);

            Assert.Equal(LaunchSelection.RecordedToday, loaded.LaunchSelection);
            Assert.Equal(closed, loaded.LastSessionEndedAt);
            Assert.Equal((3, 6, 9), (loaded.HighlightMinKillsPerRound,
                loaded.HighlightIntroSeconds, loaded.HighlightOutroSeconds));
            Assert.True(loaded.KillHighlightsOnly);
            Assert.True(loaded.StopBeforeDeaths);
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }
}
