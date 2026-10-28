using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;

namespace SonosFlow.Core.Audio;

/// <param name="ProcessId">Root process of the app (child audio processes are grouped under it).</param>
/// <param name="IsActive">Some audio session of the app is playing right now.</param>
/// <param name="OutputDeviceId">Output device the app plays on (the active one first), used to mute it locally.</param>
public sealed record AudioAppInfo(int ProcessId, string Name, string ExeName, string? ExePath, bool IsActive, bool IsIsolatedYouTubeMusic,
    string? OutputDeviceId = null);

/// <summary>
/// Applications that currently own an audio session on any output device, grouped by their root
/// process (browsers and Discord render audio from child processes). Read-only: changes nothing.
/// </summary>
[SupportedOSPlatform("windows")]
public static class AudioApps
{
    /// <summary>Profile folder of the dedicated YouTube Music browser instance (see <see cref="LaunchIsolatedYouTubeMusic"/>).</summary>
    public static string IsolatedProfileDir { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SonosFlow", "YouTubeMusicProfile");

    public static IReadOnlyList<AudioAppInfo> List()
    {
        var self = Environment.ProcessId;
        var sessions = new Dictionary<int, bool>(); // pid -> active
        var deviceOf = new Dictionary<int, (string Id, bool Active)>(); // pid -> output device (active wins)
        using (var enumerator = new MMDeviceEnumerator())
        {
            foreach (var device in enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
            {
                try
                {
                    var mgr = device.AudioSessionManager;
                    mgr.RefreshSessions();
                    var list = mgr.Sessions;
                    for (int i = 0; i < list.Count; i++)
                    {
                        var s = list[i];
                        int pid = (int)s.GetProcessID;
                        if (pid == 0 || pid == self || s.IsSystemSoundsSession) continue; // system sounds can't be captured per process
                        if (s.State == AudioSessionState.AudioSessionStateExpired) continue;
                        bool active = s.State == AudioSessionState.AudioSessionStateActive;
                        sessions[pid] = sessions.TryGetValue(pid, out var a) ? a || active : active;
                        if (!deviceOf.TryGetValue(pid, out var d) || (active && !d.Active)) deviceOf[pid] = (device.ID, active);
                    }
                }
                catch (COMException) { /* device vanished while enumerating */ }
                finally { device.Dispose(); }
            }
        }
        if (sessions.Count == 0) return [];

        var table = ProcessTable();
        var apps = new Dictionary<int, (bool Active, string Exe, string? Device, bool DeviceActive)>();
        foreach (var (pid, active) in sessions)
        {
            if (!table.TryGetValue(pid, out var entry)) continue; // process already gone
            int root = RootOf(pid, table);
            if (root == self) continue;
            var (devId, devActive) = deviceOf[pid];
            if (apps.TryGetValue(root, out var cur))
            {
                bool takeDevice = cur.Device is null || (devActive && !cur.DeviceActive);
                apps[root] = (cur.Active || active, cur.Exe, takeDevice ? devId : cur.Device, takeDevice ? devActive : cur.DeviceActive);
            }
            else apps[root] = (active, table[root].Exe, devId, devActive);
        }

        return apps.Select(kv =>
            {
                var path = ImagePath(kv.Key);
                bool isolated = ChromiumExes.Contains(kv.Value.Exe) && IsIsolatedInstance(kv.Key);
                var name = isolated ? "YouTube Music (instancia aislada)" : FriendlyName(path, kv.Value.Exe);
                return new AudioAppInfo(kv.Key, name, kv.Value.Exe, path, kv.Value.Active, isolated, kv.Value.Device);
            })
            .OrderByDescending(a => a.IsActive)
            .ThenBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// The process that owns the app's audio session (in browsers a child "audio service" process, not the root),
    /// the playing one first; 0 while the app has no session. Windows' per-app settings must be addressed through it.
    /// </summary>
    public static int SessionProcessOf(int rootPid)
    {
        int found = 0;
        bool foundActive = false;
        var table = ProcessTable();
        foreach (var (pid, active, _) in SessionList())
        {
            if (!table.ContainsKey(pid) || RootOf(pid, table) != rootPid) continue;
            if (found == 0 || (active && !foundActive)) (found, foundActive) = (pid, active);
        }
        return found;
    }

    /// <summary>Other running instances (other root processes) of the same executable that have audio sessions.</summary>
    public static int OtherInstancesWithAudio(int rootPid, string exePath)
    {
        var table = ProcessTable();
        return SessionList().Select(s => s.Pid)
            .Where(pid => table.ContainsKey(pid))
            .Select(pid => RootOf(pid, table))
            .Where(root => root != rootPid && string.Equals(ImagePath(root), exePath, StringComparison.OrdinalIgnoreCase))
            .Distinct().Count();
    }

    /// <summary>Every running process of <paramref name="exePath"/>, those with an audio session first.</summary>
    public static IReadOnlyList<int> ProcessesOf(string exePath)
    {
        var withSession = SessionList().Select(s => s.Pid).ToHashSet();
        var pids = new List<int>();
        foreach (var p in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(exePath)))
        {
            using (p)
                if (string.Equals(ImagePath(p.Id), exePath, StringComparison.OrdinalIgnoreCase)) pids.Add(p.Id);
        }
        return [.. pids.OrderByDescending(withSession.Contains)];
    }

    /// <summary>Outputs on which the app (any process under <paramref name="rootPid"/>) is playing right now.</summary>
    public static IReadOnlySet<string> PlayingOn(int rootPid)
    {
        var table = ProcessTable();
        return SessionList().Where(s => s.Active && table.ContainsKey(s.Pid) && RootOf(s.Pid, table) == rootPid)
            .Select(s => s.DeviceId).ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Processes owning a live (not expired) audio session on any output.</summary>
    private static List<(int Pid, bool Active, string DeviceId)> SessionList()
    {
        var result = new List<(int, bool, string)>();
        using var enumerator = new MMDeviceEnumerator();
        foreach (var device in enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
        {
            try
            {
                var mgr = device.AudioSessionManager;
                mgr.RefreshSessions();
                var list = mgr.Sessions;
                for (int i = 0; i < list.Count; i++)
                {
                    var s = list[i];
                    int pid = (int)s.GetProcessID;
                    if (pid == 0 || s.State == AudioSessionState.AudioSessionStateExpired) continue;
                    result.Add((pid, s.State == AudioSessionState.AudioSessionStateActive, device.ID));
                }
            }
            catch (COMException) { /* device vanished while enumerating */ }
            finally { device.Dispose(); }
        }
        return result;
    }

    /// <summary>Finds a running app by executable path (used when the selected app restarts with a new PID).</summary>
    public static AudioAppInfo? FindByExe(string exePath, bool isolatedYouTubeMusic) =>
        List().FirstOrDefault(a => a.IsIsolatedYouTubeMusic == isolatedYouTubeMusic
                                   && string.Equals(a.ExePath, exePath, StringComparison.OrdinalIgnoreCase));

    /// <summary>Root process (same executable) above <paramref name="pid"/>.</summary>
    public static int RootProcessId(int pid)
    {
        var table = ProcessTable();
        return table.ContainsKey(pid) ? RootOf(pid, table) : pid;
    }

    /// <summary>True when <paramref name="rootPid"/> is a browser instance using the dedicated YouTube Music profile.</summary>
    public static bool IsIsolatedInstance(int rootPid) =>
        CommandLine(rootPid)?.Contains(IsolatedProfileDir, StringComparison.OrdinalIgnoreCase) ?? false;

    public static bool IsRunning(int pid)
    {
        try { using var p = Process.GetProcessById(pid); return !p.HasExited; }
        catch (ArgumentException) { return false; }
        catch (InvalidOperationException) { return false; }
    }

    private static readonly HashSet<string> ChromiumExes = new(StringComparer.OrdinalIgnoreCase) { "brave.exe", "msedge.exe", "chrome.exe" };

    /// <summary>
    /// Opens music.youtube.com in a separate browser instance (Brave if installed, otherwise Edge or
    /// Chrome) with its own profile. A separate profile folder means a separate process tree, so it can
    /// be captured alone, without the tabs of the user's usual browser. Null when no browser is found.
    /// </summary>
    public static Process? LaunchIsolatedYouTubeMusic()
    {
        var browser = IsolationBrowserPath();
        if (browser is null) return null;
        Directory.CreateDirectory(IsolatedProfileDir);
        return Process.Start(new ProcessStartInfo(browser,
            $"--user-data-dir=\"{IsolatedProfileDir}\" --app=https://music.youtube.com --no-first-run --no-default-browser-check")
        {
            UseShellExecute = false,
        });
    }

    public static string? IsolationBrowserPath()
    {
        string pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        string pf86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string[] candidates =
        [
            Path.Combine(pf, "BraveSoftware", "Brave-Browser", "Application", "brave.exe"),
            Path.Combine(local, "BraveSoftware", "Brave-Browser", "Application", "brave.exe"),
            EdgePath() ?? "",
            Path.Combine(pf, "Google", "Chrome", "Application", "chrome.exe"),
            Path.Combine(pf86, "Google", "Chrome", "Application", "chrome.exe"),
        ];
        return candidates.FirstOrDefault(p => p.Length > 0 && File.Exists(p));
    }

    public static string? EdgePath()
    {
        foreach (var root in new[] { Environment.SpecialFolder.ProgramFilesX86, Environment.SpecialFolder.ProgramFiles })
        {
            var p = Path.Combine(Environment.GetFolderPath(root), "Microsoft", "Edge", "Application", "msedge.exe");
            if (File.Exists(p)) return p;
        }
        return null;
    }

    // ── Process helpers ────────────────────────────────────────

    /// <summary>Walks up while the parent runs the same executable (browser/Discord helper processes).</summary>
    internal static int RootOf(int pid, IReadOnlyDictionary<int, (int Parent, string Exe)> table)
    {
        int current = pid;
        for (int guard = 0; guard < 32; guard++)
        {
            var (parent, exe) = table[current];
            if (parent == 0 || parent == current || !table.TryGetValue(parent, out var p)
                || !p.Exe.Equals(exe, StringComparison.OrdinalIgnoreCase)) break;
            current = parent;
        }
        return current;
    }

    private static string FriendlyName(string? path, string exe)
    {
        if (path is not null)
        {
            try
            {
                var desc = FileVersionInfo.GetVersionInfo(path).FileDescription?.Trim();
                if (!string.IsNullOrEmpty(desc)) return desc;
            }
            catch (FileNotFoundException) { }
        }
        return Path.GetFileNameWithoutExtension(exe);
    }

    private static Dictionary<int, (int Parent, string Exe)> ProcessTable()
    {
        var table = new Dictionary<int, (int, string)>();
        var snap = CreateToolhelp32Snapshot(0x2 /* TH32CS_SNAPPROCESS */, 0);
        if (snap == IntPtr.Zero || snap == new IntPtr(-1)) return table;
        try
        {
            var entry = new ProcessEntry32 { dwSize = (uint)Marshal.SizeOf<ProcessEntry32>() };
            for (bool ok = Process32FirstW(snap, ref entry); ok; ok = Process32NextW(snap, ref entry))
                table[(int)entry.th32ProcessID] = ((int)entry.th32ParentProcessID, entry.szExeFile);
        }
        finally { CloseHandle(snap); }
        return table;
    }

    private static string? ImagePath(int pid)
    {
        var h = OpenProcess(0x1000 /* PROCESS_QUERY_LIMITED_INFORMATION */, false, pid);
        if (h == IntPtr.Zero) return null;
        try
        {
            var sb = new System.Text.StringBuilder(1024);
            int size = sb.Capacity;
            return QueryFullProcessImageNameW(h, 0, sb, ref size) ? sb.ToString() : null;
        }
        finally { CloseHandle(h); }
    }

    /// <summary>Command line of another process (ProcessCommandLineInformation, Windows 8.1+).</summary>
    private static string? CommandLine(int pid)
    {
        var h = OpenProcess(0x1000, false, pid);
        if (h == IntPtr.Zero) return null;
        try
        {
            NtQueryInformationProcess(h, 60, IntPtr.Zero, 0, out var needed);
            if (needed <= 0) return null;
            var buf = Marshal.AllocHGlobal(needed);
            try
            {
                if (NtQueryInformationProcess(h, 60, buf, needed, out _) != 0) return null;
                var us = Marshal.PtrToStructure<UnicodeString>(buf);
                return us.Buffer == IntPtr.Zero ? null : Marshal.PtrToStringUni(us.Buffer, us.Length / 2);
            }
            finally { Marshal.FreeHGlobal(buf); }
        }
        finally { CloseHandle(h); }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ProcessEntry32
    {
        public uint dwSize, cntUsage, th32ProcessID;
        public IntPtr th32DefaultHeapID;
        public uint th32ModuleID, cntThreads, th32ParentProcessID;
        public int pcPriClassBase;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szExeFile;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct UnicodeString
    {
        public ushort Length, MaximumLength;
        public IntPtr Buffer;
    }

    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint pid);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern bool Process32FirstW(IntPtr snap, ref ProcessEntry32 entry);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern bool Process32NextW(IntPtr snap, ref ProcessEntry32 entry);
    [DllImport("kernel32.dll")] private static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr h);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern bool QueryFullProcessImageNameW(IntPtr h, int flags, System.Text.StringBuilder name, ref int size);
    [DllImport("ntdll.dll")] private static extern int NtQueryInformationProcess(IntPtr h, int infoClass, IntPtr info, int length, out int returnLength);
}
