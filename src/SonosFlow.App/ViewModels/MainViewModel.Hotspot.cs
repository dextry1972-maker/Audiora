using System.Windows.Input;
using SonosFlow.App.Infrastructure;

namespace SonosFlow.App.ViewModels;

/// <summary>Offer to switch on the Windows Mobile Hotspot when a search finds no speakers.</summary>
public sealed partial class MainViewModel
{
    private HotspotAdvisor _hotspotAdvisor = new(new WindowsHotspot());
    private HotspotPrompt _hotspotPrompt;
    private ICommand? _enableHotspotCommand, _dismissHotspotCommand, _openHotspotSettingsCommand;

    /// <summary>Test hook: swaps the Windows hotspot for a fake.</summary>
    internal void UseHotspot(IHotspotController controller) => _hotspotAdvisor = new HotspotAdvisor(controller);

    internal bool NoSpeakersForCapture { get; private set; }

    /// <summary>Capture-only: every search comes back empty and the hotspot is a fake.</summary>
    /// <summary>Capture-only: a synthetic spectrum analyzer, to look at (and measure) the audio-reactive visuals without any audio.</summary>
    internal void SimulateAnalyzer(SonosFlow.Core.Audio.SpectrumAnalyzer analyzer) => Analyzer = analyzer;

    internal void SimulateNoSpeakers(IHotspotController controller) { UseHotspot(controller); NoSpeakersForCapture = true; }

    public HotspotPrompt HotspotPrompt
    {
        get => _hotspotPrompt;
        private set
        {
            if (!Set(ref _hotspotPrompt, value)) return;
            OnPropertyChanged(nameof(HasHotspotPrompt));
            OnPropertyChanged(nameof(HotspotText));
            OnPropertyChanged(nameof(ShowHotspotEnable));
            OnPropertyChanged(nameof(ShowHotspotRescan));
            OnPropertyChanged(nameof(ShowHotspotSettings));
            OnPropertyChanged(nameof(ShowHotspotDismiss));
            OnPropertyChanged(nameof(ShowSearchRetry));
            CommandManager.InvalidateRequerySuggested();
        }
    }

    // Only while no speaker is known (searching or not found): hidden once something is found
    public bool HasHotspotPrompt => _hotspotPrompt != HotspotPrompt.None && State is UiState.NoSpeaker or UiState.Discovering;
    public bool ShowHotspotEnable => _hotspotPrompt == HotspotPrompt.Offer;
    public bool ShowHotspotRescan => _hotspotPrompt is HotspotPrompt.AlreadyOn or HotspotPrompt.Enabled;
    public bool ShowHotspotSettings => _hotspotPrompt is HotspotPrompt.Unknown or HotspotPrompt.Failed;
    public bool ShowHotspotDismiss => _hotspotPrompt != HotspotPrompt.Enabling;

    public string HotspotText => _hotspotPrompt switch
    {
        HotspotPrompt.Offer => L.T("hotspot.prompt"),
        HotspotPrompt.AlreadyOn => L.T("hotspot.on"),
        HotspotPrompt.Unknown => L.T("hotspot.unknown"),
        HotspotPrompt.Enabling => L.T("hotspot.enabling"),
        HotspotPrompt.Enabled => L.T("hotspot.enabled"),
        HotspotPrompt.Failed => L.T("hotspot.failed"),
        _ => "",
    };

    public ICommand EnableHotspotCommand => _enableHotspotCommand ??= new RelayCommand(async () => await EnableHotspotAsync(), () => _hotspotPrompt == HotspotPrompt.Offer);
    public ICommand DismissHotspotCommand => _dismissHotspotCommand ??= new RelayCommand(() => { if (_hotspotPrompt != HotspotPrompt.Enabling) HotspotPrompt = HotspotPrompt.None; });
    public ICommand OpenHotspotSettingsCommand => _openHotspotSettingsCommand ??= new RelayCommand(() => _hotspotAdvisor.Controller.OpenSettings());

    // Read-only query, off the UI thread; nothing is switched on without the user's click.
    // early: asked a few seconds into a search that has found nothing yet. Only "switched off" is worth interrupting the search for;
    // any other answer is held and shown only if the search ends with no speaker (nothing is claimed about a hotspot that cannot be checked).
    private async Task CheckHotspotAsync(bool early = false)
    {
        if (_hotspotPrompt == HotspotPrompt.Enabling) return; // never replace an activation in progress
        var prompt = await _hotspotAdvisor.CheckAfterEmptySearchAsync();
        if (prompt is not { } p) return;
        if (early && p != HotspotPrompt.Offer) { _heldHotspotPrompt = p; return; }
        // drop it if a speaker showed up meanwhile
        bool stillNothing = !early || (IsSearching && Speakers.Count == 0);
        if (State is UiState.NoSpeaker or UiState.Discovering && stillNothing && _hotspotPrompt != HotspotPrompt.Enabling) HotspotPrompt = p;
    }

    private async Task EnableHotspotAsync()
    {
        if (_hotspotPrompt != HotspotPrompt.Offer) return;
        HotspotPrompt = HotspotPrompt.Enabling;
        var result = await _hotspotAdvisor.EnableAsync();
        HotspotPrompt = State is UiState.NoSpeaker or UiState.Discovering ? result : HotspotPrompt.None; // a speaker appeared meanwhile: no stale "search again"
        if (result == HotspotPrompt.Enabled) _discovery.NetworkChanged(); // a search still running looks again as soon as the adapter is up
    }

    private void ResetHotspotPrompt()
    {
        _heldHotspotPrompt = null;
        _hotspotAdvisor.Reset();
        if (_hotspotPrompt != HotspotPrompt.Enabling) HotspotPrompt = HotspotPrompt.None;
    }
}
