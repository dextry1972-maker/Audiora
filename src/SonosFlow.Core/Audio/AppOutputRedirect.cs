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
/// <para>
/// Windows remembers the per-app output per executable, so each change is recorded first in a shared state file
/// (one record per executable, holding the app's real previous output even across failed restores) and undone on
/// Dispose, or later by <see cref="RecoverPending"/> after a crash. Records of another running Audiora instance are
/// left alone; a named mutex serialises all access. Windows only accepts a process that has an audio session, so a
/// restore tries every process of the app, those with a session first, and stays pending until one is accepted.
/// </para>
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class AppOutputRedirect : IDisposable
{
    private sealed record Entry(string ExePath, string? PreviousDeviceId, string TargetDeviceId, int OwnerPid);

    private const string MutexName = @"Local\SonosFlow-AppOutputGuard";
    private static readonly HashSet<string> Active = new(StringComparer.OrdinalIgnoreCase); // live redirects of this process

    private readonly string _exePath;
    private readonly string _stateFile;
    private bool _disposed;

    /// <summary>False when no process of the app accepted the restore yet: it stays pending (see <see cref="RecoverPending"/>).</summary>
    public bool Restored { get; private set; }

    public AppOutputRedirect(int sessionProcessId, string exePath, string targetDeviceId, string stateFile)
    {
        _exePath = exePath;
        _stateFile = stateFile;
        using (StateLock())
        {
            var entries = Load(stateFile);
            // A record left by a lost restore still holds the app's real previous output: keep it, never our target.
            var existing = entries.FirstOrDefault(e => Same(e.ExePath, exePath));
            string? previous = existing is not null ? existing.PreviousDeviceId : AppOutputRouting.Get(sessionProcessId);
            if (Same(previous, targetDeviceId)) previous = null;
            entries.RemoveAll(e => Same(e.ExePath, exePath));
            var entry = new Entry(exePath, previous, targetDeviceId, Environment.ProcessId);
            entries.Add(entry);
            Save(stateFile, entries); // recorded before anything changes
            try
            {
                AppOutputRouting.Set(sessionProcessId, targetDeviceId);
            }
            catch (Exception ex) when (IsRoutingFailure(ex))
            {
                // One role may already have changed: put it back; if that also fails the record stays pending.
                if (TryRestore(entry)) { entries.Remove(entry); Save(stateFile, entries); }
                throw;
            }
        }
        lock (Active) Active.Add(exePath);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        lock (Active) Active.Remove(_exePath);
        using (StateLock())
        {
            var entries = Load(_stateFile);
            var entry = entries.FirstOrDefault(e => Same(e.ExePath, _exePath));
            Restored = entry is null || TryRestore(entry);
            if (entry is not null && Restored) { entries.Remove(entry); Save(_stateFile, entries); }
        }
    }

    /// <summary>
    /// Restores apps left redirected by a crash or a failed restore. Skips live redirects (this process's and another
    /// running Audiora's). True when nothing is pending any more.
    /// </summary>
    public static bool RecoverPending(string stateFile)
    {
        if (!File.Exists(stateFile)) return true;
        using (StateLock())
        {
            var entries = Load(stateFile);
            bool pending = false, changed = false;
            foreach (var e in entries.ToList())
            {
                if (IsLive(e)) continue;
                if (TryRestore(e)) { entries.Remove(e); changed = true; }
                else pending = true;
            }
            if (changed) Save(stateFile, entries);
            return !pending;
        }
    }

    /// <summary>Exceptions a failed per-app output call can raise (the factory is undocumented and may change).</summary>
    public static bool IsRoutingFailure(Exception ex) =>
        ex is COMException or ArgumentException or InvalidCastException or UnauthorizedAccessException or NotImplementedException
            or InvalidOperationException or EntryPointNotFoundException or DllNotFoundException or IOException;

    private static bool IsLive(Entry e)
    {
        if (e.OwnerPid == Environment.ProcessId) { lock (Active) return Active.Contains(e.ExePath); }
        try
        {
            using var owner = Process.GetProcessById(e.OwnerPid);
            return !owner.HasExited && owner.ProcessName.Equals("SonosFlow", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    private static bool TryRestore(Entry e)
    {
        foreach (var pid in AudioApps.ProcessesOf(e.ExePath))
        {
            try
            {
                // Only undo our own change: if the user picked another output meanwhile, leave theirs.
                if (Same(AppOutputRouting.Get(pid), e.TargetDeviceId)) AppOutputRouting.Set(pid, e.PreviousDeviceId);
                return true;
            }
            catch (Exception ex) when (IsRoutingFailure(ex)) { /* this process has no audio session: try the next */ }
        }
        return false;
    }

    private static bool Same(string? a, string? b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    private static List<Entry> Load(string stateFile)
    {
        try { return File.Exists(stateFile) ? JsonSerializer.Deserialize<List<Entry>>(File.ReadAllText(stateFile)) ?? [] : []; }
        catch (JsonException) { return []; } // written atomically, so only a hand-edited file gets here
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return []; }
    }

    private static void Save(string stateFile, List<Entry> entries)
    {
        if (entries.Count == 0) { try { File.Delete(stateFile); } catch (IOException) { } return; }
        Directory.CreateDirectory(Path.GetDirectoryName(stateFile)!);
        var tmp = stateFile + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(entries));
        File.Move(tmp, stateFile, overwrite: true);
    }

    private static IDisposable StateLock()
    {
        var mutex = new Mutex(false, MutexName);
        try { mutex.WaitOne(TimeSpan.FromSeconds(5)); }
        catch (AbandonedMutexException) { /* the previous owner died: the file is still consistent (atomic writes) */ }
        return new Releaser(mutex);
    }

    private sealed class Releaser(Mutex mutex) : IDisposable
    {
        public void Dispose()
        {
            try { mutex.ReleaseMutex(); } catch (ApplicationException) { /* not owned (wait timed out) */ }
            mutex.Dispose();
        }
    }
}
