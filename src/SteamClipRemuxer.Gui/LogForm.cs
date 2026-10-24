using SteamClipRemuxer.Core.Execution;

namespace SteamClipRemuxer.Gui;

/// <summary>A non-modal log window. Hides rather than closes, so its content survives.</summary>
public sealed class LogForm : Form
{
    private readonly RichTextBox _text;

    public LogForm()
    {
        Text = "Log";
        Size = new Size(760, 460);
        StartPosition = FormStartPosition.CenterParent;

        _text = new RichTextBox
        {
            Dock = DockStyle.Fill,
            ReadOnly = true,
            Font = Monospace(9),
            // Taken from the theme rather than fixed: a hardcoded white box stayed white when the
            // rest of the window went dark, and black text in it would then have been invisible.
            BackColor = SystemColors.Window,
            ForeColor = SystemColors.WindowText,
            DetectUrls = true,
        };
        _text.LinkClicked += (_, e) =>
        {
            if (e.LinkText is null) return;
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(e.LinkText)
            {
                UseShellExecute = true,
            });
        };

        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            FlowDirection = FlowDirection.RightToLeft,
            Height = 40,
            Padding = new Padding(6),
        };

        var close = new Button { Text = "Close", Width = 90 };
        close.Click += (_, _) => Hide();

        var copy = new Button { Text = "Copy", Width = 90 };
        copy.Click += (_, _) =>
        {
            if (_text.TextLength > 0) Clipboard.SetText(_text.Text);
        };

        var clear = new Button { Text = "Clear", Width = 90 };
        clear.Click += (_, _) => _text.Clear();

        buttons.Controls.AddRange(new Control[] { close, copy, clear });

        Controls.Add(_text);
        Controls.Add(buttons);
        _text.BringToFront();

        FormClosing += (_, e) =>
        {
            if (e.CloseReason != CloseReason.FormOwnerClosing && e.CloseReason != CloseReason.ApplicationExitCall)
            {
                e.Cancel = true;
                Hide();
            }
        };
    }

    public void Append(LogLevel level, string message)
    {
        _text.SelectionColor = ColourFor(level);
        _text.AppendText(message + Environment.NewLine);
        _text.ScrollToCaret();
    }

    /// <summary>
    /// The accent for a log level, picked for the background actually in use.
    ///
    /// The light set is too dark to read on a dark background and the dark set is too pale on a
    /// light one - plain Green in particular is almost unreadable on dark. Which set applies is
    /// decided from the window colour rather than from a theme flag, so it stays right however the
    /// colour got there.
    /// </summary>
    private static Color ColourFor(LogLevel level)
    {
        bool dark = IsDark(SystemColors.Window);

        return level switch
        {
            LogLevel.Success => dark ? Color.FromArgb(0x4E, 0xC9, 0x7B) : Color.FromArgb(0x1A, 0x7F, 0x37),
            LogLevel.Warning => dark ? Color.FromArgb(0xE5, 0xA3, 0x4F) : Color.FromArgb(0xB2, 0x6A, 0x00),
            LogLevel.Error => dark ? Color.FromArgb(0xF4, 0x71, 0x74) : Color.FromArgb(0xC0, 0x28, 0x28),
            _ => SystemColors.WindowText,
        };
    }

    /// <summary>
    /// The best monospace font actually installed.
    ///
    /// GenericMonospace resolves to Courier New, which is the single most dated-looking face
    /// Windows ships. Cascadia ships with Windows 11 and with Terminal; Consolas goes back far
    /// enough to cover everything else. Falls back rather than assuming, because a missing family
    /// is substituted silently by GDI+ and the result is Courier New again.
    /// </summary>
    private static Font Monospace(float size)
    {
        foreach (string name in new[] { "Cascadia Mono", "Cascadia Code", "Consolas" })
        {
            try
            {
                using var family = new FontFamily(name);
                return new Font(family, size);
            }
            catch (ArgumentException)
            {
                // Not installed; GDI+ throws rather than substituting when asked by name.
            }
        }

        return new Font(FontFamily.GenericMonospace, size);
    }

    /// <summary>Perceived brightness, weighted for the eye's bias toward green.</summary>
    private static bool IsDark(Color colour) =>
        (0.299 * colour.R) + (0.587 * colour.G) + (0.114 * colour.B) < 128;
}
