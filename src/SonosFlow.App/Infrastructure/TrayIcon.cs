using System.Windows;
using Forms = System.Windows.Forms;

namespace SonosFlow.App.Infrastructure;

/// <summary>Notification-area icon: status, main window, mini player, streaming, quit.</summary>
public sealed class TrayIcon : IDisposable
{
    private readonly Forms.NotifyIcon _icon;
    private readonly Forms.ToolStripMenuItem _toggle;
    private readonly Forms.ToolStripMenuItem _status;
    private readonly Forms.ToolStripMenuItem _mini;

    /// <param name="status">Connection/streaming line, read each time the menu opens.</param>
    /// <param name="miniVisible">Current mini player visibility, for the menu text.</param>
    public TrayIcon(Action open, Action toggleStream, Action toggleMini, Func<string> status, Func<bool> miniVisible, Action quit)
    {
        var iconStream = Application.GetResourceStream(new Uri("pack://application:,,,/Assets/SonosFlow.ico"))!.Stream;
        _status = new Forms.ToolStripMenuItem("…") { Enabled = false };
        _toggle = new Forms.ToolStripMenuItem("Iniciar transmisión", null, (_, _) => toggleStream());
        _mini = new Forms.ToolStripMenuItem("Mostrar el mini reproductor", null, (_, _) => toggleMini());
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add(_status);
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add(new Forms.ToolStripMenuItem("Abrir Audiora (ventana principal)", null, (_, _) => open()));
        menu.Items.Add(_mini);
        menu.Items.Add(_toggle);
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add(new Forms.ToolStripMenuItem("Salir de Audiora", null, (_, _) => quit()));
        menu.Opening += (_, _) =>
        {
            _status.Text = status();
            _mini.Text = miniVisible() ? "Ocultar el mini reproductor" : "Mostrar el mini reproductor";
        };

        _icon = new Forms.NotifyIcon
        {
            Icon = new System.Drawing.Icon(iconStream),
            Text = "Audiora",
            ContextMenuStrip = menu,
            Visible = true,
        };
        _icon.MouseClick += (_, e) => { if (e.Button == Forms.MouseButtons.Left) open(); };
    }

    public void SetStreaming(bool streaming, string speaker)
    {
        _toggle.Text = streaming ? "Detener transmisión" : "Iniciar transmisión";
        var text = streaming ? $"Audiora — transmitiendo a {speaker}" : "Audiora";
        _icon.Text = text.Length > 63 ? text[..63] : text;
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
    }
}
