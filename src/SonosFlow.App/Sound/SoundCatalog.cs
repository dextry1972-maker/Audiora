namespace SonosFlow.App.Sound;

/// <summary>Every sound of the "Audiora Enchanted" set, one per event.</summary>
public enum SoundId
{
    Connected, ConnectionLost, Reconnected, Error, StreamStart, StreamStop,
    ConfigChanged, Navigate, ToggleOn, ToggleOff,
}

/// <summary>The switch in Settings that governs a sound.</summary>
public enum SoundCategory { Interface, Notification, Connection, ConnectionLoss, Error }

/// <param name="Priority">Higher wins: low-priority micro sounds are dropped around important ones.</param>
/// <param name="MinIntervalMs">The same sound is never played twice within this time.</param>
/// <param name="Level">Loudness relative to the other sounds (micro sounds are quieter).</param>
/// <param name="Micro">Tiny interface feedback; limited as a group so fast clicking never turns into noise.</param>
public sealed record SoundInfo(SoundCategory Category, int Priority, int MinIntervalMs, double Level, bool Micro);

public static class SoundCatalog
{
    public static SoundInfo Info(SoundId id) => id switch
    {
        SoundId.Error => new(SoundCategory.Error, 5, 1500, 1.0, false),
        SoundId.ConnectionLost => new(SoundCategory.ConnectionLoss, 4, 5000, 0.9, false),
        SoundId.Connected => new(SoundCategory.Connection, 4, 1500, 1.0, false),
        SoundId.Reconnected => new(SoundCategory.Connection, 4, 3000, 0.9, false),
        SoundId.StreamStart => new(SoundCategory.Notification, 3, 600, 0.9, false),
        SoundId.StreamStop => new(SoundCategory.Notification, 3, 600, 0.9, false),
        SoundId.ConfigChanged => new(SoundCategory.Notification, 2, 200, 0.7, true),
        SoundId.ToggleOn => new(SoundCategory.Interface, 1, 90, 0.55, true),
        SoundId.ToggleOff => new(SoundCategory.Interface, 1, 90, 0.45, true),
        SoundId.Navigate => new(SoundCategory.Interface, 1, 160, 0.35, true),
        _ => throw new ArgumentOutOfRangeException(nameof(id)),
    };

    public static IReadOnlyList<SoundId> All { get; } = Enum.GetValues<SoundId>();
}
