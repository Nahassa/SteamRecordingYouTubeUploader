using SteamClipRemuxer.Core.Configuration;
using SteamClipRemuxer.Core.Youtube;

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

    private readonly NumericUpDown _minKills = new()
    {
        Width = 80, Minimum = 1, Maximum = 5,
    };

    private readonly NumericUpDown _intro = new()
    {
        Width = 80, Minimum = 0, Maximum = 120, Increment = 3,
    };

    private readonly NumericUpDown _outro = new()
    {
        Width = 80, Minimum = 0, Maximum = 120, Increment = 3,
    };

    private readonly CheckBox _includeWarmup = new()
    {
        Text = "Include kills from warmup", AutoSize = true,
    };

    private readonly ComboBox _launchSelection = new()
    {
        DropDownStyle = ComboBoxStyle.DropDownList, Width = 200,
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
    private readonly ComboBox _titleTimestamp = new()
    {
        DropDownStyle = ComboBoxStyle.DropDownList, Width = 200,
    };
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
        // Tabs rather than one scrolling column: thirty-odd controls in a single run meant the
        // thing you wanted was usually off-screen, and a setting you cannot see is a setting you
        // do not know you have. Each tab fits without scrolling at this size.
        Size = new Size(600, 620);
        MinimumSize = new Size(520, 480);
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.SizableToolWindow;
        MinimizeBox = false;
        MaximizeBox = false;

        _aspect.Items.AddRange(new object[] { "16:9", "4:3", "21:9" });
        _privacy.Items.AddRange(new object[] { "private", "unlisted", "public" });
        _upscale.Items.AddRange(new object[] { OffLabel, "1080p", "1440p" });
        _titleTimestamp.Items.AddRange(new object[] { KeepLabel, DateOnlyLabel, NoneLabel });
        _launchSelection.Items.AddRange(new object[] { SinceLastSessionLabel, TodayLabel, NothingLabel });

        var tabs = new TabControl { Dock = DockStyle.Fill, Padding = new Point(12, 6) };

        tabs.TabPages.Add(Page("Output",
            Row("Display aspect to tag:", _aspect),
            _moveProcessed,
            _fastStart,
            Note("The video stream is always copied, never re-encoded, so there is nothing to "
                + "configure for quality. Output is bit-identical to the recording.")));

        tabs.TabPages.Add(Page("Clips",
            Row("Include clips shorter than (s):", _maxClipSeconds),
            Note("Only clips shorter than this are included; a clip of exactly this length is "
                + "left out. Steam writes an untouched clip at exactly the recording buffer "
                + "length, so set this to the buffer length configured in Steam (120 by default) "
                + "and every untouched clip is excluded while anything you cropped is kept."),
            _includeLong,
            Note("Brings the untouched clips back into the list. They are mostly footage you did "
                + "not ask for, which is what 'Kill highlights only' on the main window is for."),
            _respectCrop,
            Note("Steam's crop point usually falls mid-GOP, so the cut lands on the nearest "
                + "earlier keyframe. Either way the video is copied, never re-encoded; unticking "
                + "keeps whole segments and a little more footage than you cropped."),
            _skipProcessed,
            Row("Tick on launch:", _launchSelection),
            Note("Which unprocessed clips start out ticked. 'Since last session' means recorded "
                + "after the app was last closed. A clip already remuxed or uploaded is never "
                + "ticked for you.")));

        tabs.TabPages.Add(Page("Highlights",
            Row("Keep before each kill (s):", _highlightLead),
            Row("Keep after each kill (s):", _highlightTail),
            Note("Both snap outward to Steam's three-second chunks: Steam writes one keyframe per "
                + "chunk, so three seconds is the finest a lossless cut can manage."),
            Row("Default min kills/round:", _minKills),
            Row("Default intro (s):", _intro),
            Row("Default outro (s):", _outro),
            Note("What the main window's boxes start at; each can still be changed for one run. "
                + "The intro is taken from the very start of the clip and the outro from the very "
                + "end - on a whole-map clip, the final scoreboard. 0 turns either off. Neither "
                + "repeats footage a fight already shows."),
            _includeWarmup,
            Note("Kills made before the match begins. Off by default: on a clip of a whole map "
                + "they come first, so the reel opened on warmup footage.")));

        tabs.TabPages.Add(Page("Naming",
            Row("Clip file name:", _clipFileName),
            Row("Clip YouTube title:", _clipTitle),
            Row("Compilation file name:", _compilationFileName),
            Row("Compilation YouTube title:", _compilationTitle),
            Row("Highlights file name:", _highlightsFileName),
            Row("Highlights YouTube title:", _highlightsTitle),
            Note("Placeholders: {game} {recording_date} {recording_time} {highlight} "
                + "{highlight_full} {weapon} {map} {mode} {round} {kills} {count} {fights} "
                + "{clip_name}"),
            Note("{count} is clips joined and {fights} is fights kept - a reel from three clips "
                + "can hold seven fights. {clip_name} is the clip's own file name, so a "
                + "highlights reel follows whatever the clip file name is set to. {highlight}, "
                + "{weapon}, {map} and {round} describe one clip, so they are empty on anything "
                + "spanning several; the name closes up around them rather than leaving a gap. "
                + "Renaming a clip in the list beats every box here."),
            Header("Game names"),
            Row("Game names:", _gameNames),
            Note("One per line, as 'app id = name', for example '730 = Counter-Strike 2'. A Steam "
                + "clip folder names its game only by id, so a game with no entry here is called "
                + "'App 440'. Counter-Strike 2 is built in; anything set here wins over that.")));

        tabs.TabPages.Add(Page("YouTube",
            _uploadEnabled,
            Row("Title:", _title),
            Row("Description:", _description),
            Row("Tags (comma separated):", _tags),
            Row("Privacy:", _privacy),
            Row("Category id:", _categoryId),
            _madeForKids,
            _ageRestricted,
            Note("Placeholders: {game} {clip} {recording_date} {recording_time} {filename} "
                + "{filename_ext} {date} {time} {datetime} {year} {month} {day}"),
            Header("Trimming titles"),
            Row("Timestamp in titles:", _titleTimestamp),
            Note("Applies to the YouTube title and the description, never to the file name - a "
                + "file name keeps its time because that is what stops two clips from one evening "
                + "sharing a name. 'Date only' turns 'CS2 - 2026-10-20 19-22-36 - Dust2 "
                + "multikills' into 'CS2 - 2026-10-20 - Dust2 multikills'. Use this rather than "
                + "the box below when the time differs in every title, which it does."),
            Note("Because it reaches the description too, 'Remove date and time' turns a "
                + "description of 'Recorded {recording_date}' into the bare word 'Recorded'. "
                + "Choose 'Date only', or drop {recording_date} from the description, if that "
                + "reads oddly. Renaming a clip in the list sets its title outright and is not "
                + "trimmed by this at all."),
            Row("Also remove text:", _removePatterns),
            Note($"Credentials and sign-in tokens live in {AppPaths.DataDirectory}")));

        tabs.TabPages.Add(Page("Upscale",
            Row("Upscale for YouTube:", _upscale),
            _useHardware,
            Note("Steam records 1280x960, which displays as 1706x960 - and 960 lines falls "
                + "between YouTube's 720 and 1080 rungs, so YouTube only ever builds 720p for it. "
                + "Scaling to 1080 measured 2.37 dB better than the 720 rung, against 0.78 dB for "
                + "simply giving 720p more bitrate, so most of the gain is the rung rather than "
                + "the bits."),
            Note("This is the one place anything is re-encoded, and only the copy sent to YouTube "
                + "is: the file kept on disk stays bit-identical to the recording. The scaled "
                + "copy is checked against the original before it is uploaded - geometry, colour, "
                + "audio, frame count and a similarity floor - and if anything is off, the "
                + "original is uploaded instead. The GPU option is tested by actually encoding "
                + "with it, and falls back to software if the card or this FFmpeg build cannot; "
                + "it is a speed setting, worth about 0.19 dB either way.")));

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

        Controls.Add(tabs);
        Controls.Add(buttons);
        tabs.BringToFront();

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
        // The theme's own secondary text, so a note stays legible in dark mode where a fixed
        // DimGray would have sunk into the background.
        ForeColor = SystemColors.GrayText,
        Margin = new Padding(0, 4, 0, 8),
    };

    /// <summary>
    /// A labelled control, in two columns that actually line up.
    ///
    /// The previous version placed the control at a fixed x inside a fixed-width Panel - 180 plus
    /// a 380-wide box inside 500 - so every row overflowed its own container by sixty pixels and
    /// only looked right because nothing clipped it. The same class of mistake as the button row
    /// that hid its own controls, so it is laid out rather than positioned.
    /// </summary>
    private static TableLayoutPanel Row(string label, Control control)
    {
        var row = new TableLayoutPanel
        {
            ColumnCount = 2,
            RowCount = 1,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Margin = new Padding(0, 2, 0, 2),
        };

        row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, LabelWidth));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        row.Controls.Add(
            new Label
            {
                Text = label,
                AutoSize = true,
                Anchor = AnchorStyles.Left,
                Margin = new Padding(0, 6, 8, 0),
            },
            0, 0);

        control.Margin = new Padding(0, 2, 0, 2);
        row.Controls.Add(control, 1, 0);

        return row;
    }

    /// <summary>Wide enough for the longest label here, "Compilation YouTube title:".</summary>
    private const int LabelWidth = 190;

    /// <summary>One tab, scrolling only if the window is made small enough to need it.</summary>
    private static TabPage Page(string name, params Control[] contents)
    {
        var panel = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            AutoScroll = true,
            Padding = new Padding(12),
        };

        panel.Controls.AddRange(contents);

        var page = new TabPage(name) { Padding = new Padding(4), UseVisualStyleBackColor = true };
        page.Controls.Add(panel);
        return page;
    }

    private const string OffLabel = "Off (upload as recorded)";

    private const string SinceLastSessionLabel = "Recorded since last session";
    private const string TodayLabel = "Recorded today";
    private const string NothingLabel = "Nothing";

    private static string LabelFor(LaunchSelection selection) => selection switch
    {
        LaunchSelection.RecordedToday => TodayLabel,
        LaunchSelection.None => NothingLabel,
        _ => SinceLastSessionLabel,
    };

    private static LaunchSelection SelectionFor(string? label) => label switch
    {
        TodayLabel => LaunchSelection.RecordedToday,
        NothingLabel => LaunchSelection.None,
        _ => LaunchSelection.SinceLastSession,
    };

    private const string KeepLabel = "Keep as the template builds it";
    private const string DateOnlyLabel = "Date only (drop the time)";
    private const string NoneLabel = "Remove date and time";

    private static string LabelFor(TitleTimestamp timestamp) => timestamp switch
    {
        TitleTimestamp.DateOnly => DateOnlyLabel,
        TitleTimestamp.None => NoneLabel,
        _ => KeepLabel,
    };

    private static TitleTimestamp TimestampFor(string? label) => label switch
    {
        DateOnlyLabel => TitleTimestamp.DateOnly,
        NoneLabel => TitleTimestamp.None,
        _ => TitleTimestamp.Keep,
    };

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
        _minKills.Value = Math.Clamp(
            _settings.HighlightMinKillsPerRound, (int)_minKills.Minimum, (int)_minKills.Maximum);
        _intro.Value = Math.Clamp(
            _settings.HighlightIntroSeconds, (int)_intro.Minimum, (int)_intro.Maximum);
        _outro.Value = Math.Clamp(
            _settings.HighlightOutroSeconds, (int)_outro.Minimum, (int)_outro.Maximum);
        _includeWarmup.Checked = _settings.HighlightIncludeWarmup;
        _launchSelection.SelectedItem = LabelFor(_settings.LaunchSelection);
        if (_launchSelection.SelectedIndex < 0) _launchSelection.SelectedIndex = 0;
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
        _titleTimestamp.SelectedItem = LabelFor(_settings.YouTubeTitleTimestamp);
        if (_titleTimestamp.SelectedIndex < 0) _titleTimestamp.SelectedIndex = 0;
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
        _settings.HighlightMinKillsPerRound = (int)_minKills.Value;
        _settings.HighlightIntroSeconds = (int)_intro.Value;
        _settings.HighlightOutroSeconds = (int)_outro.Value;
        _settings.HighlightIncludeWarmup = _includeWarmup.Checked;
        _settings.LaunchSelection = SelectionFor(_launchSelection.SelectedItem?.ToString());
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
        _settings.YouTubeTitleTimestamp = TimestampFor(_titleTimestamp.SelectedItem?.ToString());
        _settings.YouTubeRemoveTextPatterns = _removePatterns.Text;

        _settings.YouTubeUpscale = UpscaleFor(_upscale.SelectedItem?.ToString());
        _settings.UseHardwareEncoder = _useHardware.Checked;
    }
}
