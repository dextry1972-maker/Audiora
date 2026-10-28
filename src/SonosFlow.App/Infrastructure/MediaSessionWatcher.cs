using System.Runtime.InteropServices;
using System.Windows.Threading;
using Windows.Media.Control;

namespace SonosFlow.App.Infrastructure;

/// <summary>What Windows says is playing (title and artist as the app publishes them).</summary>
public sealed record NowPlaying(string Title, string Artist, string SourceAumid);

/// <summary>Whose media session to follow.</summary>
/// <param name="WholePc">Whole-PC capture: follow Windows' current media session.</param>
/// <param name="ProcessId">Root process of the captured application (0 = none).</param>
/// <param name="ExeName">Its executable, e.g. "brave.exe".</param>
/// <param name="IsolatedYouTubeMusic">The dedicated YouTube Music browser instance.</param>
public sealed record MediaTarget(bool WholePc, int ProcessId = 0, string? ExeName = null, bool IsolatedYouTubeMusic = false)
{
    public static MediaTarget Pc { get; } = new(true);
    /// <summary>App mode with no application running: no title.</summary>
    public static MediaTarget None { get; } = new(false);
    public static MediaTarget App(int processId, string exeName, bool isolatedYouTubeMusic) => new(false, processId, exeName, isolatedYouTubeMusic);
}

/// <summary>
/// Follows the Windows media sessions (Global System Media Transport Controls, the same data as the
/// volume flyout) and reports the title/artist of the session that belongs to the current source.
/// Read-only: never sends play/pause or any other command to the apps.
/// <para>
/// GSMTC raises its events on MTA threads with no synchronization context: everything is marshalled
/// to the UI dispatcher, debounced (apps often publish the title and the artist in separate updates)
/// and resolved off the UI thread. Failures (COM errors, sessions closing mid-query, GSMTC missing)
/// only clear the title; nothing is thrown to the UI.
/// </para>
/// </summary>
public sealed class MediaSessionWatcher : IDisposable
{
    private static readonly TimeSpan Debounce = TimeSpan.FromMilliseconds(300);
    private static readonly TimeSpan MaxDelay = TimeSpan.FromMilliseconds(1500);
    private static readonly TimeSpan CallTimeout = TimeSpan.FromSeconds(3);

    private readonly Dispatcher _ui;
    private readonly DispatcherTimer _debounce;
    private readonly object _sync = new();
    private readonly List<GlobalSystemMediaTransportControlsSession> _hooked = [];
    private GlobalSystemMediaTransportControlsSessionManager? _manager;
    private MediaTarget _target;
    private NowPlaying? _last;
    private string? _lastAumid;
    private bool _started;
    private volatile bool _disposed;
    private int _sessionsDirty = 1;
    private int _generation;
    private DateTime _firstPendingKick = DateTime.MinValue;

    /// <summary>Raised on the UI thread when the title changes; null = nothing to show.</summary>
    public event Action<NowPlaying?>? Changed;

    public MediaSessionWatcher(Dispatcher ui, MediaTarget target)
    {
        _ui = ui;
        _target = target;
        _debounce = new DispatcherTimer(DispatcherPriority.Background, ui) { Interval = Debounce };
        _debounce.Tick += (_, _) => RefreshNow();
    }

    public MediaTarget Target => _target;

    /// <summary>Connects to GSMTC in the background. Call once, on the UI thread.</summary>
    public void Start()
    {
        if (_started || _disposed) return;
        _started = true;
        _ = Task.Run(ConnectAsync);
    }

    /// <summary>Follows another source (UI thread). Same target: nothing happens.</summary>
    public void SetTarget(MediaTarget target)
    {
        if (_disposed || target == _target) return;
        _target = target;
        RefreshNow();
    }

    private async Task ConnectAsync()
    {
        try
        {
            var manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync().AsTask().WaitAsync(CallTimeout);
            lock (_sync)
            {
                if (_disposed || manager is null) return;
                _manager = manager;
                manager.SessionsChanged += OnSessionsChanged;
                manager.CurrentSessionChanged += OnCurrentSessionChanged;
            }
            Post(RefreshNow);
        }
        catch (Exception ex) when (IsExpected(ex))
        {
            AppLog.Write($"medios: no se pudo leer la información de reproducción de Windows ({ex.GetType().Name}: {ex.Message})");
        }
    }

    // ── GSMTC events (MTA threads) ──────────────────────────────

    private void OnSessionsChanged(GlobalSystemMediaTransportControlsSessionManager sender, SessionsChangedEventArgs args)
    {
        Interlocked.Exchange(ref _sessionsDirty, 1);
        Post(Kick);
    }

    private void OnCurrentSessionChanged(GlobalSystemMediaTransportControlsSessionManager sender, CurrentSessionChangedEventArgs args) => Post(Kick);

    private void OnMediaPropertiesChanged(GlobalSystemMediaTransportControlsSession sender, MediaPropertiesChangedEventArgs args) => Post(Kick);

    private void OnPlaybackInfoChanged(GlobalSystemMediaTransportControlsSession sender, PlaybackInfoChangedEventArgs args) => Post(Kick);

    private void Post(Action action)
    {
        if (_disposed) return;
        _ui.BeginInvoke(DispatcherPriority.Background, action);
    }

    // ── Debounce and refresh (UI thread) ────────────────────────

    /// <summary>Trailing 300 ms debounce, but a steady stream of events still refreshes every 1.5 s.</summary>
    private void Kick()
    {
        if (_disposed) return;
        var now = DateTime.UtcNow;
        if (!_debounce.IsEnabled) _firstPendingKick = now;
        else if (now - _firstPendingKick >= MaxDelay) return; // let the pending tick fire
        _debounce.Stop();
        _debounce.Start();
    }

    private void RefreshNow()
    {
        _debounce.Stop();
        if (_disposed) return;
        var target = _target;
        int generation = ++_generation;
        bool rehook = Interlocked.Exchange(ref _sessionsDirty, 0) == 1;
        _ = Task.Run(async () =>
        {
            var result = await ResolveSafeAsync(target, rehook);
            Post(() => Deliver(generation, result));
        });
    }

    private void Deliver(int generation, NowPlaying? result)
    {
        if (_disposed || generation != _generation) return; // a newer refresh is on its way
        if (result?.SourceAumid != _lastAumid)
        {
            _lastAumid = result?.SourceAumid;
            AppLog.Write($"medios: sesión {_lastAumid ?? "ninguna"} ({Describe(_target)})");
        }
        if (result == _last) return;
        _last = result;
        Changed?.Invoke(result);
    }

    private static string Describe(MediaTarget t) => t.WholePc ? "todo el PC"
        : t.ExeName is null ? "sin aplicación"
        : $"{t.ExeName}{(t.IsolatedYouTubeMusic ? " aislado" : "")} pid {t.ProcessId}";

    // ── Resolution (thread pool) ────────────────────────────────

    private async Task<NowPlaying?> ResolveSafeAsync(MediaTarget target, bool rehook)
    {
        try { return await ResolveAsync(target, rehook); }
        catch (Exception ex) when (IsExpected(ex))
        {
            // Typical: the session closed while it was being read. The next event refreshes again.
            Interlocked.Exchange(ref _sessionsDirty, 1);
            return null;
        }
        catch (Exception ex) // fire-and-forget boundary: a title is never worth a crash or an error dialog
        {
            Interlocked.Exchange(ref _sessionsDirty, 1);
            AppLog.Write($"medios: error inesperado al leer la reproducción ({ex.GetType().Name}: {ex.Message})");
            return null;
        }
    }

    private async Task<NowPlaying?> ResolveAsync(MediaTarget target, bool rehook)
    {
        GlobalSystemMediaTransportControlsSessionManager? manager;
        IReadOnlyList<GlobalSystemMediaTransportControlsSession> sessions;
        lock (_sync)
        {
            manager = _manager;
            if (_disposed || manager is null) return null;
            sessions = manager.GetSessions();
            if (rehook || sessions.Count != _hooked.Count) Rehook(sessions);
        }

        var session = Pick(manager, sessions, target);
        if (session is null) return null;
        var props = await session.TryGetMediaPropertiesAsync().AsTask().WaitAsync(CallTimeout);
        var title = props?.Title?.Trim();
        if (string.IsNullOrEmpty(title)) return null;
        return new NowPlaying(title, props!.Artist?.Trim() ?? "", session.SourceAppUserModelId ?? "");
    }

    /// <summary>
    /// (1) a session whose AUMID is the AUMID of one of the captured process's windows;
    /// (2) else a session named after the executable (Brave, Chrome, MSEdge, Spotify.exe…);
    /// (3) else: whole PC → Windows' current session; one application → nothing (better no title than a wrong one).
    /// </summary>
    private static GlobalSystemMediaTransportControlsSession? Pick(GlobalSystemMediaTransportControlsSessionManager manager,
        IReadOnlyList<GlobalSystemMediaTransportControlsSession> sessions, MediaTarget target)
    {
        if (target.WholePc)
            return manager.GetCurrentSession() ?? sessions.FirstOrDefault(IsPlaying);
        if (target.ExeName is null) return null;

        var windowIds = WindowAppIds.ForProcess(target.ProcessId);
        if (windowIds.Count > 0 && Best(sessions.Where(s => windowIds.Contains(Aumid(s))), manager) is { } exact)
            return exact;
        return Best(sessions.Where(s => MatchesExecutable(Aumid(s), target)), manager);
    }

    private static GlobalSystemMediaTransportControlsSession? Best(IEnumerable<GlobalSystemMediaTransportControlsSession> candidates,
        GlobalSystemMediaTransportControlsSessionManager manager)
    {
        var list = candidates.ToList();
        if (list.Count <= 1) return list.FirstOrDefault();
        if (list.FirstOrDefault(IsPlaying) is { } playing) return playing;
        var current = Aumid(manager.GetCurrentSession());
        return list.FirstOrDefault(s => Aumid(s) == current) ?? list[0];
    }

    private static readonly Dictionary<string, string> ExePrefixes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["brave.exe"] = "Brave",
        ["chrome.exe"] = "Chrome",
        ["msedge.exe"] = "MSEdge",
        ["spotify.exe"] = "Spotify.exe",
    };

    private static readonly HashSet<string> Chromium = new(StringComparer.OrdinalIgnoreCase) { "brave.exe", "chrome.exe", "msedge.exe" };

    /// <summary>Rule (2), used when no window of the process carries a matching AUMID.</summary>
    internal static bool MatchesExecutable(string aumid, MediaTarget target)
    {
        if (aumid.Length == 0 || target.ExeName is not { } exe) return false;
        if (target.IsolatedYouTubeMusic)
            return ExePrefixes.TryGetValue(exe, out var p) && aumid.StartsWith(p + ".", StringComparison.OrdinalIgnoreCase)
                   && aumid.Contains("music.youtube.com", StringComparison.OrdinalIgnoreCase);
        if (Chromium.Contains(exe))
            return ExePrefixes.TryGetValue(exe, out var p) && aumid.Equals(p, StringComparison.OrdinalIgnoreCase); // the normal browser, not its web apps
        if (ExePrefixes.TryGetValue(exe, out var prefix)
            && (aumid.Equals(prefix, StringComparison.OrdinalIgnoreCase) || aumid.StartsWith(prefix + ".", StringComparison.OrdinalIgnoreCase)))
            return true;
        if (exe.Equals("spotify.exe", StringComparison.OrdinalIgnoreCase) && aumid.StartsWith("SpotifyAB.SpotifyMusic_", StringComparison.OrdinalIgnoreCase))
            return true; // Microsoft Store version
        // Desktop apps without their own AUMID are published under the executable name or path.
        return aumid.Equals(exe, StringComparison.OrdinalIgnoreCase) || aumid.EndsWith("\\" + exe, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsPlaying(GlobalSystemMediaTransportControlsSession s) =>
        s.GetPlaybackInfo()?.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;

    private static string Aumid(GlobalSystemMediaTransportControlsSession? s) => s?.SourceAppUserModelId ?? "";

    /// <summary>Caller holds <see cref="_sync"/>.</summary>
    private void Rehook(IReadOnlyList<GlobalSystemMediaTransportControlsSession> sessions)
    {
        UnhookSessions();
        foreach (var s in sessions)
        {
            try
            {
                s.MediaPropertiesChanged += OnMediaPropertiesChanged;
                s.PlaybackInfoChanged += OnPlaybackInfoChanged;
                _hooked.Add(s);
            }
            catch (Exception ex) when (IsExpected(ex)) { /* closed meanwhile */ }
        }
    }

    private void UnhookSessions()
    {
        foreach (var s in _hooked)
        {
            try
            {
                s.MediaPropertiesChanged -= OnMediaPropertiesChanged;
                s.PlaybackInfoChanged -= OnPlaybackInfoChanged;
            }
            catch (Exception ex) when (IsExpected(ex)) { /* already gone */ }
        }
        _hooked.Clear();
    }

    /// <summary>Errors WinRT/COM report for closed sessions, a missing GSMTC, or a call that never answers.</summary>
    private static bool IsExpected(Exception ex) => ex is COMException or InvalidOperationException or UnauthorizedAccessException
        or ObjectDisposedException or ArgumentException or InvalidCastException or NotSupportedException or TimeoutException
        or OperationCanceledException or TypeLoadException;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _debounce.Stop();
        lock (_sync)
        {
            UnhookSessions();
            if (_manager is { } m)
            {
                try
                {
                    m.SessionsChanged -= OnSessionsChanged;
                    m.CurrentSessionChanged -= OnCurrentSessionChanged;
                }
                catch (Exception ex) when (IsExpected(ex)) { }
                _manager = null;
            }
        }
        Changed = null;
    }
}

/// <summary>AppUserModelID of top-level windows (what the taskbar groups by, and what browsers name their media sessions after).</summary>
internal static class WindowAppIds
{
    public static HashSet<string> ForProcess(int processId)
    {
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (processId <= 0) return ids;
        EnumWindows((hwnd, _) =>
        {
            GetWindowThreadProcessId(hwnd, out var owner);
            if (owner == (uint)processId && Of(hwnd) is { Length: > 0 } id) ids.Add(id);
            return true;
        }, IntPtr.Zero);
        return ids;
    }

    public static string? Of(IntPtr hwnd)
    {
        var iid = typeof(IPropertyStore).GUID;
        if (SHGetPropertyStoreForWindow(hwnd, ref iid, out var store) != 0 || store is null) return null;
        try
        {
            var key = AppUserModelIdKey;
            if (store.GetValue(ref key, out var value) != 0) return null;
            try { return value.Type == VtLpwstr && value.Pointer != IntPtr.Zero ? Marshal.PtrToStringUni(value.Pointer) : null; }
            finally { PropVariantClear(ref value); }
        }
        catch (COMException) { return null; }
        finally { Marshal.ReleaseComObject(store); }
    }

    private const ushort VtLpwstr = 31;
    private static readonly PropertyKey AppUserModelIdKey = new(new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"), 5);

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct PropertyKey(Guid fmtid, uint pid)
    {
        public readonly Guid FormatId = fmtid;
        public readonly uint PropertyId = pid;
    }

    /// <summary>PROPVARIANT (16 bytes on x86, 24 on x64: sized for the larger); only the VT_LPWSTR case is read.</summary>
    [StructLayout(LayoutKind.Explicit, Size = 24)]
    private struct PropVariant
    {
        [FieldOffset(0)] public ushort Type;
        [FieldOffset(8)] public IntPtr Pointer;
    }

    [ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyStore
    {
        [PreserveSig] int GetCount(out uint count);
        [PreserveSig] int GetAt(uint index, out PropertyKey key);
        [PreserveSig] int GetValue(ref PropertyKey key, out PropVariant value);
        [PreserveSig] int SetValue(ref PropertyKey key, ref PropVariant value);
        [PreserveSig] int Commit();
    }

    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);
    [DllImport("shell32.dll")] private static extern int SHGetPropertyStoreForWindow(IntPtr hwnd, ref Guid iid, [MarshalAs(UnmanagedType.Interface)] out IPropertyStore? store);
    [DllImport("ole32.dll")] private static extern int PropVariantClear(ref PropVariant value);
}
