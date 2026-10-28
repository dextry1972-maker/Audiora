using System.Windows;
using SonosFlow.App.Localization;
using Forms = System.Windows.Forms;

namespace SonosFlow.App.Infrastructure;

/// <summary>Notification-area icon: status, main window, mini player, streaming, quit.</summary>
public sealed class TrayIcon : IDisposable
{
    private readonly Forms.NotifyIcon _icon;
    private readonly Forms.ToolStripMenuItem _toggle;
    private readonly Forms.ToolStripMenuItem _status;
    private readonly Forms.ToolStripMenuItem _mini;
    private readonly Forms.ToolStripMenuItem _open;
    private readonly Forms.ToolStripMenuItem _quit;
    private readonly Func<bool> _miniVisible;
    private bool _streaming;
    private string _speaker = "";

    /// <param name="status">Connection/streaming line, read each time the menu opens.</param>
    /// <param name="miniVisible">Current mini player visibility, for the menu text.</param>
    public TrayIcon(Action open, Action toggleStream, Action toggleMini, Func<string> status, Func<bool> miniVisible, Action quit)
    {
        _miniVisible = miniVisible;
        var iconStream = Application.GetResourceStream(new Uri("pack://application:,,,/Assets/SonosFlow.ico"))!.Stream;
        _status = new Forms.ToolStripMenuItem("…") { Enabled = false };
        _toggle = new Forms.ToolStripMenuItem("", null, (_, _) => toggleStream());
        _mini = new Forms.ToolStripMenuItem("", null, (_, _) => toggleMini());
        _open = new Forms.ToolStripMenuItem("", null, (_, _) => open());
        _quit = new Forms.ToolStripMenuItem("", null, (_, _) => quit());
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add(_status);
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add(_open);
        menu.Items.Add(_mini);
        menu.Items.Add(_toggle);
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add(_quit);
        menu.Opening += (_, _) =>
        {
            _status.Text = status();
            UpdateMiniText();
        };

        _icon = new Forms.NotifyIcon
        {
            Icon = new System.Drawing.Icon(iconStream),
            Text = "Audiora",
            ContextMenuStrip = menu,
            Visible = true,
        };
        _icon.MouseClick += (_, e) => { if (e.Button == Forms.MouseButtons.Left) open(); };

        ApplyTexts();
        Loc.Instance.LanguageChanged += ApplyTexts;
    }

    public void SetStreaming(bool streaming, string speaker)
    {
        _streaming = streaming;
        _speaker = speaker;
        UpdateStreamingTexts();
    }

    private void ApplyTexts()
    {
        _open.Text = Loc.Instance.T("tray.open");
        _quit.Text = Loc.Instance.T("tray.quit");
        UpdateMiniText();
        UpdateStreamingTexts();
    }

    private void UpdateMiniText() =>
        _mini.Text = Loc.Instance.T(_miniVisible() ? "tray.mini.hide" : "tray.mini.show");

    private void UpdateStreamingTexts()
    {
        _toggle.Text = Loc.Instance.T(_streaming ? "tray.stream.stop" : "tray.stream.start");
        var text = _streaming ? Loc.Instance.F("tray.tooltip.streaming", _speaker) : "Audiora";
        _icon.Text = text.Length > 63 ? text[..63] : text;
    }

    public void Dispose()
    {
        Loc.Instance.LanguageChanged -= ApplyTexts;
        _icon.Visible = false;
        _icon.Dispose();
    }
}
