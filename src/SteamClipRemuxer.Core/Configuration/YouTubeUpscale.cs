namespace SteamClipRemuxer.Core.Configuration;

/// <summary>
/// How far to scale a video up before uploading it, or not at all.
///
/// This exists for one measured reason. Steam records this machine at 1280x960, which displays at
/// 1706.67x960 once the pixel aspect is honoured, and height 960 falls between YouTube's 720 and
/// 1080 ladder rungs - so YouTube builds 720p as the best available and the footage is watched a
/// rung below what it deserves. Reaching 1080 measured +2.37 dB against +0.78 dB for the same
/// bitrate at 720p, so most of the gain is the rung rather than the bits.
///
/// Named To1080p rather than 1080p because a C# member cannot begin with a digit. The names are
/// what gets persisted, so they are not free to change.
/// </summary>
public enum YouTubeUpscale
{
    /// <summary>Upload exactly what was built. The default, and the only lossless option.</summary>
    Off,

    /// <summary>Scale to 1080 lines, which is the rung this footage is just short of.</summary>
    To1080p,

    /// <summary>Scale to 1440 lines. Measured a further +1.93 dB, at roughly twice the bitrate.</summary>
    To1440p,
}
