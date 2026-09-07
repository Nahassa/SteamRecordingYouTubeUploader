using SteamClipRemuxer.Core.Configuration;
using SteamClipRemuxer.Core.Files;
using SteamClipRemuxer.Core.Highlights;
using SteamClipRemuxer.Core.Planning;
using SteamClipRemuxer.Core.Steam;
using SteamClipRemuxer.Core.Timelines;
using SteamClipRemuxer.Core.Youtube;

namespace SteamClipRemuxer.Core.Execution;

/// <summary>A clip folder as the list shows it: what it contains, and whether it is already done.</summary>
public sealed record ClipListing
{
    public required ClipFolder Clip { get; init; }
    public required ClipState State { get; init; }

    /// <summary>What the clip shows, when the timeline could be read.</summary>
    public Highlight? Highlight { get; init; }

    /// <summary>The name the output file would be given.</summary>
    public required string SuggestedName { get; init; }

    /// <summary>
    /// The game's name. Resolved from the app id where the settings are in scope, so a name the
    /// user has set for an id reaches the file name and the YouTube title alike.
    /// </summary>
    public required string GameName { get; init; }

    public DateTimeOffset RecordedAt => Clip.Manifest.SessionStart + Clip.Manifest.StartInSession;

    /// <summary>What the list shows for this row.</summary>
    public string Describe()
    {
        string what = Highlight?.Describe() ?? "Clip";
        string where = Highlight?.Map is { Length: > 0 } map ? $" - {map}" : "";
        string length = $" ({Clip.Manifest.Duration.TotalSeconds:0.#}s)";
        string done = State switch
        {
            ClipState.Uploaded => "  [uploaded]",
            ClipState.Remuxed => "  [remuxed]",
            _ => "",
        };

        return $"{RecordedAt.ToLocalTime():yyyy-MM-dd HH:mm}  {what}{where}{length}{done}";
    }
}

/// <summary>
/// Runs the job for Steam's own clip folders, replacing the export step: the clip goes straight
/// from what Steam recorded to a finished file, without being exported first.
///
/// Kept apart from BatchService because almost nothing is shared. There is no input file to move
/// aside afterwards, the output has to be named rather than inherited, and the metadata that
/// makes a real title possible only exists on this path.
/// </summary>
public sealed class ClipBatchService
{
    private readonly ClipRemuxService _remux;
    private readonly ClipStitchService _stitch;
    private readonly IPipelineLog _log;

    public ClipBatchService(ClipRemuxService remux, ClipStitchService stitch, IPipelineLog? log = null)
    {
        _remux = remux;
        _stitch = stitch;
        _log = log ?? NullPipelineLog.Instance;
    }

    /// <summary>
    /// Every clip worth showing, newest first. Clips left at the full recording buffer are
    /// excluded here, because an untouched clip spans whole rounds and has no single moment to
    /// upload. Clips already processed are still listed, so the reason one is not going to run
    /// is visible rather than being an unexplained absence.
    /// </summary>
    public static IReadOnlyList<ClipListing> FindClips(
        AppSettings settings, ProcessedClipLog? processed = null, IPipelineLog? log = null)
    {
        log ??= NullPipelineLog.Instance;
        processed ??= new ProcessedClipLog();

        IReadOnlyList<ClipFolder> folders = ClipFolder.Discover(settings.InputFolder, log);
        if (folders.Count == 0) return Array.Empty<ClipListing>();

        var buffer = TimeSpan.FromSeconds(Math.Max(1, settings.MaxClipSeconds));
        var listings = new List<ClipListing>();
        int skipped = 0;

        foreach (ClipFolder clip in folders)
        {
            if (!clip.Manifest.IsCropped(buffer))
            {
                skipped++;
                continue;
            }

            Highlight? highlight = ReadHighlight(clip, log);
            DateTimeOffset recordedAt = clip.Manifest.SessionStart + clip.Manifest.StartInSession;

            string game = SteamApps.NameFor(clip.Manifest.AppId, settings.GameNames);

            if (SteamApps.IsUnknown(clip.Manifest.AppId, settings.GameNames))
            {
                log.Warning(
                    $"  App id {clip.Manifest.AppId} has no name, so clips from it are called "
                    + $"'{game}'. Set one in Settings under Game names.");
            }

            listings.Add(new ClipListing
            {
                Clip = clip,
                State = processed.StateOf(clip.Manifest.Id),
                Highlight = highlight,
                GameName = game,
                SuggestedName = ClipNaming.Expand(
                    settings.ClipFileNameTemplate, game, recordedAt, highlight),
            });
        }

        if (skipped > 0)
        {
            log.Info(
                $"{skipped} clip(s) were not shorter than {settings.MaxClipSeconds}s, so they were "
                + "left out; crop a clip in Steam to include it.");
        }

        return listings;
    }

    /// <summary>Reads what the clip shows. A missing or unreadable timeline is not fatal.</summary>
    private static Highlight? ReadHighlight(ClipFolder clip, IPipelineLog log)
    {
        if (clip.TimelinePath is null) return null;

        try
        {
            SessionTimeline timeline = SessionTimeline.Load(clip.TimelinePath);
            return HighlightSelector.Select(
                timeline, clip.Manifest.StartInSession, clip.Manifest.EndInSession);
        }
        catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException)
        {
            log.Warning($"  {clip.FolderName}: timeline unreadable ({ex.Message}); no title from it.");
            return null;
        }
    }

    public async Task<IReadOnlyList<ClipOutcome>> RunAsync(
        IReadOnlyList<ClipListing> clips,
        AppSettings settings,
        ProcessedClipLog processed,
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

        bool uploading = settings.EnableYouTubeUpload && youtube is { IsAuthenticated: true };

        for (int i = 0; i < clips.Count; i++)
        {
            ct.ThrowIfCancellationRequested();

            ClipListing listing = clips[i];
            string id = listing.Clip.Manifest.Id;

            progress?.Report(new BatchProgress(i, clips.Count, listing.SuggestedName));

            if (settings.SkipAlreadyProcessed && processed.ShouldSkip(id, uploading))
            {
                _log.Info($"[{i + 1}/{clips.Count}] {listing.SuggestedName}: already done, skipped");
                continue;
            }

            _log.Info($"[{i + 1}/{clips.Count}] {listing.SuggestedName}");
            outcomes.Add(await ProcessOneAsync(
                listing, settings, options, processed, uploading ? youtube : null, ct).ConfigureAwait(false));

            // Saved as we go: a crash halfway through a batch must not lose the record of what
            // has already gone to YouTube.
            processed.Save(onError: m => _log.Warning(m));
        }

        progress?.Report(new BatchProgress(clips.Count, clips.Count, string.Empty));
        return outcomes;
    }

    private async Task<ClipOutcome> ProcessOneAsync(
        ClipListing listing,
        AppSettings settings,
        RemuxOptions options,
        ProcessedClipLog processed,
        YouTubeClient? youtube,
        CancellationToken ct)
    {
        ClipManifest manifest = listing.Clip.Manifest;

        try
        {
            ClipRemuxResult remux = await _remux
                .RemuxAsync(listing.Clip, settings.OutputFolder, listing.SuggestedName,
                    options, settings.RespectSteamCrop, ct)
                .ConfigureAwait(false);

            _log.Success($"  {Path.GetFileName(remux.OutputPath)}"
                + (remux.AspectOverridden ? " (stretched, video copied)" : " (video copied)"));

            string title = TitleTemplate.Expand(
                settings.YouTubeClipTitleTemplate, remux.OutputPath,
                settings.YouTubeRemoveDateFromFilename, settings.YouTubeRemoveTextPatterns,
                highlight: listing.Highlight, recordedAt: listing.RecordedAt,
                game: listing.GameName);

            processed.MarkRemuxed(
                manifest.Id, listing.Clip.FolderName, title, remux.OutputPath, listing.RecordedAt);

            var outcome = new ClipOutcome
            {
                InputPath = listing.Clip.Path,
                OutputPath = remux.OutputPath,
                Remuxed = true,
                AspectOverridden = remux.AspectOverridden,
                Elapsed = remux.Elapsed,
            };

            if (youtube is null) return outcome;

            return await UploadAsync(outcome, listing, title, settings, processed, youtube, ct)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log.Error($"  {ex.Message}");
            return new ClipOutcome { InputPath = listing.Clip.Path, Error = ex.Message };
        }
    }

    private async Task<ClipOutcome> UploadAsync(
        ClipOutcome outcome,
        ClipListing listing,
        string title,
        AppSettings settings,
        ProcessedClipLog processed,
        YouTubeClient youtube,
        CancellationToken ct)
    {
        string output = outcome.OutputPath!;
        _log.Info("  uploading to YouTube...");

        var request = new UploadRequest
        {
            FilePath = output,
            Title = title,
            Description = TitleTemplate.Expand(
                settings.YouTubeDescriptionTemplate, output,
                settings.YouTubeRemoveDateFromFilename, settings.YouTubeRemoveTextPatterns,
                highlight: listing.Highlight, recordedAt: listing.RecordedAt,
                game: listing.GameName),
            Tags = settings.ParsedTags,
            PrivacyStatus = settings.YouTubePrivacyStatus,
            CategoryId = settings.YouTubeCategoryId,
            MadeForKids = settings.YouTubeMadeForKids,
            AgeRestricted = settings.YouTubeAgeRestricted,
            // Keeps the moment without spending title characters on it.
            RecordedAt = listing.RecordedAt,
        };

        UploadResult result = await PostAsync(request, youtube, ct).ConfigureAwait(false);

        if (!result.Success)
        {
            // The clip is remuxed and kept; only the upload failed, and the log records it as
            // remuxed so a later run can upload it without doing the work again.
            _log.Error($"  upload failed: {result.Error}");
            return outcome with { Error = result.Error };
        }

        _log.Success($"  {result.VideoUrl}");
        processed.MarkUploaded(listing.Clip.Manifest.Id, result.VideoId);

        string moved = FileOrganizer.MoveToUploaded(output, Path.GetDirectoryName(output)!);
        return outcome with { Uploaded = true, VideoUrl = result.VideoUrl, OutputPath = moved };
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
    /// Joins the selected clips into a single compilation instead of producing one file each.
    ///
    /// Every clip that goes in is recorded as processed, and as uploaded once the compilation
    /// reaches YouTube, so it greys out and is not swept into a second compilation later. The
    /// cost of that choice is that uploading one of them on its own again means forgetting its
    /// entry in the processed log first.
    /// </summary>
    public async Task<IReadOnlyList<ClipOutcome>> RunStitchAsync(
        IReadOnlyList<ClipListing> clips,
        AppSettings settings,
        ProcessedClipLog processed,
        YouTubeClient? youtube = null,
        IProgress<BatchProgress>? progress = null,
        CancellationToken ct = default)
    {
        var options = new RemuxOptions
        {
            TargetDisplayAspect = settings.ParsedTargetAspect,
            FastStart = settings.FastStart,
        };

        bool uploading = settings.EnableYouTubeUpload && youtube is { IsAuthenticated: true };

        // Oldest first: the list shows newest at the top, which is the wrong way round to watch.
        List<ClipListing> ordered = clips
            .Where(c => !settings.SkipAlreadyProcessed
                || !processed.ShouldSkip(c.Clip.Manifest.Id, uploading))
            .OrderBy(c => c.RecordedAt)
            .ToList();

        int alreadyDone = clips.Count - ordered.Count;
        if (alreadyDone > 0)
            _log.Info($"{alreadyDone} clip(s) already done were left out of the compilation.");

        if (ordered.Count < 2)
        {
            const string message =
                "A compilation needs at least two clips that have not already been processed.";
            _log.Error(message);
            return new[] { new ClipOutcome
            {
                InputPath = clips.FirstOrDefault()?.Clip.Path ?? string.Empty,
                Error = message,
            } };
        }

        string workspace = Path.Combine(
            Path.GetTempPath(), "sclip-parts-" + Guid.NewGuid().ToString("N"));

        try
        {
            (IReadOnlyList<StitchInput> inputs, Dictionary<string, ClipListing> byPath) =
                await BuildPartsAsync(ordered, settings, options, workspace, progress, ct)
                    .ConfigureAwait(false);

            if (inputs.Count < 2)
            {
                const string message = "Fewer than two clips could be prepared, so nothing was joined.";
                _log.Error(message);
                return new[] { new ClipOutcome { InputPath = ordered[0].Clip.Path, Error = message } };
            }

            ClipListing first = byPath[inputs[0].Path];
            ClipStitchResult stitched = await _stitch
                .StitchAsync(
                    inputs, settings.OutputFolder,
                    kept => ClipNaming.Expand(
                        ClipNaming.DefaultCompilationTemplate,
                        first.GameName, first.RecordedAt, highlight: null, count: kept),
                    options, ct)
                .ConfigureAwait(false);

            List<ClipListing> included = stitched.Parts.Select(part => byPath[part.Path]).ToList();

            _log.Success($"  {Path.GetFileName(stitched.OutputPath)}"
                + $" ({included.Count} clips joined, video copied)");

            return new[] { await PublishAsync(
                stitched, included, settings, processed, uploading ? youtube : null, ct)
                .ConfigureAwait(false) };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log.Error($"  {ex.Message}");
            return new[] { new ClipOutcome { InputPath = ordered[0].Clip.Path, Error = ex.Message } };
        }
        finally
        {
            progress?.Report(new BatchProgress(ordered.Count, ordered.Count, string.Empty));
            TryDeleteDirectory(workspace);
        }
    }

    /// <summary>
    /// Remuxes each clip into a part of the compilation. One clip failing costs that clip, not
    /// the whole compilation, so the rest are still joined.
    /// </summary>
    private async Task<(IReadOnlyList<StitchInput> Inputs, Dictionary<string, ClipListing> ByPath)>
        BuildPartsAsync(
            IReadOnlyList<ClipListing> ordered,
            AppSettings settings,
            RemuxOptions options,
            string workspace,
            IProgress<BatchProgress>? progress,
            CancellationToken ct)
    {
        var inputs = new List<StitchInput>();
        var byPath = new Dictionary<string, ClipListing>(StringComparer.Ordinal);

        for (int i = 0; i < ordered.Count; i++)
        {
            ct.ThrowIfCancellationRequested();

            ClipListing listing = ordered[i];
            progress?.Report(new BatchProgress(i, ordered.Count, listing.SuggestedName));
            _log.Info($"[{i + 1}/{ordered.Count}] {listing.SuggestedName}");

            try
            {
                ClipRemuxResult part = await _remux
                    .RemuxAsync(listing.Clip, workspace, $"part{i:00}", options,
                        settings.RespectSteamCrop, ct)
                    .ConfigureAwait(false);

                inputs.Add(new StitchInput(part.OutputPath, listing.Highlight?.Describe() ?? "Clip"));
                byPath[part.OutputPath] = listing;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _log.Error($"  {ex.Message}; left out of the compilation.");
            }
        }

        return (inputs, byPath);
    }

    /// <summary>
    /// Records the compilation against every clip in it, and uploads it once. The description
    /// carries a timestamp per clip, which YouTube turns into chapters when they are long
    /// enough.
    /// </summary>
    private async Task<ClipOutcome> PublishAsync(
        ClipStitchResult stitched,
        IReadOnlyList<ClipListing> included,
        AppSettings settings,
        ProcessedClipLog processed,
        YouTubeClient? youtube,
        CancellationToken ct)
    {
        ClipListing first = included[0];

        // The per-clip title template names one highlight, which would be a lie about a
        // compilation, so this path has its own.
        string title = TitleTemplate.Expand(
            TitleTemplate.DefaultCompilationTitle, stitched.OutputPath,
            settings.YouTubeRemoveDateFromFilename, settings.YouTubeRemoveTextPatterns,
            recordedAt: first.RecordedAt, game: first.GameName, count: included.Count);

        foreach (ClipListing listing in included)
        {
            processed.MarkRemuxed(
                listing.Clip.Manifest.Id, listing.Clip.FolderName, title,
                stitched.OutputPath, listing.RecordedAt);
        }

        processed.Save(onError: m => _log.Warning(m));

        var outcome = new ClipOutcome
        {
            InputPath = first.Clip.Path,
            OutputPath = stitched.OutputPath,
            Remuxed = true,
            AspectOverridden = stitched.AspectOverridden,
            Elapsed = stitched.Elapsed,
        };

        if (youtube is null) return outcome;

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
            Title = title,
            Description = WithChapters(
                TitleTemplate.Expand(
                    settings.YouTubeDescriptionTemplate, stitched.OutputPath,
                    settings.YouTubeRemoveDateFromFilename, settings.YouTubeRemoveTextPatterns,
                    recordedAt: first.RecordedAt, game: first.GameName, count: included.Count),
                stitched.Parts),
            Tags = settings.ParsedTags,
            PrivacyStatus = settings.YouTubePrivacyStatus,
            CategoryId = settings.YouTubeCategoryId,
            MadeForKids = settings.YouTubeMadeForKids,
            AgeRestricted = settings.YouTubeAgeRestricted,
            // The earliest clip in it, which is when the compilation's footage begins.
            RecordedAt = first.RecordedAt,
        };

        UploadResult result = await PostAsync(request, youtube, ct).ConfigureAwait(false);

        if (!result.Success)
        {
            // The compilation exists and every clip in it is recorded as remuxed, so a later
            // run uploads it rather than building it again.
            _log.Error($"  upload failed: {result.Error}");
            return outcome with { Error = result.Error };
        }

        _log.Success($"  {result.VideoUrl}");

        foreach (ClipListing listing in included)
            processed.MarkUploaded(listing.Clip.Manifest.Id, result.VideoId);

        processed.Save(onError: m => _log.Warning(m));

        string moved = FileOrganizer.MoveToUploaded(
            stitched.OutputPath, Path.GetDirectoryName(stitched.OutputPath)!);

        return outcome with { Uploaded = true, VideoUrl = result.VideoUrl, OutputPath = moved };
    }

    /// <summary>Appends the timestamp list to a description, without leaving a blank line when there is none.</summary>
    public static string WithChapters(string description, IReadOnlyList<StitchPart> parts)
    {
        string chapters = StitchChapters.Describe(parts);
        return string.IsNullOrWhiteSpace(description)
            ? chapters
            : description + Environment.NewLine + Environment.NewLine + chapters;
    }

    private static void TryDeleteDirectory(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); }
        catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
