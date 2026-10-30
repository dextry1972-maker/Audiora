using System.Collections.Concurrent;
using SonosFlow.App.ViewModels;

namespace SonosFlow.App.Sound;

/// <summary>
/// Entry point for every sound. Deciding is cheap and happens on the caller's thread; rendering (the first time)
/// and playing happen on the thread pool, so the interface is never blocked. Independent of the streaming engine.
/// </summary>
public sealed class SoundService : IDisposable
{
    private readonly Func<SoundSettings> _settings;
    private readonly Func<string?, bool> _wouldReachSonos;
    private readonly ISoundOutput _output;
    private readonly Func<long> _clock;
    private readonly Action<Action> _run;
    private readonly SoundPolicy _policy = new();
    private readonly ConcurrentDictionary<SoundId, float[]> _buffers = new();
    private volatile bool _quiet;

    /// <param name="wouldReachSonos">Given the effects device id (null = default): would a sound there be captured and sent to the Sonos?</param>
    /// <param name="run">Where playback runs; the thread pool unless a test supplies its own.</param>
    public SoundService(Func<SoundSettings> settings, Func<string?, bool> wouldReachSonos, ISoundOutput output,
        Func<long>? clock = null, Action<Action>? run = null)
    {
        _settings = settings;
        _wouldReachSonos = wouldReachSonos;
        _output = output;
        _clock = clock ?? (() => Environment.TickCount64);
        _run = run ?? (a => ThreadPool.QueueUserWorkItem(_ => a()));
    }

    /// <summary>Renders every sound ahead of time (a few milliseconds each) so the first one is instant.</summary>
    public Task PreloadAsync() => Task.Run(() => { foreach (var id in SoundCatalog.All) Buffer(id); });

    public int LoadedSounds => _buffers.Count;

    /// <summary>Stops all future sounds (the app is closing; the stream is being stopped on purpose).</summary>
    public void Quiesce() => _quiet = true;

    public SoundDecision Play(SoundId id, bool preview = false)
    {
        if (_quiet) return SoundDecision.Disabled;
        var settings = _settings();
        bool captured = _wouldReachSonos(settings.DeviceId);
        var decision = _policy.Decide(id, settings, captured, _clock(), _output.ActiveVoices, preview);
        if (decision != SoundDecision.Play) return decision;
        float gain = (float)SoundSettings.GainFor(settings.Volume);
        _run(() => _output.Play(Buffer(id), gain, settings.DeviceId));
        return decision;
    }

    private float[] Buffer(SoundId id) => _buffers.GetOrAdd(id, EnchantedSynth.Render);

    public void Dispose() { _quiet = true; _output.Dispose(); }
}

/// <summary>
/// Turns the real state of the stream into sounds: a sound is only ever a consequence of a state the stream
/// has actually reached, never of an attempt. One "lost" per outage and a "reconnected" only after a real recovery.
/// </summary>
public sealed class SoundDirector(Action<SoundId> play)
{
    private UiState? _previous;
    private bool _lost;

    /// <summary>Automatic starts (resuming at Windows sign-in) are silent; the user pressed nothing.</summary>
    public bool Quiet { get; set; }

    public void OnState(UiState next)
    {
        var previous = _previous;
        _previous = next;
        if (previous is null || previous == next) return;
        switch (next)
        {
            case UiState.Starting:
                _lost = false;
                if (!Quiet) play(SoundId.StreamStart);
                break;
            case UiState.Streaming:
                if (previous == UiState.Recovering && _lost) { if (!Quiet) play(SoundId.Reconnected); }
                else if (previous == UiState.Starting && !Quiet) play(SoundId.Connected);
                _lost = false;
                break;
            case UiState.Recovering:
                if (previous == UiState.Streaming && !_lost) { _lost = true; play(SoundId.ConnectionLost); }
                break;
            case UiState.Error:
                _lost = false;
                if (!Quiet) play(SoundId.Error);
                break;
            default: // Ready, NoSpeaker, Discovering: only a stream that really existed is "stopped"
                if (previous is UiState.Streaming or UiState.Recovering) play(SoundId.StreamStop);
                _lost = false;
                break;
        }
    }
}
