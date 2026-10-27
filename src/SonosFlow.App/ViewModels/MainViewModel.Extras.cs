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

    public void HandleHotkey(HotkeyAction action)
    {
        switch (action)
        {
            case HotkeyAction.VolumeUp when ControlsReady: Volume += HotkeyVolumeStep; break;
            case HotkeyAction.VolumeDown when ControlsReady: Volume -= HotkeyVolumeStep; break;
            case HotkeyAction.PauseResume:
                if (PauseResumeCommand.CanExecute(null)) PauseResumeCommand.Execute(null);
                break;
            case HotkeyAction.ToggleMini: MiniVisible = !MiniVisible; break;
        }
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
