using System.Text.RegularExpressions;
using SteamClipRemuxer.Core.Timelines;

namespace SteamClipRemuxer.Core.Highlights;

/// <summary>
/// Works out what a clip shows, from the timeline entries inside its window.
///
/// Steam's own multi-kill labels are not trusted, because they are wrong in both directions on
/// real recordings:
///
/// - They undercount. One measured session has a P250 kill 3.4 seconds before a "Double kill"
///   event, which is a triple that Steam reported as two separate things.
/// - They overcount. In deathmatch, where players respawn, 45 of 74 labelled multi-kills name
///   the same victim more than once, and one labels the player's own suicide a "Double kill".
///
/// So the kills are recounted from the atomic events and re-clustered here. Steam's labelled
/// events still have to be read, because the kills composing one are not listed separately -
/// but they are read for their victim count, not their verdict.
/// </summary>
public static class HighlightSelector
{
    /// <summary>
    /// How far apart two kills can be and still count as one engagement. Counter-Strike's own
    /// multi-kill window is five seconds; eight is loose enough to hold a fight that pauses for
    /// a reload without joining two separate ones.
    /// </summary>
    public static readonly TimeSpan EngagementWindow = TimeSpan.FromSeconds(8);

    private static readonly Regex KilledBy = new(
        @"^You killed (?<victims>.+?)(?: with (?:the )?(?<weapon>.+?))?$",
        RegexOptions.Compiled);

    private static readonly HashSet<string> KillIcons = new(StringComparer.OrdinalIgnoreCase)
    {
        "cs2_gun_kill", "cs2_grenade_kill", "cs2_inferno_kill",
    };

    private static readonly HashSet<string> MultiKillIcons = new(StringComparer.OrdinalIgnoreCase)
    {
        "cs2_double_kill", "cs2_multi_kill",
    };

    private sealed record Kill(TimeSpan Time, string? Weapon);

    /// <summary>
    /// Builds a highlight for the clip covering [start, end). Returns null when the window holds
    /// nothing worth naming, so the caller can fall back rather than publish "Highlight".
    /// </summary>
    public static Highlight? Select(SessionTimeline timeline, TimeSpan start, TimeSpan end)
    {
        // A labelled multi-kill carries its own duration and can begin just before the clip while
        // still being what the clip is about, so the search reaches back by one engagement.
        List<TimelineEntry> candidates = timeline.Entries
            .Where(e => e.Type == "event" && e.End >= start - EngagementWindow && e.Time <= end)
            .ToList();

        List<Kill> kills = candidates.SelectMany(ToKills).OrderBy(k => k.Time).ToList();

        bool planted = candidates.Any(e =>
            e.Icon.Equals("cs2_bomb_plant", StringComparison.OrdinalIgnoreCase) &&
            e.Description.StartsWith("You ", StringComparison.OrdinalIgnoreCase));

        bool defused = candidates.Any(e =>
            e.Icon.Equals("cs2_bomb_defused", StringComparison.OrdinalIgnoreCase) &&
            e.Description.StartsWith("You ", StringComparison.OrdinalIgnoreCase));

        string? map = timeline.MapAt(start);
        string? mode = timeline.ModeAt(start);
        int? round = timeline.RoundAt(start);

        if (kills.Count == 0)
        {
            // Still worth a title if the player did something else notable in the window.
            if (!planted && !defused) return null;

            return new Highlight
            {
                KillCount = 0,
                Map = map,
                Mode = mode,
                Round = round,
                PlantedBomb = planted,
                DefusedBomb = defused,
            };
        }

        List<Kill> best = BestEngagement(kills);

        // Only claim a weapon when every kill in the engagement used the same one: "Double kill
        // with the AK-47" is wrong if half of it was a grenade, and equally wrong if Steam
        // recorded no weapon for part of it.
        bool everyKillNamesAWeapon = best.All(k => !string.IsNullOrEmpty(k.Weapon));
        string[] distinctWeapons = best
            .Select(k => k.Weapon)
            .Where(w => !string.IsNullOrEmpty(w))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray()!;

        return new Highlight
        {
            KillCount = best.Count,
            Weapon = everyKillNamesAWeapon && distinctWeapons.Length == 1 ? distinctWeapons[0] : null,
            Map = map,
            Mode = mode,
            Round = round,
            PlantedBomb = planted,
            DefusedBomb = defused,
        };
    }

    /// <summary>The largest cluster of kills falling within one engagement window of each other.</summary>
    private static List<Kill> BestEngagement(List<Kill> kills)
    {
        var clusters = new List<List<Kill>>();
        var current = new List<Kill>();

        foreach (Kill kill in kills)
        {
            if (current.Count > 0 && kill.Time - current[^1].Time > EngagementWindow)
            {
                clusters.Add(current);
                current = new List<Kill>();
            }

            current.Add(kill);
        }

        if (current.Count > 0) clusters.Add(current);

        return clusters.OrderByDescending(c => c.Count).ThenBy(c => c[0].Time).First();
    }

    /// <summary>
    /// Turns one event into the kills it represents. A labelled multi-kill has to be expanded
    /// from its description, because Steam does not also list the kills composing it.
    /// </summary>
    private static IEnumerable<Kill> ToKills(TimelineEntry entry)
    {
        if (KillIcons.Contains(entry.Icon))
        {
            // An atomic kill puts the victim in the title and the weapon in the description,
            // e.g. title "You killed Osch", description "with the AK-47".
            yield return new Kill(entry.Time, CleanWeapon(entry.Description));
            yield break;
        }

        if (!MultiKillIcons.Contains(entry.Icon)) yield break;

        Match match = KilledBy.Match(entry.Description);
        if (!match.Success) yield break;

        string? weapon = CleanWeapon(match.Groups["weapon"].Value);
        int victims = CountVictims(match.Groups["victims"].Value);

        // Spread them across the event's own duration so clustering sees one engagement rather
        // than several kills stacked on a single instant.
        TimeSpan step = victims > 1 && entry.Duration > TimeSpan.Zero
            ? entry.Duration / victims
            : TimeSpan.FromSeconds(1);

        for (int i = 0; i < victims; i++)
        {
            yield return new Kill(entry.Time + step * i, weapon);
        }
    }

    /// <summary>
    /// Counts the victims named in a multi-kill description without keeping any of them.
    /// Steam writes "A and B" for two and "A, B, and C" beyond that.
    /// </summary>
    private static int CountVictims(string victims)
    {
        if (string.IsNullOrWhiteSpace(victims)) return 0;

        string normalised = Regex.Replace(victims, @",?\s+and\s+", ",", RegexOptions.IgnoreCase);
        int count = normalised.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Length;

        // A name containing a comma would inflate this. It only ever affects the count, never
        // the output, because no name reaches the title either way.
        return Math.Max(count, 1);
    }

    private static string? CleanWeapon(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;

        string weapon = raw.Trim();
        if (weapon.StartsWith("with the ", StringComparison.OrdinalIgnoreCase)) weapon = weapon[9..];
        else if (weapon.StartsWith("with ", StringComparison.OrdinalIgnoreCase)) weapon = weapon[5..];

        weapon = weapon.Trim();

        // "with fire" and "with the world" are how Steam records burn and world damage. Neither
        // is a weapon anyone would want in a title.
        if (weapon.Length == 0 || weapon.Equals("fire", StringComparison.OrdinalIgnoreCase)
                               || weapon.Equals("world", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return weapon;
    }
}
