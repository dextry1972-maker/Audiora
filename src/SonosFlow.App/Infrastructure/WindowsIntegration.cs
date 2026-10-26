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

    private static bool PointsToThisApp(string lnkPath)
    {
        if (!File.Exists(lnkPath) || Type.GetTypeFromProgID("WScript.Shell") is not { } shellType) return false;
        dynamic shell = Activator.CreateInstance(shellType)!;
        try
        {
            string target = shell.CreateShortcut(lnkPath).TargetPath;
            return string.Equals(Path.GetFullPath(target), Path.GetFullPath(AppPaths.ExePath), StringComparison.OrdinalIgnoreCase);
        }
        catch (ArgumentException) { return false; } // empty or malformed target
        finally
        {
            System.Runtime.InteropServices.Marshal.FinalReleaseComObject(shell);
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

        // Current name and the pre-rename one; only shortcuts that launch this app are removed.
        foreach (var folder in new[] { Environment.SpecialFolder.DesktopDirectory, Environment.SpecialFolder.Programs })
        foreach (var name in new[] { "Audiora.lnk", "Sonos Flow.lnk" })
        {
            var lnk = Path.Combine(Environment.GetFolderPath(folder), name);
            try { if (PointsToThisApp(lnk)) File.Delete(lnk); } catch (IOException) { }
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
