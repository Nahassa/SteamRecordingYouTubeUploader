using SteamRecUtility.Core.Configuration;
using SteamRecUtility.Core.Files;
using SteamRecUtility.Core.Planning;
using SteamRecUtility.Core.Youtube;

namespace SteamRecUtility.Core.Execution;

public sealed record ClipOutcome
{
    public required string InputPath { get; init; }
    public string? OutputPath { get; init; }
    public bool Remuxed { get; init; }
    public bool AspectOverridden { get; init; }
    public bool Uploaded { get; init; }
    public string? VideoUrl { get; init; }
    public string? Error { get; init; }
    public TimeSpan Elapsed { get; init; }

    public bool Succeeded => Error is null;
}

public sealed record BatchProgress(int Completed, int Total, string CurrentFile);

/// <summary>
/// Runs the whole job for a set of clips: remux losslessly, file the original away, and
/// optionally upload. Shared by the GUI and the CLI so both behave identically.
/// </summary>
public sealed class BatchService
{
    private readonly RemuxService _remux;
    private readonly IPipelineLog _log;

    public BatchService(RemuxService remux, IPipelineLog? log = null)
    {
        _remux = remux;
        _log = log ?? NullPipelineLog.Instance;
    }

    public static IReadOnlyList<string> FindRecordings(string inputFolder)
    {
        if (!Directory.Exists(inputFolder)) return Array.Empty<string>();

        string[] extensions = { ".mp4", ".mkv", ".mov", ".m4v" };
        return Directory.EnumerateFiles(inputFolder, "*.*", SearchOption.TopDirectoryOnly)
            .Where(f => extensions.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase))
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public async Task<IReadOnlyList<ClipOutcome>> RunAsync(
        IReadOnlyList<string> inputFiles,
        AppSettings settings,
        YouTubeClient? youtube = null,
        IProgress<BatchProgress>? progress = null,
        CancellationToken ct = default)
    {
        var outcomes = new List<ClipOutcome>();
        var options = new RemuxOptions
        {
            TargetDisplayAspect = settings.ParsedTargetAspect,
            FastStart = settings.FastStart,
        };

        for (int i = 0; i < inputFiles.Count; i++)
        {
            ct.ThrowIfCancellationRequested();

            string input = inputFiles[i];
            string name = Path.GetFileName(input);
            progress?.Report(new BatchProgress(i, inputFiles.Count, name));
            _log.Info($"[{i + 1}/{inputFiles.Count}] {name}");

            outcomes.Add(await ProcessOneAsync(input, settings, options, youtube, ct).ConfigureAwait(false));
        }

        progress?.Report(new BatchProgress(inputFiles.Count, inputFiles.Count, string.Empty));
        Summarise(outcomes);
        return outcomes;
    }

    private async Task<ClipOutcome> ProcessOneAsync(
        string input,
        AppSettings settings,
        RemuxOptions options,
        YouTubeClient? youtube,
        CancellationToken ct)
    {
        try
        {
            RemuxResult remux = await _remux
                .RemuxAsync(input, settings.OutputFolder, options, ct)
                .ConfigureAwait(false);

            if (settings.MoveProcessedFiles)
            {
                // Only after the output is written and verified.
                FileOrganizer.MoveToProcessed(input, Path.GetDirectoryName(input)!);
                _log.Info($"  original moved to {FileOrganizer.ProcessedFolderName}/");
            }

            var outcome = new ClipOutcome
            {
                InputPath = input,
                OutputPath = remux.OutputPath,
                Remuxed = true,
                AspectOverridden = remux.AspectOverridden,
                Elapsed = remux.Elapsed,
            };

            if (settings.EnableYouTubeUpload && youtube is { IsAuthenticated: true })
                return await UploadAsync(outcome, settings, youtube, ct).ConfigureAwait(false);

            return outcome;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log.Error($"  {ex.Message}");
            return new ClipOutcome { InputPath = input, Error = ex.Message };
        }
    }

    private async Task<ClipOutcome> UploadAsync(
        ClipOutcome outcome, AppSettings settings, YouTubeClient youtube, CancellationToken ct)
    {
        string output = outcome.OutputPath!;
        _log.Info("  uploading to YouTube...");

        var request = new UploadRequest
        {
            FilePath = output,
            Title = TitleTemplate.Expand(
                settings.YouTubeTitleTemplate, output,
                settings.YouTubeRemoveDateFromFilename, settings.YouTubeRemoveTextPatterns),
            Description = TitleTemplate.Expand(
                settings.YouTubeDescriptionTemplate, output,
                settings.YouTubeRemoveDateFromFilename, settings.YouTubeRemoveTextPatterns),
            Tags = settings.ParsedTags,
            PrivacyStatus = settings.YouTubePrivacyStatus,
            CategoryId = settings.YouTubeCategoryId,
            MadeForKids = settings.YouTubeMadeForKids,
            AgeRestricted = settings.YouTubeAgeRestricted,
        };

        var reported = new HashSet<int>();
        var progress = new Progress<int>(p =>
        {
            int step = p / 25 * 25;
            if (p >= 0 && reported.Add(step) && step > 0) _log.Info($"  upload {step}%");
        });

        UploadResult result = await youtube.UploadAsync(request, progress, ct).ConfigureAwait(false);

        if (!result.Success)
        {
            // The clip is still remuxed and kept locally; only the upload failed.
            _log.Error($"  upload failed: {result.Error}");
            return outcome with { Error = result.Error };
        }

        _log.Success($"  {result.VideoUrl}");

        // The output is kept, just filed under uploaded/.
        string moved = FileOrganizer.MoveToUploaded(output, Path.GetDirectoryName(output)!);
        _log.Info($"  moved to {FileOrganizer.UploadedFolderName}/");

        return outcome with { Uploaded = true, VideoUrl = result.VideoUrl, OutputPath = moved };
    }

    private void Summarise(IReadOnlyList<ClipOutcome> outcomes)
    {
        int ok = outcomes.Count(o => o.Succeeded);
        int failed = outcomes.Count - ok;
        int uploaded = outcomes.Count(o => o.Uploaded);

        _log.Info("");
        string summary = $"{ok}/{outcomes.Count} remuxed losslessly";
        if (uploaded > 0) summary += $", {uploaded} uploaded";
        if (failed > 0) summary += $", {failed} failed";

        if (failed > 0) _log.Warning(summary); else _log.Success(summary);
    }
}
