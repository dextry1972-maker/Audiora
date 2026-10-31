using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Net.Sockets;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using SonosFlow.App.Infrastructure;
using SonosFlow.Core.Audio;
using SonosFlow.Core.Sonos;
using SonosFlow.Core.Streaming;

namespace SonosFlow.App.ViewModels;

public enum UiState { Discovering, NoSpeaker, Ready, Starting, Streaming, Recovering, Error }

public sealed partial class MainViewModel : ObservableObject
{
    private readonly AppSettings _settings;
    private readonly Dispatcher _ui = Application.Current.Dispatcher;
    private readonly DispatcherTimer _poll;
    private readonly DispatcherTimer _volumeDebounce;
    private readonly DispatcherTimer _eqDebounce;

    private StreamingSession? _session;
    private PendingStart? _starting;
    private Task? _abortingStart;
    private bool _lastStartRetryable;
    private SonosClient? _control;
    private DateTime _lastUserVolumeChange = DateTime.MinValue;
    private DateTime _lastUserEqChange = DateTime.MinValue;
    private bool _applyingRemote;

    public MainViewModel(AppSettings settings)
    {
        _settings = settings;
        _muteLocal = settings.MuteLocal;
        _fullLevel = settings.FullLevel;
        _minimizeToTray = settings.MinimizeToTray;
        _resumeOnLaunch = settings.ResumeOnLaunch;
        Backups = new BackupViewModel(settings);

        _poll = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _poll.Tick += async (_, _) => await PollSpeakerAsync();
        _volumeDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(120) };
        _volumeDebounce.Tick += async (_, _) => { _volumeDebounce.Stop(); await SafeControlAsync(c => c.SetVolumeAsync(Volume)); };
        _eqDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
        _eqDebounce.Tick += async (_, _) =>
        {
            _eqDebounce.Stop();
            await SafeControlAsync(async c =>
            {
                await c.SetBassAsync(Bass);
                await c.SetTrebleAsync(Treble);
                await c.SetLoudnessAsync(Loudness);
            });
        };

        ToggleStreamCommand = new RelayCommand(async () => { CancelStartupWait(); await ToggleStreamAsync(); }, () => CanToggle);
        RescanCommand = new RelayCommand(async () => { CancelStartupWait(); await DiscoverAsync(); }, () => State is not (UiState.Discovering or UiState.Starting));
        SelectSpeakerCommand = new RelayCommand(p => { if (p is SpeakerItem s) { CancelStartupWait(); Select(s); } }, _ => !IsStreaming);
        NavigateCommand = new RelayCommand(p =>
        {
            var before = Page;
            Page = p as string ?? "home";
            if (Page != before) SoundNavigated(before); // only a click on a tab ticks, not a programmatic page change
        });
        OpenLogsCommand = new RelayCommand(() =>
        {
            Directory.CreateDirectory(AppPaths.Logs);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{AppPaths.Logs}\"") { UseShellExecute = true });
        });
        RefreshOutputsCommand = new RelayCommand(LoadOutputs, () => !IsStreaming);
        ResetEqCommand = new RelayCommand(() => { Bass = 0; Treble = 0; Loudness = true; });

        LoadOutputs();
        InitSources();
        InitMedia();
        Localization.Loc.Instance.LanguageChanged += OnLanguageChanged;
    }

    public BackupViewModel Backups { get; }

    // ── Collections ─────────────────────────────────────────────
    public ObservableCollection<SpeakerItem> Speakers { get; } = [];
    public ObservableCollection<OutputDeviceInfo> Outputs { get; } = [];

    // ── Commands ────────────────────────────────────────────────
    public ICommand ToggleStreamCommand { get; }
    public ICommand RescanCommand { get; }
    public ICommand SelectSpeakerCommand { get; }
    public ICommand NavigateCommand { get; }
    public ICommand OpenLogsCommand { get; }
    public ICommand RefreshOutputsCommand { get; }
    public ICommand ResetEqCommand { get; }

    /// <summary>Raised when streaming starts/stops so the tray icon can follow.</summary>
    public event Action? StreamingChanged;

    // ── State ───────────────────────────────────────────────────
    private UiState _state = UiState.Discovering;
    public UiState State
    {
        get => _state;
        private set
        {
            if (!Set(ref _state, value)) return;
            OnPropertyChanged(nameof(IsStreaming));
            OnPropertyChanged(nameof(IsActive));
            OnPropertyChanged(nameof(StatusTitle));
            OnPropertyChanged(nameof(StreamButtonText));
            OnPropertyChanged(nameof(CanToggle));
            OnPropertyChanged(nameof(CanEditOptions));
            OnPropertyChanged(nameof(CanEditFullLevel));
            OnPropertyChanged(nameof(LocalOptionsEnabled));
            OnPropertyChanged(nameof(FullLevelEnabled));
            OnPropertyChanged(nameof(MiniStatusText));
            OnPropertyChanged(nameof(HasHotspotPrompt));
            CommandManager.InvalidateRequerySuggested();
            foreach (var s in Speakers) s.IsStreaming = s.IsSelected && IsStreaming;
            StreamingChanged?.Invoke();
            SoundStateReached(value);
        }
    }

    public bool IsStreaming => State is UiState.Streaming or UiState.Recovering or UiState.Starting;
    /// <summary>Audio is flowing (drives the "live" look).</summary>
    public bool IsActive => State is UiState.Streaming;
    public bool CanToggle => SelectedSpeaker is not null && State is UiState.Ready or UiState.Streaming or UiState.Recovering or UiState.Error
                             && (IsStreaming || !IsAppMode || SelectedApp is not null);

    public string StatusTitle => State switch
    {
        UiState.Discovering => L.T("status.title.discovering"),
        UiState.NoSpeaker => L.T("status.title.noSpeaker"),
        UiState.Ready => L.T("status.title.ready"),
        UiState.Starting => L.T("status.title.starting"),
        UiState.Streaming => L.T("status.title.streaming"),
        UiState.Recovering => L.T("status.title.recovering"),
        UiState.Error => L.T("status.title.error"),
        _ => "",
    };

    public string StreamButtonText => L.T(IsStreaming ? "status.button.stop" : "status.button.start");

    // Stored as "how to build the text", so a language change also translates the message already on screen.
    private Func<string> _statusDetail = () => L.T("status.detail.searching");
    public string StatusDetail => _statusDetail();
    private void Detail(Func<string> text) { _statusDetail = text; OnPropertyChanged(nameof(StatusDetail)); }

    private string _page = "home";
    public string Page { get => _page; set => Set(ref _page, value); }

    // ── Speaker ─────────────────────────────────────────────────
    private SpeakerItem? _selectedSpeaker;
    public SpeakerItem? SelectedSpeaker
    {
        get => _selectedSpeaker;
        private set
        {
            if (!Set(ref _selectedSpeaker, value)) return;
            OnPropertyChanged(nameof(HasSpeaker));
            OnPropertyChanged(nameof(CanToggle));
        }
    }
    public bool HasSpeaker => SelectedSpeaker is not null;

    private bool _speakerOnline;
    public bool SpeakerOnline { get => _speakerOnline; private set { if (Set(ref _speakerOnline, value)) OnPropertyChanged(nameof(ConnectionText)); } }
    public string ConnectionText => SelectedSpeaker is null ? "—" : L.T(SpeakerOnline ? "status.connection.online" : "status.connection.offline");

    private int _volume;
    public int Volume
    {
        get => _volume;
        set
        {
            if (!Set(ref _volume, Math.Clamp(value, 0, 100)) || _applyingRemote) return;
            _lastUserVolumeChange = DateTime.UtcNow;
            _volumeDebounce.Stop();
            _volumeDebounce.Start();
        }
    }

    private bool _controlsReady;
    public bool ControlsReady { get => _controlsReady; private set => Set(ref _controlsReady, value); }

    private int _bass;
    public int Bass { get => _bass; set { if (Set(ref _bass, value)) EqChanged(); } }
    private int _treble;
    public int Treble { get => _treble; set { if (Set(ref _treble, value)) EqChanged(); } }
    private bool _loudness;
    public bool Loudness { get => _loudness; set { if (Set(ref _loudness, value)) EqChanged(); } }

    private void EqChanged()
    {
        if (_applyingRemote) return;
        _lastUserEqChange = DateTime.UtcNow;
        _eqDebounce.Stop();
        _eqDebounce.Start();
    }

    // ── Source / options ────────────────────────────────────────
    private OutputDeviceInfo? _selectedOutput;
    public OutputDeviceInfo? SelectedOutput
    {
        get => _selectedOutput;
        set
        {
            if (!Set(ref _selectedOutput, value)) return;
            _settings.CaptureDeviceId = value is { IsDefault: false } ? value.Id : null;
            _settings.Save();
        }
    }

    private bool _muteLocal;
    public bool MuteLocal
    {
        get => _muteLocal;
        set
        {
            if (!Set(ref _muteLocal, value)) return;
            _settings.MuteLocal = value;
            if (!value) FullLevel = false; // full level is only safe while the PC is muted
            _settings.Save();
            OnPropertyChanged(nameof(CanEditFullLevel));
            OnPropertyChanged(nameof(FullLevelEnabled));
            if ((_session ?? _starting?.Session) is { } session) _ = ApplyMuteLiveAsync(session, value);
        }
    }

    /// <summary>
    /// While streaming, "Silenciar el PC" applies at once: off → the app (or the PC) is heard locally again on
    /// whatever output Windows uses now; on → it goes quiet again. The stream and the Sonos are not touched.
    /// </summary>
    private async Task ApplyMuteLiveAsync(StreamingSession session, bool mute)
    {
        bool applied;
        try { applied = await session.SetMuteLocalAsync(mute); }
        catch (Exception ex) // fire-and-forget from the setter: never leave it unobserved
        {
            AppLog.Write($"silenciar el PC durante la transmisión: {ex.GetType().Name}");
            applied = false;
        }
        if (!session.IsActive) return; // the stream ended meanwhile: nothing to show or undo
        AppLog.Write($"silenciar el PC → {(mute ? "sí" : "no")} durante la transmisión{(applied ? "" : " (no aplicado)")}");
        // Couldn't mute (the PC stays audible): make the switch say so, and keep that as the preference.
        if (!applied && mute && MuteLocal && (_session ?? _starting?.Session) == session) MuteLocal = false;
    }

    private bool _fullLevel;
    public bool FullLevel
    {
        get => _fullLevel;
        set
        {
            if (!Set(ref _fullLevel, value && MuteLocal)) return;
            _settings.FullLevel = _fullLevel;
            _settings.Save();
        }
    }

    /// <summary>Capture options apply when a stream starts, so they are locked while one runs.</summary>
    public bool CanEditOptions => !IsStreaming;
    public bool CanEditFullLevel => MuteLocal && !IsStreaming;

    private bool _minimizeToTray;
    public bool MinimizeToTray { get => _minimizeToTray; set { if (Set(ref _minimizeToTray, value)) { _settings.MinimizeToTray = value; _settings.Save(); } } }

    private bool _resumeOnLaunch;
    public bool ResumeOnLaunch { get => _resumeOnLaunch; set { if (Set(ref _resumeOnLaunch, value)) { _settings.ResumeOnLaunch = value; _settings.Save(); } } }

    public bool StartWithWindows
    {
        get => WindowsIntegration.StartsWithWindows;
        set { WindowsIntegration.StartsWithWindows = value; OnPropertyChanged(); }
    }

    public string Version => L.F("app.version", typeof(MainViewModel).Assembly.GetName().Version?.ToString(3));
    public string LogsPath => AppPaths.Logs;

    // ── Live stats (real values; estimates are labelled) ───────
    public SpectrumAnalyzer? Analyzer { get => _analyzer; private set => Set(ref _analyzer, value); }
    private SpectrumAnalyzer? _analyzer;

    private string _elapsedText = "00:00:00";
    public string ElapsedText { get => _elapsedText; private set => Set(ref _elapsedText, value); }
    private string _rateText = "—";
    public string RateText { get => _rateText; private set => Set(ref _rateText, value); }
    private string _formatText = "WAV PCM · 16 bit · 2 ch";
    public string FormatText { get => _formatText; private set => Set(ref _formatText, value); }
    private string _sourceFormatText = "—";
    public string SourceFormatText { get => _sourceFormatText; private set => Set(ref _sourceFormatText, value); }
    private string _bufferText = "—";
    public string BufferText { get => _bufferText; private set => Set(ref _bufferText, value); }
    private string? _transportState;
    /// <summary>The Sonos's own transport state, translated live.</summary>
    public string TransportText => _transportState switch
    {
        null => "—",
        "PLAYING" => L.T("stats.transport.playing"),
        "TRANSITIONING" => L.T("stats.transport.loading"),
        "PAUSED_PLAYBACK" => L.T("stats.transport.paused"),
        "STOPPED" => L.T("stats.transport.stopped"),
        "UNREACHABLE" => L.T("stats.transport.unreachable"),
        var other => other,
    };
    private void SetTransport(string? state)
    {
        if (_transportState == state) return;
        _transportState = state;
        OnPropertyChanged(nameof(TransportText));
    }
    private string _dropsText = "0";
    public string DropsText { get => _dropsText; private set => Set(ref _dropsText, value); }
    private string _reconnectsText = "0";
    public string ReconnectsText { get => _reconnectsText; private set => Set(ref _reconnectsText, value); }
    private Func<string>? _lastError;
    /// <summary>Last problem worth telling the user (translated live); "none" text when there is none.</summary>
    public string LastErrorText => _lastError?.Invoke() ?? L.T("status.noError");
    public bool HasLastError => _lastError is not null;
    private void LastError(Func<string>? text)
    {
        _lastError = text;
        OnPropertyChanged(nameof(LastErrorText));
        OnPropertyChanged(nameof(HasLastError));
    }
    private string _streamUrl = "—";
    public string StreamUrl { get => _streamUrl; private set => Set(ref _streamUrl, value); }

    // ── Lifecycle ───────────────────────────────────────────────
    /// <param name="allowResume">False in screenshot modes: they must never start a stream from saved settings.</param>
    public async Task InitializeAsync(bool autoStart, bool allowResume = true)
    {
        bool restored = await Task.Run(() => LocalOutputGuard.RecoverPending(AppPaths.GuardStateFile));
        if (File.Exists(RedirectStateFile))
        {
            bool done = false;
            try { done = await Task.Run(() => AppOutputRedirect.RecoverPending(RedirectStateFile)); }
            catch (Exception ex) when (AppOutputRedirect.IsRoutingFailure(ex)) { AppLog.Write($"restaurar salida de app: {ex.GetType().Name}"); }
            AppLog.Write(done ? "salida de la app restaurada tras un cierre inesperado" : "salida de la app pendiente de restaurar (la app no está abierta)");
            restored |= done;
        }
        if (restored) Detail(() => L.T("status.detail.restoredAfterCrash"));
        await DiscoverAsync();
        _poll.Start();
        // At Windows sign-in the hotspot and the Sonos may need a while: wait for them (bounded) instead of giving up.
        if (autoStart || (allowResume && _settings.ResumeOnLaunch && _settings.WasStreaming))
            await WaitForSpeakerAndStartAsync();
    }

    public async Task DiscoverAsync()
    {
        if (IsStreaming) return;
        State = UiState.Discovering;
        Detail(() => L.T("status.detail.searching"));
        IReadOnlyList<SonosDevice> found;
        try { found = NoSpeakersForCapture ? [] : await SonosDiscovery.DiscoverAsync(TimeSpan.FromSeconds(3)); }
        catch (Exception ex) when (ex is SocketException or InvalidOperationException) { found = []; }

        Speakers.Clear();
        foreach (var d in found) Speakers.Add(new SpeakerItem(d));

        var pick = Speakers.FirstOrDefault(s => s.Device.Udn == _settings.LastSpeakerUdn)
                   ?? Speakers.FirstOrDefault(s => s.Model.Contains("Five", StringComparison.OrdinalIgnoreCase))
                   ?? Speakers.FirstOrDefault();
        if (pick is null)
        {
            SelectedSpeaker = null;
            State = UiState.NoSpeaker;
            Detail(() => L.T("status.detail.noSpeakerHint"));
            _ = CheckHotspotAsync();
            return;
        }
        ResetHotspotPrompt();
        Select(pick);
        State = UiState.Ready;
        int speakerCount = Speakers.Count;
        Detail(() => L.Plural("status.detail.speakersFound", speakerCount));
    }

    private void Select(SpeakerItem item)
    {
        foreach (var s in Speakers) s.IsSelected = s == item;
        SelectedSpeaker = item;
        _settings.LastSpeakerUdn = item.Device.Udn;
        _settings.Save();
        _control?.Dispose();
        _control = new SonosClient(item.Device.Address);
        ControlsReady = false;
        _ = PollSpeakerAsync(forceControls: true);
    }

    private void LoadOutputs()
    {
        Outputs.Clear();
        foreach (var o in AudioDevices.ListOutputs()) Outputs.Add(o);
        SelectedOutput = Outputs.FirstOrDefault(o => o.Id == _settings.CaptureDeviceId) ?? Outputs.FirstOrDefault(o => o.IsDefault) ?? Outputs.FirstOrDefault();
    }

    private async Task ToggleStreamAsync()
    {
        if (IsStreaming) await StopAsync(reason: "usuario (botón o bandeja)");
        else await StartAsync();
    }

    public async Task StartAsync()
    {
        _lastStartRetryable = false;
        if (SelectedSpeaker is not { } speaker || IsStreaming) return;
        if (CurrentSpec() is not { } spec)
        {
            State = UiState.Error;
            Detail(() => L.T("status.detail.chooseApp"));
            return;
        }
        State = UiState.Starting;
        AppLog.Write($"inicio solicitado → {speaker.Name} ({speaker.Address})");
        Detail(() => L.F("status.detail.preparing", speaker.Name));
        LastError(null);
        _lastErrorCode = null; // a new session may report the same problem again

        var session = new StreamingSession(speaker.Device, Speakers.Select(s => s.Device.Address), spec,
            new StreamOptions(_settings.Port, MuteLocal, FullLevel, AppPaths.Logs, PrebufferMs), AppPaths.GuardStateFile);
        session.Diagnostic += msg => AppLog.Write($"red: {msg}");
        session.StatsUpdated += s => _ui.BeginInvoke(() => OnStats(s));
        session.StateChanged += s => _ui.BeginInvoke(() => OnSessionState(s));
        session.SourceChanged += src => _ui.BeginInvoke(() =>
        {
            if (_session is null) return;
            SourceFormatText = $"{_session.CaptureDeviceName} · {_session.SourceFormat}";
            AppLog.Write($"fuente activa: {src.DisplayName}");
        });

        var cts = new CancellationTokenSource();
        var pending = new PendingStart(session, session.StartAsync(cts.Token), cts);
        _starting = pending;
        try
        {
            await pending.Start;
        }
        catch (Exception ex)
        {
            if (pending.Cancelled) return; // StopAsync owns the cleanup
            _starting = null;
            await session.DisposeAsync();
            State = UiState.Error;
            Detail(() => Describe(ex));
            _lastStartRetryable = ex is HttpRequestException or TaskCanceledException
                or SonosSoapException { UpnpErrorCode: not 800 } // e.g. HTTP 500 while the Sonos boots; 800 = grouped speaker
                or SocketException { SocketErrorCode: SocketError.AddressAlreadyInUse or SocketError.AddressNotAvailable or SocketError.NetworkUnreachable or SocketError.HostUnreachable };
            AppLog.Write($"inicio fallido: {ex.GetType().Name}: {ex.Message}");
            LastError(() => Describe(ex));
            return;
        }
        if (pending.Cancelled) return; // stopped (or the app is closing) while connecting: StopAsync disposes it
        _starting = null;

        _session = session;
        Analyzer = session.Spectrum;
        FormatText = session.StreamFormat;
        SourceFormatText = $"{session.CaptureDeviceName} · {session.SourceFormat}";
        StreamUrl = session.StreamUrl;
        _settings.WasStreaming = true;
        _settings.Save();
        State = UiState.Streaming;
        Detail(() => L.F("status.detail.streaming", SourceName, speaker.Name));
        AppLog.Write($"transmitiendo {spec.DisplayName}: {session.StreamUrl}");
    }

    public async Task StopAsync(bool rememberStopped = true, string reason = "sin especificar")
    {
        if (_starting is { } pending)
        {
            // Still connecting: cancel the start, let it unwind, then undo it, so nothing (a muted PC, the port)
            // is left behind. A second Stop meanwhile (tray + quit) waits for the same cleanup.
            _starting = null;
            pending.Cancelled = true;
            AppLog.Write($"detenido durante la conexión: {reason}");
            _abortingStart = AbortStartAsync(pending);
        }
        if (_abortingStart is { } aborting)
        {
            await aborting;
            if (_abortingStart == aborting) _abortingStart = null;
        }
        var session = _session;
        if (session is not null) AppLog.Write($"detenido: {reason}");
        _session = null;
        Analyzer = null;
        IsPaused = false;
        if (session is not null) await session.DisposeAsync();
        if (rememberStopped)
        {
            _settings.WasStreaming = false;
            _settings.Save();
        }
        if (State is not UiState.Error) Detail(() => SelectedSpeaker is null ? "" : L.T("status.detail.stopped"));
        State = SelectedSpeaker is null ? UiState.NoSpeaker : UiState.Ready;
        RateText = "—";
        BufferText = "—";
        SetTransport(null);
        ResetNetwork();
        if (_sleepAt is not null) CancelSleep(log: true);
    }

    private sealed class PendingStart(StreamingSession session, Task start, CancellationTokenSource cts)
    {
        public StreamingSession Session { get; } = session;
        public Task Start { get; } = start;
        public CancellationTokenSource Cts { get; } = cts;
        public bool Cancelled { get; set; }
    }

    private async Task AbortStartAsync(PendingStart pending)
    {
        try
        {
            pending.Cts.Cancel();
            try { await pending.Start; } catch (Exception) { } // it fails or completes; either way it's undone below
            await pending.Session.DisposeAsync();
        }
        catch (Exception ex)
        {
            AppLog.Write($"limpieza tras cancelar el inicio: {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            _abortingStart = null;
        }
    }

    private void OnSessionState(SessionState s)
    {
        if (_session is null) return;
        switch (s)
        {
            case SessionState.Streaming: State = UiState.Streaming; break;
            case SessionState.Recovering:
                State = UiState.Recovering;
                Detail(() => L.T("status.detail.recovering"));
                break;
            case SessionState.Failed:
                _ = FailAsync();
                break;
            case SessionState.Released:
                _ = ReleasedAsync();
                break;
        }
    }

    /// <summary>The user took the Sonos for something else: stop quietly (not an error) and don't resume later.</summary>
    private async Task ReleasedAsync()
    {
        var reason = _lastError ?? (() => "");
        await StopAsync(rememberStopped: true, reason: $"Sonos: {_lastErrorCode}");
        Detail(reason);
        LastError(null);
    }

    private async Task FailAsync()
    {
        var reason = _lastError ?? (() => L.T("status.title.error"));
        if (_director is { } director) director.Failing = true; // the teardown is part of the error: one sound, not two
        try { await StopAsync(rememberStopped: false, reason: $"error: {_lastErrorCode}"); }
        finally { if (_director is { } d) d.Failing = false; }
        State = UiState.Error;
        Detail(reason);
    }

    private void OnStats(StreamStats s)
    {
        if (_session is null) return;
        RememberStats(s);
        ElapsedText = s.Elapsed.ToString(@"hh\:mm\:ss");
        RateText = $"{s.SendRateKBps:0} kB/s";
        SetTransport(s.TransportState);
        BufferText = s.BufferEstimateSeconds is { } b ? L.F("stats.buffer", Math.Max(0, b)) : "—";
        DropsText = s.DroppedChunks.ToString();
        ReconnectsText = s.Reconnects.ToString();
        UpdateNetwork(s);
        if (s.LastError is { } err && err != _lastErrorCode) { _lastErrorCode = err; LastError(() => L.Message(err)); }
        if (_sourceAvailable != s.SourceAvailable) { _sourceAvailable = s.SourceAvailable; SourceChangedNotify(); }
        SpeakerOnline = s.TransportState != "UNREACHABLE";
        if (SelectedSpeaker is { } sp) sp.IsOnline = SpeakerOnline;
    }

    private async Task PollSpeakerAsync(bool forceControls = false)
    {
        var control = _control;
        var speaker = SelectedSpeaker;
        if (control is null || speaker is null) return;
        try
        {
            if (_session is null)
            {
                await control.GetTransportStateAsync();
                SpeakerOnline = true;
                speaker.IsOnline = true;
            }

            bool volumeIdle = (DateTime.UtcNow - _lastUserVolumeChange).TotalSeconds > 1.5 && !_volumeDebounce.IsEnabled;
            bool eqIdle = (DateTime.UtcNow - _lastUserEqChange).TotalSeconds > 1.5 && !_eqDebounce.IsEnabled;
            int vol = volumeIdle || forceControls ? await control.GetVolumeAsync() : Volume;
            (int bass, int treble, bool loud) = eqIdle || forceControls
                ? (await control.GetBassAsync(), await control.GetTrebleAsync(), await control.GetLoudnessAsync())
                : (Bass, Treble, Loudness);

            if (control != _control) return; // selection changed meanwhile
            _applyingRemote = true;
            try
            {
                Volume = vol;
                Bass = bass;
                Treble = treble;
                Loudness = loud;
            }
            finally { _applyingRemote = false; }
            ControlsReady = true;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or SonosSoapException)
        {
            if (_session is null)
            {
                SpeakerOnline = false;
                speaker.IsOnline = false;
            }
        }
    }

    private async Task SafeControlAsync(Func<SonosClient, Task> action)
    {
        if (_control is null) return;
        try { await action(_control); }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or SonosSoapException)
        {
            LastError(() => L.F("errors.sonosSettingFailed", ex.Message));
        }
    }

    private string Describe(Exception ex) => ex switch
    {
        SocketException { SocketErrorCode: SocketError.AddressAlreadyInUse } =>
            L.F("errors.portInUse", _settings.Port),
        SonosSoapException { UpnpErrorCode: 800 } =>
            L.T("errors.groupedSpeaker"),
        // Technical details are passed as codes: the Core's own exception text is not translated.
        SonosSoapException soap => L.F("errors.sonosRejected", $"UPnP {soap.UpnpErrorCode?.ToString() ?? "?"}"),
        HttpRequestException or TaskCanceledException => L.T("errors.sonosNotResponding"),
        PlatformNotSupportedException => L.T("errors.appCaptureUnsupported"),
        NotSupportedException ns => L.F("errors.formatUnsupported", ns.Message),
        System.Runtime.InteropServices.COMException com when IsAppMode =>
            L.F("errors.appCaptureFailed", SourceName, $"0x{com.HResult:X8}"),
        TimeoutException when IsAppMode =>
            L.F("errors.appCaptureFailed", SourceName, "timeout"),
        _ => ex.Message,
    };

    /// <summary>Windows is signing out or shutting down: give the PC back at once (see StreamingSession.RestoreLocalNow).</summary>
    public void RestoreLocalForShutdown()
    {
        try { (_session ?? _starting?.Session)?.RestoreLocalNow(TimeSpan.FromSeconds(1)); }
        catch (Exception ex) { AppLog.Write($"restaurar al cerrar sesión: {ex.GetType().Name}"); }
    }

    public async Task ShutdownAsync()
    {
        _poll.Stop();
        CancelStartupWait();
        // Keep WasStreaming so "resume on launch" picks up where the user left off.
        await StopAsync(rememberStopped: false, reason: "cierre de la aplicación");
        _control?.Dispose();
    }
}
