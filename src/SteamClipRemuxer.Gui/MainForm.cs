using SteamClipRemuxer.Core.Configuration;
using SteamClipRemuxer.Core.Execution;
using SteamClipRemuxer.Core.Highlights;
using SteamClipRemuxer.Core.Probing;
using SteamClipRemuxer.Core.Steam;
using SteamClipRemuxer.Core.Thumbnails;
using SteamClipRemuxer.Core.Timelines;
using SteamClipRemuxer.Core.Youtube;

namespace SteamClipRemuxer.Gui;

/// <summary>One clip in the list. Probed lazily, the first time it is selected.</summary>
internal sealed class ClipEntry
{
    public required string Path { get; init; }
    public SourceMedia? Media { get; set; }
    public string? ProbeError { get; set; }

    /// <summary>Set when the row came from one of Steam's clip folders rather than an exported file.</summary>
    public ClipListing? Listing { get; init; }

    /// <summary>Already remuxed or uploaded, so the row is drawn greyed out.</summary>
    public bool IsProcessed => Listing is not null && Listing.State != ClipState.New;

    public override string ToString() =>
        Listing?.Describe() ?? System.IO.Path.GetFileName(Path);
}

public sealed class MainForm : Form
{
    private readonly AppSettings _settings;
    private readonly ProcessRunner _runner = new();
    private readonly MediaProbe _probe;
    private readonly ThumbnailExtractor _thumbnails;
    private readonly BatchService _batch;
    private readonly ClipBatchService _clipBatch;
    private readonly ProcessedClipLog _processed;
    private readonly YouTubeClient _youtube = new();

    private readonly LogForm _log = new();
    private readonly IProgress<(LogLevel Level, string Message)> _logSink;

    private CancellationTokenSource? _cancellation;

    private readonly TextBox _inputFolder = new() { Dock = DockStyle.Fill };
    private readonly TextBox _outputFolder = new() { Dock = DockStyle.Fill };
    private readonly CheckedListBox _clips = new()
    {
        Dock = DockStyle.Fill,
        IntegralHeight = false,
        CheckOnClick = true,
        // Owner drawn so a clip already handled can be greyed out rather than hidden: a row that
        // silently fails to appear is hard to tell from one the tool never found.
        DrawMode = DrawMode.OwnerDrawFixed,
        ItemHeight = 20,
    };

    private readonly ComboBox _source = new()
    {
        DropDownStyle = ComboBoxStyle.DropDownList,
        Width = 190,
        Height = 30,
    };
    private readonly PictureBox _preview = new() { Dock = DockStyle.Fill, SizeMode = PictureBoxSizeMode.Zoom, BackColor = Color.Black };
    private readonly Label _clipInfo = new() { Dock = DockStyle.Bottom, Height = 68, Padding = new Padding(6) };

    private readonly Button _remux = new() { Text = "Remux Selected", Width = 140, Height = 30 };
    private readonly Button _cancel = new() { Text = "Cancel", Width = 90, Height = 30, Enabled = false };
    private readonly Button _selectAll = new() { Text = "Select All", Width = 100, Height = 30 };
    private readonly ProgressBar _progress = new() { Dock = DockStyle.Left, Width = 260 };
    private readonly Label _status = new() { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, Padding = new Padding(8, 0, 0, 0) };

    public MainForm()
    {
        AppPaths.EnsureCreated();
        _settings = AppSettings.Load(onError: m => BeginInvoke(() => Log(LogLevel.Warning, m)));

        _probe = new MediaProbe(_runner);
        _thumbnails = new ThumbnailExtractor(_runner);

        // Progress<T> captures this thread's context, so background work reaches the UI
        // without a single manual Invoke.
        _logSink = new Progress<(LogLevel Level, string Message)>(e => Log(e.Level, e.Message));

        var remuxService = new RemuxService(
            _probe, _runner, new VideoStreamHasher(_runner),
            new DelegatePipelineLog((level, message) => _logSink.Report((level, message))));
        _batch = new BatchService(remuxService,
            new DelegatePipelineLog((level, message) => _logSink.Report((level, message))));

        var clipRemuxService = new ClipRemuxService(
            _runner, _probe, new VideoStreamHasher(_runner),
            new DelegatePipelineLog((level, message) => _logSink.Report((level, message))));
        _clipBatch = new ClipBatchService(clipRemuxService,
            new DelegatePipelineLog((level, message) => _logSink.Report((level, message))));

        _processed = ProcessedClipLog.Load(onError: m => BeginInvoke(() => Log(LogLevel.Warning, m)));

        BuildLayout();
        Restore();
        _ = TryRestoreYouTubeAsync();
    }

    private void BuildLayout()
    {
        Text = "Steam Clip Remuxer";
        Size = new Size(1040, 660);
        MinimumSize = new Size(820, 520);
        StartPosition = FormStartPosition.CenterScreen;

        // --- folders -------------------------------------------------------
        var folders = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            Height = 72,
            ColumnCount = 3,
            RowCount = 2,
            Padding = new Padding(8),
        };
        folders.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 60));
        folders.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        folders.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 90));

        var browseIn = new Button { Text = "Browse", Dock = DockStyle.Fill };
        browseIn.Click += (_, _) => PickFolder(_inputFolder, "Folder containing Steam recordings", reload: true);
        var browseOut = new Button { Text = "Browse", Dock = DockStyle.Fill };
        browseOut.Click += (_, _) => PickFolder(_outputFolder, "Where remuxed clips are written", reload: false);

        folders.Controls.Add(new Label { Text = "Input:", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft }, 0, 0);
        folders.Controls.Add(_inputFolder, 1, 0);
        folders.Controls.Add(browseIn, 2, 0);
        folders.Controls.Add(new Label { Text = "Output:", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft }, 0, 1);
        folders.Controls.Add(_outputFolder, 1, 1);
        folders.Controls.Add(browseOut, 2, 1);

        // --- clips | preview -----------------------------------------------
        var split = new SplitContainer { Dock = DockStyle.Fill };
        _clips.SelectedIndexChanged += async (_, _) => await ShowPreviewAsync().ConfigureAwait(true);
        split.Panel1.Controls.Add(_clips);

        var right = new Panel { Dock = DockStyle.Fill };
        _clipInfo.Text = "Select a clip to preview it.";
        right.Controls.Add(_preview);
        right.Controls.Add(_clipInfo);
        _preview.BringToFront();
        split.Panel2.Controls.Add(right);

        // --- actions --------------------------------------------------------
        var actions = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            Height = 48,
            Padding = new Padding(8, 8, 8, 8),
            FlowDirection = FlowDirection.LeftToRight,
        };

        _remux.Click += async (_, _) => await RunBatchAsync().ConfigureAwait(true);
        _cancel.Click += (_, _) => _cancellation?.Cancel();
        _selectAll.Click += (_, _) => ToggleAll();

        var reload = new Button { Text = "Reload", Width = 90, Height = 30 };
        reload.Click += (_, _) => LoadClips();

        // On the main window rather than buried in settings: it changes what the list shows, so
        // it belongs next to the list.
        _source.Items.AddRange(new object[]
        {
            new SourceChoice(ClipSource.ExportedFiles, "Exported video files"),
            new SourceChoice(ClipSource.SteamClips, "Steam clips (unexported)"),
        });
        _source.SelectedIndexChanged += (_, _) =>
        {
            if (_source.SelectedItem is not SourceChoice choice) return;
            if (choice.Value == _settings.ClipSource) return;

            _settings.ClipSource = choice.Value;
            _settings.Save(onError: m => Log(LogLevel.Warning, m));
            LoadClips();
        };

        var settings = new Button { Text = "Settings", Width = 100, Height = 30 };
        settings.Click += (_, _) => OpenSettings();

        var timelines = new Button { Text = "Fix Timelines", Width = 110, Height = 30 };
        timelines.Click += (_, _) => FixTimelines();

        var showLog = new Button { Text = "Show Log", Width = 100, Height = 30 };
        showLog.Click += (_, _) => ShowLog();

        actions.Controls.AddRange(new Control[]
        {
            _remux, _cancel, _selectAll, reload,
            new Label { Text = "Source:", Width = 52, Height = 30, TextAlign = ContentAlignment.MiddleRight },
            _source,
            settings, timelines, showLog,
        });

        _clips.DrawItem += DrawClipRow;

        // --- status ----------------------------------------------------------
        var statusBar = new Panel { Dock = DockStyle.Bottom, Height = 30, Padding = new Padding(8, 4, 8, 4) };
        statusBar.Controls.Add(_status);
        statusBar.Controls.Add(_progress);
        _status.BringToFront();

        Controls.Add(split);
        Controls.Add(actions);
        Controls.Add(statusBar);
        Controls.Add(folders);
        folders.BringToFront();
        split.BringToFront();   // Fill must be docked last, so it must sit at index 0

        // Safe only once the control has been sized.
        Shown += (_, _) =>
        {
            try { split.SplitterDistance = Math.Min(300, Math.Max(120, split.Width - 200)); }
            catch (InvalidOperationException) { /* leave the default split */ }
        };

        FormClosing += (_, _) => Persist();
    }

    // ------------------------------------------------------------------ state

    private void Restore()
    {
        _inputFolder.Text = _settings.InputFolder;
        _outputFolder.Text = _settings.OutputFolder;
        SelectSource(_settings.ClipSource);
        if (Directory.Exists(_inputFolder.Text)) LoadClips();
    }

    private void Persist()
    {
        _settings.InputFolder = _inputFolder.Text;
        _settings.OutputFolder = _outputFolder.Text;
        _settings.Save(onError: m => Log(LogLevel.Warning, m));
    }

    private void PickFolder(TextBox target, string description, bool reload)
    {
        using var dialog = new FolderBrowserDialog { Description = description, UseDescriptionForTitle = true };
        if (Directory.Exists(target.Text)) dialog.SelectedPath = target.Text;
        if (dialog.ShowDialog(this) != DialogResult.OK) return;

        target.Text = dialog.SelectedPath;
        if (reload) LoadClips();
    }

    private void LoadClips()
    {
        _clips.Items.Clear();
        ClearPreview();

        if (!Directory.Exists(_inputFolder.Text))
        {
            SetStatus("Input folder not found.");
            return;
        }

        _settings.InputFolder = _inputFolder.Text;

        if (_settings.ClipSource == ClipSource.SteamClips) LoadSteamClips();
        else LoadExportedFiles();

        if (_clips.Items.Count > 0) _clips.SelectedIndex = 0;
    }

    private void LoadExportedFiles()
    {
        IReadOnlyList<string> files = BatchService.FindRecordings(_inputFolder.Text);
        foreach (string file in files) _clips.Items.Add(new ClipEntry { Path = file }, isChecked: true);

        SetStatus($"{files.Count} clip(s) found.");
    }

    private void LoadSteamClips()
    {
        var log = new DelegatePipelineLog((level, message) => Log(level, message));

        if (ClipFolder.ResolveClipsRoot(_inputFolder.Text) is null)
        {
            // Being specific here matters: the folder looks right to a person, and the reason it
            // yields nothing is not visible from the outside.
            SetStatus("No Steam clips found in this folder.");
            Log(LogLevel.Warning,
                $"No clip_* folders under '{_inputFolder.Text}'. Point Input at your Steam "
                + "recording folder (the one holding a 'clips' subfolder), or at 'clips' itself.");
            return;
        }

        IReadOnlyList<ClipListing> clips = ClipBatchService.FindClips(_settings, _processed, log);

        foreach (ClipListing clip in clips)
        {
            // Already handled clips are listed but not selected, so pressing Remux does not
            // silently redo them while they stay visible.
            _clips.Items.Add(
                new ClipEntry { Path = clip.Clip.Path, Listing = clip },
                isChecked: clip.State == ClipState.New);
        }

        int done = clips.Count(c => c.State != ClipState.New);
        SetStatus(done == 0
            ? $"{clips.Count} clip(s) found."
            : $"{clips.Count} clip(s) found, {done} already done.");
    }

    /// <summary>
    /// Draws a row. Owner drawing means the checkbox has to be painted too, since the control
    /// only draws it for us in its normal mode.
    /// </summary>
    private void DrawClipRow(object? sender, DrawItemEventArgs e)
    {
        e.DrawBackground();
        if (e.Index < 0 || e.Index >= _clips.Items.Count) return;

        var entry = _clips.Items[e.Index] as ClipEntry;
        bool processed = entry?.IsProcessed ?? false;
        bool selected = (e.State & DrawItemState.Selected) == DrawItemState.Selected;

        var glyph = new Point(e.Bounds.Left + 2, e.Bounds.Top + (e.Bounds.Height - 14) / 2);
        var state = _clips.GetItemChecked(e.Index)
            ? (processed
                ? System.Windows.Forms.VisualStyles.CheckBoxState.CheckedDisabled
                : System.Windows.Forms.VisualStyles.CheckBoxState.CheckedNormal)
            : (processed
                ? System.Windows.Forms.VisualStyles.CheckBoxState.UncheckedDisabled
                : System.Windows.Forms.VisualStyles.CheckBoxState.UncheckedNormal);
        CheckBoxRenderer.DrawCheckBox(e.Graphics, glyph, state);

        var text = new Rectangle(
            e.Bounds.Left + 22, e.Bounds.Top, Math.Max(0, e.Bounds.Width - 24), e.Bounds.Height);

        Color colour = processed
            ? (selected ? SystemColors.HighlightText : SystemColors.GrayText)
            : e.ForeColor;

        TextRenderer.DrawText(
            e.Graphics, entry?.ToString() ?? string.Empty, e.Font ?? Font, text, colour,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);

        e.DrawFocusRectangle();
    }

    private void ToggleAll()
    {
        bool allChecked = _clips.Items.Count > 0 && _clips.CheckedItems.Count == _clips.Items.Count;
        for (int i = 0; i < _clips.Items.Count; i++) _clips.SetItemChecked(i, !allChecked);
        _selectAll.Text = allChecked ? "Select All" : "Deselect All";
    }

    // ---------------------------------------------------------------- preview

    private void ClearPreview()
    {
        Image? previous = _preview.Image;
        _preview.Image = null;
        previous?.Dispose();
    }

    private async Task ShowPreviewAsync()
    {
        if (_clips.SelectedItem is not ClipEntry entry) return;

        ClearPreview();
        _clipInfo.Text = "Reading clip...";

        // A Steam clip is a folder, not a file. Probing it asks ffprobe to open a directory,
        // which fails with "Permission denied". Everything the preview needs is already in the
        // folder: clip.pb describes the clip and Steam has written a thumbnail.
        if (entry.Listing is { } listing)
        {
            ShowSteamClipPreview(listing);
            return;
        }

        try
        {
            entry.Media ??= await _probe.ProbeAsync(entry.Path).ConfigureAwait(true);
            SourceMedia media = entry.Media;

            AspectRatio target = _settings.ParsedTargetAspect;
            bool willStretch = media.DisplayAspect != target;
            AspectRatio resultingSar = willStretch
                ? AspectRatio.SarForTargetDisplay(media.Width, media.Height, target)
                : media.SampleAspect;

            _clipInfo.Text =
                $"{media.VideoCodec} {media.Width}x{media.Height}, "
                + $"stored aspect {media.DisplayAspect}, {media.Streams.Count} stream(s)"
                + Environment.NewLine
                + (willStretch
                    ? $"Will be tagged {target} (SAR {resultingSar}). Video copied, not re-encoded."
                    : $"Already {target}. Video copied, not re-encoded.")
                + Environment.NewLine
                + $"Colour range {media.ColorRange ?? "unspecified"} - preserved as-is.";

            // Preview at the aspect the clip WILL display at, so it shows the stretch.
            var previewSource = willStretch
                ? media with { SampleAspect = resultingSar }
                : media;

            byte[] jpeg = await _thumbnails.ExtractAsync(previewSource).ConfigureAwait(true);

            // Copy out of the stream: an Image built directly on a MemoryStream needs that
            // stream to outlive it.
            using var ms = new MemoryStream(jpeg);
            using var decoded = Image.FromStream(ms);
            _preview.Image = new Bitmap(decoded);
        }
        catch (Exception ex)
        {
            entry.ProbeError = ex.Message;
            _clipInfo.Text = $"Could not read this clip: {ex.Message}";
            Log(LogLevel.Warning, $"{entry}: {ex.Message}");
        }
    }

    /// <summary>
    /// Preview for one of Steam's clip folders. Uses the thumbnail Steam already wrote rather
    /// than decoding a frame, so it is instant and needs no external process at all.
    /// </summary>
    private void ShowSteamClipPreview(ClipListing listing)
    {
        ClipManifest manifest = listing.Clip.Manifest;
        AspectRatio target = _settings.ParsedTargetAspect;

        var lines = new List<string>();

        string frame = manifest.Width > 0 && manifest.Height > 0
            ? $"{manifest.Width}x{manifest.Height} ({new AspectRatio(manifest.Width, manifest.Height)} frame), "
            : "";
        lines.Add($"{frame}{manifest.Duration.TotalSeconds:0.#}s");

        if (listing.Highlight is { } highlight)
        {
            var context = new List<string>();
            if (highlight.Map is { Length: > 0 } map) context.Add(map);
            if (highlight.Mode is { Length: > 0 } mode) context.Add(mode);
            if (highlight.Round is { } round) context.Add($"round {round}");
            if (highlight.PlantedBomb) context.Add("you planted");
            if (highlight.DefusedBomb) context.Add("you defused");

            lines.Add(context.Count > 0
                ? $"{highlight.Describe()} - {string.Join(", ", context)}"
                : highlight.Describe());
        }
        else if (listing.Clip.TimelinePath is null)
        {
            lines.Add("No timeline in this clip, so the title falls back to a plain name.");
        }

        lines.Add($"Will be tagged {target}. Video copied, not re-encoded.");
        lines.Add($"-> {listing.SuggestedName}.mp4");

        _clipInfo.Text = string.Join(Environment.NewLine, lines);

        if (listing.Clip.ThumbnailPath is not { } thumbnail)
        {
            return;
        }

        try
        {
            // Read the bytes rather than Image.FromFile, which holds the file open for the
            // lifetime of the image and would keep a lock on Steam's own folder.
            using var stream = new MemoryStream(File.ReadAllBytes(thumbnail));
            using var decoded = Image.FromStream(stream);
            _preview.Image = StretchToDisplayAspect(decoded, target);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            Log(LogLevel.Warning, $"{listing.SuggestedName}: thumbnail unreadable ({ex.Message}).");
        }
    }

    /// <summary>
    /// Redraws the thumbnail at the aspect the clip will display at. Steam's thumbnail is the
    /// stored 4:3 frame, so showing it untouched would preview the squashed picture rather than
    /// the stretched one the remux produces.
    /// </summary>
    private static Bitmap StretchToDisplayAspect(Image source, AspectRatio displayAspect)
    {
        int height = Math.Max(1, source.Height);
        int width = Math.Max(1, (int)Math.Round(
            height * (double)displayAspect.Numerator / displayAspect.Denominator));

        var stretched = new Bitmap(width, height);
        using (var graphics = Graphics.FromImage(stretched))
        {
            graphics.InterpolationMode =
                System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
            graphics.DrawImage(source, 0, 0, width, height);
        }

        return stretched;
    }

    // ------------------------------------------------------------------ batch

    private async Task RunBatchAsync()
    {
        List<ClipEntry> checkedEntries = _clips.CheckedItems.Cast<ClipEntry>().ToList();
        List<string> selected = checkedEntries.Select(c => c.Path).ToList();
        List<ClipListing> selectedClips = checkedEntries
            .Where(c => c.Listing is not null)
            .Select(c => c.Listing!)
            .ToList();

        if (selected.Count == 0)
        {
            MessageBox.Show(this, "No clips are selected.", "Nothing to do",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        if (string.IsNullOrWhiteSpace(_outputFolder.Text))
        {
            MessageBox.Show(this, "Choose an output folder first.", "Output folder required",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        Persist();
        _settings.InputFolder = _inputFolder.Text;
        _settings.OutputFolder = _outputFolder.Text;

        if (_settings.EnableYouTubeUpload && !_youtube.IsAuthenticated)
        {
            UploadResult auth = await _youtube.AuthenticateAsync().ConfigureAwait(true);
            if (!auth.Success)
            {
                MessageBox.Show(this, auth.Error, "YouTube sign-in failed",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
        }

        _cancellation = new CancellationTokenSource();
        SetBusy(true);
        _progress.Value = 0;
        _progress.Maximum = selected.Count;
        ShowLog();

        var progress = new Progress<BatchProgress>(p =>
        {
            _progress.Value = Math.Min(p.Completed, _progress.Maximum);
            SetStatus(p.CurrentFile.Length > 0
                ? $"{p.Completed + 1}/{p.Total}  {p.CurrentFile}"
                : $"{p.Total}/{p.Total} done");
        });

        try
        {
            IReadOnlyList<ClipOutcome> outcomes = _settings.ClipSource == ClipSource.SteamClips
                ? await _clipBatch
                    .RunAsync(selectedClips, _settings, _processed, _youtube, progress, _cancellation.Token)
                    .ConfigureAwait(true)
                : await _batch
                    .RunAsync(selected, _settings, _youtube, progress, _cancellation.Token)
                    .ConfigureAwait(true);

            int failed = outcomes.Count(o => !o.Succeeded);
            SetStatus(failed == 0 ? "Finished." : $"Finished with {failed} failure(s).");
            LoadClips();
        }
        catch (OperationCanceledException)
        {
            Log(LogLevel.Warning, "Cancelled.");
            SetStatus("Cancelled.");
        }
        catch (Exception ex)
        {
            Log(LogLevel.Error, ex.Message);
            SetStatus("Failed.");
        }
        finally
        {
            SetBusy(false);
            _cancellation.Dispose();
            _cancellation = null;
        }
    }

    private void SetBusy(bool busy)
    {
        _remux.Enabled = !busy;
        _selectAll.Enabled = !busy;
        _cancel.Enabled = busy;
        UseWaitCursor = busy;
    }

    // ------------------------------------------------------------------ other

    private void OpenSettings()
    {
        using var dialog = new SettingsForm(_settings);
        if (dialog.ShowDialog(this) != DialogResult.OK) return;

        _settings.Save(onError: m => Log(LogLevel.Warning, m));
        _ = ShowPreviewAsync();          // the target aspect may have changed
    }

    private void FixTimelines()
    {
        if (!Directory.Exists(_inputFolder.Text))
        {
            MessageBox.Show(this, "Choose an input folder first.", "Input folder required",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        ShowLog();
        TimelineFixResult result = TimelineFixer.FixAll(
            _inputFolder.Text, new DelegatePipelineLog(Log));
        SetStatus($"Timelines: {result.Fixed} fixed, {result.AlreadyValid} already valid, {result.Errors} error(s).");
    }

    private async Task TryRestoreYouTubeAsync()
    {
        if (!YouTubeClient.HasCredentialsFile) return;
        if (await _youtube.TryRestoreAsync().ConfigureAwait(true))
            Log(LogLevel.Info, "YouTube session restored.");
    }

    private void ShowLog()
    {
        if (!_log.Visible) _log.Show(this);
        _log.BringToFront();
    }

    private void Log(LogLevel level, string message) => _log.Append(level, message);

    private void SetStatus(string text) => _status.Text = text;

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            ClearPreview();
            _cancellation?.Dispose();
            _log.Dispose();
        }
        base.Dispose(disposing);
    }

    private void SelectSource(ClipSource source)
    {
        for (int i = 0; i < _source.Items.Count; i++)
        {
            if (_source.Items[i] is SourceChoice choice && choice.Value == source)
            {
                _source.SelectedIndex = i;
                return;
            }
        }
    }
}

/// <summary>A clip source as the drop-down shows it.</summary>
internal sealed record SourceChoice(ClipSource Value, string Label)
{
    public override string ToString() => Label;
}
