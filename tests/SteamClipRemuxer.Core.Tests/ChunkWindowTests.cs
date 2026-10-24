using SteamClipRemuxer.Core.Execution;
using SteamClipRemuxer.Core.Highlights;
using SteamClipRemuxer.Core.Steam;
using SteamClipRemuxer.Core.Timelines;
using Xunit;

namespace SteamClipRemuxer.Core.Tests;

public class ChunkWindowTests
{
    /// <summary>Five three-second chunks, the shape Steam writes.</summary>
    private static readonly double[] Chunks = { 0, 3, 6, 9, 12 };
    private const double End = 15;

    private static Engagement Fight(double at, int kills = 1, int? round = null, double? last = null) => new()
    {
        FirstKill = TimeSpan.FromSeconds(at),
        LastKill = TimeSpan.FromSeconds(last ?? at),
        KillCount = kills,
        Round = round,
    };

    private static IReadOnlyList<ChunkRun> Plan(
        IEnumerable<Engagement> fights,
        HighlightWindowOptions? options = null,
        IReadOnlyList<RoundStart>? rounds = null,
        IReadOnlyList<TimeSpan>? deaths = null,
        double offset = 0,
        double[]? chunks = null) =>
        ChunkWindows.Plan(
            chunks ?? Chunks, End, offset, fights.ToList(),
            rounds ?? Array.Empty<RoundStart>(),
            deaths ?? Array.Empty<TimeSpan>(),
            options ?? new HighlightWindowOptions { Lead = TimeSpan.Zero, Tail = TimeSpan.Zero });

    [Fact]
    public void A_kill_in_the_middle_of_a_chunk_keeps_that_whole_chunk()
    {
        // Three seconds is the finest a copy can cut: Steam writes one keyframe per chunk.
        ChunkRun run = Assert.Single(Plan(new[] { Fight(7) }));

        Assert.Equal(3, run.FirstIndex);
        Assert.Equal(3, run.LastIndex);
        Assert.Equal(1, run.ChunkCount);
    }

    [Fact]
    public void Lead_and_tail_push_the_run_outward()
    {
        var options = new HighlightWindowOptions
        {
            Lead = TimeSpan.FromSeconds(3),
            Tail = TimeSpan.FromSeconds(3),
        };

        // Kill at 7s, window 4-10, which touches chunks 2 (3-6), 3 (6-9) and 4 (9-12).
        ChunkRun run = Assert.Single(Plan(new[] { Fight(7) }, options));

        Assert.Equal(2, run.FirstIndex);
        Assert.Equal(4, run.LastIndex);
    }

    [Fact]
    public void A_run_never_reaches_past_the_chunks_that_exist()
    {
        var options = new HighlightWindowOptions
        {
            Lead = TimeSpan.FromSeconds(60),
            Tail = TimeSpan.FromSeconds(60),
        };

        ChunkRun run = Assert.Single(Plan(new[] { Fight(7) }, options));

        Assert.Equal(1, run.FirstIndex);
        Assert.Equal(5, run.LastIndex);
    }

    [Fact]
    public void Overlapping_runs_become_one()
    {
        var options = new HighlightWindowOptions { Lead = TimeSpan.FromSeconds(3), Tail = TimeSpan.FromSeconds(3) };

        ChunkRun run = Assert.Single(Plan(new[] { Fight(4), Fight(8) }, options));

        Assert.Equal(1, run.FirstIndex);
        Assert.Equal(4, run.LastIndex);
        Assert.Equal(2, run.Engagements.Count);
    }

    [Fact]
    public void Runs_that_touch_become_one()
    {
        // Chunk 1 and chunk 2 are adjacent, so there is no dead footage to leave out.
        ChunkRun run = Assert.Single(Plan(new[] { Fight(1), Fight(4) }));

        Assert.Equal(1, run.FirstIndex);
        Assert.Equal(2, run.LastIndex);
    }

    [Fact]
    public void A_gap_of_a_whole_chunk_stays_two_runs()
    {
        // The point of the exercise: the chunk between them is the footage being thrown away.
        IReadOnlyList<ChunkRun> runs = Plan(new[] { Fight(1), Fight(10) });

        Assert.Equal(2, runs.Count);
        Assert.Equal((1, 1), (runs[0].FirstIndex, runs[0].LastIndex));
        Assert.Equal((4, 4), (runs[1].FirstIndex, runs[1].LastIndex));
    }

    [Fact]
    public void The_video_session_offset_comes_out_of_the_kill_time()
    {
        // Kill times are in the timeline's clock and the chunks in the video session's. On the
        // sample the two are 25.4s apart, which is eight chunks of error.
        ChunkRun run = Assert.Single(Plan(new[] { Fight(32) }, offset: 25.4));

        Assert.Equal(3, run.FirstIndex);
    }

    [Fact]
    public void Chunk_starts_are_read_rather_than_assumed_to_be_exactly_three_seconds()
    {
        // Measured on the real clip: 4125.010, 4128.010, 4131.011 - gaps of 2.999 and 3.001.
        double[] real = { 4125.010492, 4128.009940, 4131.010821 };

        ChunkRun run = Assert.Single(ChunkWindows.Plan(
            real, 4134.044040, 0,
            new[] { Fight(4129.5) },
            Array.Empty<RoundStart>(), Array.Empty<TimeSpan>(),
            new HighlightWindowOptions { Lead = TimeSpan.Zero, Tail = TimeSpan.Zero }));

        Assert.Equal(2, run.FirstIndex);
        Assert.Equal(2, run.LastIndex);
    }
}

public class ChunkWindowRoundTests
{
    private static readonly double[] Chunks = { 0, 3, 6, 9, 12, 15, 18 };
    private const double End = 21;

    private static Engagement Fight(double at, int kills = 1, int? round = null) => new()
    {
        FirstKill = TimeSpan.FromSeconds(at),
        LastKill = TimeSpan.FromSeconds(at),
        KillCount = kills,
        Round = round,
    };

    private static readonly RoundStart[] Rounds =
    {
        new(TimeSpan.Zero, 5),
        new(TimeSpan.FromSeconds(9), 6),
    };

    [Fact]
    public void A_run_does_not_carry_past_the_end_of_its_round()
    {
        // A clip is usually longer than a round - the measured competitive rounds run 97-106s
        // against a 120s buffer - so without this the buy time lands in the reel.
        var options = new HighlightWindowOptions { Lead = TimeSpan.Zero, Tail = TimeSpan.FromSeconds(6) };

        IReadOnlyList<ChunkRun> runs = ChunkWindows.Plan(
            Chunks, End, 0, new[] { Fight(7, round: 5) }, Rounds, Array.Empty<TimeSpan>(), options);

        ChunkRun run = Assert.Single(runs);
        Assert.Equal(3, run.LastIndex);   // chunk 4 starts at 9s, which is round 6
    }

    [Fact]
    public void A_run_does_not_reach_back_before_its_round_began()
    {
        var options = new HighlightWindowOptions { Lead = TimeSpan.FromSeconds(6), Tail = TimeSpan.Zero };

        ChunkRun run = Assert.Single(ChunkWindows.Plan(
            Chunks, End, 0, new[] { Fight(10, round: 6) }, Rounds, Array.Empty<TimeSpan>(), options));

        Assert.Equal(4, run.FirstIndex);  // chunk 3 ends at 9s, where round 6 starts
    }

    [Fact]
    public void Adjacent_runs_in_different_rounds_stay_apart()
    {
        // Welding these together is exactly how the round transition gets back into the reel.
        var options = new HighlightWindowOptions { Lead = TimeSpan.Zero, Tail = TimeSpan.Zero };

        IReadOnlyList<ChunkRun> runs = ChunkWindows.Plan(
            Chunks, End, 0,
            new[] { Fight(7, round: 5), Fight(10, round: 6) },
            Rounds, Array.Empty<TimeSpan>(), options);

        Assert.Equal(2, runs.Count);
        Assert.Equal(5, runs[0].Round);
        Assert.Equal(6, runs[1].Round);
    }

    [Fact]
    public void The_chapter_names_the_round()
    {
        ChunkRun run = Assert.Single(ChunkWindows.Plan(
            Chunks, End, 0, new[] { Fight(7, kills: 2, round: 5) }, Rounds, Array.Empty<TimeSpan>(),
            new HighlightWindowOptions { Lead = TimeSpan.Zero, Tail = TimeSpan.Zero }));

        Assert.Equal("Round 5 - Double kill", run.Label);
    }

    [Fact]
    public void Without_rounds_the_chapter_is_just_the_fight()
    {
        ChunkRun run = Assert.Single(ChunkWindows.Plan(
            Chunks, End, 0, new[] { Fight(7, kills: 3) },
            Array.Empty<RoundStart>(), Array.Empty<TimeSpan>(),
            new HighlightWindowOptions { Lead = TimeSpan.Zero, Tail = TimeSpan.Zero }));

        Assert.Equal("Triple kill", run.Label);
    }

    [Fact]
    public void A_run_named_for_its_biggest_fight_rather_than_the_sum()
    {
        // Two fights more than an engagement window apart are not one multi-kill, however close
        // their chunks happen to be.
        ChunkRun run = Assert.Single(ChunkWindows.Plan(
            Chunks, End, 0,
            new[] { Fight(1, kills: 1, round: 5), Fight(4, kills: 2, round: 5) },
            Rounds, Array.Empty<TimeSpan>(),
            new HighlightWindowOptions { Lead = TimeSpan.Zero, Tail = TimeSpan.Zero }));

        Assert.Equal("Round 5 - Double kill", run.Label);
        Assert.Equal(3, run.Kills);
    }
}

public class ChunkWindowFilterTests
{
    private static readonly double[] Chunks = { 0, 3, 6, 9, 12, 15, 18 };
    private const double End = 21;

    private static readonly RoundStart[] Rounds =
    {
        new(TimeSpan.Zero, 5),
        new(TimeSpan.FromSeconds(9), 6),
    };

    private static Engagement Fight(double at, int kills, int round) => new()
    {
        FirstKill = TimeSpan.FromSeconds(at),
        LastKill = TimeSpan.FromSeconds(at),
        KillCount = kills,
        Round = round,
    };

    private static IReadOnlyList<ChunkRun> Plan(int minimum, params Engagement[] fights) =>
        ChunkWindows.Plan(
            Chunks, End, 0, fights, Rounds, Array.Empty<TimeSpan>(),
            new HighlightWindowOptions
            {
                Lead = TimeSpan.Zero,
                Tail = TimeSpan.Zero,
                MinimumKillsPerRound = minimum,
            });

    [Fact]
    public void A_threshold_of_one_keeps_everything()
    {
        Assert.Equal(2, Plan(1, Fight(1, 1, 5), Fight(10, 1, 6)).Count);
    }

    [Fact]
    public void A_round_below_the_threshold_contributes_nothing()
    {
        IReadOnlyList<ChunkRun> runs = Plan(2, Fight(1, 1, 5), Fight(10, 2, 6));

        ChunkRun run = Assert.Single(runs);
        Assert.Equal(6, run.Round);
    }

    [Fact]
    public void Kills_in_a_round_are_added_up_across_its_fights()
    {
        // Two separate single kills in one round still make it a two-kill round.
        IReadOnlyList<ChunkRun> runs = Plan(2, Fight(1, 1, 5), Fight(7, 1, 5));

        Assert.All(runs, r => Assert.Equal(5, r.Round));
        Assert.Equal(2, runs.Sum(r => r.Kills));
    }

    [Fact]
    public void A_threshold_nothing_reaches_leaves_no_runs() =>
        Assert.Empty(Plan(4, Fight(1, 1, 5), Fight(10, 2, 6)));

    [Fact]
    public void Fights_with_no_round_are_filtered_as_one_group()
    {
        // A game without rounds should not slip past a threshold simply for lacking them.
        Engagement[] fights =
        {
            new() { FirstKill = TimeSpan.FromSeconds(1), LastKill = TimeSpan.FromSeconds(1), KillCount = 1 },
            new() { FirstKill = TimeSpan.FromSeconds(10), LastKill = TimeSpan.FromSeconds(10), KillCount = 1 },
        };

        Assert.NotEmpty(ChunkWindows.Plan(
            Chunks, End, 0, fights, Array.Empty<RoundStart>(), Array.Empty<TimeSpan>(),
            new HighlightWindowOptions { MinimumKillsPerRound = 2 }));

        Assert.Empty(ChunkWindows.Plan(
            Chunks, End, 0, fights, Array.Empty<RoundStart>(), Array.Empty<TimeSpan>(),
            new HighlightWindowOptions { MinimumKillsPerRound = 3 }));
    }
}

public class ChunkWindowDeathTests
{
    private static readonly double[] Chunks = { 0, 3, 6, 9, 12, 15 };
    private const double End = 18;

    private static Engagement Fight(double at) => new()
    {
        FirstKill = TimeSpan.FromSeconds(at),
        LastKill = TimeSpan.FromSeconds(at),
        KillCount = 1,
    };

    private static IReadOnlyList<ChunkRun> Plan(bool stop, params double[] deaths) =>
        ChunkWindows.Plan(
            Chunks, End, 0, new[] { Fight(4) }, Array.Empty<RoundStart>(),
            deaths.Select(TimeSpan.FromSeconds).ToList(),
            new HighlightWindowOptions
            {
                Lead = TimeSpan.Zero,
                Tail = TimeSpan.FromSeconds(9),
                StopBeforeDeaths = stop,
            });

    [Fact]
    public void The_run_carries_past_a_death_unless_told_not_to()
    {
        ChunkRun run = Assert.Single(Plan(stop: false, 7));
        Assert.Equal(5, run.LastIndex);
    }

    [Fact]
    public void Stopping_before_a_death_shortens_the_run()
    {
        ChunkRun run = Assert.Single(Plan(stop: true, 7));
        Assert.Equal(3, run.LastIndex);
    }

    [Fact]
    public void A_death_before_the_kill_does_not_shorten_anything()
    {
        ChunkRun run = Assert.Single(Plan(stop: true, 1));
        Assert.Equal(5, run.LastIndex);
    }

    [Fact]
    public void The_chunk_holding_the_kill_is_never_dropped()
    {
        // A death moments after the kill must not leave a run with nothing in it.
        ChunkRun run = Assert.Single(Plan(stop: true, 4.1));
        Assert.Equal(2, run.FirstIndex);
        Assert.Equal(2, run.LastIndex);
    }
}

public class KeyframeReadingTests
{
    [Fact]
    public void Parses_the_times_ffprobe_prints()
    {
        // Real output: one value per line, ffprobe's csv writer leaves a trailing comma.
        IReadOnlyList<double> times = ClipHighlightService.ParseKeyframes(
            "4125.010492\n4128.009940\n4131.010821\n");

        Assert.Equal(new[] { 4125.010492, 4128.009940, 4131.010821 }, times);
    }

    [Fact]
    public void Ignores_blank_lines_and_trailing_separators() =>
        Assert.Equal(
            new[] { 1.5, 4.5 },
            ClipHighlightService.ParseKeyframes("1.500000,\n\n4.500000,\n"));

    [Fact]
    public void No_keyframes_is_empty_rather_than_an_error() =>
        Assert.Empty(ClipHighlightService.ParseKeyframes("\n"));

    [Fact]
    public void The_reported_duration_is_used_when_it_lands_past_the_last_keyframe()
    {
        // ffprobe reports these assembled streams' duration as the end timestamp, because they
        // keep the session's clock: start 4125.010, "duration" 4134.044.
        Assert.Equal(
            4134.044040,
            ClipHighlightService.EndOf(new[] { 4125.010492, 4128.009940, 4131.010821 }, 4134.044040));
    }

    [Fact]
    public void Otherwise_the_last_chunk_is_assumed_to_run_as_long_as_the_longest()
    {
        Assert.Equal(9, ClipHighlightService.EndOf(new double[] { 0, 3, 6 }, 6));
    }
}

public class SegmentRangeTests : IDisposable
{
    private readonly string _folder = Path.Combine(
        Path.GetTempPath(), "sclip-range-" + Guid.NewGuid().ToString("N"));

    public SegmentRangeTests()
    {
        Directory.CreateDirectory(_folder);
        File.WriteAllBytes(Path.Combine(_folder, "init-stream0.m4s"), new byte[] { 0 });
        for (int i = 1; i <= 5; i++)
            File.WriteAllBytes(Path.Combine(_folder, $"chunk-stream0-{i:00000}.m4s"), new[] { (byte)i });
    }

    public void Dispose()
    {
        try { Directory.Delete(_folder, recursive: true); } catch (IOException) { }
        GC.SuppressFinalize(this);
    }

    private async Task<byte[]> Assemble(int first, int last)
    {
        string destination = Path.Combine(_folder, "out", $"{first}-{last}.mp4");
        await DashSegments.AssembleRangeAsync(_folder, 0, first, last, destination);
        return await File.ReadAllBytesAsync(destination);
    }

    [Fact]
    public async Task Writes_the_init_segment_and_only_the_chunks_asked_for() =>
        Assert.Equal(new byte[] { 0, 2, 3 }, await Assemble(2, 3));

    [Fact]
    public async Task A_single_chunk_is_a_valid_run() =>
        Assert.Equal(new byte[] { 0, 4 }, await Assemble(4, 4));

    [Fact]
    public async Task A_range_reaching_past_the_end_stops_at_the_last_chunk() =>
        Assert.Equal(new byte[] { 0, 4, 5 }, await Assemble(4, 99));

    [Fact]
    public async Task Assembling_everything_still_works() =>
        Assert.Equal(new byte[] { 0, 1, 2, 3, 4, 5 }, await Assemble(1, 5));

    [Fact]
    public async Task A_range_past_every_chunk_is_refused_rather_than_writing_nothing() =>
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => Assemble(9, 12));
}
