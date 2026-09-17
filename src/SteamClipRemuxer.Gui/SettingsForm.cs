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

    private readonly CheckBox _includeLong = new()
    {
        Text = "Also list clips at or above the threshold", AutoSize = true,
    };

    private readonly NumericUpDown _highlightLead = new()
    {
        Width = 80, Minimum = 0, Maximum = 60, Increment = 3,
    };

    private readonly NumericUpDown _highlightTail = new()
    {
        Width = 80, Minimum = 0, Maximum = 60, Increment = 3,
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
    private readonly TextBox _clipTitle = new() { Width = 380 };
    private readonly TextBox _compilationFileName = new() { Width = 380 };
    private readonly TextBox _compilationTitle = new() { Width = 380 };
    private readonly TextBox _highlightsFileName = new() { Width = 380 };
    private readonly TextBox _highlightsTitle = new() { Width = 380 };

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

    private readonly ComboBox _upscale = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 140 };

    private readonly CheckBox _useHardware = new()
    {
        Text = "Use the GPU encoder (NVENC) when it can", AutoSize = true,
    };

    public SettingsForm(AppSettings settings)
    {
        _settings = settings;

        Text = "Settings";
        // Taller than the content needs on purpose: the naming block is six rows, and a form
        // that opens mid-scroll is how a setting goes unnoticed. AutoScroll still covers the rest.
        Size = new Size(560, 760);
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.SizableToolWindow;
        MinimizeBox = false;
        MaximizeBox = false;

        _aspect.Items.AddRange(new object[] { "16:9", "4:3", "21:9" });
        _privacy.Items.AddRange(new object[] { "private", "unlisted", "public" });
        _upscale.Items.AddRange(new object[] { OffLabel, "1080p", "1440p" });

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
        layout.Controls.Add(Row("Include clips shorter than (s):", _maxClipSeconds));
        layout.Controls.Add(Note(
            "Only clips shorter than this are included; a clip of exactly this length is left "
            + "out. Steam writes an untouched clip at exactly the recording buffer length, so "
            + "set this to the buffer length configured in Steam (120 by default) and every "
            + "untouched clip is excluded while anything you cropped is kept."));
        layout.Controls.Add(_includeLong);
        layout.Controls.Add(Note(
            "Brings the untouched clips back into the list. They are mostly footage you did not "
            + "ask for, which is what 'Kill highlights only' on the main window is for."));
        layout.Controls.Add(_respectCrop);
        layout.Controls.Add(Note(
            "Steam's crop point usually falls mid-GOP, so the cut lands on the nearest earlier "
            + "keyframe. Either way the video is copied, never re-encoded; unticking keeps whole "
            + "segments and a little more footage than you cropped."));
        layout.Controls.Add(_skipProcessed);
        layout.Controls.Add(Row("Keep before each kill (s):", _highlightLead));
        layout.Controls.Add(Row("Keep after each kill (s):", _highlightTail));
        layout.Controls.Add(Note(
            "Used by 'Kill highlights only'. Both snap outward to Steam's three-second chunks: "
            + "Steam writes one keyframe per chunk, so three seconds is the finest a lossless "
            + "cut can manage."));

        layout.Controls.Add(Row("Clip file name:", _clipFileName));
        layout.Controls.Add(Row("Clip YouTube title:", _clipTitle));
        layout.Controls.Add(Row("Compilation file name:", _compilationFileName));
        layout.Controls.Add(Row("Compilation YouTube title:", _compilationTitle));
        layout.Controls.Add(Row("Highlights file name:", _highlightsFileName));
        layout.Controls.Add(Row("Highlights YouTube title:", _highlightsTitle));
        layout.Controls.Add(Note(
            "Placeholders: {game} {recording_date} {recording_time} {highlight} {highlight_full} "
            + "{weapon} {map} {mode} {round} {kills} {count} {fights} {clip_name}"));
        layout.Controls.Add(Note(
            "{count} is clips joined and {fights} is fights kept - a reel from three clips can "
            + "hold seven fights. {clip_name} is the clip's own file name, so a highlights reel "
            + "follows whatever the clip file name is set to. {highlight}, {weapon}, {map} and "
            + "{round} describe one clip, so they are empty on anything spanning several; the "
            + "name closes up around them rather than leaving a gap. Renaming a clip in the list "
            + "beats every box here."));

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

        layout.Controls.Add(Row("Upscale for YouTube:", _upscale));
        layout.Controls.Add(_useHardware);
        layout.Controls.Add(Note(
            "Steam records 1280x960, which displays as 1706x960 - and 960 lines falls between "
            + "YouTube's 720 and 1080 rungs, so YouTube only ever builds 720p for it. Scaling to "
            + "1080 measured 2.37 dB better than the 720 rung, against 0.78 dB for simply giving "
            + "720p more bitrate, so most of the gain is the rung rather than the bits."));
        layout.Controls.Add(Note(
            "This is the one place anything is re-encoded, and only the copy sent to YouTube is: "
            + "the file kept on disk stays bit-identical to the recording. The scaled copy is "
            + "checked against the original before it is uploaded - geometry, colour, audio, "
            + "frame count and a similarity floor - and if anything is off, the original is "
            + "uploaded instead. The GPU option is tested by actually encoding with it, and falls "
            + "back to software if the card or this FFmpeg build cannot; it is a speed setting, "
            + "worth about 0.19 dB either way."));
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

    private const string OffLabel = "Off (upload as recorded)";

    /// <summary>
    /// The enum's names cannot be shown as they are - a member may not begin with a digit, so
    /// To1080p is what it has to be called in code - and the persisted value is the name, not the
    /// label, so the two are mapped here rather than parsed from the text.
    /// </summary>
    private static string LabelFor(YouTubeUpscale upscale) => upscale switch
    {
        YouTubeUpscale.To1080p => "1080p",
        YouTubeUpscale.To1440p => "1440p",
        _ => OffLabel,
    };

    private static YouTubeUpscale UpscaleFor(string? label) => label switch
    {
        "1080p" => YouTubeUpscale.To1080p,
        "1440p" => YouTubeUpscale.To1440p,
        _ => YouTubeUpscale.Off,
    };

    /// <summary>What a template box holds, or the built-in default when it has been emptied.</summary>
    private static string Templated(TextBox box, string fallback) =>
        string.IsNullOrWhiteSpace(box.Text) ? fallback : box.Text;

    private void ApplyToUi()
    {
        _aspect.Text = _settings.TargetDisplayAspect;
        _moveProcessed.Checked = _settings.MoveProcessedFiles;
        _fastStart.Checked = _settings.FastStart;

        _maxClipSeconds.Value = Math.Clamp(
            _settings.MaxClipSeconds, (int)_maxClipSeconds.Minimum, (int)_maxClipSeconds.Maximum);
        _includeLong.Checked = _settings.IncludeLongClips;
        _highlightLead.Value = Math.Clamp(
            _settings.HighlightLeadSeconds, (int)_highlightLead.Minimum, (int)_highlightLead.Maximum);
        _highlightTail.Value = Math.Clamp(
            _settings.HighlightTailSeconds, (int)_highlightTail.Minimum, (int)_highlightTail.Maximum);
        _respectCrop.Checked = _settings.RespectSteamCrop;
        _skipProcessed.Checked = _settings.SkipAlreadyProcessed;
        _clipFileName.Text = _settings.ClipFileNameTemplate;
        _clipTitle.Text = _settings.YouTubeClipTitleTemplate;
        _compilationFileName.Text = _settings.CompilationFileNameTemplate;
        _compilationTitle.Text = _settings.YouTubeCompilationTitleTemplate;
        _highlightsFileName.Text = _settings.HighlightsFileNameTemplate;
        _highlightsTitle.Text = _settings.YouTubeHighlightsTitleTemplate;

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

        _upscale.SelectedItem = LabelFor(_settings.YouTubeUpscale);
        if (_upscale.SelectedIndex < 0) _upscale.SelectedIndex = 0;
        _useHardware.Checked = _settings.UseHardwareEncoder;
    }

    private void Apply()
    {
        _settings.TargetDisplayAspect = string.IsNullOrWhiteSpace(_aspect.Text) ? "16:9" : _aspect.Text.Trim();
        _settings.MoveProcessedFiles = _moveProcessed.Checked;
        _settings.FastStart = _fastStart.Checked;

        _settings.MaxClipSeconds = (int)_maxClipSeconds.Value;
        _settings.IncludeLongClips = _includeLong.Checked;
        _settings.HighlightLeadSeconds = (int)_highlightLead.Value;
        _settings.HighlightTailSeconds = (int)_highlightTail.Value;
        _settings.RespectSteamCrop = _respectCrop.Checked;
        _settings.SkipAlreadyProcessed = _skipProcessed.Checked;
        // A blank box means "give me the default back", not "name every file Clip".
        _settings.ClipFileNameTemplate = Templated(
            _clipFileName, SteamClipRemuxer.Core.Highlights.ClipNaming.DefaultTemplate);
        _settings.YouTubeClipTitleTemplate = Templated(
            _clipTitle, SteamClipRemuxer.Core.Youtube.TitleTemplate.DefaultClipTitle);
        _settings.CompilationFileNameTemplate = Templated(
            _compilationFileName,
            SteamClipRemuxer.Core.Highlights.ClipNaming.DefaultCompilationTemplate);
        _settings.YouTubeCompilationTitleTemplate = Templated(
            _compilationTitle,
            SteamClipRemuxer.Core.Youtube.TitleTemplate.DefaultCompilationTitle);
        _settings.HighlightsFileNameTemplate = Templated(
            _highlightsFileName,
            SteamClipRemuxer.Core.Highlights.ClipNaming.DefaultHighlightsTemplate);
        _settings.YouTubeHighlightsTitleTemplate = Templated(
            _highlightsTitle,
            SteamClipRemuxer.Core.Youtube.TitleTemplate.DefaultHighlightsTitle);

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

        _settings.YouTubeUpscale = UpscaleFor(_upscale.SelectedItem?.ToString());
        _settings.UseHardwareEncoder = _useHardware.Checked;
    }
}
