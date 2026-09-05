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
            Font = new Font(FontFamily.GenericMonospace, 9),
            BackColor = Color.White,
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
        _text.SelectionColor = level switch
        {
            LogLevel.Success => Color.Green,
            LogLevel.Warning => Color.DarkOrange,
            LogLevel.Error => Color.Red,
            _ => Color.Black,
        };
        _text.AppendText(message + Environment.NewLine);
        _text.ScrollToCaret();
    }
}
