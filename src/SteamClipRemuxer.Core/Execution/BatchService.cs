using SteamClipRemuxer.Core.Configuration;
using SteamClipRemuxer.Core.Files;
using SteamClipRemuxer.Core.Highlights;
using SteamClipRemuxer.Core.Planning;
using SteamClipRemuxer.Core.Youtube;

namespace SteamClipRemuxer.Core.Execution;

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
    private readonly ClipStitchService _stitch;
    private readonly IPipelineLog _log;

    public BatchService(RemuxService remux, ClipStitchService stitch, IPipelineLog? log = null)
    {
        _remux = remux;
        _stitch = stitch;
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

        UploadResult result = await PostAsync(request, youtube, ct).ConfigureAwait(false);

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

    /// <summary>Sends one upload, reporting its progress in quarters rather than every percent.</summary>
    private async Task<UploadResult> PostAsync(
        UploadRequest request, YouTubeClient youtube, CancellationToken ct)
    {
        var reported = new HashSet<int>();
        var progress = new Progress<int>(p =>
        {
            int step = p / 25 * 25;
            if (p >= 0 && reported.Add(step) && step > 0) _log.Info($"  upload {step}%");
        });

        return await youtube.UploadAsync(request, progress, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Joins the selected recordings into one compilation instead of remuxing each on its own.
    ///
    /// These files are already finished, so unlike the Steam-clip path there is nothing to
    /// remux first; they go straight into the join. The trade is that a file carries no
    /// highlight metadata, so each timestamp is labelled with the clip's own name.
    /// </summary>
    public async Task<IReadOnlyList<ClipOutcome>> RunStitchAsync(
        IReadOnlyList<string> inputFiles,
        AppSettings settings,
        YouTubeClient? youtube = null,
        IProgress<BatchProgress>? progress = null,
        CancellationToken ct = default)
    {
        if (inputFiles.Count < 2)
        {
            const string message = "A compilation needs at least two clips.";
            _log.Error(message);
            return new[] { new ClipOutcome { InputPath = inputFiles.FirstOrDefault() ?? "", Error = message } };
        }

        var options = new RemuxOptions
        {
            TargetDisplayAspect = settings.ParsedTargetAspect,
            FastStart = settings.FastStart,
        };

        var inputs = inputFiles
            .Select(f => new StitchInput(f, LabelFor(f, settings)))
            .ToList();

        ClipName first = ClipName.Parse(Path.GetFileNameWithoutExtension(inputFiles[0]));
        DateTimeOffset recordedAt = first.RecordedAt is { } stamped
            ? new DateTimeOffset(stamped, TimeSpan.Zero)
            : new DateTimeOffset(File.GetLastWriteTime(inputFiles[0]));

        try
        {
            progress?.Report(new BatchProgress(0, inputFiles.Count, Path.GetFileName(inputFiles[0])));
            _log.Info($"Joining {inputFiles.Count} clip(s)...");

            ClipStitchResult stitched = await _stitch
                .StitchAsync(
                    inputs, settings.OutputFolder,
                    kept => ClipNaming.Expand(
                        ClipNaming.DefaultCompilationTemplate,
                        first.Game, recordedAt, highlight: null, count: kept),
                    options, ct)
                .ConfigureAwait(false);

            _log.Success($"  {Path.GetFileName(stitched.OutputPath)}"
                + $" ({stitched.Parts.Count} clips joined, video copied)");

            if (settings.MoveProcessedFiles)
            {
                // Only the files that actually went in, and only now that the output is written
                // and verified.
                foreach (StitchPart part in stitched.Parts)
                    FileOrganizer.MoveToProcessed(part.Path, Path.GetDirectoryName(part.Path)!);

                _log.Info($"  originals moved to {FileOrganizer.ProcessedFolderName}/");
            }

            var outcome = new ClipOutcome
            {
                InputPath = inputFiles[0],
                OutputPath = stitched.OutputPath,
                Remuxed = true,
                AspectOverridden = stitched.AspectOverridden,
                Elapsed = stitched.Elapsed,
            };

            if (!settings.EnableYouTubeUpload || youtube is not { IsAuthenticated: true })
                return new[] { outcome };

            return new[] { await UploadCompilationAsync(
                outcome, stitched, recordedAt, first.Game, settings, youtube, ct).ConfigureAwait(false) };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log.Error($"  {ex.Message}");
            return new[] { new ClipOutcome { InputPath = inputFiles[0], Error = ex.Message } };
        }
        finally
        {
            progress?.Report(new BatchProgress(inputFiles.Count, inputFiles.Count, string.Empty));
        }
    }

    /// <summary>The name a timestamp carries, with whatever the user strips from titles removed.</summary>
    private static string LabelFor(string path, AppSettings settings)
    {
        string label = TitleTemplate.Expand(
            "{clip}", path, settings.YouTubeRemoveDateFromFilename, settings.YouTubeRemoveTextPatterns);

        return label.Length > 0 ? label : Path.GetFileNameWithoutExtension(path);
    }

    private async Task<ClipOutcome> UploadCompilationAsync(
        ClipOutcome outcome,
        ClipStitchResult stitched,
        DateTimeOffset recordedAt,
        string game,
        AppSettings settings,
        YouTubeClient youtube,
        CancellationToken ct)
    {
        if (!StitchChapters.QualifyAsYouTubeChapters(stitched.Parts))
        {
            _log.Info(
                "  the clips are listed with timestamps, but they are too few or too short for "
                + "YouTube to show them as chapters.");
        }

        _log.Info("  uploading to YouTube...");

        var request = new UploadRequest
        {
            FilePath = stitched.OutputPath,
            // The per-clip title template names one clip, which would be a lie about a
            // compilation, so this path has its own.
            Title = TitleTemplate.Expand(
                TitleTemplate.DefaultCompilationTitle, stitched.OutputPath,
                settings.YouTubeRemoveDateFromFilename, settings.YouTubeRemoveTextPatterns,
                recordedAt: recordedAt, game: game.Length > 0 ? game : null, count: stitched.Parts.Count),
            Description = ClipBatchService.WithChapters(
                TitleTemplate.Expand(
                    settings.YouTubeDescriptionTemplate, stitched.OutputPath,
                    settings.YouTubeRemoveDateFromFilename, settings.YouTubeRemoveTextPatterns,
                    recordedAt: recordedAt, count: stitched.Parts.Count),
                stitched.Parts),
            Tags = settings.ParsedTags,
            PrivacyStatus = settings.YouTubePrivacyStatus,
            CategoryId = settings.YouTubeCategoryId,
            MadeForKids = settings.YouTubeMadeForKids,
            AgeRestricted = settings.YouTubeAgeRestricted,
            RecordedAt = recordedAt,
        };

        UploadResult result = await PostAsync(request, youtube, ct).ConfigureAwait(false);

        if (!result.Success)
        {
            // The compilation is written and kept locally; only the upload failed.
            _log.Error($"  upload failed: {result.Error}");
            return outcome with { Error = result.Error };
        }

        _log.Success($"  {result.VideoUrl}");

        string moved = FileOrganizer.MoveToUploaded(
            stitched.OutputPath, Path.GetDirectoryName(stitched.OutputPath)!);

        return outcome with { Uploaded = true, VideoUrl = result.VideoUrl, OutputPath = moved };
    }
}
