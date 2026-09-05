namespace SteamClipRemuxer.Core.Steam;

/// <summary>
/// What Steam records in a clip folder's clip.pb.
///
/// This is the join between a saved clip and its recording session's timeline: the timeline
/// covers the whole session (90 minutes is normal) while a clip is seconds of it, and nothing
/// in the exported .mp4 says which seconds. <see cref="StartInSession"/> plus
/// <see cref="Duration"/> answers that exactly, so no timestamp guessing is involved.
///
/// The field numbers are inferred from real clip.pb files rather than from a published schema,
/// so anything optional is treated as optional and unknown fields are skipped.
/// </summary>
public sealed record ClipManifest
{
    /// <summary>File name (no extension) of the timeline this clip indexes into, e.g. "timeline_73020260828_204331".</summary>
    public required string TimelineFile { get; init; }

    /// <summary>When the recording session began. Timeline entry times are offsets from here.</summary>
    public required DateTimeOffset SessionStart { get; init; }

    /// <summary>Where the clip begins within the session, in the timeline's own coordinates.</summary>
    public required TimeSpan StartInSession { get; init; }

    /// <summary>How long the clip is. This is what distinguishes a cropped clip from a full buffer.</summary>
    public required TimeSpan Duration { get; init; }

    /// <summary>Folder holding the DASH segments, e.g. "bg_730_20260828_204356".</summary>
    public string VideoSessionFolder { get; init; } = string.Empty;

    /// <summary>
    /// How far the video session starts after the timeline session. Observed between 19.6s and
    /// 35.3s across samples, so it is never safe to assume: session.mpd's Period@start is measured
    /// from the video session, and only adding this reaches the timeline's coordinates.
    /// </summary>
    public TimeSpan VideoSessionOffset { get; init; }

    /// <summary>Steam's own suggested export name. Absent until the clip has been exported at least once.</summary>
    public string? SuggestedName { get; init; }

    public int Width { get; init; }
    public int Height { get; init; }

    /// <summary>
    /// Grouped attributes Steam attached to the clip, keyed by group: "Map" -> "Dust II",
    /// "Mode" -> "Competitive". Present on only some clips, so the timeline remains the
    /// authority for map and mode; this is a shortcut when it happens to be there.
    /// </summary>
    public IReadOnlyDictionary<string, string> Attributes { get; init; } =
        new Dictionary<string, string>();

    /// <summary>Labelled stats, e.g. "K/D/A" -> "28/12/4", "Score" -> "13 : 9". Also optional.</summary>
    public IReadOnlyDictionary<string, string> Stats { get; init; } =
        new Dictionary<string, string>();

    /// <summary>Where the clip ends within the session.</summary>
    public TimeSpan EndInSession => StartInSession + Duration;

    /// <summary>
    /// True when the clip is shorter than Steam's configured recording buffer, i.e. the user
    /// actually cropped it. An uncropped clip is exactly the buffer length - 120000ms on the
    /// samples measured - so a strict comparison separates the two cleanly.
    /// </summary>
    public bool IsCropped(TimeSpan bufferLength) => Duration < bufferLength;

    // Field numbers observed in real clip.pb files. Named rather than inlined so the layout is
    // documented in one place if Valve changes it.
    private const int SessionBlock = 1;
    private const int StartInSessionMs = 2;
    private const int SuggestedNameField = 7;
    private const int WidthField = 12;
    private const int HeightField = 13;

    private const int TimelineNameField = 1;
    private const int SessionStartEpochField = 3;
    private const int DurationMsField = 4;
    private const int VideoBlockField = 5;
    private const int AttributesBlockField = 6;

    private const int VideoFolderField = 1;
    private const int VideoOffsetMsField = 10;

    private const int AttributeTagField = 6;
    private const int StatField = 9;

    /// <summary>
    /// Parses clip.pb. Returns null when the file is not recognisable rather than throwing:
    /// a clip folder Steam wrote in a format we do not understand should be skipped, not
    /// crash a batch that has other clips to get through.
    /// </summary>
    public static ClipManifest? Parse(ReadOnlySpan<byte> bytes)
    {
        string? timelineFile = null;
        long sessionEpoch = 0, startMs = -1, durationMs = -1, videoOffsetMs = 0;
        string videoFolder = string.Empty;
        string? suggestedName = null;
        int width = 0, height = 0;
        var attributes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var stats = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        var reader = new ProtobufReader(bytes);
        while (!reader.AtEnd)
        {
            if (!reader.TryReadTag(out int field, out WireType wire)) return null;

            switch (field)
            {
                case SessionBlock when wire == WireType.LengthDelimited:
                {
                    if (!reader.TryReadLengthDelimited(out ReadOnlySpan<byte> block)) return null;
                    if (!ParseSessionBlock(block, ref timelineFile, ref sessionEpoch, ref durationMs,
                            ref videoFolder, ref videoOffsetMs, attributes, stats)) return null;
                    break;
                }
                case StartInSessionMs when wire == WireType.Varint:
                    if (!reader.TryReadVarint(out ulong start)) return null;
                    startMs = (long)start;
                    break;
                case SuggestedNameField when wire == WireType.LengthDelimited:
                    if (!reader.TryReadString(out string name)) return null;
                    suggestedName = name;
                    break;
                case WidthField when wire == WireType.Varint:
                    if (!reader.TryReadVarint(out ulong w)) return null;
                    width = (int)w;
                    break;
                case HeightField when wire == WireType.Varint:
                    if (!reader.TryReadVarint(out ulong h)) return null;
                    height = (int)h;
                    break;
                default:
                    // Includes field 3, an epoch that looks like the clip's start time but
                    // disagreed with SessionStart + StartInSession on two of five real files.
                    // The offset is the one that lands on the right events, so field 3 is
                    // deliberately not read.
                    if (!reader.TrySkip(wire)) return null;
                    break;
            }
        }

        if (timelineFile is null || sessionEpoch <= 0 || startMs < 0 || durationMs < 0) return null;

        return new ClipManifest
        {
            TimelineFile = timelineFile,
            SessionStart = DateTimeOffset.FromUnixTimeSeconds(sessionEpoch),
            StartInSession = TimeSpan.FromMilliseconds(startMs),
            Duration = TimeSpan.FromMilliseconds(durationMs),
            VideoSessionFolder = videoFolder,
            VideoSessionOffset = TimeSpan.FromMilliseconds(videoOffsetMs),
            SuggestedName = suggestedName,
            Width = width,
            Height = height,
            Attributes = attributes,
            Stats = stats,
        };
    }

    public static ClipManifest? Load(string path)
    {
        try
        {
            return Parse(File.ReadAllBytes(path));
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static bool ParseSessionBlock(
        ReadOnlySpan<byte> block, ref string? timelineFile, ref long sessionEpoch,
        ref long durationMs, ref string videoFolder, ref long videoOffsetMs,
        Dictionary<string, string> attributes, Dictionary<string, string> stats)
    {
        var reader = new ProtobufReader(block);
        while (!reader.AtEnd)
        {
            if (!reader.TryReadTag(out int field, out WireType wire)) return false;

            switch (field)
            {
                case TimelineNameField when wire == WireType.LengthDelimited:
                    if (!reader.TryReadString(out string tl)) return false;
                    timelineFile = tl;
                    break;
                case SessionStartEpochField when wire == WireType.Varint:
                    if (!reader.TryReadVarint(out ulong epoch)) return false;
                    sessionEpoch = (long)epoch;
                    break;
                case DurationMsField when wire == WireType.Varint:
                    if (!reader.TryReadVarint(out ulong dur)) return false;
                    durationMs = (long)dur;
                    break;
                case VideoBlockField when wire == WireType.LengthDelimited:
                {
                    if (!reader.TryReadLengthDelimited(out ReadOnlySpan<byte> video)) return false;
                    if (!ParseVideoBlock(video, ref videoFolder, ref videoOffsetMs)) return false;
                    break;
                }
                case AttributesBlockField when wire == WireType.LengthDelimited:
                {
                    if (!reader.TryReadLengthDelimited(out ReadOnlySpan<byte> attrs)) return false;
                    if (!ParseAttributesBlock(attrs, attributes, stats)) return false;
                    break;
                }
                default:
                    if (!reader.TrySkip(wire)) return false;
                    break;
            }
        }

        return true;
    }

    private static bool ParseVideoBlock(
        ReadOnlySpan<byte> block, ref string videoFolder, ref long videoOffsetMs)
    {
        var reader = new ProtobufReader(block);
        while (!reader.AtEnd)
        {
            if (!reader.TryReadTag(out int field, out WireType wire)) return false;

            switch (field)
            {
                case VideoFolderField when wire == WireType.LengthDelimited:
                    if (!reader.TryReadString(out string folder)) return false;
                    videoFolder = folder;
                    break;
                case VideoOffsetMsField when wire == WireType.Varint:
                    if (!reader.TryReadVarint(out ulong offset)) return false;
                    videoOffsetMs = (long)offset;
                    break;
                default:
                    if (!reader.TrySkip(wire)) return false;
                    break;
            }
        }

        return true;
    }

    /// <summary>
    /// Reads the optional block carrying map, mode and scoreline. Tags are {value, group} pairs
    /// and stats are {label, value, priority} triples, so both are stored keyed by the second
    /// and first string respectively.
    /// </summary>
    private static bool ParseAttributesBlock(
        ReadOnlySpan<byte> block, Dictionary<string, string> attributes, Dictionary<string, string> stats)
    {
        var reader = new ProtobufReader(block);
        while (!reader.AtEnd)
        {
            if (!reader.TryReadTag(out int field, out WireType wire)) return false;

            if (field == AttributeTagField && wire == WireType.LengthDelimited)
            {
                if (!reader.TryReadLengthDelimited(out ReadOnlySpan<byte> tag)) return false;
                if (TryReadPair(tag, out string value, out string group) && group.Length > 0)
                {
                    attributes[group] = value;
                }
            }
            else if (field == StatField && wire == WireType.LengthDelimited)
            {
                if (!reader.TryReadLengthDelimited(out ReadOnlySpan<byte> stat)) return false;
                if (TryReadPair(stat, out string label, out string value) && label.Length > 0)
                {
                    stats[label] = value;
                }
            }
            else if (!reader.TrySkip(wire))
            {
                return false;
            }
        }

        return true;
    }

    private static bool TryReadPair(ReadOnlySpan<byte> block, out string first, out string second)
    {
        first = string.Empty;
        second = string.Empty;

        var reader = new ProtobufReader(block);
        while (!reader.AtEnd)
        {
            if (!reader.TryReadTag(out int field, out WireType wire)) return false;

            if (wire == WireType.LengthDelimited && field is 1 or 2)
            {
                if (!reader.TryReadString(out string text)) return false;
                if (field == 1) first = text; else second = text;
            }
            else if (!reader.TrySkip(wire))
            {
                return false;
            }
        }

        return true;
    }
}
