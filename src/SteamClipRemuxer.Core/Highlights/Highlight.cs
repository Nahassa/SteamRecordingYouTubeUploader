namespace SteamClipRemuxer.Core.Highlights;

/// <summary>
/// What a clip shows, reduced to the parts worth putting in a title.
///
/// Deliberately carries no player names. Steam's own event descriptions name the victims
/// ("You killed Wilthy and Matti | RustFlip.gg with the AK-47"), which is not something to
/// publish to YouTube, so names are dropped during selection rather than filtered out later.
/// </summary>
public sealed record Highlight
{
    /// <summary>Kills by the recording player in the clip's best single engagement.</summary>
    public required int KillCount { get; init; }

    /// <summary>The weapon, when the whole engagement used one. Null when it was mixed or Steam omitted it.</summary>
    public string? Weapon { get; init; }

    public string? Map { get; init; }
    public string? Mode { get; init; }
    public int? Round { get; init; }

    /// <summary>Whether the player planted or defused during the clip; both read well in a description.</summary>
    public bool PlantedBomb { get; init; }
    public bool DefusedBomb { get; init; }

    /// <summary>
    /// Counter-Strike's own name for a multi-kill of this size. Beyond an ace it degrades to a
    /// count rather than inventing a term.
    /// </summary>
    public string Label => KillCount switch
    {
        <= 0 => "Highlight",
        1 => "Kill",
        2 => "Double kill",
        3 => "Triple kill",
        4 => "Quad kill",
        5 => "Ace",
        _ => $"{KillCount} kills",
    };

    /// <summary>
    /// The label with the weapon when one applies. Steam sometimes records a multi-kill with no
    /// weapon at all - one real sample reads "You killed rokita4 and Kopala2" with nothing
    /// further - so this degrades to the bare label rather than emitting a dangling "with the".
    /// </summary>
    public string Describe() =>
        Weapon is null or "" ? Label : $"{Label} with the {Weapon}";
}
