using System.Globalization;
using System.Text.Json;

namespace SteamClipRemuxer.Core.Timelines;

/// <summary>One entry from Steam's session timeline.</summary>
public sealed record TimelineEntry
{
    public required TimeSpan Time { get; init; }
    public required string Type { get; init; }
    public string Title { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
    public string Icon { get; init; } = string.Empty;
    public TimeSpan Duration { get; init; }

    /// <summary>Steam's own view of how clippable this is: 3 for a multi-kill, 2 for a kill, 1 otherwise.</summary>
    public int PossibleClip { get; init; }

    /// <summary>Tags on a phase entry, keyed by group: "Map" -> "Nuke", "Mode" -> "Competitive".</summary>
    public IReadOnlyDictionary<string, string> Tags { get; init; } = new Dictionary<string, string>();

    public TimeSpan End => Time + Duration;
}

/// <summary>
/// A recording session's timeline, as Steam writes it beside the clips.
///
/// Times are offsets from the start of the session, which is what a clip's
/// <c>ClipManifest.StartInSession</c> indexes into. One timeline covers the whole session -
/// ninety minutes and several maps is normal - so it is only useful alongside a clip window.
/// </summary>
public sealed class SessionTimeline
{
    public IReadOnlyList<TimelineEntry> Entries { get; }

    public SessionTimeline(IReadOnlyList<TimelineEntry> entries)
    {
        Entries = entries.OrderBy(e => e.Time).ToList();
    }

    /// <summary>Entries beginning inside the window.</summary>
    public IEnumerable<TimelineEntry> EntriesIn(TimeSpan start, TimeSpan end) =>
        Entries.Where(e => e.Time >= start && e.Time <= end);

    /// <summary>
    /// The value of a phase tag in force at a moment, e.g. "Map" or "Mode". Phases carry a
    /// duration, but a clip can sit after the last phase Steam wrote, so the most recent phase
    /// at or before the moment is used rather than requiring containment.
    /// </summary>
    public string? TagAt(TimeSpan moment, string group)
    {
        TimelineEntry? phase = Entries
            .Where(e => e.Type == "phase" && e.Time <= moment && e.Tags.ContainsKey(group))
            .LastOrDefault();

        return phase is null ? null : phase.Tags[group];
    }

    public string? MapAt(TimeSpan moment) => TagAt(moment, "Map");

    public string? ModeAt(TimeSpan moment) => TagAt(moment, "Mode");

    /// <summary>The round number in force at a moment, from the most recent "Start of round N".</summary>
    public int? RoundAt(TimeSpan moment)
    {
        TimelineEntry? round = Entries
            .Where(e => e.Type == "event" && e.Time <= moment && e.Title.StartsWith("Start of round ", StringComparison.Ordinal))
            .LastOrDefault();

        if (round is null) return null;

        string tail = round.Title["Start of round ".Length..];
        return int.TryParse(tail, NumberStyles.Integer, CultureInfo.InvariantCulture, out int n) ? n : null;
    }

    public static SessionTimeline Parse(string json)
    {
        var entries = new List<TimelineEntry>();

        using JsonDocument document = JsonDocument.Parse(json);
        if (!document.RootElement.TryGetProperty("entries", out JsonElement entriesElement))
        {
            return new SessionTimeline(entries);
        }

        // Steam sometimes writes "entries" as an object keyed by index rather than an array;
        // TimelineFixer repairs that on disk, but reading has to tolerate both so a timeline
        // that has not been through the fixer still works.
        IEnumerable<JsonElement> items = entriesElement.ValueKind switch
        {
            JsonValueKind.Array => entriesElement.EnumerateArray(),
            JsonValueKind.Object => OrderedByNumericKey(entriesElement),
            _ => Enumerable.Empty<JsonElement>(),
        };

        foreach (JsonElement item in items)
        {
            if (item.ValueKind != JsonValueKind.Object) continue;
            entries.Add(ReadEntry(item));
        }

        return new SessionTimeline(entries);
    }

    public static SessionTimeline Load(string path) => Parse(File.ReadAllText(path));

    private static IEnumerable<JsonElement> OrderedByNumericKey(JsonElement obj)
    {
        // Keys are indices, so an ordinal sort yields 0, 1, 10, 11, 2 and scrambles the session.
        return obj.EnumerateObject()
            .OrderBy(p => long.TryParse(p.Name, NumberStyles.Integer, CultureInfo.InvariantCulture, out long n)
                ? n
                : long.MaxValue)
            .ThenBy(p => p.Name, StringComparer.Ordinal)
            .Select(p => p.Value);
    }

    private static TimelineEntry ReadEntry(JsonElement item)
    {
        var tags = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (item.TryGetProperty("tags", out JsonElement tagsElement) &&
            tagsElement.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement tag in tagsElement.EnumerateArray())
            {
                string? group = ReadString(tag, "group");
                string? name = ReadString(tag, "name");
                if (!string.IsNullOrEmpty(group) && name is not null) tags[group] = name;
            }
        }

        return new TimelineEntry
        {
            Time = TimeSpan.FromMilliseconds(ReadLong(item, "time")),
            Duration = TimeSpan.FromMilliseconds(ReadLong(item, "duration")),
            Type = ReadString(item, "type") ?? string.Empty,
            Title = ReadString(item, "title") ?? string.Empty,
            Description = ReadString(item, "description") ?? string.Empty,
            Icon = ReadString(item, "icon") ?? string.Empty,
            PossibleClip = (int)ReadLong(item, "possible_clip"),
            Tags = tags,
        };
    }

    private static string? ReadString(JsonElement element, string name) =>
        element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    /// <summary>
    /// Reads a number that Steam writes as a JSON string in some fields and a bare number in
    /// others: "time" and "duration" are quoted, "possible_clip" and "priority" are not.
    /// </summary>
    private static long ReadLong(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out JsonElement value)) return 0;

        return value.ValueKind switch
        {
            JsonValueKind.Number => value.TryGetInt64(out long n) ? n : 0,
            JsonValueKind.String => long.TryParse(
                value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out long s) ? s : 0,
            _ => 0,
        };
    }
}
