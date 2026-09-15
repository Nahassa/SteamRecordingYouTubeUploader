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

    /// <summary>Already remuxed or uploaded, so the row is greyed out.</summary>
    public bool IsProcessed => Listing is not null && Listing.State != ClipState.New;

    /// <summary>
    /// Whether the row is ticked. Held here rather than only on the list item, so hiding a row
    /// behind a filter and bringing it back does not lose the choice.
    /// </summary>
    public bool Checked { get; set; }

    public ClipStatus Status => Listing?.Status ?? ClipStatus.New;

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
    /// <summary>
    /// Details view rather than a CheckedListBox, for two reasons: it has columns for the
    /// per-clip status, and it ticks only when the box itself is clicked. A CheckedListBox with
    /// CheckOnClick toggles wherever the row is clicked, so choosing a clip to preview and
    /// choosing it for processing were the same gesture.
    /// </summary>
    private readonly ListView _clips = new()
    {
        Dock = DockStyle.Fill,
        View = View.Details,
        CheckBoxes = true,
        FullRowSelect = true,
        HideSelection = false,
        MultiSelect = false,
        HeaderStyle = ColumnHeaderStyle.Nonclickable,
    };

    /// <summary>Every row found on disk, including those a filter is currently hiding.</summary>
    private readonly List<ClipEntry> _all = new();

    /// <summary>True while the list is being rebuilt, so ItemChecked does not write back.</summary>
    private bool _populating;

    private readonly FlowLayoutPanel _filters = new()
    {
        Dock = DockStyle.Top,
        Height = 28,
        FlowDirection = FlowDirection.LeftToRight,
        WrapContents = false,
        Padding = new Padding(4, 4, 0, 0),
    };

    private readonly CheckBox _showNew = new() { Text = "New", AutoSize = true, Checked = true };
    private readonly CheckBox _showRemuxed = new() { Text = "Remuxed", AutoSize = true, Checked = true };
    private readonly CheckBox _showUploaded = new() { Text = "Uploaded", AutoSize = true, Checked = true };
    private readonly CheckBox _showPending = new() { Text = "Pending upload", AutoSize = true, Checked = true };
    private readonly CheckBox _showMissing = new() { Text = "Output missing", AutoSize = true, Checked = true };
    private readonly CheckBox _showHighlights = new() { Text = "Highlights", AutoSize = true, Checked = true };

    private readonly ComboBox _source = new()
    {
        DropDownStyle = ComboBoxStyle.DropDownList,
        Width = 190,
        Height = 30,
    };

    /// <summary>
    /// Joins the checked clips into one file. Deliberately not a saved setting: a compilation
    /// should be something you ask for, not something the app quietly keeps doing.
    /// </summary>
    private readonly CheckBox _stitch = new()
    {
        Text = "Stitch into one video",
        AutoSize = true,
        Height = 30,
        Padding = new Padding(8, 6, 0, 0),
    };

    /// <summary>
    /// Cuts each clip down to the fights in it. Steam clips only: the kill times come out of the
    /// clip's timeline, and an exported file does not have one.
    /// </summary>
    private readonly CheckBox _highlights = new()
    {
        Text = "Kill highlights only",
        AutoSize = true,
        Height = 30,
        Padding = new Padding(8, 6, 0, 0),
    };

    private readonly CheckBox _skipDeaths = new()
    {
        Text = "Stop before deaths",
        AutoSize = true,
        Height = 30,
        Padding = new Padding(8, 6, 0, 0),
    };

    private readonly ToolTip _tips = new();

    private readonly NumericUpDown _minKills = new()
    {
        Width = 46, Minimum = 1, Maximum = 5, Value = 1,
    };

    private readonly Label _minKillsLabel = new()
    {
        Text = "Min kills/round:", AutoSize = true, Height = 30, Padding = new Padding(8, 7, 0, 0),
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

        // Shared by both sources: the join only ever sees finished files, whichever produced them.
        var stitchService = new ClipStitchService(
            _runner, _probe,
            new DelegatePipelineLog((level, message) => _logSink.Report((level, message))));

        _batch = new BatchService(remuxService, stitchService,
            new DelegatePipelineLog((level, message) => _logSink.Report((level, message))));

        var clipRemuxService = new ClipRemuxService(
            _runner, _probe, new VideoStreamHasher(_runner),
            new DelegatePipelineLog((level, message) => _logSink.Report((level, message))));
        var highlightService = new ClipHighlightService(
            _runner, _probe, new VideoStreamHasher(_runner),
            new DelegatePipelineLog((level, message) => _logSink.Report((level, message))));

        _clipBatch = new ClipBatchService(clipRemuxService, stitchService, highlightService,
            new DelegatePipelineLog((level, message) => _logSink.Report((level, message))));

        _processed = ProcessedClipLog.Load(onError: m => BeginInvoke(() => Log(LogLevel.Warning, m)));

        BuildLayout();
        Restore();
        _ = TryRestoreYouTubeAsync();
    }

    private void BuildLayout()
    {
        Text = "Steam Clip Remuxer";
        // Wide enough for the widest bottom row (850px); see the budget where those rows are built.
        MinimumSize = new Size(900, 520);
        RestoreGeometry();

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

        _clips.Columns.Add("Recorded", 120);
        _clips.Columns.Add("Clip", 300);
        _clips.Columns.Add("Length", 60, HorizontalAlignment.Right);
        // One narrow column per status, ticked when it holds. Single letters because the width
        // is worth more to the clip's name than to spelling out five headings.
        _clips.Columns.Add("R", 26, HorizontalAlignment.Center);
        _clips.Columns.Add("U", 26, HorizontalAlignment.Center);
        _clips.Columns.Add("P", 26, HorizontalAlignment.Center);
        _clips.Columns.Add("M", 26, HorizontalAlignment.Center);
        _clips.Columns.Add("H", 26, HorizontalAlignment.Center);

        _tips.SetToolTip(_clips,
            "R remuxed - U uploaded - P pending upload - M output missing - H cut to highlights");

        _clips.SelectedIndexChanged += async (_, _) => await ShowPreviewAsync().ConfigureAwait(true);
        _clips.ItemChecked += (_, e) =>
        {
            if (_populating) return;
            if (e.Item.Tag is ClipEntry entry) entry.Checked = e.Item.Checked;
        };

        foreach (CheckBox filter in new[]
                 { _showNew, _showRemuxed, _showUploaded, _showPending, _showMissing, _showHighlights })
        {
            filter.Margin = new Padding(0, 3, 12, 0);
            // Filtering works from the rows already read, so it never touches the disk.
            filter.CheckedChanged += (_, _) => ApplyFilter();
            _filters.Controls.Add(filter);
        }

        var left = new Panel { Dock = DockStyle.Fill };
        left.Controls.Add(_clips);
        left.Controls.Add(_filters);
        _clips.BringToFront();
        split.Panel1.Controls.Add(left);

        var right = new Panel { Dock = DockStyle.Fill };
        _clipInfo.Text = "Select a clip to preview it.";

        // The preview sits in its own padded panel so the black letterbox is inset on all four
        // sides rather than running into the window frame.
        var previewFrame = new Panel { Dock = DockStyle.Fill, Padding = new Padding(8) };
        previewFrame.Controls.Add(_preview);

        right.Controls.Add(previewFrame);
        right.Controls.Add(_clipInfo);
        previewFrame.BringToFront();
        split.Panel2.Controls.Add(right);

        // --- actions --------------------------------------------------------
        // AutoSize with no fixed Height, deliberately. These were one fixed-height panel, and
        // when the row outgrew the window the overflow wrapped onto a second line that the
        // height then clipped - taking Settings, Fix Timelines and Show Log off the window with
        // no sign they were ever there. Growing is visible; clipping is not.
        var actions = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Padding = new Padding(8, 8, 8, 8),
            FlowDirection = FlowDirection.LeftToRight,
        };

        // The per-run choices sit on their own row above the buttons, which is what keeps each
        // row inside the window's 900px minimum width.
        var modes = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Padding = new Padding(8, 0, 8, 4),
            FlowDirection = FlowDirection.LeftToRight,
        };

        _remux.Click += async (_, _) => await RunBatchAsync().ConfigureAwait(true);

        _stitch.CheckedChanged += (_, _) => UpdateModeControls();
        _highlights.CheckedChanged += (_, _) => UpdateModeControls();
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
            UpdateModeControls();
            LoadClips();
        };

        var settings = new Button { Text = "Settings", Width = 100, Height = 30 };
        settings.Click += (_, _) => OpenSettings();

        var timelines = new Button { Text = "Fix Timelines", Width = 110, Height = 30 };
        timelines.Click += (_, _) => FixTimelines();

        var showLog = new Button { Text = "Show Log", Width = 100, Height = 30 };
        showLog.Click += (_, _) => ShowLog();

        // Width budget, so the next control added here is spending something visible. Explicit
        // widths plus the default 3px margin a side, plus the panel's own padding:
        //   actions  140+90+100+90+100+110+100  -> 788px
        //   modes     52+190+142+130+132+100+46 -> 850px
        // Both inside the 900px minimum window width set in BuildLayout. Exceed it and the row
        // wraps to a second line and the panel grows - visible, but no longer one clean row.
        actions.Controls.AddRange(new Control[]
        {
            _remux, _cancel, _selectAll, reload, settings, timelines, showLog,
        });

        modes.Controls.AddRange(new Control[]
        {
            new Label { Text = "Source:", Width = 52, Height = 30, TextAlign = ContentAlignment.MiddleRight },
            _source,
            _stitch, _highlights, _skipDeaths, _minKillsLabel, _minKills,
        });

        // --- status ----------------------------------------------------------
        var statusBar = new Panel { Dock = DockStyle.Bottom, Height = 30, Padding = new Padding(8, 4, 8, 4) };
        statusBar.Controls.Add(_status);
        statusBar.Controls.Add(_progress);
        _status.BringToFront();

        // --- menu -------------------------------------------------------------
        // A second route to the three actions that a crowded row can push off the window. The
        // buttons stay; this is the one that cannot be hidden.
        var menu = new MenuStrip();

        var file = new ToolStripMenuItem("&File");
        file.DropDownItems.Add("&Settings...", null, (_, _) => OpenSettings());
        file.DropDownItems.Add(new ToolStripSeparator());
        file.DropDownItems.Add("E&xit", null, (_, _) => Close());

        var tools = new ToolStripMenuItem("&Tools");
        tools.DropDownItems.Add("&Reload Clips", null, (_, _) => LoadClips());
        tools.DropDownItems.Add("&Fix Timelines", null, (_, _) => FixTimelines());

        var view = new ToolStripMenuItem("&View");
        view.DropDownItems.Add("Show &Log", null, (_, _) => ShowLog());

        menu.Items.AddRange(new ToolStripItem[] { file, tools, view });

        Controls.Add(split);

        // Bottom docking is resolved from the highest index downward, so the row added later
        // lands lower: modes first puts the toggles above the buttons they modify.
        Controls.Add(modes);
        Controls.Add(actions);
        Controls.Add(statusBar);
        Controls.Add(folders);
        folders.BringToFront();
        split.BringToFront();   // Fill must be docked last, so it must sit at index 0

        // Added last so it holds the highest index, docks first, and sits above the folder rows.
        Controls.Add(menu);
        MainMenuStrip = menu;

        // Safe only once the control has been sized. A saved distance wins: this used to run on
        // every show, so a dragged splitter survived until the next launch and was then reset.
        Shown += (_, _) =>
        {
            int wanted = _settings.SplitterDistance > 0
                ? _settings.SplitterDistance
                : Math.Min(300, Math.Max(120, split.Width - 200));

            try { split.SplitterDistance = wanted; }
            catch (InvalidOperationException) { /* too narrow for it; leave the default */ }
        };

        split.SplitterMoved += (_, _) => _settings.SplitterDistance = split.SplitterDistance;

        FormClosing += (_, _) =>
        {
            SaveGeometry();
            Persist();
        };
    }

    // ------------------------------------------------------------------ state

    private void Restore()
    {
        _inputFolder.Text = _settings.InputFolder;
        _outputFolder.Text = _settings.OutputFolder;
        SelectSource(_settings.ClipSource);
        UpdateModeControls();
        if (Directory.Exists(_inputFolder.Text)) LoadClips();
    }

    private void Persist()
    {
        _settings.InputFolder = _inputFolder.Text;
        _settings.OutputFolder = _outputFolder.Text;
        _settings.Save(onError: m => Log(LogLevel.Warning, m));
    }

    /// <summary>
    /// Puts the window back where it was. A saved position is only honoured while it still lands
    /// on an attached screen - restoring onto a monitor that has since been unplugged leaves a
    /// window that cannot be reached.
    /// </summary>
    private void RestoreGeometry()
    {
        var size = new Size(
            _settings.WindowWidth > 0 ? _settings.WindowWidth : 1040,
            _settings.WindowHeight > 0 ? _settings.WindowHeight : 660);

        size = new Size(
            Math.Max(size.Width, MinimumSize.Width),
            Math.Max(size.Height, MinimumSize.Height));

        var saved = new Rectangle(_settings.WindowLeft, _settings.WindowTop, size.Width, size.Height);

        if ((_settings.WindowLeft != 0 || _settings.WindowTop != 0)
            && Screen.AllScreens.Any(screen => screen.WorkingArea.IntersectsWith(saved)))
        {
            StartPosition = FormStartPosition.Manual;
            Bounds = saved;
        }
        else
        {
            StartPosition = FormStartPosition.CenterScreen;
            Size = size;
        }

        if (_settings.WindowMaximized) WindowState = FormWindowState.Maximized;
    }

    /// <summary>
    /// Records the window for next time. RestoreBounds rather than Bounds, so a maximized or
    /// minimized window stores the size it will return to rather than the screen.
    /// </summary>
    private void SaveGeometry()
    {
        Rectangle bounds = WindowState == FormWindowState.Normal ? Bounds : RestoreBounds;

        _settings.WindowWidth = bounds.Width;
        _settings.WindowHeight = bounds.Height;
        _settings.WindowLeft = bounds.X;
        _settings.WindowTop = bounds.Y;
        _settings.WindowMaximized = WindowState == FormWindowState.Maximized;
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
        _all.Clear();
        ClearPreview();

        if (!Directory.Exists(_inputFolder.Text))
        {
            ApplyFilter();
            SetStatus("Input folder not found.");
            return;
        }

        _settings.InputFolder = _inputFolder.Text;

        if (_settings.ClipSource == ClipSource.SteamClips) LoadSteamClips();
        else LoadExportedFiles();

        ApplyFilter();
    }

    private void LoadExportedFiles()
    {
        IReadOnlyList<string> files = BatchService.FindRecordings(_inputFolder.Text);
        foreach (string file in files) _all.Add(new ClipEntry { Path = file, Checked = true });

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
            // Already handled clips are listed but not ticked, so pressing the button does not
            // silently redo them while they stay visible.
            _all.Add(new ClipEntry
            {
                Path = clip.Clip.Path,
                Listing = clip,
                Checked = clip.State == ClipState.New,
            });
        }

        int done = clips.Count(c => c.State != ClipState.New);
        SetStatus(done == 0
            ? $"{clips.Count} clip(s) found."
            : $"{clips.Count} clip(s) found, {done} already done.");
    }

    /// <summary>Whether a row survives the status filters. Everything ticked shows everything.</summary>
    private bool PassesFilter(ClipEntry entry)
    {
        ClipStatus status = entry.Status;

        return (_showNew.Checked && status.Untouched)
            || (_showRemuxed.Checked && status.Remuxed)
            || (_showUploaded.Checked && status.Uploaded)
            || (_showPending.Checked && status.PendingUpload)
            || (_showMissing.Checked && status.OutputMissing)
            || (_showHighlights.Checked && status.CutToHighlights);
    }

    /// <summary>
    /// Rebuilds the visible rows from what has already been read, so filtering never touches the
    /// disk. Each row's tick comes off the entry rather than the list, which is what lets a row
    /// be hidden by a filter and brought back without losing the choice.
    /// </summary>
    private void ApplyFilter()
    {
        _populating = true;
        try
        {
            _clips.BeginUpdate();
            _clips.Items.Clear();

            foreach (ClipEntry entry in _all.Where(PassesFilter))
                _clips.Items.Add(RowFor(entry));

            _clips.EndUpdate();
        }
        finally
        {
            _populating = false;
        }

        if (_clips.Items.Count > 0) _clips.Items[0].Selected = true;
        UpdateFilterStatus();
    }

    private ListViewItem RowFor(ClipEntry entry)
    {
        ClipListing? listing = entry.Listing;
        ClipStatus status = entry.Status;

        string recorded = listing is null
            ? string.Empty
            : listing.RecordedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm");

        string name = listing?.Summary() ?? System.IO.Path.GetFileName(entry.Path);
        string length = listing is null
            ? string.Empty
            : $"{listing.Clip.Manifest.Duration.TotalSeconds:0.#}s";

        var row = new ListViewItem(new[]
        {
            recorded,
            name,
            length,
            Tick(status.Remuxed),
            Tick(status.Uploaded),
            Tick(status.PendingUpload),
            Tick(status.OutputMissing),
            Tick(status.CutToHighlights),
        })
        {
            Tag = entry,
            Checked = entry.Checked,
        };

        // Greyed rather than hidden: a row that silently fails to appear is hard to tell from one
        // the tool never found.
        if (entry.IsProcessed) row.ForeColor = SystemColors.GrayText;

        return row;
    }

    private static string Tick(bool done) => done ? "\u2713" : string.Empty;

    private void UpdateFilterStatus()
    {
        if (_all.Count == 0 || _clips.Items.Count == _all.Count) return;

        SetStatus($"{_clips.Items.Count} of {_all.Count} clip(s) shown.");
    }

    /// <summary>Ticks or unticks the rows on show, leaving anything a filter is hiding alone.</summary>
    private void ToggleAll()
    {
        bool allChecked = _clips.Items.Count > 0 && _clips.CheckedItems.Count == _clips.Items.Count;

        foreach (ListViewItem row in _clips.Items)
        {
            row.Checked = !allChecked;
            if (row.Tag is ClipEntry entry) entry.Checked = !allChecked;
        }

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
        if (Selected() is not { } entry) return;

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
        List<ClipEntry> checkedEntries = Ticked();
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

        if (_stitch.Checked && !_highlights.Checked && selected.Count < 2)
        {
            MessageBox.Show(this, "Select at least two clips to stitch together.",
                "Not enough clips", MessageBoxButtons.OK, MessageBoxIcon.Information);
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
            IReadOnlyList<ClipOutcome> outcomes = (_settings.ClipSource, _stitch.Checked) switch
            {
                // Highlights read the clip's timeline, so they exist only on the Steam-clip path.
                (ClipSource.SteamClips, _) when _highlights.Checked => await _clipBatch
                    .RunHighlightsAsync(selectedClips, _settings, _processed, WindowOptions(),
                        _stitch.Checked, _youtube, progress, _cancellation.Token)
                    .ConfigureAwait(true),
                (ClipSource.SteamClips, true) => await _clipBatch
                    .RunStitchAsync(selectedClips, _settings, _processed, _youtube, progress, _cancellation.Token)
                    .ConfigureAwait(true),
                (ClipSource.SteamClips, false) => await _clipBatch
                    .RunAsync(selectedClips, _settings, _processed, _youtube, progress, _cancellation.Token)
                    .ConfigureAwait(true),
                (_, true) => await _batch
                    .RunStitchAsync(selected, _settings, _youtube, progress, _cancellation.Token)
                    .ConfigureAwait(true),
                _ => await _batch
                    .RunAsync(selected, _settings, _youtube, progress, _cancellation.Token)
                    .ConfigureAwait(true),
            };

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

    /// <summary>How much to keep around each fight, from the settings plus the per-run choices.</summary>
    private HighlightWindowOptions WindowOptions() => new()
    {
        Lead = TimeSpan.FromSeconds(Math.Max(0, _settings.HighlightLeadSeconds)),
        Tail = TimeSpan.FromSeconds(Math.Max(0, _settings.HighlightTailSeconds)),
        MinimumKillsPerRound = (int)_minKills.Value,
        StopBeforeDeaths = _skipDeaths.Checked,
    };

    /// <summary>
    /// Keeps the per-run controls honest about what is available. Highlights need a timeline, so
    /// they are offered for Steam's clip folders and nothing else, and the button says which of
    /// the four combinations is about to run.
    /// </summary>
    private void UpdateModeControls()
    {
        bool steamClips = _settings.ClipSource == ClipSource.SteamClips;

        if (!steamClips && _highlights.Checked) _highlights.Checked = false;
        _highlights.Enabled = steamClips;

        bool cutting = steamClips && _highlights.Checked;
        _skipDeaths.Enabled = cutting;
        _minKills.Enabled = cutting;
        _minKillsLabel.Enabled = cutting;

        _remux.Text = (cutting, _stitch.Checked) switch
        {
            (true, true) => "Cut && Join",
            (true, false) => "Cut Highlights",
            (false, true) => "Stitch Selected",
            _ => "Remux Selected",
        };

        _tips.SetToolTip(_highlights, steamClips
            ? "Keep only the chunks around your kills."
            : "Only Steam clips carry the timeline that says where your kills are.");
    }

    private void SetBusy(bool busy)
    {
        _remux.Enabled = !busy;
        _selectAll.Enabled = !busy;
        _stitch.Enabled = !busy;
        _highlights.Enabled = !busy && _settings.ClipSource == ClipSource.SteamClips;
        _skipDeaths.Enabled = !busy && _highlights.Checked;
        _minKills.Enabled = !busy && _highlights.Checked;
        _filters.Enabled = !busy;
        _cancel.Enabled = busy;
        UseWaitCursor = busy;
    }

    // ------------------------------------------------------------------ other

    private void OpenSettings()
    {
        using var dialog = new SettingsForm(_settings);
        if (dialog.ShowDialog(this) != DialogResult.OK) return;

        _settings.Save(onError: m => Log(LogLevel.Warning, m));

        // Rebuild the list rather than only refreshing the preview. Each row caches the game
        // name, the file name it would be given and the decision that it is short enough to
        // process, all taken when the list was built - so leaving the rows alone makes a
        // settings change look like it was ignored until the app is restarted.
        ReloadKeepingSelection();

        // Setting the selection to a row that is already selected raises no event, so the
        // preview would otherwise keep the old target aspect.
        _ = ShowPreviewAsync();
    }

    /// <summary>
    /// Reloads the list without losing which rows were ticked.
    ///
    /// Only rows that were on the list before keep their state. A clip that appears *because* of
    /// the change - turning on "Also list clips at or above the threshold" is the obvious case -
    /// takes the tick a fresh load would have given it. Restoring the old set wholesale left
    /// those rows unticked, so the button did nothing with the very clips the change revealed.
    /// </summary>
    private void ReloadKeepingSelection()
    {
        var before = new Dictionary<string, bool>(StringComparer.Ordinal);
        foreach (ClipEntry entry in _all) before[KeyOf(entry)] = entry.Checked;

        LoadClips();

        bool changed = false;
        foreach (ClipEntry entry in _all)
        {
            if (!before.TryGetValue(KeyOf(entry), out bool wasChecked)) continue;
            if (entry.Checked == wasChecked) continue;

            entry.Checked = wasChecked;
            changed = true;
        }

        if (changed) ApplyFilter();
    }

    /// <summary>The selected row's clip, or null when nothing is selected.</summary>
    private ClipEntry? Selected() =>
        _clips.SelectedItems.Count > 0 ? _clips.SelectedItems[0].Tag as ClipEntry : null;

    /// <summary>The ticked rows that are actually on show.</summary>
    private List<ClipEntry> Ticked() =>
        _clips.CheckedItems.Cast<ListViewItem>()
            .Select(row => row.Tag)
            .OfType<ClipEntry>()
            .ToList();

    /// <summary>What identifies a row across a reload: the clip's own identity where it has one.</summary>
    private static string KeyOf(ClipEntry entry) => entry.Listing?.Clip.Manifest.Id ?? entry.Path;

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
