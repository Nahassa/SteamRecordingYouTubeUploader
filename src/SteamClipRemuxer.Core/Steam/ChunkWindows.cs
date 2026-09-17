using System.Globalization;
using SteamClipRemuxer.Core.Highlights;
using SteamClipRemuxer.Core.Timelines;

namespace SteamClipRemuxer.Core.Steam;

/// <summary>How much of a clip to keep around each fight.</summary>
public sealed record HighlightWindowOptions
{
    /// <summary>Footage kept before the first kill of a fight. Snaps outward to a chunk boundary.</summary>
    public TimeSpan Lead { get; init; } = TimeSpan.FromSeconds(3);

    /// <summary>Footage kept after the last kill of a fight. Snaps outward to a chunk boundary.</summary>
    public TimeSpan Tail { get; init; } = TimeSpan.FromSeconds(3);

    /// <summary>
    /// Rounds contributing fewer kills than this are dropped whole. 1 keeps everything.
    ///
    /// Counted over the kills inside this clip, not the round's full tally: the point is to keep
    /// rounds worth watching, and a round whose other two kills happened before the clip begins
    /// has nothing to show for them.
    /// </summary>
    public int MinimumKillsPerRound { get; init; } = 1;

    /// <summary>End a run before the player's own death rather than carrying past it.</summary>
    public bool StopBeforeDeaths { get; init; }
}

/// <summary>A contiguous span of Steam's chunks to keep, numbered as Steam numbers them.</summary>
public sealed record ChunkRun
{
    /// <summary>First chunk to keep, 1-based, inclusive.</summary>
    public required int FirstIndex { get; init; }

    /// <summary>Last chunk to keep, 1-based, inclusive.</summary>
    public required int LastIndex { get; init; }

    public int? Round { get; init; }

    /// <summary>The fights this run covers, oldest first.</summary>
    public required IReadOnlyList<Engagement> Engagements { get; init; }

    public int ChunkCount => LastIndex - FirstIndex + 1;

    public int Kills => Engagements.Sum(e => e.KillCount);

    /// <summary>
    /// What this one run reads, in the log. A run covering two separate fights is named for the
    /// bigger one: they are more than an engagement window apart, so summing them into a "triple
    /// kill" would claim a multi-kill that did not happen.
    ///
    /// A round is different, and <see cref="ChunkWindows.RoundLabel"/> does sum it. "Ace" in
    /// Counter-Strike means killing the enemy team over a round, not inside one engagement, so
    /// a round's total is a real thing to name where a run's is not.
    /// </summary>
    public string Label
    {
        get
        {
            Engagement best = Engagements
                .OrderByDescending(e => e.KillCount)
                .ThenBy(e => e.FirstKill)
                .First();

            return Round is { } round ? $"Round {round} - {best.Label}" : best.Label;
        }
    }

    /// <summary>
    /// Identifies the chapter this run belongs to, or null when it has no round to share. Runs
    /// carrying the same key and lying next to each other are listed as one timestamp.
    /// </summary>
    public string? ChapterGroup =>
        Round is { } round ? round.ToString(CultureInfo.InvariantCulture) : null;
}

/// <summary>
/// Works out which of Steam's chunks to keep so a clip is reduced to the fights in it.
///
/// Chunk selection rather than seeking, because Steam's chunks are exactly the unit a lossless
/// cut can work in. Each one begins with a keyframe - measured at 4125.010, 4128.010 and
/// 4131.011 on the sample, one I-frame per chunk over a 180-frame GOP - so a chunk can be kept
/// or dropped whole with no re-encoding and nothing hidden.
///
/// Asking FFmpeg to seek instead looks equivalent and is not: `-ss 6 -t 3` on that sample wrote
/// 360 packets for a 180-frame window and hid the excess behind an edit list. The concat demuxer
/// discards edit lists, so the three seconds meant to be skipped reappear in the joined reel.
///
/// Pure: no FFmpeg, no filesystem, so every rule here is covered by tests.
/// </summary>
public static class ChunkWindows
{
    /// <summary>
    /// What a whole round reads, over every run cut from it.
    ///
    /// Summed rather than named for its biggest fight, because a round is the unit the count
    /// belongs to: a double kill and a triple kill in round 19 are five kills in round 19, which
    /// Counter-Strike calls an ace. Listing them as two timestamps says the round happened twice.
    ///
    /// The weapon survives only when every kill in the round used the same one - the rule
    /// <see cref="Engagement"/> already applies within a fight, applied one level up.
    /// </summary>
    public static string RoundLabel(IReadOnlyList<ChunkRun> runsOfOneRound)
    {
        if (runsOfOneRound.Count == 0) throw new ArgumentException("No runs.", nameof(runsOfOneRound));

        List<Engagement> fights = runsOfOneRound.SelectMany(r => r.Engagements).ToList();

        // Nulls are filtered before the comparison rather than counted as a value: Steam records
        // no weapon at all for some multi-kills, and StringComparer throws on a null anyway.
        bool everyFightNamesAWeapon = fights.All(f => !string.IsNullOrEmpty(f.Weapon));
        string[] weapons = fights
            .Select(f => f.Weapon)
            .Where(w => !string.IsNullOrEmpty(w))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray()!;

        var whole = new Highlight
        {
            KillCount = fights.Sum(f => f.KillCount),
            // Claimed only when the whole round used one weapon. A round where Steam recorded a
            // weapon for half the kills is not a round fought with that weapon.
            Weapon = everyFightNamesAWeapon && weapons.Length == 1 ? weapons[0] : null,
        };

        int? round = runsOfOneRound[0].Round;
        return round is { } number ? $"Round {number} - {whole.Describe()}" : whole.Describe();
    }

    /// <summary>
    /// The runs of chunks to keep, in playback order and non-overlapping.
    /// </summary>
    /// <param name="chunkStarts">
    /// Where each chunk begins, in the video session's clock, oldest first. Read from the
    /// assembled stream's keyframes rather than assumed to be 3.0s apart - the measured gaps are
    /// 2.999 and 3.001 - so nothing breaks if Steam changes the segment length.
    /// </param>
    /// <param name="streamEnd">Where the last chunk ends, in the same clock.</param>
    /// <param name="videoSessionOffsetSeconds">
    /// The gap between the timeline's clock and the video session's, from clip.pb. Kill times are
    /// in the former and the chunks in the latter; ignoring it misplaces every window by 25
    /// seconds on the sample.
    /// </param>
    public static IReadOnlyList<ChunkRun> Plan(
        IReadOnlyList<double> chunkStarts,
        double streamEnd,
        double videoSessionOffsetSeconds,
        IReadOnlyList<Engagement> engagements,
        IReadOnlyList<RoundStart> roundStarts,
        IReadOnlyList<TimeSpan> deaths,
        HighlightWindowOptions? options = null)
    {
        options ??= new HighlightWindowOptions();

        if (chunkStarts.Count == 0 || engagements.Count == 0) return Array.Empty<ChunkRun>();

        var runs = new List<ChunkRun>();

        foreach (Engagement fight in KeepQualifyingRounds(engagements, options.MinimumKillsPerRound))
        {
            (TimeSpan from, TimeSpan to) = Window(fight, roundStarts, deaths, options);

            int first = ChunkAt(chunkStarts, from.TotalSeconds - videoSessionOffsetSeconds);
            int last = ChunkAt(chunkStarts, to.TotalSeconds - videoSessionOffsetSeconds);

            (first, last) = TrimToRound(
                chunkStarts, streamEnd, videoSessionOffsetSeconds, first, last, fight, roundStarts);

            runs.Add(new ChunkRun
            {
                FirstIndex = first,
                LastIndex = last,
                Round = fight.Round,
                Engagements = new[] { fight },
            });
        }

        return Merge(runs);
    }

    /// <summary>
    /// Drops every fight in a round that did not produce enough kills inside this clip.
    ///
    /// Fights with no round at all are treated as one group, so a game without rounds is filtered
    /// on the clip's whole kill count rather than silently keeping everything.
    /// </summary>
    private static IEnumerable<Engagement> KeepQualifyingRounds(
        IReadOnlyList<Engagement> engagements, int minimumKillsPerRound)
    {
        if (minimumKillsPerRound <= 1) return engagements.OrderBy(e => e.FirstKill);

        return engagements
            .GroupBy(e => e.Round)
            .Where(round => round.Sum(e => e.KillCount) >= minimumKillsPerRound)
            .SelectMany(round => round)
            .OrderBy(e => e.FirstKill);
    }

    /// <summary>The span of time a fight is worth, before it is widened to whole chunks.</summary>
    private static (TimeSpan From, TimeSpan To) Window(
        Engagement fight,
        IReadOnlyList<RoundStart> roundStarts,
        IReadOnlyList<TimeSpan> deaths,
        HighlightWindowOptions options)
    {
        TimeSpan from = fight.FirstKill - options.Lead;
        TimeSpan to = fight.LastKill + options.Tail;

        // A clip is usually longer than a round, so without this the round-end screen and the
        // next round's buy time end up in the reel.
        (TimeSpan? started, TimeSpan? next) = RoundBounds(fight.FirstKill, roundStarts);
        if (started is { } begins && from < begins) from = begins;
        if (next is { } ends && to > ends) to = ends;

        if (options.StopBeforeDeaths)
        {
            TimeSpan? death = deaths.Where(d => d > fight.LastKill).Cast<TimeSpan?>().FirstOrDefault();
            if (death is { } died && to > died) to = died;
        }

        return (from, to);
    }

    /// <summary>When the round holding a moment started, and when the next one does.</summary>
    private static (TimeSpan? Started, TimeSpan? Next) RoundBounds(
        TimeSpan moment, IReadOnlyList<RoundStart> roundStarts)
    {
        TimeSpan? started = null;
        TimeSpan? next = null;

        foreach (RoundStart round in roundStarts)
        {
            if (round.At <= moment) started = round.At;
            else { next = round.At; break; }
        }

        return (started, next);
    }

    /// <summary>
    /// Removes chunks that lie wholly outside the fight's round. A chunk straddling the boundary
    /// is kept: it holds the end of the round, and three seconds is as fine as a copy can cut.
    /// </summary>
    private static (int First, int Last) TrimToRound(
        IReadOnlyList<double> chunkStarts,
        double streamEnd,
        double offsetSeconds,
        int first,
        int last,
        Engagement fight,
        IReadOnlyList<RoundStart> roundStarts)
    {
        (TimeSpan? started, TimeSpan? next) = RoundBounds(fight.FirstKill, roundStarts);

        // The chunk holding the first kill is never dropped, however the boundaries fall.
        int keep = ChunkAt(chunkStarts, fight.FirstKill.TotalSeconds - offsetSeconds);

        if (next is { } ends)
        {
            double boundary = ends.TotalSeconds - offsetSeconds;
            while (last > keep && chunkStarts[last - 1] >= boundary) last--;
        }

        if (started is { } begins)
        {
            double boundary = begins.TotalSeconds - offsetSeconds;
            while (first < keep && End(chunkStarts, streamEnd, first) <= boundary) first++;
        }

        return (first, last);
    }

    /// <summary>
    /// Joins runs that touch or overlap. Runs from different rounds are left alone even when
    /// adjacent, because welding them together is exactly how the buy time between two rounds
    /// gets back into the reel.
    /// </summary>
    private static IReadOnlyList<ChunkRun> Merge(List<ChunkRun> runs)
    {
        var merged = new List<ChunkRun>();

        foreach (ChunkRun run in runs.OrderBy(r => r.FirstIndex).ThenBy(r => r.LastIndex))
        {
            ChunkRun? previous = merged.Count > 0 ? merged[^1] : null;

            if (previous is not null
                && previous.Round == run.Round
                && run.FirstIndex <= previous.LastIndex + 1)
            {
                merged[^1] = previous with
                {
                    LastIndex = Math.Max(previous.LastIndex, run.LastIndex),
                    Engagements = previous.Engagements.Concat(run.Engagements).ToList(),
                };

                continue;
            }

            merged.Add(run);
        }

        return merged;
    }

    /// <summary>The 1-based chunk covering a moment, clamped to the chunks that exist.</summary>
    private static int ChunkAt(IReadOnlyList<double> chunkStarts, double at)
    {
        if (at < chunkStarts[0]) return 1;

        for (int i = chunkStarts.Count - 1; i >= 0; i--)
        {
            if (at >= chunkStarts[i]) return i + 1;
        }

        return 1;
    }

    /// <summary>Where a 1-based chunk ends.</summary>
    private static double End(IReadOnlyList<double> chunkStarts, double streamEnd, int index) =>
        index < chunkStarts.Count ? chunkStarts[index] : streamEnd;
}
