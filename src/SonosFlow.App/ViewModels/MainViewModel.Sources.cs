using System.Collections.ObjectModel;
using System.Windows.Input;
using System.Windows.Threading;
using SonosFlow.App.Infrastructure;
using SonosFlow.Core.Audio;
using SonosFlow.Core.Streaming;

namespace SonosFlow.App.ViewModels;

/// <summary>
/// An option whose label follows the UI language. The lists are created once and items compare by Key, so a language
/// change never empties a ComboBox selection (which would write null back into the bound setting).
/// </summary>
public sealed class ChoiceItem(string key, Func<string> label) : ObservableObject
{
    public string Key { get; } = key;
    public string Label => label();
    public void RefreshText() => OnPropertyChanged(nameof(Label));
    public override bool Equals(object? obj) => obj is ChoiceItem other && other.Key == Key;
    public override int GetHashCode() => Key.GetHashCode();
    public override string ToString() => Label;
}

/// <summary>
/// Source selection (whole PC or one application), pause, and the state shared with the mini player.
/// Both windows bind to this same view model, so there is one session and one Sonos connection.
/// </summary>
public sealed partial class MainViewModel
{
    private DispatcherTimer? _appsTimer;
    private bool _refreshingApps;

    public ObservableCollection<AppSourceItem> Apps { get; } = [];

    public ICommand UseSystemCommand { get; private set; } = null!;
    public ICommand UseAppModeCommand { get; private set; } = null!;
    public ICommand SelectAppCommand { get; private set; } = null!;
    public ICommand LaunchYouTubeMusicCommand { get; private set; } = null!;
    public ICommand PauseResumeCommand { get; private set; } = null!;
    public ICommand ToggleMiniCommand { get; private set; } = null!;
    public ICommand ShowMainCommand { get; private set; } = null!;
    public ICommand ToggleMiniSourcesCommand { get; private set; } = null!;

    /// <summary>The main window should be shown (from the mini player or the tray).</summary>
    public event Action? ShowMainRequested;

    private void InitSources()
    {
        _isAppMode = _settings.SourceMode == "app";
        _miniVisible = _settings.MiniVisible;
        _miniTopmost = _settings.MiniTopmost;
        _miniRememberPosition = _settings.MiniRememberPosition;
        _miniDimWhenIdle = _settings.MiniDimWhenIdle;
        _minimizeToMini = _settings.MinimizeToMini;
        _closeAction = _settings.CloseAction ?? (_settings.MinimizeToTray ? "tray" : "exit");
        InitSourceChoice();

        UseSystemCommand = new RelayCommand(() => { MarkSourceChosen(); IsAppMode = false; });
        UseAppModeCommand = new RelayCommand(() => { MarkSourceChosen(); IsAppMode = true; });
        SelectAppCommand = new RelayCommand(p => { if (p is AppSourceItem a) _ = SelectAppAsync(a); });
        LaunchYouTubeMusicCommand = new RelayCommand(LaunchYouTubeMusic);
        PauseResumeCommand = new RelayCommand(async () => await PauseResumeAsync(), () => CanToggle);
        ToggleMiniCommand = new RelayCommand(() => MiniVisible = !MiniVisible);
        ShowMainCommand = new RelayCommand(() => ShowMainRequested?.Invoke());
        ToggleMiniSourcesCommand = new RelayCommand(() => MiniSourcesExpanded = !MiniSourcesExpanded);

        _appsTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2.5) };
        _appsTimer.Tick += async (_, _) => await RefreshAppsAsync();
        _appsTimer.Start();
        _ = RefreshAppsAsync();
    }

    // ── Source ─────────────────────────────────────────────────

    private bool _isAppMode;
    /// <summary>False: everything the PC plays. True: only <see cref="SelectedApp"/>.</summary>
    public bool IsAppMode
    {
        get => _isAppMode;
        set
        {
            if (!Set(ref _isAppMode, value)) return;
            MarkSourceChosen(); // picking a mode (here or in the mini player) is choosing a source
            _settings.SourceMode = value ? "app" : "system";
            _settings.Save();
            SourceChangedNotify();
            _ = ApplySourceAsync();
        }
    }

    private AppSourceItem? _selectedApp;
    public AppSourceItem? SelectedApp
    {
        get => _selectedApp;
        private set
        {
            if (!Set(ref _selectedApp, value)) return;
            foreach (var a in Apps) a.IsSelected = a == value;
            SourceChangedNotify();
        }
    }

    public string SourceName => IsAppMode ? SelectedApp?.Name ?? RememberedAppName ?? L.T("source.none") : L.T("source.wholePc");

    private string? RememberedAppName => _settings.SourceAppExe is { } exe
        ? (_settings.SourceAppIsolated ? L.T("source.ytIsolated") : Path.GetFileNameWithoutExtension(exe))
        : null;

    private bool _sourceAvailable = true;
    public string SourceStatusText => !IsAppMode
        ? L.T("source.status.wholePc")
        : SelectedApp is null
            ? L.T(_settings.SourceAppExe is null ? "source.status.chooseApp" : "source.status.appNotOpen")
            : !_sourceAvailable ? L.T("source.status.appClosedWaiting")
            : L.T(SelectedApp.IsActive ? "source.app.playing" : "source.app.silent");

    /// <summary>Options fixed at stream start ("Nivel completo"). "Silenciar el PC" itself can change while streaming.</summary>
    public bool LocalOptionsEnabled => !IsStreaming;
    /// <summary>"Nivel completo" raises the Windows volume, needed only for whole-output capture (taken after that volume).</summary>
    public bool FullLevelEnabled => LocalOptionsEnabled && MuteLocal && !IsAppMode;

    private void SourceChangedNotify()
    {
        OnPropertyChanged(nameof(SourceName));
        OnPropertyChanged(nameof(SourceStatusText));
        OnPropertyChanged(nameof(LocalOptionsEnabled));
        OnPropertyChanged(nameof(FullLevelEnabled));
        OnPropertyChanged(nameof(CanToggle));
        NotifyFlow();
        CommandManager.InvalidateRequerySuggested();
    }

    private async Task SelectAppAsync(AppSourceItem app)
    {
        MarkSourceChosen();
        SelectedApp = app;
        _settings.SourceAppExe = app.Info.ExePath ?? app.Info.ExeName;
        _settings.SourceAppIsolated = app.Info.IsIsolatedYouTubeMusic;
        _settings.Save();
        if (!IsAppMode) IsAppMode = true; // also applies the source
        else await ApplySourceAsync();
    }

    private CaptureSpec? CurrentSpec() => IsAppMode
        ? SelectedApp is { } a ? CaptureSpec.Application(a.Info) : null
        : CaptureSpec.SystemOutput(SelectedOutput is { IsDefault: false } o ? o.Id : null);

    /// <summary>Switches the live stream to the selected source without reconnecting the Sonos.</summary>
    private async Task ApplySourceAsync()
    {
        if (_session is not { } session || CurrentSpec() is not { } spec) return;
        if (session.Source == spec) return;
        AppLog.Write($"cambio de fuente → {spec.DisplayName}");
        try
        {
            if (await session.SwitchSourceAsync(spec))
            {
                SourceFormatText = $"{session.CaptureDeviceName} · {session.SourceFormat}";
                _sourceAvailable = true;
                SourceChangedNotify();
                return;
            }
            // Different sample rate than the running stream: restart it once.
            await StopAsync(rememberStopped: false, reason: "cambio de fuente con otro formato");
            await StartAsync();
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidOperationException or TimeoutException or PlatformNotSupportedException)
        {
            LastError(() => L.F("errors.switchSource", spec.DisplayName, ex.Message));
            AppLog.Write($"no se pudo cambiar a «{spec.DisplayName}»: {ex.Message}");
        }
    }

    private async Task RefreshAppsAsync()
    {
        if (_refreshingApps || _simulateApps) return;
        _refreshingApps = true;
        try
        {
            IReadOnlyList<AudioAppInfo> found;
            // An app left redirected (crash, or a restore Windows refused) is restored as soon as Windows accepts it
            // (it needs a process of that app with audio). Live redirects, this one's or another Audiora's, are skipped.
            if (File.Exists(AppPaths.GuardStateFile)
                && await Task.Run(() => LocalOutputGuard.RecoverPending(AppPaths.GuardStateFile)))
                AppLog.Write("salida local restaurada después de volver a estar disponible");
            if (File.Exists(RedirectStateFile))
            {
                try { await Task.Run(() => AppOutputRedirect.RecoverPending(RedirectStateFile)); }
                catch (Exception ex) when (AppOutputRedirect.IsRoutingFailure(ex)) { AppLog.Write($"restaurar salida de app: {ex.GetType().Name}"); }
            }
            try { found = await Task.Run(AudioApps.List); }
            catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidOperationException) { return; }
            if (_simulateApps) return; // capture modes: the apps on screen are the simulated ones

            // Two root processes of the same app (e.g. Brave with two profiles) share a key: keep the first.
            var byKey = found.GroupBy(a => AppSourceItem.Key_(a.ExePath ?? a.ExeName, a.IsIsolatedYouTubeMusic))
                             .ToDictionary(g => g.Key, g => g.First());
            foreach (var gone in Apps.Where(a => !byKey.ContainsKey(a.Key)).ToList()) Apps.Remove(gone);
            int index = 0;
            foreach (var (key, info) in byKey)
            {
                var existing = Apps.FirstOrDefault(a => a.Key == key);
                if (existing is null) Apps.Insert(Math.Min(index, Apps.Count), new AppSourceItem(info));
                else existing.Info = info;
                index++;
            }

            // Keep the remembered application selected (it may have restarted with a new PID).
            var wanted = _settings.SourceAppExe is { } exe ? AppSourceItem.Key_(exe, _settings.SourceAppIsolated) : null;
            SelectedApp = wanted is null ? null : Apps.FirstOrDefault(a => a.Key == wanted);
            SourceChangedNotify();
        }
        finally
        {
            _refreshingApps = false;
        }
    }

    private void LaunchYouTubeMusic()
    {
        try
        {
            if (AudioApps.LaunchIsolatedYouTubeMusic() is null)
            {
                LastError(() => L.T("errors.noBrowserForYouTubeMusic"));
                return;
            }
            // Pre-select it: it appears in the list as soon as it starts playing.
            _settings.SourceAppExe = AudioApps.IsolationBrowserPath();
            _settings.SourceAppIsolated = true;
            _settings.Save();
            MarkSourceChosen();
            if (!IsAppMode) IsAppMode = true;
            SourceChangedNotify();
            AppLog.Write("YouTube Music aislado abierto");
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            LastError(() => L.F("errors.youTubeMusicLaunch", ex.Message));
        }
    }

    // ── Pause ──────────────────────────────────────────────────

    private bool _isPaused;
    /// <summary>Streaming continues but sends silence; the Sonos stays connected so resuming is instant.</summary>
    public bool IsPaused
    {
        get => _isPaused;
        private set { if (Set(ref _isPaused, value)) OnPropertyChanged(nameof(MiniStatusText)); }
    }

    private async Task PauseResumeAsync()
    {
        if (!IsStreaming)
        {
            await StartAsync();
            return;
        }
        IsPaused = !IsPaused;
        _session?.SetPaused(IsPaused);
        AppLog.Write(IsPaused ? "pausa" : "reanudar");
    }

    public string MiniStatusText => State switch
    {
        UiState.Streaming when IsPaused => L.T("status.mini.paused"),
        UiState.Streaming => L.T("status.mini.streaming"),
        UiState.Starting => L.T("status.title.starting"),
        UiState.Recovering => L.T("status.title.recovering"),
        UiState.Error => L.T("status.mini.error"),
        UiState.Discovering => L.T("status.title.discovering"),
        UiState.NoSpeaker => L.T("status.mini.noSpeaker"),
        _ => L.T("status.title.ready"),
    };

    // ── Mini player state and settings ─────────────────────────

    private bool _miniVisible;
    public bool MiniVisible { get => _miniVisible; set { if (Set(ref _miniVisible, value)) { _settings.MiniVisible = value; _settings.Save(); } } }

    private bool _miniSourcesExpanded;
    public bool MiniSourcesExpanded { get => _miniSourcesExpanded; set => Set(ref _miniSourcesExpanded, value); }

    private bool _miniTopmost;
    public bool MiniTopmost { get => _miniTopmost; set { if (Set(ref _miniTopmost, value)) { _settings.MiniTopmost = value; _settings.Save(); } } }

    private bool _miniRememberPosition;
    public bool MiniRememberPosition { get => _miniRememberPosition; set { if (Set(ref _miniRememberPosition, value)) { _settings.MiniRememberPosition = value; _settings.Save(); } } }

    private bool _miniDimWhenIdle;
    public bool MiniDimWhenIdle { get => _miniDimWhenIdle; set { if (Set(ref _miniDimWhenIdle, value)) { _settings.MiniDimWhenIdle = value; _settings.Save(); } } }

    private bool _minimizeToMini;
    public bool MinimizeToMini { get => _minimizeToMini; set { if (Set(ref _minimizeToMini, value)) { _settings.MinimizeToMini = value; _settings.Save(); } } }

    public IReadOnlyList<ChoiceItem> CloseActions { get; } =
    [
        new("tray", () => L.T("choices.close.tray")),
        new("mini", () => L.T("choices.close.mini")),
        new("exit", () => L.T("choices.close.exit")),
    ];

    private string _closeAction = "tray";
    public string CloseAction
    {
        get => _closeAction;
        set
        {
            if (value is null || !Set(ref _closeAction, value)) return;
            _settings.CloseAction = value;
            _settings.MinimizeToTray = value != "exit"; // keep the older switch consistent
            _settings.Save();
        }
    }

    public (double? Left, double? Top) MiniPosition => (_settings.MiniLeft, _settings.MiniTop);

    public void SaveMiniPosition(double left, double top)
    {
        if (!MiniRememberPosition) return;
        _settings.MiniLeft = left;
        _settings.MiniTop = top;
        _settings.Save();
    }

    /// <summary>Quick presets for the margin; any other value (the slider) shows as "Custom".</summary>
    public IReadOnlyList<ChoiceItem> PrebufferChoices { get; } =
    [
        new("0", () => L.T("choices.prebuffer.none")),
        new("100", () => L.F("choices.prebuffer.ms", 100)),
        new("300", () => L.F("choices.prebuffer.ms", 300)),
        new("1000", () => L.T("choices.prebuffer.recommended")),
        new(PrebufferScale.CustomKey, () => L.T("choices.prebuffer.custom")),
    ];

    /// <summary>Diagnostics only (<c>--prebuffer-ms</c>): a margin outside the list above for this run; never saved to the settings.</summary>
    internal int? PrebufferOverrideMs { get; set; }

    /// <summary>"Margen anti-cortes", applied when a stream starts.</summary>
    public int PrebufferMs
    {
        get => _settings.PrebufferMs;
        set
        {
            if (!ApplyPrebuffer(value)) return;
            _prebufferSaveDebounce?.Stop();
            _settings.Save();
        }
    }

    /// <summary>
    /// The slider's value: snapped to the step, and capped at the slider's end (older settings may hold up to 3 s). Every step
    /// of a drag is applied and shown at once; the settings file is written once the drag settles (like the effects volume).
    /// </summary>
    public double PrebufferSliderMs
    {
        get => PrebufferScale.ToSlider(PrebufferMs);
        set
        {
            if (!ApplyPrebuffer(PrebufferScale.FromSlider(value))) return;
            _prebufferSaveDebounce ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
            _prebufferSaveDebounce.Tick -= SavePrebuffer;
            _prebufferSaveDebounce.Tick += SavePrebuffer;
            _prebufferSaveDebounce.Stop();
            _prebufferSaveDebounce.Start();
        }
    }

    private DispatcherTimer? _prebufferSaveDebounce;

    private void SavePrebuffer(object? sender, EventArgs e) { _prebufferSaveDebounce?.Stop(); _settings.Save(); }

    /// <summary>Writes a margin chosen with the slider just before closing (the debounce would otherwise lose it).</summary>
    internal void FlushPendingPrebufferSave()
    {
        if (_prebufferSaveDebounce is { IsEnabled: true }) SavePrebuffer(null, EventArgs.Empty);
    }

    /// <summary>Sets the margin in memory (the next stream uses it) and refreshes everything that shows it. False if unchanged.</summary>
    private bool ApplyPrebuffer(int value)
    {
        value = Math.Max(0, value);
        if (_settings.PrebufferMs == value) return false;
        _settings.PrebufferMs = value;
        OnPropertyChanged(nameof(PrebufferMs));
        OnPropertyChanged(nameof(PrebufferKey));
        OnPropertyChanged(nameof(PrebufferSliderMs));
        OnPropertyChanged(nameof(PrebufferText));
        return true;
    }

    /// <summary>The exact value in use ("300 ms"), shown next to the slider.</summary>
    public string PrebufferText => L.F("choices.prebuffer.ms", PrebufferMs);

    public string PrebufferKey
    {
        get
        {
            return PrebufferScale.KeyFor(PrebufferMs);
        }
        set
        {
            if (int.TryParse(value, out var ms)) PrebufferMs = ms;
            else _ui.BeginInvoke(() => OnPropertyChanged(nameof(PrebufferKey)));   // "Custom" is only a label: the combo goes back to what is in use
        }
    }

    /// <summary>Latest real statistics from the running session (null when stopped).</summary>
    public StreamStats? LastStats { get; private set; }

    internal void RememberStats(StreamStats s) => LastStats = s;

    internal Task RefreshAppsNowAsync() => RefreshAppsAsync();

    internal Task SelectAppForTestAsync(AppSourceItem app) => SelectAppAsync(app);

    /// <summary>One-line connection status for the tray menu.</summary>
    public string TrayStatusText => SelectedSpeaker is null
        ? L.T("status.tray.noSonos")
        : $"{SelectedSpeaker.Name}: {ConnectionText} · {MiniStatusText}" + (IsStreaming ? $" · {SourceName}" : "");
}
