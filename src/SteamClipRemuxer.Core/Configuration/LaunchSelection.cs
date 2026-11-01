namespace SteamClipRemuxer.Core.Configuration;

/// <summary>Which clips start out ticked when the list is first read.</summary>
public enum LaunchSelection
{
    /// <summary>Nothing is ticked; every clip is chosen by hand.</summary>
    None,

    /// <summary>Unprocessed clips recorded on today's local date.</summary>
    RecordedToday,

    /// <summary>Unprocessed clips recorded after the app was last closed.</summary>
    SinceLastSession,
}

public static class LaunchSelectionRule
{
    /// <summary>
    /// Whether a clip starts out ticked.
    ///
    /// A processed clip is never ticked, whatever the mode: pressing the button should not
    /// quietly redo work that is still visible in the list. Ticking every unprocessed clip was
    /// the old rule, and it fails as soon as some clips are never meant to be processed - they
    /// stay unprocessed forever and are ticked on every launch.
    /// </summary>
    /// <param name="lastSessionEnded">
    /// When the app was last closed. Null on a first run, when "since last session" has
    /// nothing to measure from, so nothing is ticked rather than everything.
    /// </param>
    /// <param name="zone">Whose calendar "today" is. The local zone unless a test says otherwise.</param>
    public static bool ShouldTick(
        LaunchSelection mode,
        bool unprocessed,
        DateTimeOffset recordedAt,
        DateTimeOffset now,
        DateTimeOffset? lastSessionEnded,
        TimeZoneInfo? zone = null)
    {
        if (!unprocessed) return false;

        zone ??= TimeZoneInfo.Local;

        return mode switch
        {
            LaunchSelection.RecordedToday =>
                TimeZoneInfo.ConvertTime(recordedAt, zone).Date == TimeZoneInfo.ConvertTime(now, zone).Date,
            LaunchSelection.SinceLastSession =>
                lastSessionEnded is { } ended && recordedAt > ended,
            _ => false,
        };
    }
}
