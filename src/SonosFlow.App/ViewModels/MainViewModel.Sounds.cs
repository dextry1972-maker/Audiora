using System.Collections.ObjectModel;
using System.Windows.Input;
using System.Windows.Threading;
using SonosFlow.App.Infrastructure;
using SonosFlow.App.Sound;
using SonosFlow.Core.Audio;

namespace SonosFlow.App.ViewModels;

/// <summary>Settings → Sonidos and the link between real stream states and the "Audiora Enchanted" sounds.</summary>
public sealed partial class MainViewModel
{
    private SoundService? _sounds;
    private SoundDirector? _director;
    private DispatcherTimer? _soundsSaveDebounce;
    private string _soundsNote = "";
    private string? _defaultOutputId;
    private long _defaultOutputAt = long.MinValue / 2;
    private ICommand? _previewSoundCommand, _resetSoundsCommand;

    /// <summary>Output choices for the effects; the first ("") follows the system default.</summary>
    public ObservableCollection<ChoiceItem> SoundDevices { get; } = [new ChoiceItem("", () => L.T("sounds.device.default"))];

    internal void AttachSounds(SoundService sounds)
    {
        _sounds = sounds;
        _director = new SoundDirector(id => sounds.Play(id));
        RefreshSoundDevices();
    }

    // ── what the service asks the view model ────────────────────────────────

    internal SoundSettings CurrentSoundSettings() => new(
        _settings.InterfaceSounds, _settings.NotificationSounds, _settings.ConnectionSounds, _settings.ConnectionLossSounds,
        _settings.ErrorSounds, _settings.SoundsVolume, _settings.SoundsDeviceId);

    /// <summary>Would a sound on <paramref name="effectsDeviceId"/> be captured and sent to the Sonos right now?</summary>
    internal bool WouldSoundReachSonos(string? effectsDeviceId) => SoundGate.WouldReachSonos(
        IsStreaming, IsAppMode, _settings.SourceAppExe, Environment.ProcessPath,
        SelectedOutput is { IsDefault: false } o ? o.Id : null, effectsDeviceId, DefaultOutputId());

    // The default output is looked up at most every 3 s (a COM call); unknown means "assume it would be captured".
    private string? DefaultOutputId()
    {
        long now = Environment.TickCount64;
        if (now - _defaultOutputAt < 3000) return _defaultOutputId;
        _defaultOutputAt = now;
        try { using var d = AudioDevices.Resolve(null); _defaultOutputId = d.ID; }
        catch (Exception) { _defaultOutputId = null; }
        return _defaultOutputId;
    }

    private void SoundStateReached(UiState state) => _director?.OnState(state);
    private void SoundNavigated(string previousPage) => _sounds?.Play(SoundId.Navigate);

    private async Task StartSilentlyAsync()
    {
        if (_director is { } director) director.Quiet = true;
        try { await StartAsync(); }
        finally { if (_director is { } d) d.Quiet = false; }
    }

    /// <summary>The app is closing and will stop the stream by itself: no goodbye sounds.</summary>
    internal void QuiesceSounds() => _sounds?.Quiesce();

    // ── settings ────────────────────────────────────────────────────────────

    public bool InterfaceSounds { get => _settings.InterfaceSounds; set => SetSound(_settings.InterfaceSounds, v => _settings.InterfaceSounds = v, value); }
    public bool NotificationSounds { get => _settings.NotificationSounds; set => SetSound(_settings.NotificationSounds, v => _settings.NotificationSounds = v, value); }
    public bool ConnectionSounds { get => _settings.ConnectionSounds; set => SetSound(_settings.ConnectionSounds, v => _settings.ConnectionSounds = v, value); }
    public bool ConnectionLossSounds { get => _settings.ConnectionLossSounds; set => SetSound(_settings.ConnectionLossSounds, v => _settings.ConnectionLossSounds = v, value); }
    public bool ErrorSounds { get => _settings.ErrorSounds; set => SetSound(_settings.ErrorSounds, v => _settings.ErrorSounds = v, value); }

    private void SetSound(bool current, Action<bool> apply, bool value, [System.Runtime.CompilerServices.CallerMemberName] string? name = null)
    {
        if (current == value) return;
        apply(value);
        _settings.Save();
        OnPropertyChanged(name);
    }

    public int SoundsVolume
    {
        get => _settings.SoundsVolume;
        set
        {
            value = Math.Clamp(value, 0, 100);
            if (_settings.SoundsVolume == value) return;
            _settings.SoundsVolume = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(SoundsVolumeText));
            // A slider drag changes this many times a second: write the file once it settles.
            _soundsSaveDebounce ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
            _soundsSaveDebounce.Tick -= SaveSoundsVolume;
            _soundsSaveDebounce.Tick += SaveSoundsVolume;
            _soundsSaveDebounce.Stop();
            _soundsSaveDebounce.Start();
        }
    }

    private void SaveSoundsVolume(object? sender, EventArgs e) { _soundsSaveDebounce?.Stop(); _settings.Save(); }

    public string SoundsVolumeText => $"{SoundsVolume} %";

    /// <summary>"" = system default. Null is ignored: a refreshed list briefly empties the ComboBox selection.</summary>
    public string? SoundsDeviceKey
    {
        get => _settings.SoundsDeviceId ?? "";
        set
        {
            if (value is null) return;
            var id = value.Length == 0 ? null : value;
            if (id == _settings.SoundsDeviceId) return;
            _settings.SoundsDeviceId = id;
            _settings.Save();
            OnPropertyChanged();
        }
    }

    public string SoundsNote
    {
        get => _soundsNote;
        private set { if (Set(ref _soundsNote, value)) OnPropertyChanged(nameof(HasSoundsNote)); }
    }

    public bool HasSoundsNote => _soundsNote.Length > 0;

    /// <summary>Rebuilds the device list (called when the settings page opens, so plugged-in devices appear).</summary>
    public void RefreshSoundDevices()
    {
        var saved = _settings.SoundsDeviceId;
        IReadOnlyList<OutputDeviceInfo> outputs;
        try { outputs = AudioDevices.ListOutputs(); }
        catch (Exception) { outputs = []; }

        var wanted = new List<ChoiceItem> { new("", () => L.T("sounds.device.default")) };
        wanted.AddRange(outputs.Select(o => new ChoiceItem(o.Id, () => o.Name)));
        if (saved is not null && wanted.All(c => c.Key != saved)) // unplugged: say so instead of silently changing the choice
            wanted.Add(new ChoiceItem(saved, () => L.T("sounds.device.unavailable")));

        // Updated in place: emptying a ComboBox drops its selection, and the chosen device must stay chosen.
        bool changed = false;
        for (int i = SoundDevices.Count - 1; i >= 0; i--)
            if (wanted.All(w => w.Key != SoundDevices[i].Key)) { SoundDevices.RemoveAt(i); changed = true; }
        for (int i = 0; i < wanted.Count; i++)
        {
            int at = SoundDevices.ToList().FindIndex(c => c.Key == wanted[i].Key);
            if (at < 0) { SoundDevices.Insert(Math.Min(i, SoundDevices.Count), wanted[i]); changed = true; }
            else if (_placeholderSoundKeys.Contains(wanted[i].Key) != (wanted[i].Key.Length > 0 && wanted[i].Key == saved && outputs.All(o => o.Id != saved)))
            { SoundDevices[at] = wanted[i]; changed = true; } // an unplugged device came back (or vanished): refresh its label
        }
        _placeholderSoundKeys.Clear();
        if (saved is not null && outputs.All(o => o.Id != saved)) _placeholderSoundKeys.Add(saved);
        if (changed) OnPropertyChanged(nameof(SoundsDeviceKey)); // re-select after items were replaced
    }

    private readonly HashSet<string> _placeholderSoundKeys = [];

    // ── preview and reset ───────────────────────────────────────────────────

    public ICommand PreviewSoundCommand => _previewSoundCommand ??= new RelayCommand(p =>
    {
        if (p is not string name || !Enum.TryParse<SoundId>(name, out var id)) return;
        SoundsNote = _sounds?.Play(id, preview: true) switch
        {
            SoundDecision.CaptureConflict => L.T("sounds.note.captured"),
            SoundDecision.Muted => L.T("sounds.note.muted"),
            _ => "",
        };
    });

    /// <summary>A short sample at the new level once the volume slider is released.</summary>
    public void PlaySoundsVolumeSample() => PreviewSoundCommand.Execute(nameof(SoundId.ConfigChanged));

    public ICommand ResetSoundsCommand => _resetSoundsCommand ??= new RelayCommand(() =>
    {
        var d = new AppSettings();
        _settings.InterfaceSounds = d.InterfaceSounds;
        _settings.NotificationSounds = d.NotificationSounds;
        _settings.ConnectionSounds = d.ConnectionSounds;
        _settings.ConnectionLossSounds = d.ConnectionLossSounds;
        _settings.ErrorSounds = d.ErrorSounds;
        _settings.SoundsVolume = d.SoundsVolume;
        _settings.SoundsDeviceId = d.SoundsDeviceId;
        _settings.Save();
        SoundsNote = "";
        foreach (var n in new[] { nameof(InterfaceSounds), nameof(NotificationSounds), nameof(ConnectionSounds), nameof(ConnectionLossSounds),
                     nameof(ErrorSounds), nameof(SoundsVolume), nameof(SoundsVolumeText), nameof(SoundsDeviceKey) })
            OnPropertyChanged(n);
        PlaySoundsVolumeSample();
    });
}
