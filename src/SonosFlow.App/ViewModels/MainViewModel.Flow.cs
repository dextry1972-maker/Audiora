using SonosFlow.App.Infrastructure;
using SonosFlow.Core.Audio;
using SonosFlow.Core.Sonos;

namespace SonosFlow.App.ViewModels;

/// <summary>
/// The guided flow on the main screen: first choose what to listen to (the whole PC or one application), then stream. This only
/// decides what the screen explains and when the stream button is available; the capture itself, the modes and the Sonos side
/// are untouched.
/// </summary>
public sealed partial class MainViewModel
{
    private bool _sourceChosen;
    private bool _simulateApps, _simulatedSpeaker;

    private void InitSourceChoice()
    {
        _sourceChosen = SourceFlow.InitialChoice(_settings.SourceChosen, AppSettings.LastLoadFoundFile);
        if (_settings.SourceChosen is null)
        {
            _settings.SourceChosen = _sourceChosen; // remembered explicitly from now on
            _settings.Save();
        }
    }

    /// <summary>Marks the source as chosen (any click on a source, a stream that starts). Never un-chosen afterwards.</summary>
    private void MarkSourceChosen()
    {
        if (_sourceChosen) return;
        _sourceChosen = true;
        _settings.SourceChosen = true;
        _settings.Save();
        SourceChangedNotify();
    }

    public SourceFlowStep FlowStep => SourceFlow.StepOf(_sourceChosen, IsAppMode, SelectedApp is not null);

    /// <summary>A valid source is chosen: "Todo el PC", or an application that is really there.</summary>
    public bool SourceReady => FlowStep == SourceFlowStep.ReadyToStream;

    // The two source cards. Two-way so a keyboard/mouse choice on the radio group reaches the view model; "false" is just the
    // other card being checked, so it is ignored.
    public bool WholePcSelected
    {
        get => _sourceChosen && !IsAppMode;
        set { if (value) { MarkSourceChosen(); IsAppMode = false; } }
    }

    public bool AppModeSelected
    {
        get => _sourceChosen && IsAppMode;
        set { if (value) { MarkSourceChosen(); IsAppMode = true; } }
    }

    /// <summary>Guidance is for a connected speaker that is ready: not while searching, streaming or in error.</summary>
    public bool ShowGuide => State == UiState.Ready;

    /// <summary>The state's own detail line. While the guide explains the next step it stays out of the way (except while searching).</summary>
    public bool ShowStatusDetail => State != UiState.Error && (State != UiState.Ready || IsSearching);

    /// <summary>The background waves should not compete with the choice of source.</summary>
    public bool WavesDim => ShowGuide && !SourceReady;

    private string SpeakerName => SelectedSpeaker?.Name ?? "";

    public string StepLabel => L.T(SourceReady ? "main.flow.step2" : "main.flow.step1");

    public string StepTitle => FlowStep switch
    {
        SourceFlowStep.ChooseSource => L.T("main.flow.step1.title"),
        SourceFlowStep.ChooseApp => L.T("main.flow.step1.app"),
        _ => L.F("main.flow.step2.title", SpeakerName),
    };

    /// <summary>"Todo el PC seleccionado" / "YouTube Music seleccionado".</summary>
    public string SourceConfirmText => IsAppMode && SelectedApp is { } app
        ? L.F("main.flow.selected.app", app.Name)
        : L.T("main.flow.selected.pc");

    public string GuideTitle => SourceReady ? SourceConfirmText : L.T("main.flow.guide.title");
    public string GuideText => SourceReady ? "" : L.F("main.flow.guide.text", SpeakerName);
    public bool HasGuideText => !SourceReady;

    /// <summary>
    /// Label under the big button. Locked: it says why. Ready: it names where the audio will go. Otherwise the usual text
    /// (the tray and the mini player keep using <see cref="StreamButtonText"/>).
    /// </summary>
    public string StreamLabelText => State != UiState.Ready ? StreamButtonText
        : !SourceReady ? L.T("main.stream.pickSource")
        : L.F("main.stream.toSpeaker", SpeakerName);

    public string StreamTooltip => State == UiState.Ready && !SourceReady ? L.T("main.stream.pickSource.tooltip") : StreamLabelText;

    private void NotifyFlow()
    {
        OnPropertyChanged(nameof(FlowStep));
        OnPropertyChanged(nameof(SourceReady));
        OnPropertyChanged(nameof(WholePcSelected));
        OnPropertyChanged(nameof(AppModeSelected));
        OnPropertyChanged(nameof(ShowGuide));
        OnPropertyChanged(nameof(ShowStatusDetail));
        OnPropertyChanged(nameof(WavesDim));
        OnPropertyChanged(nameof(StepLabel));
        OnPropertyChanged(nameof(StepTitle));
        OnPropertyChanged(nameof(SourceConfirmText));
        OnPropertyChanged(nameof(GuideTitle));
        OnPropertyChanged(nameof(GuideText));
        OnPropertyChanged(nameof(HasGuideText));
        OnPropertyChanged(nameof(StreamLabelText));
        OnPropertyChanged(nameof(StreamTooltip));
        OnPropertyChanged(nameof(StatusTitle));
    }

    // ── Capture-only: put the screen in each state without a speaker, a stream or the real network ──

    /// <summary>
    /// <c>--fake-flow none|pc|app|app-ready|stream</c>: a connected "Bedroom" and the given source state. Nothing is saved and the real
    /// network and audio are never touched.
    /// </summary>
    internal void SimulateFlow(string mode)
    {
        NoSpeakersForCapture = false;
        _simulateApps = true;
        _simulatedSpeaker = true;
        var bedroom = new SonosDevice(System.Net.IPAddress.Parse("192.0.2.10"), "Bedroom", "Sonos Five", "S24", "RINCON_SIMULATED");
        var item = new SpeakerItem(bedroom) { IsSelected = true };
        Speakers.Clear();
        Speakers.Add(item);
        SelectedSpeaker = item;
        ResetHotspotPrompt();
        Apps.Clear();
        SelectedApp = null;

        _sourceChosen = mode != "none";
        _isAppMode = mode is "app" or "app-ready";
        if (mode is "app" or "app-ready")
        {
            Apps.Add(new AppSourceItem(new AudioAppInfo(1, "Brave", "brave.exe", null, true, false)));
            Apps.Add(new AppSourceItem(new AudioAppInfo(2, "Discord", "discord.exe", null, false, false)));
            Apps.Add(new AppSourceItem(new AudioAppInfo(3, "YouTube Music", "brave.exe", null, true, true)));
        }
        if (mode == "app-ready") SelectedApp = Apps[2];
        State = mode == "stream" ? UiState.Streaming : UiState.Ready;
        if (mode == "stream") Detail(() => $"{SpeakerName} → {SourceName}");
        SourceChangedNotify();
        OnPropertyChanged(nameof(IsAppMode));
        NotifyFlow();
    }
}
