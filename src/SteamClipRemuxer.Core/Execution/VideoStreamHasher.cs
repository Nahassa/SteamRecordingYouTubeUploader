namespace SteamClipRemuxer.Core.Execution;

public interface IVideoStreamHasher
{
    Task<string> HashAsync(string filePath, CancellationToken ct = default);
}

/// <summary>
/// Hashes only the encoded video payload, ignoring container metadata. Because remuxing is
/// lossless, input and output hashes must match exactly - which turns "did this work?" from a
/// judgement call into an assertion. An encoding pipeline could never offer this.
/// </summary>
public sealed class VideoStreamHasher : IVideoStreamHasher
{
    private readonly IProcessRunner _runner;
    private readonly string _ffmpegPath;

    public VideoStreamHasher(IProcessRunner runner, string ffmpegPath = "ffmpeg")
    {
        _runner = runner;
        _ffmpegPath = ffmpegPath;
    }

    public static IReadOnlyList<string> BuildArguments(string filePath) => new[]
    {
        "-v", "error", "-i", filePath,
        "-map", "0:v", "-c", "copy",
        "-f", "streamhash", "-hash", "md5", "-",
    };

    /// <summary>
    /// Extracts the hash from ffmpeg's streamhash output. Comment lines start with '#'; the
    /// single data line is "stream#, size, hash".
    /// </summary>
    public static string ParseHash(string streamHashOutput)
    {
        foreach (string raw in streamHashOutput.Split('\n'))
        {
            string line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;

            string[] parts = line.Split(',');
            if (parts.Length >= 3) return parts[^1].Trim();
        }

        throw new IntegrityCheckException("ffmpeg produced no stream hash.");
    }

    public async Task<string> HashAsync(string filePath, CancellationToken ct = default)
    {
        ProcessResult r = await _runner.RunAsync(_ffmpegPath, BuildArguments(filePath), ct).ConfigureAwait(false);
        if (!r.Succeeded)
            throw new IntegrityCheckException($"Could not hash '{filePath}': {r.StandardError.Trim()}");

        return ParseHash(r.StandardOutput);
    }
}

public sealed class IntegrityCheckException : Exception
{
    public IntegrityCheckException(string message) : base(message) { }
}
