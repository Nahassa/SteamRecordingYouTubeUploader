using System.Globalization;
using System.Text.RegularExpressions;

namespace SteamClipRemuxer.Core.Steam;

/// <summary>
/// The DASH segments Steam writes for one clip, and how to turn them back into playable files.
///
/// session.mpd cannot be handed to FFmpeg directly. Its Period@start is measured from the start
/// of the recording session - over an hour on a real sample - while mediaPresentationDuration is
/// only the clip's own length, so the demuxer computes a nonsense period and stops after the
/// first segment. Feeding it a real clip produced 3.008 seconds of a 7.435 second clip.
///
/// Concatenating the initialisation segment with its media segments produces a valid fragmented
/// MP4 that FFmpeg reads completely, which is the standard way to do this and is a byte copy.
/// </summary>
public static class DashSegments
{
    private static readonly Regex ChunkNumber = new(@"-(\d+)\.m4s$", RegexOptions.Compiled);

    public const int VideoStream = 0;
    public const int AudioStream = 1;

    /// <summary>Whether the folder holds segments for the given stream.</summary>
    public static bool HasStream(string videoFolder, int stream) =>
        File.Exists(InitPath(videoFolder, stream)) && Chunks(videoFolder, stream).Count > 0;

    public static string InitPath(string videoFolder, int stream) =>
        Path.Combine(videoFolder, $"init-stream{stream.ToString(CultureInfo.InvariantCulture)}.m4s");

    /// <summary>
    /// The media segments for a stream, in playback order.
    ///
    /// Steam zero-pads the numbers, so an ordinal sort happens to work today, but the same
    /// assumption about Steam's numbering is what scrambled timeline entries into 0, 1, 10, 2.
    /// Sorting numerically costs nothing and cannot break if the padding changes.
    /// </summary>
    public static IReadOnlyList<string> Chunks(string videoFolder, int stream)
    {
        if (!Directory.Exists(videoFolder)) return Array.Empty<string>();

        return Directory
            .EnumerateFiles(videoFolder, $"chunk-stream{stream.ToString(CultureInfo.InvariantCulture)}-*.m4s")
            .Select(path => (path, number: NumberOf(path)))
            .OrderBy(x => x.number)
            .ThenBy(x => x.path, StringComparer.OrdinalIgnoreCase)
            .Select(x => x.path)
            .ToList();
    }

    private static long NumberOf(string path)
    {
        Match match = ChunkNumber.Match(Path.GetFileName(path));
        return match.Success && long.TryParse(
            match.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out long n)
            ? n
            : long.MaxValue;
    }

    /// <summary>
    /// Writes the initialisation segment followed by every media segment into one file, which
    /// FFmpeg can then read as an ordinary fragmented MP4. Pure concatenation: no bytes of the
    /// encoded stream are altered, which is what keeps the whole pipeline lossless.
    /// </summary>
    public static async Task<string> AssembleAsync(
        string videoFolder, int stream, string destination, CancellationToken ct = default)
    {
        string init = InitPath(videoFolder, stream);
        if (!File.Exists(init)) throw new FileNotFoundException($"No init segment for stream {stream}.", init);

        IReadOnlyList<string> chunks = Chunks(videoFolder, stream);
        if (chunks.Count == 0) throw new FileNotFoundException($"No media segments for stream {stream}.", videoFolder);

        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);

        await using (var output = new FileStream(
            destination, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20, useAsync: true))
        {
            foreach (string part in new[] { init }.Concat(chunks))
            {
                ct.ThrowIfCancellationRequested();

                await using var input = new FileStream(
                    part, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, useAsync: true);
                await input.CopyToAsync(output, ct).ConfigureAwait(false);
            }
        }

        return destination;
    }
}
