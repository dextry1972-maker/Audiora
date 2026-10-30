using System.Diagnostics;
using System.Text.Json;
using NAudio.CoreAudioApi;

namespace SonosFlow.Core.Audio;

/// <summary>Records undo information before muting, retaining it until restoration succeeds.</summary>
public sealed class LocalOutputGuard : IDisposable
{
    private sealed record SavedState(string DeviceId, bool WasMuted, float PreviousVolume, bool VolumeChanged,
        int OwnerPid = 0, string? GuardId = null);

    internal interface IVolume
    {
        bool Mute { get; set; }
        float Level { get; set; }
    }

    private sealed class EndpointVolume(AudioEndpointVolume volume) : IVolume
    {
        public bool Mute { get => volume.Mute; set => volume.Mute = value; }
        public float Level { get => volume.MasterVolumeLevelScalar; set => volume.MasterVolumeLevelScalar = value; }
    }

    private const string MutexName = @"Local\SonosFlow-LocalOutputGuard";
    private static readonly HashSet<string> Active = [];
    private readonly IVolume _volume;
    private readonly SavedState _saved;
    private readonly string _stateFile;
    private bool _restored;

    public LocalOutputGuard(MMDevice device, bool fullLevel, string stateFile)
        : this(device.ID, new EndpointVolume(device.AudioEndpointVolume), fullLevel, stateFile) { }

    internal LocalOutputGuard(string deviceId, IVolume volume, bool fullLevel, string stateFile)
    {
        _volume = volume;
        _stateFile = stateFile;
        using var gate = StateLock();
        if (File.Exists(stateFile)) throw new IOException("Hay una restauración de audio local pendiente.");
        _saved = new SavedState(deviceId, volume.Mute, volume.Level, fullLevel,
            Environment.ProcessId, Guid.NewGuid().ToString("N"));
        Save(stateFile, _saved);
        try
        {
            volume.Mute = true;
            if (fullLevel) volume.Level = 1f;
            lock (Active) Active.Add(_saved.GuardId!);
        }
        catch
        {
            // A failed setter may already have changed the endpoint. Keep the record if undo fails.
            try { Restore(volume, _saved); File.Delete(stateFile); } catch { }
            throw;
        }
    }

    public void Dispose()
    {
        // Even a lock timeout must leave the pending record recoverable by the next poll.
        lock (Active) Active.Remove(_saved.GuardId!);
        using var gate = StateLock();
        if (_restored) return;
        // A failed restore must be retryable by Dispose and by startup recovery.
        var current = File.Exists(_stateFile)
            ? JsonSerializer.Deserialize<SavedState>(File.ReadAllText(_stateFile)) : null;
        if (current?.GuardId != _saved.GuardId)
        {
            _restored = true; // Recovery already finished, or a new guard now owns the endpoint.
            return;
        }
        Restore(_volume, _saved);
        File.Delete(_stateFile);
        _restored = true;
    }

    public static bool RecoverPending(string stateFile) => RecoverPending(stateFile, saved =>
    {
        using var enumerator = new MMDeviceEnumerator();
        using var device = enumerator.GetDevice(saved.DeviceId);
        Restore(new EndpointVolume(device.AudioEndpointVolume), saved);
    });

    internal static bool RecoverPending(string stateFile, Func<string, IVolume> volumeFor) =>
        RecoverPending(stateFile, saved => Restore(volumeFor(saved.DeviceId), saved));

    private static bool RecoverPending(string stateFile, Action<SavedState> restore)
    {
        try
        {
            using var gate = StateLock();
            if (!File.Exists(stateFile)) return false;
            var saved = JsonSerializer.Deserialize<SavedState>(File.ReadAllText(stateFile));
            if (saved is null || IsLive(saved)) return false;
            restore(saved);
            File.Delete(stateFile);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException
            or System.Runtime.InteropServices.COMException or ArgumentException or TimeoutException)
        {
            return false;
        }
    }

    private static bool IsLive(SavedState saved)
    {
        if (saved.OwnerPid == Environment.ProcessId)
        {
            lock (Active) return saved.GuardId is { } id && Active.Contains(id);
        }
        if (saved.OwnerPid <= 0) return false; // Compatible with records from older versions.
        try
        {
            using var owner = Process.GetProcessById(saved.OwnerPid);
            return !owner.HasExited && owner.ProcessName.Equals("SonosFlow", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        { return false; }
    }

    private static void Restore(IVolume volume, SavedState saved)
    {
        // Lower the level before unmuting, and preserve a level the user changed meanwhile.
        if (saved.VolumeChanged && Math.Abs(volume.Level - 1f) < 0.0001f) volume.Level = saved.PreviousVolume;
        if (volume.Mute) volume.Mute = saved.WasMuted;
    }

    private static void Save(string stateFile, SavedState saved)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(stateFile))!);
        var tmp = stateFile + ".tmp";
        using (var stream = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            JsonSerializer.Serialize(stream, saved);
            stream.Flush(flushToDisk: true);
        }
        File.Move(tmp, stateFile, overwrite: false);
    }

    private static IDisposable StateLock()
    {
        var mutex = new Mutex(false, MutexName);
        try
        {
            try
            {
                if (!mutex.WaitOne(TimeSpan.FromSeconds(3))) throw new IOException("El registro de audio local está ocupado.");
            }
            catch (AbandonedMutexException) { }
            return new Releaser(mutex);
        }
        catch { mutex.Dispose(); throw; }
    }

    private sealed class Releaser(Mutex mutex) : IDisposable
    {
        public void Dispose() { mutex.ReleaseMutex(); mutex.Dispose(); }
    }
}
