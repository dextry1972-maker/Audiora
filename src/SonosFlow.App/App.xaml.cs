using System.ComponentModel;
using System.Windows;
using System.Windows.Threading;
using SonosFlow.App.Infrastructure;
using SonosFlow.App.ViewModels;

namespace SonosFlow.App;

/// <summary>
/// Command line:
///   --minimized            start hidden in the tray (used by "start with Windows")
///   --uninstall            remove shortcuts, HKCU entries and the program folder
///   --autostart            start streaming as soon as a speaker is found
///   --exit-after N         quit after N seconds (testing)
///   --screenshot FILE      save a PNG of the window after loading and quit (testing)
///   --page settings        open on the settings page
///   --size WxH             window size for --screenshot
/// </summary>
public partial class App : Application
{
    private const string MutexName = @"Local\SonosFlow.SingleInstance";
    private const string ShowEventName = @"Local\SonosFlow.Show";

    private Mutex? _mutex;
    private EventWaitHandle? _showEvent;
    private MainViewModel? _vm;
    private MainWindow? _window;
    private TrayIcon? _tray;
    private bool _quitting;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var args = e.Args;
        string? Arg(string name) { int i = Array.IndexOf(args, name); return i >= 0 && i + 1 < args.Length ? args[i + 1] : null; }
        DispatcherUnhandledException += OnUnhandled;

        if (args.Contains("--uninstall"))
        {
            RunUninstall();
            return;
        }

        string? screenshot = Arg("--screenshot");
        if (screenshot is null && !AcquireSingleInstance()) return;

        AppLog.Write($"app iniciada{(args.Length > 0 ? " " + string.Join(' ', args) : "")}");
        var settings = AppSettings.Load();
        _vm = new MainViewModel(settings);
        _window = new MainWindow(_vm);
        _window.Closing += OnWindowClosing;
        if (Arg("--page") is { } page) _vm.Page = page;
        if (screenshot is not null && Arg("--size")?.Split('x') is [var w, var h] && double.TryParse(w, out var width) && double.TryParse(h, out var height))
        {
            _window.Width = width;
            _window.Height = height;
        }

        if (screenshot is null)
        {
            _tray = new TrayIcon(ShowWindow, () => _vm.ToggleStreamCommand.Execute(null), () => _ = QuitAsync("Salir desde la bandeja"));
            _vm.StreamingChanged += () => _tray.SetStreaming(_vm.IsStreaming, _vm.SelectedSpeaker?.Name ?? "");
        }

        if (!args.Contains("--minimized")) _window.Show();

        await _vm.InitializeAsync(autoStart: args.Contains("--autostart"));

        if (screenshot is not null)
        {
            await Task.Delay(int.TryParse(Arg("--delay"), out var ms) ? ms : 1500);
            _window.SaveScreenshot(screenshot);
            await QuitAsync("fin del modo captura");
            return;
        }

        if (int.TryParse(Arg("--exit-after"), out var seconds))
        {
            await Task.Delay(TimeSpan.FromSeconds(seconds));
            await QuitAsync("--exit-after");
        }
    }

    private bool AcquireSingleInstance()
    {
        _mutex = new Mutex(true, MutexName, out bool first);
        if (!first)
        {
            // Bring the running copy to the front instead of starting a second stream.
            try { EventWaitHandle.OpenExisting(ShowEventName).Set(); } catch (WaitHandleCannotBeOpenedException) { }
            Shutdown();
            return false;
        }
        _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
        var wait = new Thread(() =>
        {
            while (_showEvent.WaitOne())
                Dispatcher.BeginInvoke(ShowWindow);
        }) { IsBackground = true };
        wait.Start();
        return true;
    }

    private void ShowWindow()
    {
        if (_window is null) return;
        _window.Show();
        if (_window.WindowState == WindowState.Minimized) _window.WindowState = WindowState.Normal;
        _window.Activate();
    }

    private void OnWindowClosing(object? sender, CancelEventArgs e)
    {
        if (_quitting) return;
        e.Cancel = true;
        if (_vm?.MinimizeToTray == true && _tray is not null) _window!.Hide();
        else _ = QuitAsync("ventana cerrada");
    }

    private async Task QuitAsync(string reason)
    {
        if (_quitting) return;
        _quitting = true;
        AppLog.Write($"salida de la app: {reason}");
        if (_vm is not null) await _vm.ShutdownAsync(); // stops the stream and restores the PC speakers
        _tray?.Dispose();
        _window?.Close();
        Shutdown();
    }

    protected override void OnSessionEnding(SessionEndingCancelEventArgs e)
    {
        base.OnSessionEnding(e);
        _ = QuitAsync("cierre de sesión de Windows");
    }

    private void RunUninstall()
    {
        var answer = MessageBox.Show(
            "¿Desinstalar Audiora?\n\nSe eliminarán el programa y sus accesos directos. Tus ajustes y registros se conservan.",
            "Audiora", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (answer == MessageBoxResult.Yes)
        {
            WindowsIntegration.Uninstall();
            MessageBox.Show("Audiora se ha desinstalado.", "Audiora", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        Shutdown();
    }

    private void OnUnhandled(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(AppPaths.Logs);
            File.AppendAllText(Path.Combine(AppPaths.Logs, "errors.log"), $"[{DateTime.Now:O}] {e.Exception}\n\n");
        }
        catch (IOException) { }
        MessageBox.Show($"Se produjo un error inesperado:\n{e.Exception.Message}\n\nDetalles en la carpeta de registros.", "Audiora",
            MessageBoxButton.OK, MessageBoxImage.Warning);
        e.Handled = true;
    }
}
