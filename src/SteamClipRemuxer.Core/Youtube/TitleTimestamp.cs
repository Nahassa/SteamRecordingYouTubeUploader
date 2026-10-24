namespace SteamClipRemuxer.Core.Youtube;

/// <summary>
/// What becomes of a timestamp a title has inherited.
///
/// Three states rather than the checkbox this replaces, because the useful middle one was
/// missing. A file name needs the time: it is what keeps two double kills from one evening from
/// colliding on a single filename. A YouTube title does not - the moment is sent as the video's
/// recording date, where YouTube can actually use it - and a title built from the file name drags
/// the time along with it.
///
/// Editing the template cannot fix that case, because the timestamp is inside {filename} or
/// {clip} rather than in a placeholder of its own. Hence <see cref="DateOnly"/>.
/// </summary>
public enum TitleTimestamp
{
    /// <summary>Leave whatever the template produced.</summary>
    Keep,

    /// <summary>Drop the time and keep the date: "2026-10-20 19-22-36" becomes "2026-10-20".</summary>
    DateOnly,

    /// <summary>Drop the whole timestamp. What "Strip the timestamp out of titles" used to do.</summary>
    None,
}
