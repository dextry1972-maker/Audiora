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

public sealed class MainViewModel : ObservableObject
{
    private readonly AppSettings _settings;
    private readonly Dispatcher _ui = Application.Current.Dispatcher;
    private readonly DispatcherTimer _poll;
    private readonly DispatcherTimer _volumeDebounce;
    private readonly DispatcherTimer _eqDebounce;

    private StreamingSession? _session;
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

        ToggleStreamCommand = new RelayCommand(async () => await ToggleStreamAsync(), () => CanToggle);
        RescanCommand = new RelayCommand(async () => await DiscoverAsync(), () => State is not (UiState.Discovering or UiState.Starting));
        SelectSpeakerCommand = new RelayCommand(p => { if (p is SpeakerItem s) Select(s); }, _ => !IsStreaming);
        NavigateCommand = new RelayCommand(p => Page = p as string ?? "home");
        OpenLogsCommand = new RelayCommand(() =>
        {
            Directory.CreateDirectory(AppPaths.Logs);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{AppPaths.Logs}\"") { UseShellExecute = true });
        });
        RefreshOutputsCommand = new RelayCommand(LoadOutputs, () => !IsStreaming);
        ResetEqCommand = new RelayCommand(() => { Bass = 0; Treble = 0; Loudness = true; });

        LoadOutputs();
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
            CommandManager.InvalidateRequerySuggested();
            foreach (var s in Speakers) s.IsStreaming = s.IsSelected && IsStreaming;
            StreamingChanged?.Invoke();
        }
    }

    public bool IsStreaming => State is UiState.Streaming or UiState.Recovering or UiState.Starting;
    /// <summary>Audio is flowing (drives the "live" look).</summary>
    public bool IsActive => State is UiState.Streaming;
    public bool CanToggle => SelectedSpeaker is not null && State is UiState.Ready or UiState.Streaming or UiState.Recovering or UiState.Error;

    public string StatusTitle => State switch
    {
        UiState.Discovering => "Buscando altavoces…",
        UiState.NoSpeaker => "No se encontró ningún Sonos",
        UiState.Ready => "Listo para transmitir",
        UiState.Starting => "Conectando…",
        UiState.Streaming => "Transmitiendo",
        UiState.Recovering => "Reconectando…",
        UiState.Error => "No se pudo transmitir",
        _ => "",
    };

    public string StreamButtonText => IsStreaming ? "Detener" : "Transmitir";

    private string _statusDetail = "Buscando dispositivos Sonos en tu red local";
    public string StatusDetail { get => _statusDetail; private set => Set(ref _statusDetail, value); }

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
    public string ConnectionText => SelectedSpeaker is null ? "—" : SpeakerOnline ? "En línea" : "Sin conexión";

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
        }
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

    public string Version => $"Versión {typeof(MainViewModel).Assembly.GetName().Version?.ToString(3)}";
    public string LogsPath => AppPaths.Logs;

    // ── Live stats (real values; estimates are labelled) ───────
    public SpectrumAnalyzer? Analyzer { get => _analyzer; private set => Set(ref _analyzer, value); }
    private SpectrumAnalyzer? _analyzer;

    private string _elapsedText = "00:00:00";
    public string ElapsedText { get => _elapsedText; private set => Set(ref _elapsedText, value); }
    private string _rateText = "—";
    public string RateText { get => _rateText; private set => Set(ref _rateText, value); }
    private string _formatText = "WAV PCM · 16 bit · estéreo";
    public string FormatText { get => _formatText; private set => Set(ref _formatText, value); }
    private string _sourceFormatText = "—";
    public string SourceFormatText { get => _sourceFormatText; private set => Set(ref _sourceFormatText, value); }
    private string _bufferText = "—";
    public string BufferText { get => _bufferText; private set => Set(ref _bufferText, value); }
    private string _transportText = "—";
    public string TransportText { get => _transportText; private set => Set(ref _transportText, value); }
    private string _dropsText = "0";
    public string DropsText { get => _dropsText; private set => Set(ref _dropsText, value); }
    private string _reconnectsText = "0";
    public string ReconnectsText { get => _reconnectsText; private set => Set(ref _reconnectsText, value); }
    private string _lastErrorText = "Ninguno";
    public string LastErrorText { get => _lastErrorText; private set => Set(ref _lastErrorText, value); }
    private string _streamUrl = "—";
    public string StreamUrl { get => _streamUrl; private set => Set(ref _streamUrl, value); }

    // ── Lifecycle ───────────────────────────────────────────────
    public async Task InitializeAsync(bool autoStart)
    {
        if (LocalOutputGuard.RecoverPending(AppPaths.GuardStateFile))
            StatusDetail = "Se restauró el sonido del PC tras un cierre inesperado";
        await DiscoverAsync();
        _poll.Start();
        if (SelectedSpeaker is not null && (autoStart || (_settings.ResumeOnLaunch && _settings.WasStreaming)))
            await StartAsync();
    }

    public async Task DiscoverAsync()
    {
        if (IsStreaming) return;
        State = UiState.Discovering;
        StatusDetail = "Buscando dispositivos Sonos en tu red local";
        IReadOnlyList<SonosDevice> found;
        try { found = await SonosDiscovery.DiscoverAsync(TimeSpan.FromSeconds(3)); }
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
            StatusDetail = "Comprueba que el PC y el Sonos están en la misma red y que la red es privada";
            return;
        }
        Select(pick);
        State = UiState.Ready;
        StatusDetail = $"{Speakers.Count} altavoz{(Speakers.Count == 1 ? "" : "es")} en la red";
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
        if (SelectedSpeaker is not { } speaker || IsStreaming) return;
        State = UiState.Starting;
        AppLog.Write($"inicio solicitado → {speaker.Name} ({speaker.Address})");
        StatusDetail = $"Preparando el audio para {speaker.Name}";
        LastErrorText = "Ninguno";

        var session = new StreamingSession(speaker.Device, Speakers.Select(s => s.Device.Address),
            SelectedOutput is { IsDefault: false } o ? o.Id : null,
            new StreamOptions(_settings.Port, MuteLocal, FullLevel, AppPaths.Logs), AppPaths.GuardStateFile);
        session.StatsUpdated += s => _ui.BeginInvoke(() => OnStats(s));
        session.StateChanged += s => _ui.BeginInvoke(() => OnSessionState(s));

        try
        {
            await session.StartAsync();
        }
        catch (Exception ex)
        {
            await session.DisposeAsync();
            State = UiState.Error;
            StatusDetail = Describe(ex);
            AppLog.Write($"inicio fallido: {StatusDetail} [{ex.GetType().Name}]");
            LastErrorText = StatusDetail;
                return;
        }

        _session = session;
        Analyzer = session.Spectrum;
        FormatText = session.StreamFormat;
        SourceFormatText = $"{session.CaptureDeviceName} · {session.SourceFormat}";
        StreamUrl = session.StreamUrl;
        _settings.WasStreaming = true;
        _settings.Save();
        State = UiState.Streaming;
        StatusDetail = $"Audio del PC → {speaker.Name}";
        AppLog.Write($"transmitiendo: {session.StreamUrl}");
    }

    public async Task StopAsync(bool rememberStopped = true, string reason = "sin especificar")
    {
        var session = _session;
        if (session is not null) AppLog.Write($"detenido: {reason}");
        _session = null;
        Analyzer = null;
        if (session is not null) await session.DisposeAsync();
        if (rememberStopped)
        {
            _settings.WasStreaming = false;
            _settings.Save();
        }
        if (State is not UiState.Error) StatusDetail = SelectedSpeaker is null ? "" : "Transmisión detenida";
        State = SelectedSpeaker is null ? UiState.NoSpeaker : UiState.Ready;
        RateText = "—";
        BufferText = "—";
        TransportText = "—";
    }

    private void OnSessionState(SessionState s)
    {
        if (_session is null) return;
        switch (s)
        {
            case SessionState.Streaming: State = UiState.Streaming; break;
            case SessionState.Recovering:
                State = UiState.Recovering;
                StatusDetail = "El Sonos dejó de recibir el audio; reenviando la transmisión";
                break;
            case SessionState.Failed:
                _ = FailAsync();
                break;
        }
    }

    private async Task FailAsync()
    {
        var reason = LastErrorText;
        await StopAsync(rememberStopped: false, reason: $"error: {reason}");
        State = UiState.Error;
        StatusDetail = reason;
    }

    private void OnStats(StreamStats s)
    {
        if (_session is null) return;
        ElapsedText = s.Elapsed.ToString(@"hh\:mm\:ss");
        RateText = $"{s.SendRateKBps:0} kB/s";
        TransportText = s.TransportState switch
        {
            "PLAYING" => "Reproduciendo",
            "TRANSITIONING" => "Cargando",
            "PAUSED_PLAYBACK" => "En pausa",
            "STOPPED" => "Detenido",
            "UNREACHABLE" => "Sin respuesta",
            var other => other,
        };
        BufferText = s.BufferEstimateSeconds is { } b ? $"≈ {Math.Max(0, b):0.0} s (estimado)" : "—";
        DropsText = s.DroppedChunks.ToString();
        ReconnectsText = s.Reconnects.ToString();
        if (s.LastError is { } err) LastErrorText = err;
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
            LastErrorText = $"No se pudo aplicar el ajuste al Sonos: {ex.Message}";
        }
    }

    private string Describe(Exception ex) => ex switch
    {
        SocketException { SocketErrorCode: SocketError.AddressAlreadyInUse } =>
            $"El puerto {_settings.Port} ya está en uso (¿hay otra transmisión abierta?)",
        SonosSoapException { UpnpErrorCode: 800 } =>
            "El altavoz está agrupado y no es el principal del grupo. Sácalo del grupo en la app de Sonos o elige el principal.",
        SonosSoapException soap => $"El Sonos rechazó la orden: {soap.Message}",
        HttpRequestException or TaskCanceledException => "El Sonos no responde. Comprueba que está encendido y en la misma red.",
        NotSupportedException ns => ns.Message,
        _ => ex.Message,
    };

    public async Task ShutdownAsync()
    {
        _poll.Stop();
        // Keep WasStreaming so "resume on launch" picks up where the user left off.
        await StopAsync(rememberStopped: false, reason: "cierre de la aplicación");
        _control?.Dispose();
    }
}
