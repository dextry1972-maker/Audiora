using System.Text.Json;
using NAudio.CoreAudioApi;

namespace SonosFlow.Core.Audio;

/// <summary>
/// Silences the PC's own speakers while streaming and, optionally, raises the Windows volume to 100 %
/// so the Sonos receives full level (loopback is captured after the Windows volume, but before mute).
/// The previous state is written to disk first, so a crash is undone on the next launch.
/// </summary>
public sealed class LocalOutputGuard : IDisposable
{
    private sealed record SavedState(string DeviceId, bool WasMuted, float PreviousVolume, bool VolumeChanged);

    private readonly AudioEndpointVolume _volume;
    private readonly SavedState _saved;
    private readonly string _stateFile;
    private bool _restored;

    /// <param name="fullLevel">Only honoured together with muting, so the PC speakers never get louder.</param>
    public LocalOutputGuard(MMDevice device, bool fullLevel, string stateFile)
    {
        _volume = device.AudioEndpointVolume;
        _stateFile = stateFile;
        _saved = new SavedState(device.ID, _volume.Mute, _volume.MasterVolumeLevelScalar, fullLevel);

        Directory.CreateDirectory(Path.GetDirectoryName(stateFile)!);
        File.WriteAllText(stateFile, JsonSerializer.Serialize(_saved));

        // Mute first, then raise the level, so nothing is ever audible at a higher volume.
        _volume.Mute = true;
        if (fullLevel) _volume.MasterVolumeLevelScalar = 1f;
    }

    public void Dispose()
    {
        if (_restored) return;
        _restored = true;
        Restore(_volume, _saved);
        TryDelete(_stateFile);
    }

    /// <summary>Undo a guard left behind by a crash or forced shutdown.</summary>
    public static bool RecoverPending(string stateFile)
    {
        if (!File.Exists(stateFile)) return false;
        try
        {
            var saved = JsonSerializer.Deserialize<SavedState>(File.ReadAllText(stateFile));
            if (saved is null) return false;
            using var enumerator = new MMDeviceEnumerator();
            using var device = enumerator.GetDevice(saved.DeviceId);
            Restore(device.AudioEndpointVolume, saved);
            return true;
        }
        catch (Exception ex) when (ex is IOException or JsonException or System.Runtime.InteropServices.COMException or ArgumentException)
        {
            return false; // Device gone or file unreadable: nothing safe to restore.
        }
        finally
        {
            TryDelete(stateFile);
        }
    }

    private static void Restore(AudioEndpointVolume volume, SavedState saved)
    {
        // Lower the level before unmuting.
        if (saved.VolumeChanged) volume.MasterVolumeLevelScalar = saved.PreviousVolume;
        volume.Mute = saved.WasMuted;
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch (IOException) { }
    }
}
