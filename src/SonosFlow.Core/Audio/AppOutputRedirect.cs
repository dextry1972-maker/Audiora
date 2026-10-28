using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text.Json;

namespace SonosFlow.Core.Audio;

/// <summary>
/// "Silenciar el PC" in «Una aplicación» mode: sends only that app to an output nobody listens to, so it goes quiet
/// locally while the per-app capture (and the Sonos) keep receiving it; every other app, the device's mute and the
/// Windows volume stay untouched. Muting the app's session instead is not an option: session mute and volume are
/// applied before process loopback (measured with CaptureCheck local-silence: muted → −163 dBFS in the capture).
/// Windows remembers the per-app output, so the previous value is written to disk first and restored on Dispose,
/// or on the next launch after a crash (as soon as the app runs again: Windows needs one of its processes).
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class AppOutputRedirect : IDisposable
{
    private sealed record SavedState(string ExePath, string? PreviousDeviceId, string TargetDeviceId);

    private readonly int _processId;
    private readonly SavedState _saved;
    private readonly string _stateFile;
    private bool _restored;

    public string TargetDeviceId => _saved.TargetDeviceId;

    public AppOutputRedirect(int processId, string exePath, string targetDeviceId, string stateFile)
    {
        _processId = processId;
        _stateFile = stateFile;
        _saved = new SavedState(exePath, AppOutputRouting.Get(processId), targetDeviceId);
        Directory.CreateDirectory(Path.GetDirectoryName(stateFile)!);
        File.WriteAllText(stateFile, JsonSerializer.Serialize(_saved));
        AppOutputRouting.Set(processId, targetDeviceId);
    }

    /// <summary>False when the app was not running, so the restore stays pending (see <see cref="RecoverPending"/>).</summary>
    public bool Restored => _restored;

    public void Dispose()
    {
        if (_restored) return;
        int? pid = AudioApps.IsRunning(_processId) ? _processId : AudioApps.AnyProcessOf(_saved.ExePath);
        if (pid is null) return; // the state file stays; RecoverPending finishes the job when the app runs again
        _restored = Restore(pid.Value, _saved);
        if (_restored) TryDelete(_stateFile);
    }

    /// <summary>Undo a redirect left behind by a crash. True when nothing is pending any more.</summary>
    public static bool RecoverPending(string stateFile)
    {
        if (!File.Exists(stateFile)) return true;
        try
        {
            var saved = JsonSerializer.Deserialize<SavedState>(File.ReadAllText(stateFile));
            if (saved is null) { TryDelete(stateFile); return true; }
            if (AudioApps.AnyProcessOf(saved.ExePath) is not { } pid) return false; // app not running: try again later
            if (Restore(pid, saved)) TryDelete(stateFile);
            return !File.Exists(stateFile);
        }
        catch (JsonException) { TryDelete(stateFile); return true; }
        catch (IOException) { return false; }
    }

    private static bool Restore(int processId, SavedState saved)
    {
        try
        {
            // Only undo our own change: if the user picked another output meanwhile, leave theirs.
            if (string.Equals(AppOutputRouting.Get(processId), saved.TargetDeviceId, StringComparison.OrdinalIgnoreCase))
                AppOutputRouting.Set(processId, saved.PreviousDeviceId);
            return true;
        }
        catch (Exception ex) when (ex is COMException or ArgumentException or InvalidOperationException)
        {
            return false;
        }
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch (IOException) { }
    }
}
