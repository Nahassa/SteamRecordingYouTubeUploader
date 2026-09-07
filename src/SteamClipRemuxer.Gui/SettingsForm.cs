using SteamClipRemuxer.Core.Configuration;

namespace SteamClipRemuxer.Gui;

/// <summary>
/// Options. Short by design: with a lossless remux there is no encoder, resolution, quality
/// or colour setting left to expose.
/// </summary>
public sealed class SettingsForm : Form
{
    private readonly AppSettings _settings;

    private readonly ComboBox _aspect = new() { DropDownStyle = ComboBoxStyle.DropDown, Width = 140 };
    private readonly CheckBox _moveProcessed = new() { Text = "Move originals into processed/", AutoSize = true };
    private readonly CheckBox _fastStart = new() { Text = "Optimise for playback (faststart)", AutoSize = true };

    private readonly NumericUpDown _maxClipSeconds = new()
    {
        Width = 80, Minimum = 1, Maximum = 3600, Increment = 5,
    };

    private readonly CheckBox _respectCrop = new()
    {
        Text = "Cut to the range cropped in Steam", AutoSize = true,
    };

    private readonly CheckBox _skipProcessed = new()
    {
        Text = "Skip clips already processed", AutoSize = true,
    };

    private readonly TextBox _clipFileName = new() { Width = 380 };

    private readonly TextBox _gameNames = new()
    {
        Width = 380, Multiline = true, Height = 70, ScrollBars = ScrollBars.Vertical,
        AcceptsReturn = true,
    };

    private readonly CheckBox _uploadEnabled = new() { Text = "Upload to YouTube after remuxing", AutoSize = true };
    private readonly TextBox _title = new() { Width = 380 };
    private readonly TextBox _description = new() { Width = 380 };
    private readonly TextBox _tags = new() { Width = 380 };
    private readonly ComboBox _privacy = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 140 };
    private readonly TextBox _categoryId = new() { Width = 80 };
    private readonly CheckBox _madeForKids = new() { Text = "Made for kids", AutoSize = true };
    private readonly CheckBox _ageRestricted = new() { Text = "Age restricted (18+)", AutoSize = true };
    private readonly CheckBox _removeDate = new() { Text = "Strip the timestamp out of titles", AutoSize = true };
    private readonly TextBox _removePatterns = new() { Width = 380 };

    public SettingsForm(AppSettings settings)
    {
        _settings = settings;

        Text = "Settings";
        Size = new Size(560, 620);
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.SizableToolWindow;
        MinimizeBox = false;
        MaximizeBox = false;

        _aspect.Items.AddRange(new object[] { "16:9", "4:3", "21:9" });
        _privacy.Items.AddRange(new object[] { "private", "unlisted", "public" });

        var layout = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            AutoScroll = true,
            Padding = new Padding(12),
        };

        layout.Controls.Add(Header("Output"));
        layout.Controls.Add(Row("Display aspect to tag:", _aspect));
        layout.Controls.Add(_moveProcessed);
        layout.Controls.Add(_fastStart);
        layout.Controls.Add(Note(
            "The video stream is always copied, never re-encoded, so there is nothing to "
            + "configure for quality. Output is bit-identical to the recording."));

        layout.Controls.Add(Header("Steam clips"));
        layout.Controls.Add(Row("Longest clip to process (s):", _maxClipSeconds));
        layout.Controls.Add(Note(
            "Steam writes an untouched clip at exactly the recording buffer length, so anything "
            + "shorter is one you cropped yourself. Match this to the buffer length set in Steam."));
        layout.Controls.Add(_respectCrop);
        layout.Controls.Add(Note(
            "Steam's crop point usually falls mid-GOP, so the cut lands on the nearest earlier "
            + "keyframe. Either way the video is copied, never re-encoded; unticking keeps whole "
            + "segments and a little more footage than you cropped."));
        layout.Controls.Add(_skipProcessed);
        layout.Controls.Add(Row("Clip file name:", _clipFileName));
        layout.Controls.Add(Note(
            "Placeholders: {game} {recording_date} {recording_time} {highlight} {highlight_full} "
            + "{weapon} {map} {mode} {round} {kills}"));

        layout.Controls.Add(Row("Game names:", _gameNames));
        layout.Controls.Add(Note(
            "One per line, as 'app id = name', for example '730 = Counter-Strike 2'. A Steam clip "
            + "folder names its game only by id, so a game with no entry here is called "
            + "'App 440'. Counter-Strike 2 is built in; anything set here wins over that."));

        layout.Controls.Add(Header("YouTube"));
        layout.Controls.Add(_uploadEnabled);
        layout.Controls.Add(Row("Title:", _title));
        layout.Controls.Add(Row("Description:", _description));
        layout.Controls.Add(Row("Tags (comma separated):", _tags));
        layout.Controls.Add(Row("Privacy:", _privacy));
        layout.Controls.Add(Row("Category id:", _categoryId));
        layout.Controls.Add(_madeForKids);
        layout.Controls.Add(_ageRestricted);
        layout.Controls.Add(_removeDate);
        layout.Controls.Add(Row("Also remove text:", _removePatterns));
        layout.Controls.Add(Note(
            "Placeholders: {game} {clip} {recording_date} {recording_time} {filename} "
            + "{filename_ext} {date} {time} {datetime} {year} {month} {day}"));
        layout.Controls.Add(Note($"Credentials and sign-in tokens live in {AppPaths.DataDirectory}"));

        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            FlowDirection = FlowDirection.RightToLeft,
            Height = 46,
            Padding = new Padding(8),
        };

        var ok = new Button { Text = "OK", Width = 90, DialogResult = DialogResult.OK };
        ok.Click += (_, _) => Apply();
        var cancel = new Button { Text = "Cancel", Width = 90, DialogResult = DialogResult.Cancel };
        buttons.Controls.AddRange(new Control[] { ok, cancel });

        AcceptButton = ok;
        CancelButton = cancel;

        Controls.Add(layout);
        Controls.Add(buttons);
        layout.BringToFront();

        ApplyToUi();
    }

    private static Label Header(string text) => new()
    {
        Text = text,
        AutoSize = true,
        Font = new Font(DefaultFont, FontStyle.Bold),
        Margin = new Padding(0, 12, 0, 6),
    };

    private static Label Note(string text) => new()
    {
        Text = text,
        MaximumSize = new Size(480, 0),
        AutoSize = true,
        ForeColor = Color.DimGray,
        Margin = new Padding(0, 4, 0, 8),
    };

    private static Panel Row(string label, Control control)
    {
        var panel = new Panel { Height = 28, Width = 500, Margin = new Padding(0, 2, 0, 2) };
        panel.Controls.Add(new Label { Text = label, AutoSize = true, Location = new Point(0, 6) });
        control.Location = new Point(180, 2);
        panel.Controls.Add(control);
        return panel;
    }

    private void ApplyToUi()
    {
        _aspect.Text = _settings.TargetDisplayAspect;
        _moveProcessed.Checked = _settings.MoveProcessedFiles;
        _fastStart.Checked = _settings.FastStart;

        _maxClipSeconds.Value = Math.Clamp(
            _settings.MaxClipSeconds, (int)_maxClipSeconds.Minimum, (int)_maxClipSeconds.Maximum);
        _respectCrop.Checked = _settings.RespectSteamCrop;
        _skipProcessed.Checked = _settings.SkipAlreadyProcessed;
        _clipFileName.Text = _settings.ClipFileNameTemplate;

        _gameNames.Text = SteamClipRemuxer.Core.Steam.SteamApps.FormatOverrides(_settings.GameNames);

        _uploadEnabled.Checked = _settings.EnableYouTubeUpload;
        _title.Text = _settings.YouTubeTitleTemplate;
        _description.Text = _settings.YouTubeDescriptionTemplate;
        _tags.Text = _settings.YouTubeTags;
        _privacy.SelectedItem = _settings.YouTubePrivacyStatus;
        if (_privacy.SelectedIndex < 0) _privacy.SelectedIndex = 0;
        _categoryId.Text = _settings.YouTubeCategoryId;
        _madeForKids.Checked = _settings.YouTubeMadeForKids;
        _ageRestricted.Checked = _settings.YouTubeAgeRestricted;
        _removeDate.Checked = _settings.YouTubeRemoveDateFromFilename;
        _removePatterns.Text = _settings.YouTubeRemoveTextPatterns;
    }

    private void Apply()
    {
        _settings.TargetDisplayAspect = string.IsNullOrWhiteSpace(_aspect.Text) ? "16:9" : _aspect.Text.Trim();
        _settings.MoveProcessedFiles = _moveProcessed.Checked;
        _settings.FastStart = _fastStart.Checked;

        _settings.MaxClipSeconds = (int)_maxClipSeconds.Value;
        _settings.RespectSteamCrop = _respectCrop.Checked;
        _settings.SkipAlreadyProcessed = _skipProcessed.Checked;
        _settings.ClipFileNameTemplate = string.IsNullOrWhiteSpace(_clipFileName.Text)
            ? SteamClipRemuxer.Core.Highlights.ClipNaming.DefaultTemplate
            : _clipFileName.Text;

        _settings.GameNames = SteamClipRemuxer.Core.Steam.SteamApps.ParseOverrides(_gameNames.Text);

        _settings.EnableYouTubeUpload = _uploadEnabled.Checked;
        _settings.YouTubeTitleTemplate = _title.Text;
        _settings.YouTubeDescriptionTemplate = _description.Text;
        _settings.YouTubeTags = _tags.Text;
        _settings.YouTubePrivacyStatus = _privacy.SelectedItem?.ToString() ?? "private";
        _settings.YouTubeCategoryId = string.IsNullOrWhiteSpace(_categoryId.Text) ? "20" : _categoryId.Text.Trim();
        _settings.YouTubeMadeForKids = _madeForKids.Checked;
        _settings.YouTubeAgeRestricted = _ageRestricted.Checked;
        _settings.YouTubeRemoveDateFromFilename = _removeDate.Checked;
        _settings.YouTubeRemoveTextPatterns = _removePatterns.Text;
    }
}
