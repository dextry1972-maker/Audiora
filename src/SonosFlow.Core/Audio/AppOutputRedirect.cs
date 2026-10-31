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
    private sealed record Entry(string ExePath, string? PreviousDeviceId, string TargetDeviceId, int OwnerPid, string? GuardId = null);

    private const string MutexName = @"Local\SonosFlow-AppOutputGuard";
    private static readonly HashSet<string> Active = new(StringComparer.OrdinalIgnoreCase); // live redirects of this process

    internal interface IRouting
    {
        string? Get(int pid);
        void Set(int pid, string? deviceId);
        IReadOnlyList<int> ProcessesOf(string exePath);
    }

    private sealed class WindowsRouting : IRouting
    {
        public string? Get(int pid) => AppOutputRouting.Get(pid);
        public void Set(int pid, string? deviceId) => AppOutputRouting.Set(pid, deviceId);
        public IReadOnlyList<int> ProcessesOf(string exePath) => AudioApps.ProcessesOf(exePath);
    }

    private readonly IRouting _routing;
    private readonly string _guardId = Guid.NewGuid().ToString("N");
    private readonly string _exePath;
    private readonly string _stateFile;
    private bool _disposed;

    /// <summary>False when no process of the app accepted the restore yet: it stays pending (see <see cref="RecoverPending"/>).</summary>
    public bool Restored { get; private set; }

    public AppOutputRedirect(int sessionProcessId, string exePath, string targetDeviceId, string stateFile)
        : this(sessionProcessId, exePath, targetDeviceId, stateFile, new WindowsRouting()) { }

    internal AppOutputRedirect(int sessionProcessId, string exePath, string targetDeviceId, string stateFile, IRouting routing)
    {
        _routing = routing;
        _exePath = exePath;
        _stateFile = stateFile;
        using (StateLock())
        {
            var entries = Load(stateFile);
            // A record left by a lost restore still holds the app's real previous output: keep it, never our target.
            var existing = entries.FirstOrDefault(e => Same(e.ExePath, exePath));
            if (existing is not null && IsLive(existing))
                throw new InvalidOperationException("The application already has a live output redirect.");
            var current = _routing.Get(sessionProcessId);
            string? previous = existing is not null && Same(current, existing.TargetDeviceId)
                ? existing.PreviousDeviceId : current;
            if (Same(previous, targetDeviceId)) previous = null;
            entries.RemoveAll(e => Same(e.ExePath, exePath));
            var entry = new Entry(exePath, previous, targetDeviceId, Environment.ProcessId, _guardId);
            entries.Add(entry);
            Save(stateFile, entries); // recorded before anything changes
            try
            {
                _routing.Set(sessionProcessId, targetDeviceId);
            }
            catch (Exception ex) when (IsRoutingFailure(ex))
            {
                // One role may already have changed: put it back; if that also fails the record stays pending.
                if (TryRestore(entry, _routing)) { entries.Remove(entry); Save(stateFile, entries); }
                throw;
            }
            lock (Active) Active.Add(_guardId);
        }
    }

    public void Dispose()
    {
        IDisposable stateLock;
        try { stateLock = StateLock(); }
        catch
        {
            // Leave the undo record pending for recovery even when teardown cannot acquire its lock.
            lock (Active) Active.Remove(_guardId);
            throw;
        }
        using (stateLock)
        {
            if (_disposed) return;
            try
            {
                var entries = Load(_stateFile);
                var entry = entries.FirstOrDefault(e => Same(e.ExePath, _exePath) && e.GuardId == _guardId);
                Restored = entry is null || TryRestore(entry, _routing);
                if (entry is not null && Restored) { entries.Remove(entry); Save(_stateFile, entries); }
                _disposed = true;
            }
            finally { lock (Active) Active.Remove(_guardId); }
        }
    }

    /// <summary>
    /// Restores apps left redirected by a crash or a failed restore. Skips live redirects (this process's and another
    /// running Audiora's). True when nothing is pending any more.
    /// </summary>
    public static bool RecoverPending(string stateFile) => RecoverPending(stateFile, new WindowsRouting());

    internal static bool RecoverPending(string stateFile, IRouting routing)
    {
        try
        {
            using (StateLock())
            {
                var entries = Load(stateFile);
                bool pending = false, changed = false;
                foreach (var e in entries.ToList())
                {
                    if (IsLive(e)) continue;
                    if (TryRestore(e, routing)) { entries.Remove(e); changed = true; }
                    else pending = true;
                }
                if (changed) Save(stateFile, entries);
                return !pending;
            }
        }
        catch (Exception ex) when (IsRoutingFailure(ex)) { return false; }
    }

    /// <summary>Exceptions a failed per-app output call can raise (the factory is undocumented and may change).</summary>
    public static bool IsRoutingFailure(Exception ex) =>
        ex is COMException or ArgumentException or InvalidCastException or UnauthorizedAccessException or NotImplementedException
            or InvalidOperationException or EntryPointNotFoundException or DllNotFoundException or IOException;

    private static bool IsLive(Entry e)
    {
        if (e.OwnerPid == Environment.ProcessId) { lock (Active) return Active.Contains(e.GuardId ?? e.ExePath); }
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

    private static bool TryRestore(Entry e, IRouting routing)
    {
        foreach (var pid in routing.ProcessesOf(e.ExePath))
        {
            try
            {
                // Only undo our own change: if the user picked another output meanwhile, leave theirs.
                if (Same(routing.Get(pid), e.TargetDeviceId)) routing.Set(pid, e.PreviousDeviceId);
                return true;
            }
            catch (Exception ex) when (IsRoutingFailure(ex)) { /* this process has no audio session: try the next */ }
        }
        return false;
    }

    private static bool Same(string? a, string? b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    private static List<Entry> Load(string stateFile)
    {
        try
        {
            var entries = JsonSerializer.Deserialize<List<Entry>>(File.ReadAllText(stateFile));
            if (entries is null || entries.Any(e => e is null || string.IsNullOrWhiteSpace(e.ExePath)
                    || string.IsNullOrWhiteSpace(e.TargetDeviceId)))
                throw new IOException("The output redirect recovery state is invalid.");
            return entries;
        }
        catch (FileNotFoundException) { return []; }
        catch (DirectoryNotFoundException) { return []; }
        catch (JsonException ex) { throw new IOException("The output redirect recovery state is unreadable.", ex); }
    }

    private static void Save(string stateFile, List<Entry> entries)
    {
        if (entries.Count == 0) { try { File.Delete(stateFile); } catch (IOException) { } return; }
        Directory.CreateDirectory(Path.GetDirectoryName(stateFile)!);
        var tmp = stateFile + ".tmp";
        using (var stream = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            JsonSerializer.Serialize(stream, entries);
            stream.Flush(flushToDisk: true);
        }
        File.Move(tmp, stateFile, overwrite: true);
    }

    private static IDisposable StateLock()
    {
        var mutex = new Mutex(false, MutexName);
        try
        {
            try
            {
                if (!mutex.WaitOne(TimeSpan.FromSeconds(3)))
                    throw new IOException("Timed out waiting for the output redirect recovery lock.");
            }
            catch (AbandonedMutexException) { /* The abandoned mutex is now owned by this thread. */ }
            return new Releaser(mutex);
        }
        catch { mutex.Dispose(); throw; }
    }

    private sealed class Releaser(Mutex mutex) : IDisposable
    {
        public void Dispose()
        {
            try { mutex.ReleaseMutex(); }
            finally { mutex.Dispose(); }
        }
    }
}
