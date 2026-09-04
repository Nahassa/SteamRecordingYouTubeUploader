using SteamRecUtility.Core.Execution;

namespace SteamRecUtility.Core.Probing;

public interface IMediaProbe
{
    Task<SourceMedia> ProbeAsync(string filePath, CancellationToken ct = default);
}

/// <summary>
/// Reads a file's real properties with ffprobe. Every pipeline decision depends on this;
/// nothing downstream is permitted to assume geometry, codec, colour or stream layout.
/// </summary>
public sealed class MediaProbe : IMediaProbe
{
    private readonly IProcessRunner _runner;
    private readonly string _ffprobePath;

    public MediaProbe(IProcessRunner runner, string ffprobePath = "ffprobe")
    {
        _runner = runner;
        _ffprobePath = ffprobePath;
    }

    public static IReadOnlyList<string> BuildArguments(string filePath) => new[]
    {
        "-v", "error",
        "-print_format", "json",
        "-show_format",
        "-show_streams",
        filePath,
    };

    public async Task<SourceMedia> ProbeAsync(string filePath, CancellationToken ct = default)
    {
        ProcessResult result = await _runner
            .RunAsync(_ffprobePath, BuildArguments(filePath), ct)
            .ConfigureAwait(false);

        if (!result.Succeeded)
        {
            string detail = result.StandardError.Trim();
            throw new InvalidMediaException(
                $"ffprobe failed for '{filePath}' (exit {result.ExitCode})"
                + (detail.Length > 0 ? $": {detail}" : "."));
        }

        return SourceMedia.Parse(result.StandardOutput, filePath);
    }
}
