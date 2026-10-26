using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace SonosFlow.Setup;

/// <summary>Single-page dark installer window.</summary>
public sealed class SetupForm : Form
{
    private static readonly Color Bg = Color.FromArgb(11, 14, 17);
    private static readonly Color Card = Color.FromArgb(19, 24, 28);
    private static readonly Color TextMain = Color.FromArgb(241, 244, 246);
    private static readonly Color TextDim = Color.FromArgb(139, 149, 157);
    private static readonly Color Accent = Color.FromArgb(49, 214, 123);

    private readonly InstallOptions _options;
    private readonly CheckBox _desktop;
    private readonly CheckBox _launch;
    private readonly Button _install;
    private readonly ProgressBar _bar;
    private readonly Label _status;
    private bool _finished;

    public SetupForm(InstallOptions options)
    {
        _options = options;
        Text = "Instalar Audiora";
        Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Bg;
        ForeColor = TextMain;
        Font = new Font("Segoe UI", 9.5f);
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(520, 360);

        var logo = new PictureBox
        {
            Image = Icon.ExtractAssociatedIcon(Application.ExecutablePath)!.ToBitmap(),
            SizeMode = PictureBoxSizeMode.Zoom,
            Bounds = new Rectangle(32, 30, 48, 48),
        };
        var title = new Label { Text = "Audiora", Font = new Font("Segoe UI Semibold", 18f), AutoSize = true, Location = new Point(92, 28) };
        var subtitle = new Label { Text = "Transmite el audio de tu PC a tu Sonos por Wi-Fi", ForeColor = TextDim, AutoSize = true, Location = new Point(94, 62) };

        var where = new Label
        {
            Text = "Se instalará para tu usuario en:\n" + options.Directory + "\nNo necesita permisos de administrador.",
            ForeColor = TextDim, AutoSize = false, Bounds = new Rectangle(32, 104, 456, 58),
        };

        _desktop = MakeCheck("Crear acceso directo en el escritorio", 172, options.DesktopShortcut);
        _launch = MakeCheck("Abrir Audiora al terminar", 202, options.Launch);

        _bar = new ProgressBar { Bounds = new Rectangle(32, 250, 456, 6), Style = ProgressBarStyle.Continuous, Visible = false };
        _status = new Label { ForeColor = TextDim, AutoSize = false, Bounds = new Rectangle(32, 262, 456, 22) };

        _install = new Button
        {
            Text = "Instalar",
            Bounds = new Rectangle(358, 296, 130, 40),
            FlatStyle = FlatStyle.Flat,
            BackColor = Accent,
            ForeColor = Color.FromArgb(4, 20, 10),
            Font = new Font("Segoe UI Semibold", 10f),
            Cursor = Cursors.Hand,
        };
        _install.FlatAppearance.BorderSize = 0;
        _install.Click += async (_, _) => await OnInstallClickAsync();

        var cancel = new Button
        {
            Text = "Cancelar",
            Bounds = new Rectangle(240, 296, 108, 40),
            FlatStyle = FlatStyle.Flat,
            BackColor = Card,
            ForeColor = TextMain,
            Cursor = Cursors.Hand,
        };
        cancel.FlatAppearance.BorderColor = Color.FromArgb(42, 52, 59);
        cancel.Click += (_, _) => Close();

        Controls.AddRange([logo, title, subtitle, where, _desktop, _launch, _bar, _status, cancel, _install]);
        AcceptButton = _install;
    }

    private CheckBox MakeCheck(string text, int y, bool value) => new()
    {
        Text = text, Checked = value, AutoSize = true, Location = new Point(34, y), ForeColor = TextMain, Cursor = Cursors.Hand,
    };

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        base.OnPaintBackground(e);
        // Subtle emerald glow in the corner, matching the app.
        using var path = new GraphicsPath();
        path.AddEllipse(ClientSize.Width - 260, -200, 420, 360);
        using var brush = new PathGradientBrush(path) { CenterColor = Color.FromArgb(40, Accent), SurroundColors = [Color.FromArgb(0, Accent)] };
        e.Graphics.FillPath(brush, path);
    }

    private async Task OnInstallClickAsync()
    {
        if (_finished)
        {
            Close();
            return;
        }

        _options.DesktopShortcut = _desktop.Checked;
        _options.Launch = _launch.Checked;
        if (Installer.CheckPreconditions(_options) is { } problem)
        {
            MessageBox.Show(this, problem, "Audiora", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        _install.Enabled = _desktop.Enabled = _launch.Enabled = false;
        _bar.Visible = true;
        var progress = new Progress<(int Percent, string Status)>(p => { _bar.Value = p.Percent; _status.Text = p.Status; });
        try
        {
            await Task.Run(() => Installer.Install(_options, progress));
        }
        catch (Exception ex)
        {
            _status.Text = "La instalación no se completó.";
            MessageBox.Show(this, ex.Message, "Audiora", MessageBoxButtons.OK, MessageBoxIcon.Error);
            _install.Enabled = _desktop.Enabled = _launch.Enabled = true;
            return;
        }

        _finished = true;
        _status.Text = "Listo. Audiora está instalado.";
        _install.Text = "Finalizar";
        _install.Enabled = true;
        if (_options.Launch) Process.Start(new ProcessStartInfo(Installer.ExePath(_options)) { UseShellExecute = true });
    }
}
