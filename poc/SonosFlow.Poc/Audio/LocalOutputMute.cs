using NAudio.CoreAudioApi;

namespace SonosFlow.Poc.Audio;

/// <summary>
/// Mutes the PC's own speakers while streaming and restores the previous mute state on dispose.
/// Whether loopback capture survives an endpoint mute depends on the driver, so callers should
/// verify the captured level after muting.
/// </summary>
public sealed class LocalOutputMute : IDisposable
{
    private readonly AudioEndpointVolume _volume;
    private readonly bool _wasMuted;
    private bool _restored;

    public LocalOutputMute(MMDevice device)
    {
        _volume = device.AudioEndpointVolume;
        _wasMuted = _volume.Mute;
        _volume.Mute = true;
    }

    public void Dispose()
    {
        if (_restored) return;
        _restored = true;
        _volume.Mute = _wasMuted;
    }
}
