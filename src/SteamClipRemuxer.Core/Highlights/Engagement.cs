namespace SteamClipRemuxer.Core.Highlights;

/// <summary>
/// One fight: the kills close enough together to belong to the same moment.
///
/// A clip holds as many of these as the player had fights in it. <see cref="HighlightSelector"/>
/// reduces them to the single best one when all it needs is a title; cutting a clip down to its
/// kills needs every one of them, and needs to know where each begins and ends.
///
/// Carries no player names, for the same reason <see cref="Highlight"/> does not.
/// </summary>
public sealed record Engagement
{
    /// <summary>When the first kill of the fight landed, in the timeline's clock.</summary>
    public required TimeSpan FirstKill { get; init; }

    /// <summary>When the last one did. Equal to <see cref="FirstKill"/> for a single kill.</summary>
    public required TimeSpan LastKill { get; init; }

    public required int KillCount { get; init; }

    /// <summary>The weapon, when the whole fight used one.</summary>
    public string? Weapon { get; init; }

    /// <summary>The round it happened in, where the game has rounds.</summary>
    public int? Round { get; init; }

    /// <summary>
    /// What to call it. Built through <see cref="Highlight"/> rather than separately, so a reel's
    /// chapter reads exactly as the clip's own title would.
    /// </summary>
    public string Label => new Highlight { KillCount = KillCount, Weapon = Weapon }.Describe();

    /// <summary>The label with the round in front, for a reel that spans more than one.</summary>
    public string LabelWithRound => Round is { } round ? $"Round {round} - {Label}" : Label;
}
