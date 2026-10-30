namespace SonosFlow.App.Sound;

/// <summary>What the user chose in Settings → Sonidos.</summary>
public sealed record SoundSettings(
    bool Interface = true, bool Notification = true, bool Connection = true, bool ConnectionLoss = true, bool Error = true,
    int Volume = 60, string? DeviceId = null)
{
    public bool Allows(SoundCategory c) => c switch
    {
        SoundCategory.Interface => Interface,
        SoundCategory.Notification => Notification,
        SoundCategory.Connection => Connection,
        SoundCategory.ConnectionLoss => ConnectionLoss,
        SoundCategory.Error => Error,
        _ => false,
    };

    /// <summary>Linear gain for the independent effects volume (0..100); perceptual curve, 100 = full scale of the sounds.</summary>
    public static double GainFor(int volume) => Math.Pow(Math.Clamp(volume, 0, 100) / 100.0, 1.5);
}

public enum SoundDecision { Play, Muted, Disabled, CaptureConflict, RateLimited, Superseded, Busy }

/// <summary>
/// Whether a sound is played right now. Pure rules, no audio: switches, volume 0, "would this reach the Sonos",
/// per-sound and group rate limits, and a cap on simultaneous voices.
/// </summary>
public sealed class SoundPolicy
{
    public const int MaxVoices = 3;
    private const int MicroGroupGapMs = 110;     // any two micro sounds
    private const int AfterImportantMs = 350;    // micro sounds right after an important one
    private const int PreviewGapMs = 150;

    private readonly object _gate = new();
    private readonly Dictionary<SoundId, long> _last = [];
    private long _lastMicro = long.MinValue / 2, _lastImportant = long.MinValue / 2, _lastPreview = long.MinValue / 2;

    public SoundDecision Decide(SoundId id, SoundSettings settings, bool captured, long nowMs, int activeVoices, bool preview = false)
    {
        var info = SoundCatalog.Info(id);
        if (settings.Volume <= 0) return SoundDecision.Muted;
        if (captured) return SoundDecision.CaptureConflict; // even a preview: it would be recorded and sent to the Sonos

        lock (_gate)
        {
            if (preview)
            {
                if (nowMs - _lastPreview < PreviewGapMs) return SoundDecision.RateLimited;
                _lastPreview = nowMs;
                return SoundDecision.Play;
            }

            if (!settings.Allows(info.Category)) return SoundDecision.Disabled;
            if (_last.TryGetValue(id, out var last) && nowMs - last < info.MinIntervalMs) return SoundDecision.RateLimited;
            if (info.Micro)
            {
                if (nowMs - _lastMicro < MicroGroupGapMs) return SoundDecision.RateLimited;
                if (nowMs - _lastImportant < AfterImportantMs) return SoundDecision.Superseded;
            }
            if (activeVoices >= MaxVoices && info.Priority < 4) return SoundDecision.Busy;

            _last[id] = nowMs;
            if (info.Micro) _lastMicro = nowMs;
            else if (info.Priority >= 3) _lastImportant = nowMs;
            return SoundDecision.Play;
        }
    }
}

/// <summary>
/// The effects are played by Audiora itself on a local output. Capturing "the whole PC" records everything rendered
/// on that output, so a sound there would be sent to the Sonos: it is silenced instead. One app is captured by
/// process, which never includes Audiora's own sounds (unless Audiora itself is the chosen source).
/// </summary>
public static class SoundGate
{
    public static bool WouldReachSonos(bool streamingActive, bool appMode, string? sourceAppExe, string? ownExe,
        string? captureDeviceId, string? effectsDeviceId, string? defaultDeviceId)
    {
        if (!streamingActive) return false;
        if (appMode)
            return sourceAppExe is not null && ownExe is not null
                   && string.Equals(Path.GetFileNameWithoutExtension(sourceAppExe), Path.GetFileNameWithoutExtension(ownExe), StringComparison.OrdinalIgnoreCase);
        var captured = captureDeviceId ?? defaultDeviceId;
        var effects = effectsDeviceId ?? defaultDeviceId;
        // Unknown on either side: assume the worst and stay silent.
        return captured is null || effects is null || string.Equals(captured, effects, StringComparison.OrdinalIgnoreCase);
    }
}
