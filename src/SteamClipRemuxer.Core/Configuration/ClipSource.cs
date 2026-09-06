namespace SteamClipRemuxer.Core.Configuration;

/// <summary>
/// Where the tool looks for clips. Both live under the same input folder: Steam keeps its
/// unexported clips in a "clips" subfolder of the directory the exports are written to.
/// </summary>
public enum ClipSource
{
    /// <summary>
    /// Video files already exported from Steam, sitting in the input folder. What the tool has
    /// always done. An export carries no link back to its recording session, so clips read this
    /// way have no map, mode or kill information to build a title from.
    /// </summary>
    ExportedFiles,

    /// <summary>
    /// Steam's own clip folders, under "clips". Replaces the export step: the tool assembles the
    /// DASH segments itself, so a clip goes straight from Steam to a finished file without being
    /// exported first. Each folder carries a clip.pb locating it in the session timeline, which
    /// is what makes an informative title possible at all.
    /// </summary>
    SteamClips,
}
