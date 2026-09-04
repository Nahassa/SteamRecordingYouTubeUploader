using System.Diagnostics;
using SteamRecUtility.Core.Planning;
using SteamRecUtility.Core.Probing;

namespace SteamRecUtility.Core.Execution;

public sealed record RemuxResult
{
    public required string InputPath { get; init; }
    public required string OutputPath { get; init; }
    public required bool AspectOverridden { get; init; }
    public required AspectRatio ResultingSampleAspect { get; init; }
    public required AspectRatio ResultingDisplayAspect { get; init; }
    public required bool IntegrityVerified { get; init; }
    public required TimeSpan Elapsed { get; init; }
}

/// <summary>
/// Remuxes one file: probe, plan, run, verify, commit. The video bitstream is copied, never
/// re-encoded, so the operation is lossless and its correctness is directly checkable.
/// </summary>
public sealed class RemuxService
{
    private readonly IMediaProbe _probe;
    private readonly IProcessRunner _runner;
    private readonly IVideoStreamHasher _hasher;
    private readonly IPipelineLog _log;
    private readonly string _ffmpegPath;

    public RemuxService(
        IMediaProbe probe,
        IProcessRunner runner,
        IVideoStreamHasher hasher,
        IPipelineLog? log = null,
        string ffmpegPath = "ffmpeg")
    {
        _probe = probe;
        _runner = runner;
        _hasher = hasher;
        _log = log ?? NullPipelineLog.Instance;
        _ffmpegPath = ffmpegPath;
    }

    /// <summary>
    /// Where a remux of <paramref name="inputPath"/> is written, and the partial file used
    /// while it is in progress. The partial keeps the real extension so ffmpeg still selects
    /// the right muxer, and sits beside the final file so committing it is a rename.
    /// </summary>
    public static (string output, string partial) ResolvePaths(string inputPath, string outputDirectory)
    {
        string name = Path.GetFileName(inputPath);
        string output = Path.Combine(outputDirectory, name);
        string partial = Path.Combine(
            outputDirectory,
            Path.GetFileNameWithoutExtension(name) + ".partial" + Path.GetExtension(name));
        return (output, partial);
    }

    public static bool IsSameFile(string a, string b)
    {
        try
        {
            return string.Equals(Path.GetFullPath(a), Path.GetFullPath(b),
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
        }
        catch
        {
            return false;
        }
    }

    public async Task<RemuxResult> RemuxAsync(
        string inputPath,
        string outputDirectory,
        RemuxOptions? options = null,
        CancellationToken ct = default)
    {
        var stopwatch = Stopwatch.StartNew();
        Directory.CreateDirectory(outputDirectory);

        (string outputPath, string partialPath) = ResolvePaths(inputPath, outputDirectory);

        // Overwriting the source with -y would destroy the only copy of the recording.
        if (IsSameFile(inputPath, outputPath))
        {
            throw new RemuxException(
                $"Output would overwrite the source file '{inputPath}'. "
                + "Choose an output folder different from the input folder.");
        }

        SourceMedia source = await _probe.ProbeAsync(inputPath, ct).ConfigureAwait(false);
        RemuxPlan plan = RemuxPlanner.Plan(source, partialPath, options);
        _log.Info($"  {plan.Describe()}");

        try
        {
            ProcessResult run = await _runner.RunAsync(_ffmpegPath, plan.Arguments, ct).ConfigureAwait(false);
            if (!run.Succeeded)
            {
                throw new RemuxException(
                    $"ffmpeg failed for '{Path.GetFileName(inputPath)}' (exit {run.ExitCode}): "
                    + LastMeaningfulLine(run.StandardError));
            }

            // Lossless means provable: the copied video payload must be byte-identical.
            string before = await _hasher.HashAsync(inputPath, ct).ConfigureAwait(false);
            string after = await _hasher.HashAsync(partialPath, ct).ConfigureAwait(false);
            if (!string.Equals(before, after, StringComparison.OrdinalIgnoreCase))
            {
                throw new IntegrityCheckException(
                    $"Video stream changed during remux of '{Path.GetFileName(inputPath)}' "
                    + $"({before} -> {after}). The output was discarded.");
            }

            File.Move(partialPath, outputPath, overwrite: true);
            stopwatch.Stop();

            _log.Success(
                $"  remuxed losslessly in {stopwatch.Elapsed.TotalSeconds:0.00}s "
                + $"(video stream verified identical)");

            return new RemuxResult
            {
                InputPath = inputPath,
                OutputPath = outputPath,
                AspectOverridden = plan.AspectOverridden,
                ResultingSampleAspect = plan.ResultingSampleAspect,
                ResultingDisplayAspect = plan.ResultingDisplayAspect,
                IntegrityVerified = true,
                Elapsed = stopwatch.Elapsed,
            };
        }
        catch
        {
            // Never leave a partial file that could be mistaken for a finished conversion.
            TryDelete(partialPath);
            throw;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch
        {
            // Best effort; the partial extension keeps it from being mistaken for output.
        }
    }

    private static string LastMeaningfulLine(string stderr)
    {
        string[] lines = stderr.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return lines.Length > 0 ? lines[^1] : "no error output";
    }
}

public sealed class RemuxException : Exception
{
    public RemuxException(string message) : base(message) { }
}
