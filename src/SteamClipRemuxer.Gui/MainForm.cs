using SteamClipRemuxer.Core.Configuration;
using SteamClipRemuxer.Core.Execution;
using SteamClipRemuxer.Core.Probing;
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

    public override string ToString() => System.IO.Path.GetFileName(Path);
}

public sealed class MainForm : Form
{
    private readonly AppSettings _settings;
    private readonly ProcessRunner _runner = new();
    private readonly MediaProbe _probe;
    private readonly ThumbnailExtractor _thumbnails;
    private readonly BatchService _batch;
    private readonly YouTubeClient _youtube = new();

    private readonly LogForm _log = new();
    private readonly IProgress<(LogLevel Level, string Message)> _logSink;

    private CancellationTokenSource? _cancellation;

    private readonly TextBox _inputFolder = new() { Dock = DockStyle.Fill };
    private readonly TextBox _outputFolder = new() { Dock = DockStyle.Fill };
    private readonly CheckedListBox _clips = new() { Dock = DockStyle.Fill, IntegralHeight = false, CheckOnClick = true };
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

        var settings = new Button { Text = "Settings", Width = 100, Height = 30 };
        settings.Click += (_, _) => OpenSettings();

        var timelines = new Button { Text = "Fix Timelines", Width = 110, Height = 30 };
        timelines.Click += (_, _) => FixTimelines();

        var showLog = new Button { Text = "Show Log", Width = 100, Height = 30 };
        showLog.Click += (_, _) => ShowLog();

        actions.Controls.AddRange(new Control[]
        {
            _remux, _cancel, _selectAll, reload, settings, timelines, showLog,
        });

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

        IReadOnlyList<string> files = BatchService.FindRecordings(_inputFolder.Text);
        foreach (string file in files) _clips.Items.Add(new ClipEntry { Path = file }, isChecked: true);

        SetStatus($"{files.Count} clip(s) found.");
        if (files.Count > 0) _clips.SelectedIndex = 0;
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

    // ------------------------------------------------------------------ batch

    private async Task RunBatchAsync()
    {
        List<string> selected = _clips.CheckedItems.Cast<ClipEntry>().Select(c => c.Path).ToList();

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
            IReadOnlyList<ClipOutcome> outcomes = await _batch
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
}
