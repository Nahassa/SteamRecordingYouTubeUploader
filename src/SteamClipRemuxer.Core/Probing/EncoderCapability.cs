using SteamClipRemuxer.Core.Execution;

namespace SteamClipRemuxer.Core.Probing;

/// <summary>
/// Whether FFmpeg on this machine can actually run a given encoder or filter, established by
/// running it.
///
/// `ffmpeg -encoders | grep nvenc` proves only that FFmpeg was *compiled* with NVENC. It returns
/// true on a machine with no NVIDIA card in it, which is why this class exists at all: the answer
/// comes from a trial encode into /dev/null, or it is not an answer.
///
/// The option set matters as much as the encoder. `-b_ref_mode` and `-tune hq` are absent from
/// older builds, so probing a bare `-c:v hevc_nvenc` would pass on a build that then rejects the
/// real command. Callers pass the exact options they intend to emit.
/// </summary>
public sealed class EncoderCapability
{
    private readonly IProcessRunner _runner;
    private readonly IPipelineLog _log;
    private readonly string _ffmpegPath;

    /// <summary>
    /// Answers already established, keyed by FFmpeg's version banner and the probe itself, so
    /// swapping the binary re-probes rather than trusting a stale yes.
    /// </summary>
    private readonly Dictionary<string, bool> _answers = new(StringComparer.Ordinal);

    private string? _version;

    public EncoderCapability(IProcessRunner runner, IPipelineLog? log = null, string ffmpegPath = "ffmpeg")
    {
        _runner = runner;
        _log = log ?? NullPipelineLog.Instance;
        _ffmpegPath = ffmpegPath;
    }

    /// <summary>A tenth of a second of nothing, encoded and thrown away. Enough to reject a bad flag.</summary>
    private const string Nothing = "nullsrc=s=256x256:d=0.1";

    public static IReadOnlyList<string> BuildEncoderTrial(string encoder, IReadOnlyList<string> options)
    {
        var args = new List<string>
        {
            "-hide_banner", "-loglevel", "error",
            "-f", "lavfi", "-i", Nothing,
            "-c:v", encoder,
        };

        args.AddRange(options);
        args.AddRange(new[] { "-f", "null", "-" });
        return args;
    }

    /// <summary>
    /// A trial of one filter, fed the pixel format the real source has.
    ///
    /// The format is part of the question rather than a detail. zscale refuses the deprecated
    /// full-range formats outright on some builds, and yuvj420p is exactly what Steam records, so
    /// a probe run against the default yuv420p would answer a question nobody asked.
    /// </summary>
    public static IReadOnlyList<string> BuildFilterTrial(string filter, string pixelFormat) => new[]
    {
        "-hide_banner", "-loglevel", "error",
        "-f", "lavfi", "-i", $"{Nothing},format={pixelFormat}",
        "-vf", filter,
        "-f", "null", "-",
    };

    public Task<bool> CanEncodeAsync(
        string encoder, IReadOnlyList<string> options, CancellationToken ct = default) =>
        AskAsync($"encoder {encoder} {string.Join(' ', options)}",
            BuildEncoderTrial(encoder, options), ct);

    public Task<bool> CanFilterAsync(
        string filter, string pixelFormat, CancellationToken ct = default) =>
        AskAsync($"filter {filter} from {pixelFormat}",
            BuildFilterTrial(filter, pixelFormat), ct);

    private async Task<bool> AskAsync(string key, IReadOnlyList<string> args, CancellationToken ct)
    {
        string cacheKey = await VersionAsync(ct).ConfigureAwait(false) + "\n" + key;
        if (_answers.TryGetValue(cacheKey, out bool known)) return known;

        // Cancellation is deliberately not caught. It exits non-zero like a failure does, and
        // reading that as "the hardware cannot do this" would quietly downgrade every later
        // encode in the session - a defect this codebase has shipped before.
        ProcessResult result;
        try
        {
            result = await _runner.RunAsync(_ffmpegPath, args, ct).ConfigureAwait(false);
        }
        catch (ProcessLaunchException ex)
        {
            _log.Warning($"  could not run FFmpeg to test {key}: {ex.Message}");
            return _answers[cacheKey] = false;
        }

        bool can = result.Succeeded;
        _answers[cacheKey] = can;
        return can;
    }

    private async Task<string> VersionAsync(CancellationToken ct)
    {
        if (_version is not null) return _version;

        try
        {
            ProcessResult result = await _runner
                .RunAsync(_ffmpegPath, new[] { "-hide_banner", "-version" }, ct)
                .ConfigureAwait(false);

            // First line only: the rest is the configure line, which is long and changes nothing
            // about which flags work.
            _version = result.StandardOutput.Split('\n', 2)[0].Trim();
        }
        catch (ProcessLaunchException)
        {
            _version = "unknown";
        }

        return _version = _version.Length == 0 ? "unknown" : _version;
    }
}
