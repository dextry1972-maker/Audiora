using System.Windows;
using Forms = System.Windows.Forms;

namespace SonosFlow.App.Infrastructure;

/// <summary>Notification-area icon: open the window, toggle streaming, quit.</summary>
public sealed class TrayIcon : IDisposable
{
    private readonly Forms.NotifyIcon _icon;
    private readonly Forms.ToolStripMenuItem _toggle;

    public TrayIcon(Action open, Action toggleStream, Action quit)
    {
        var iconStream = Application.GetResourceStream(new Uri("pack://application:,,,/Assets/SonosFlow.ico"))!.Stream;
        _toggle = new Forms.ToolStripMenuItem("Iniciar transmisión", null, (_, _) => toggleStream());
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add(new Forms.ToolStripMenuItem("Abrir Sonos Flow", null, (_, _) => open()));
        menu.Items.Add(_toggle);
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add(new Forms.ToolStripMenuItem("Salir", null, (_, _) => quit()));

        _icon = new Forms.NotifyIcon
        {
            Icon = new System.Drawing.Icon(iconStream),
            Text = "Sonos Flow",
            ContextMenuStrip = menu,
            Visible = true,
        };
        _icon.MouseClick += (_, e) => { if (e.Button == Forms.MouseButtons.Left) open(); };
    }

    public void SetStreaming(bool streaming, string speaker)
    {
        _toggle.Text = streaming ? "Detener transmisión" : "Iniciar transmisión";
        var text = streaming ? $"Sonos Flow — transmitiendo a {speaker}" : "Sonos Flow";
        _icon.Text = text.Length > 63 ? text[..63] : text;
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
    }
}
