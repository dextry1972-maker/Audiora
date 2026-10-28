using SonosFlow.App.Infrastructure;
using SonosFlow.Core.Streaming;

namespace SonosFlow.App.ViewModels;

/// <summary>Global keyboard shortcuts and the network-stability indicator.</summary>
public sealed partial class MainViewModel
{
    // ── Global shortcuts ───────────────────────────────────────

    public const int HotkeyVolumeStep = 5;

    /// <summary>Raised when the shortcuts switch changes, so the app can (un)register them.</summary>
    public event Action? HotkeysSettingChanged;

    public bool HotkeysEnabled
    {
        get => _settings.GlobalHotkeys;
        set
        {
            if (_settings.GlobalHotkeys == value) return;
            _settings.GlobalHotkeys = value;
            _settings.Save();
            OnPropertyChanged();
            HotkeysSettingChanged?.Invoke();
        }
    }

    private string _hotkeysStatus = "";
    /// <summary>Which shortcuts could not be registered (another program owns them); empty when all work.</summary>
    public string HotkeysStatus { get => _hotkeysStatus; set { if (Set(ref _hotkeysStatus, value)) OnPropertyChanged(nameof(HasHotkeysProblem)); } }
    public bool HasHotkeysProblem => HotkeysStatus.Length > 0;

    public string HotkeysHelp =>
        $"{GlobalHotkeys.All[0].Label} / {GlobalHotkeys.All[1].Label} volumen del Sonos (±{HotkeyVolumeStep}) · {GlobalHotkeys.All[2].Label} pausa o reanuda · {GlobalHotkeys.All[3].Label} mini reproductor. Funcionan aunque estés en un juego y no toman el foco";

    /// <summary>Asks the app to flash the on-screen indicator: icon glyph, text, optional 0..1 bar.</summary>
    public event Action<string, string, double?>? HudRequested;

    public void HandleHotkey(HotkeyAction action)
    {
        switch (action)
        {
            case HotkeyAction.VolumeUp or HotkeyAction.VolumeDown:
                if (!ControlsReady)
                {
                    HudRequested?.Invoke("", "Sonos sin conexión", null);
                    break;
                }
                Volume += action == HotkeyAction.VolumeUp ? HotkeyVolumeStep : -HotkeyVolumeStep;
                HudRequested?.Invoke(Volume == 0 ? "" : "", $"{SelectedSpeaker?.Name ?? "Sonos"} · {Volume} %", Volume / 100.0);
                break;
            case HotkeyAction.PauseResume:
                if (!PauseResumeCommand.CanExecute(null)) break;
                bool wasStreaming = IsStreaming;
                PauseResumeCommand.Execute(null); // the pause toggle itself is synchronous
                HudRequested?.Invoke(!wasStreaming ? "" : IsPaused ? "" : "",
                    !wasStreaming ? "Conectando con el Sonos…" : IsPaused ? "En pausa" : "Reanudado", null);
                break;
            case HotkeyAction.ToggleMini: MiniVisible = !MiniVisible; break;
        }
    }

    // ── Sleep timer ────────────────────────────────────────────

    public IReadOnlyList<ChoiceItem> SleepChoices { get; } =
    [
        new("0", "Sin temporizador"),
        new("30", "Detener en 30 min"),
        new("60", "Detener en 60 min"),
        new("90", "Detener en 90 min"),
    ];

    private System.Windows.Threading.DispatcherTimer? _sleepTimer;
    private DateTime? _sleepAt;
    private string _sleepKey = "0";

    /// <summary>Stops the stream after the chosen minutes; "0" cancels. Not persisted: it applies to this stream only.</summary>
    public string SleepKey
    {
        get => _sleepKey;
        set
        {
            if (!int.TryParse(value, out var minutes) || value == _sleepKey) return;
            if (minutes <= 0 || State is not (UiState.Streaming or UiState.Recovering)) // not while still connecting
            {
                CancelSleep(log: minutes <= 0 && _sleepAt is not null);
                return;
            }
            _sleepKey = value;
            _sleepAt = DateTime.UtcNow.AddMinutes(minutes);
            _sleepTimer ??= CreateSleepTimer();
            _sleepTimer.Start();
            AppLog.Write($"temporizador: detener en {minutes} min");
            OnPropertyChanged();
            UpdateSleepText();
        }
    }

    private string _sleepRemainingText = "";
    public string SleepRemainingText { get => _sleepRemainingText; private set { if (Set(ref _sleepRemainingText, value)) OnPropertyChanged(nameof(SleepActive)); } }
    public bool SleepActive => SleepRemainingText.Length > 0;

    private System.Windows.Threading.DispatcherTimer CreateSleepTimer()
    {
        var t = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        t.Tick += async (_, _) =>
        {
            if (_sleepAt is not { } at) return;
            if (DateTime.UtcNow < at)
            {
                UpdateSleepText();
                return;
            }
            CancelSleep(log: false);
            await StopAsync(reason: "temporizador de apagado");
            StatusDetail = "Detenida por el temporizador";
        };
        return t;
    }

    private void UpdateSleepText()
    {
        if (_sleepAt is not { } at) { SleepRemainingText = ""; return; }
        var left = at - DateTime.UtcNow;
        if (left < TimeSpan.Zero) left = TimeSpan.Zero;
        SleepRemainingText = $"Se detiene en {(int)left.TotalMinutes}:{left.Seconds:00}";
    }

    private void CancelSleep(bool log)
    {
        _sleepTimer?.Stop();
        _sleepAt = null;
        if (log) AppLog.Write("temporizador cancelado");
        _sleepKey = "0";
        OnPropertyChanged(nameof(SleepKey));
        SleepRemainingText = "";
    }

    // ── Network stability ──────────────────────────────────────

    private string _networkText = "—";
    public string NetworkText { get => _networkText; private set => Set(ref _networkText, value); }

    private bool _networkWarning;
    /// <summary>A stall longer than the margin happened: the Sonos probably ran dry (audible cut).</summary>
    public bool NetworkWarning { get => _networkWarning; private set => Set(ref _networkWarning, value); }

    private void UpdateNetwork(StreamStats s)
    {
        NetworkText = s.Stalls == 0
            ? "Estable"
            : $"{s.Stalls} atasco{(s.Stalls == 1 ? "" : "s")} · máx {s.StallWorstMs / 1000:0.0} s";
        NetworkWarning = s.StallsOverMargin > 0;
    }

    private void ResetNetwork()
    {
        NetworkText = "—";
        NetworkWarning = false;
    }
}
