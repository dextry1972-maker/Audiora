using System.Diagnostics;
using System.Runtime.Versioning;
using SonosFlow.Core.Audio;

namespace SonosFlow.Core.Streaming;

/// <summary>The real process and audio-session queries behind <see cref="SourceAppFollower"/>.</summary>
[SupportedOSPlatform("windows")]
internal sealed class WindowsSourceAppProbe : ISourceAppProbe
{
    public bool IsRunning(int pid) => AudioApps.IsRunning(pid);

    /// <summary>Only process names: no audio sessions, no command lines. Cheap enough to ask every few seconds.</summary>
    public bool AnyProcessOf(string exePath)
    {
        var processes = Process.GetProcessesByName(Path.GetFileNameWithoutExtension(exePath));
        try { return processes.Length > 0; }
        finally { foreach (var p in processes) p.Dispose(); }
    }

    public int? FindInstanceWithAudio(string exePath, bool isolatedYouTubeMusic) =>
        AudioApps.FindByExe(exePath, isolatedYouTubeMusic)?.ProcessId;
}
