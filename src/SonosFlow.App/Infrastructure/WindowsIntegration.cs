using System.Diagnostics;
using Microsoft.Win32;

namespace SonosFlow.App.Infrastructure;

/// <summary>Per-user (HKCU) integration only: start with Windows and uninstall. Never touches HKLM.</summary>
public static class WindowsIntegration
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\SonosFlow";
    private const string RunValue = "SonosFlow";

    public static bool StartsWithWindows
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(RunValue) is string;
        }
        set
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey);
            if (value) key.SetValue(RunValue, $"\"{AppPaths.ExePath}\" --minimized");
            else key.DeleteValue(RunValue, throwOnMissingValue: false);
        }
    }

    public static bool IsInstalledCopy =>
        AppPaths.ExePath.StartsWith(AppPaths.InstallDir, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Removes what the installer created: shortcuts, HKCU entries and the program folder.
    /// Settings and logs in the user profile are kept.
    /// </summary>
    public static void Uninstall()
    {
        StartsWithWindows = false;
        Registry.CurrentUser.DeleteSubKeyTree(UninstallKey, throwOnMissingSubKey: false);

        foreach (var lnk in new[]
                 {
                     Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "Sonos Flow.lnk"),
                     Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "Sonos Flow.lnk"),
                 })
        {
            try { File.Delete(lnk); } catch (IOException) { }
        }

        if (IsInstalledCopy)
        {
            // The running exe can't delete its own folder; a short-lived cmd does it after we exit.
            Process.Start(new ProcessStartInfo("cmd.exe",
                $"/c timeout /t 2 /nobreak >nul & rmdir /s /q \"{AppPaths.InstallDir}\"")
            {
                CreateNoWindow = true,
                UseShellExecute = false,
            });
        }
    }
}
