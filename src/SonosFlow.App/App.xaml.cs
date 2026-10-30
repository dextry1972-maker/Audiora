using System.ComponentModel;
using System.Windows;
using System.Windows.Threading;
using SonosFlow.App.Infrastructure;
using SonosFlow.App.Localization;
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
///   --screenshot-hud FILE  save a PNG of the volume indicator and quit (testing)
///   --delay MS, --expand   wait before a screenshot; expand the mini player's source list
///   --selftest-session PID --selftest-out FILE   end-to-end check with the real Sonos (see SelfTest)
/// Test modes never take the single-instance mutex, register hotkeys, save settings or resume a stream.
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
    private VolumeHudWindow? _hud;
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
        string? hudShot = Arg("--screenshot-hud");
        string? welcomeShot = Arg("--screenshot-welcome");
        bool testCapture = screenshot is not null || miniShot is not null || hudShot is not null || welcomeShot is not null
                           || Arg("--selftest-session") is not null;
        if (!testCapture && !AcquireSingleInstance()) return;
        if (testCapture) AppSettings.PersistenceEnabled = false;

        AppLog.Write($"app iniciada{(args.Length > 0 ? " " + string.Join(' ', args) : "")}");
        var settings = AppSettings.Load();

        if (welcomeShot is not null)
        {
            if (Arg("--lang") is { } previewLang) Loc.Instance.SetLanguage(previewLang);
            var welcome = new Views.LanguageWindow(Arg("--lang") ?? Languages.SuggestFromSystem());
            welcome.Show();
            await Task.Delay(1200);
            try { welcome.SaveScreenshot(welcomeShot); } finally { welcome.Close(); Shutdown(); }
            return;
        }
        ChooseLanguage(settings, forced: Arg("--lang"), allowWelcome: !testCapture);
        Loc.Instance.LanguageChanged += ShowHotkeysStatus; // only the "in use" note is text: no re-registration
        _vm = new MainViewModel(settings);
        _window = new MainWindow(_vm);
        _window.Closing += OnWindowClosing;
        _window.StateChanged += OnMainStateChanged;
        _mini = new MiniPlayerWindow(_vm);
        _vm.ShowMainRequested += ShowWindow;
        _vm.HudRequested += (glyph, text, fraction) => { if (!_quitting) (_hud ??= new VolumeHudWindow()).Flash(glyph, text, fraction); };
        _vm.PropertyChanged += (_, pe) => { if (pe.PropertyName == nameof(MainViewModel.MiniVisible)) ApplyMiniVisibility(); };
        if (Arg("--page") is { } page) _vm.Page = page;
        if (testCapture && Arg("--mode") == "system") _vm.IsAppMode = false; // captures of the live look (settings are not saved in test modes)
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

        // The HUD capture needs no speaker; other test modes never resume a stream from the saved settings.
        if (hudShot is null) await _vm.InitializeAsync(autoStart: args.Contains("--autostart"), allowResume: !testCapture);

        if (int.TryParse(Arg("--selftest-session"), out var tonePid) && Arg("--selftest-out") is { } outFile)
        {
            try { await SelfTest.RunAsync(_vm, tonePid, outFile); }
            finally { await QuitAsync("fin del autotest"); } // always stop the stream and restore the PC
            return;
        }

        if (hudShot is not null)
        {
            try
            {
                _hud ??= new VolumeHudWindow();
                _hud.Flash("", "Bedroom · 45 %", 0.45);
                await Task.Delay(300);
                _hud.SaveScreenshot(hudShot);
            }
            finally { await QuitAsync("fin del modo captura"); }
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

    /// <summary>
    /// Starting language: the welcome screen only on a genuinely fresh install; otherwise the saved language,
    /// Spanish for settings from before languages existed, or the nearest supported one for an invalid value.
    /// Other settings are never touched.
    /// </summary>
    private static void ChooseLanguage(AppSettings settings, string? forced, bool allowWelcome)
    {
        if (forced is not null) // test and screenshot runs: preview a language without saving anything
        {
            Loc.Instance.SetLanguage(forced);
            return;
        }
        // An unreadable file lost its settings anyway (a copy is kept): ask again instead of forcing Spanish.
        var decision = LanguageStartup.Decide(AppSettings.LastLoadFoundFile && !AppSettings.LastLoadUnreadable, settings.Language, settings.LanguageOnboardingDone,
            Languages.SuggestFromSystem());
        string language = decision.Language;
        if (decision.ShowWelcome && allowWelcome)
        {
            Loc.Instance.SetLanguage(decision.Language);
            language = Views.LanguageWindow.Ask(decision.Language) ?? decision.Language;
        }
        Loc.Instance.SetLanguage(language);
        if (decision.ShowWelcome || decision.MustPersist)
        {
            settings.Language = Loc.Instance.Code;
            settings.LanguageOnboardingDone = true;
            settings.Save(); // no-op in test runs
        }
        AppLog.Write($"idioma: {Loc.Instance.Code} ({decision.Kind})");
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
            _hotkeysTaken = [];
            ShowHotkeysStatus();
            return;
        }
        var taken = _hotkeys.Enable();
        _hotkeysTaken = taken;
        ShowHotkeysStatus();
        if (taken.Count > 0) AppLog.Write($"atajos no registrados: {string.Join(", ", taken)}");
    }

    private IReadOnlyList<string> _hotkeysTaken = [];

    private void ShowHotkeysStatus()
    {
        if (_vm is null) return;
        _vm.HotkeysStatus = _hotkeysTaken.Count == 0 ? "" : Loc.Instance.F("hotkeys.taken", string.Join(", ", _hotkeysTaken));
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
        _hud?.Close();
        _mini?.CloseForShutdown();
        _window?.Close();
        Shutdown();
    }

    protected override void OnSessionEnding(SessionEndingCancelEventArgs e)
    {
        base.OnSessionEnding(e);
        // Windows ends the process soon after this returns: restore the PC first, on a pool thread that never needs the UI
        // thread (so waiting here can't deadlock), then run the normal quit.
        if (_vm is { } vm)
        {
            bool done = false;
            try { done = Task.Run(vm.RestoreLocalForShutdown).Wait(TimeSpan.FromSeconds(5)); } // lock 1 s + state mutex ≤ 3 s
            catch (AggregateException) { }
            AppLog.Write(done ? "cierre de sesión de Windows: salida del PC restaurada" : "cierre de sesión de Windows: la restauración no terminó a tiempo (queda pendiente para el próximo arranque)");
        }
        _ = QuitAsync("cierre de sesión de Windows");
    }

    private void RunUninstall()
    {
        var saved = AppSettings.Load();
        Loc.Instance.SetLanguage(Languages.Resolve(saved.Language) ?? (AppSettings.LastLoadFoundFile ? Languages.Source : Languages.SuggestFromSystem()));
        var answer = MessageBox.Show(Loc.Instance.T("app.uninstall.confirm"), Loc.Instance.T("app.uninstall.title"),
            MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (answer == MessageBoxResult.Yes)
        {
            WindowsIntegration.Uninstall();
            MessageBox.Show(Loc.Instance.T("app.uninstall.done"), Loc.Instance.T("app.uninstall.title"), MessageBoxButton.OK, MessageBoxImage.Information);
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
        MessageBox.Show(Loc.Instance.F("app.unexpectedError", e.Exception.Message), "Audiora",
            MessageBoxButton.OK, MessageBoxImage.Warning);
        e.Handled = true;
    }
}
