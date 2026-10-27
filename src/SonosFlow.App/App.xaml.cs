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
///   --screenshot-mini FILE save a PNG of the mini player (over a light backdrop) and quit (testing)
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
    private MiniPlayerWindow? _mini;
    private GlobalHotkeys? _hotkeys;
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
        string? miniShot = Arg("--screenshot-mini");
        bool testCapture = screenshot is not null || miniShot is not null || Arg("--selftest-session") is not null;
        if (!testCapture && !AcquireSingleInstance()) return;
        if (testCapture) AppSettings.PersistenceEnabled = false;

        AppLog.Write($"app iniciada{(args.Length > 0 ? " " + string.Join(' ', args) : "")}");
        var settings = AppSettings.Load();
        _vm = new MainViewModel(settings);
        _window = new MainWindow(_vm);
        _window.Closing += OnWindowClosing;
        _window.StateChanged += OnMainStateChanged;
        _mini = new MiniPlayerWindow(_vm);
        _vm.ShowMainRequested += ShowWindow;
        _vm.PropertyChanged += (_, pe) => { if (pe.PropertyName == nameof(MainViewModel.MiniVisible)) ApplyMiniVisibility(); };
        if (Arg("--page") is { } page) _vm.Page = page;
        if (testCapture && Arg("--size")?.Split('x') is [var w, var h] && double.TryParse(w, out var width) && double.TryParse(h, out var height))
        {
            _window.Width = width;
            _window.Height = height;
        }

        if (!testCapture)
        {
            _tray = new TrayIcon(ShowWindow, () => _vm.ToggleStreamCommand.Execute(null), () => _vm.MiniVisible = !_vm.MiniVisible,
                () => _vm.TrayStatusText, () => _vm.MiniVisible, () => _ = QuitAsync("Salir desde la bandeja"));
            _vm.StreamingChanged += () => _tray.SetStreaming(_vm.IsStreaming, _vm.SelectedSpeaker?.Name ?? "");
            // Not in test runs: a second copy must not take the shortcuts from the user's running Audiora.
            _hotkeys = new GlobalHotkeys(_vm.HandleHotkey);
            _vm.HotkeysSettingChanged += ApplyHotkeys;
            ApplyHotkeys();
        }

        if (!args.Contains("--minimized") && miniShot is null) _window.Show();
        if (miniShot is not null) _vm.MiniVisible = true;
        ApplyMiniVisibility();

        await _vm.InitializeAsync(autoStart: args.Contains("--autostart"));

        if (int.TryParse(Arg("--selftest-session"), out var tonePid) && Arg("--selftest-out") is { } outFile)
        {
            try { await SelfTest.RunAsync(_vm, tonePid, outFile); }
            finally { await QuitAsync("fin del autotest"); } // always stop the stream and restore the PC
            return;
        }

        if (miniShot is not null)
        {
            await Task.Delay(int.TryParse(Arg("--delay"), out var mdelay) ? mdelay : 1500);
            if (args.Contains("--expand")) { _vm.MiniSourcesExpanded = true; await Task.Delay(400); }
            try
            {
                _mini.SaveScreenshot(miniShot, System.Windows.Media.Color.FromRgb(0xEE, 0xF1, 0xF4));
                AppLog.Write($"captura mini: visible={_mini.IsVisible} topmost={_mini.Topmost} opacidad={_mini.Opacity:0.00} pos=({_mini.Left:0},{_mini.Top:0})");
            }
            catch (ArgumentException ex) { AppLog.Write($"captura mini fallida: {ex.Message}"); }
            finally { await QuitAsync("fin del modo captura"); }
            return;
        }

        if (screenshot is not null)
        {
            await Task.Delay(int.TryParse(Arg("--delay"), out var ms) ? ms : 1500);
            // A test capture must never leave the stream (and a muted PC) running, even if it fails.
            try { _window.SaveScreenshot(screenshot); }
            catch (ArgumentException ex) { AppLog.Write($"captura fallida (¿ventana oculta?): {ex.Message}"); }
            finally { await QuitAsync("fin del modo captura"); }
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

    /// <summary>Shows or hides the mini player. Never touches the stream.</summary>
    private void ApplyMiniVisibility()
    {
        if (_mini is null || _vm is null || _quitting) return;
        if (_vm.MiniVisible && !_mini.IsVisible) _mini.ShowAtSavedPosition();
        else if (!_vm.MiniVisible && _mini.IsVisible) { _mini.Hide(); _vm.MiniSourcesExpanded = false; }
    }

    private void ApplyHotkeys()
    {
        if (_hotkeys is null || _vm is null) return;
        if (!_vm.HotkeysEnabled)
        {
            _hotkeys.Disable();
            _vm.HotkeysStatus = "";
            return;
        }
        var taken = _hotkeys.Enable();
        _vm.HotkeysStatus = taken.Count == 0 ? "" : $"En uso por otro programa (no disponibles): {string.Join(", ", taken)}";
        if (taken.Count > 0) AppLog.Write($"atajos no registrados: {string.Join(", ", taken)}");
    }

    private void OnMainStateChanged(object? sender, EventArgs e)
    {
        // "Al minimizar: mostrar el mini reproductor": hide the main window instead of the taskbar button.
        if (_window?.WindowState != WindowState.Minimized || _vm?.MinimizeToMini != true) return;
        _window.Hide();
        _window.WindowState = WindowState.Normal;
        _vm.MiniVisible = true;
    }

    private void OnWindowClosing(object? sender, CancelEventArgs e)
    {
        if (_quitting) return;
        e.Cancel = true;
        switch (_vm?.CloseAction)
        {
            case "mini":
                _window!.Hide();
                _vm.MiniVisible = true;
                break;
            case "tray" when _tray is not null:
                _window!.Hide();
                break;
            default:
                _ = QuitAsync("ventana cerrada");
                break;
        }
    }

    private async Task QuitAsync(string reason)
    {
        if (_quitting) return;
        _quitting = true;
        AppLog.Write($"salida de la app: {reason}");
        if (_vm is not null) await _vm.ShutdownAsync(); // stops the stream and restores the PC speakers
        _tray?.Dispose();
        _hotkeys?.Dispose();
        _mini?.CloseForShutdown();
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
