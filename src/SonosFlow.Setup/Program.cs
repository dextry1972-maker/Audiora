using System;
using System.Linq;
using System.Windows.Forms;

namespace SonosFlow.Setup;

/// <summary>
/// Sonos Flow installer.
///   (no arguments)    interactive install
///   /quiet            install with defaults, no window
///   /dir PATH         install somewhere else (testing)
///   /noshortcuts      skip desktop and Start menu shortcuts
///   /noregistry       skip the "Installed apps" entry
///   /nolaunch         don't open the app afterwards
/// </summary>
internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        bool Has(string flag) => args.Any(a => a.Equals(flag, StringComparison.OrdinalIgnoreCase));
        int dirIdx = Array.FindIndex(args, a => a.Equals("/dir", StringComparison.OrdinalIgnoreCase));

        var options = new InstallOptions
        {
            DesktopShortcut = !Has("/noshortcuts"),
            StartMenuShortcut = !Has("/noshortcuts"),
            Register = !Has("/noregistry"),
            Launch = !Has("/nolaunch"),
        };
        if (dirIdx >= 0 && dirIdx + 1 < args.Length) options.Directory = args[dirIdx + 1];

        if (Has("/quiet"))
        {
            if (Installer.CheckPreconditions(options) is { } problem)
            {
                System.IO.File.WriteAllText(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "SonosFlow-Setup.log"), problem);
                return 2;
            }
            try
            {
                Installer.Install(options, new Progress<(int, string)>());
                return 0;
            }
            catch (Exception ex)
            {
                // WinExe has no console; leave the reason where it can be found.
                System.IO.File.WriteAllText(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "SonosFlow-Setup.log"), ex.ToString());
                return 1;
            }
        }

        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.Run(new SetupForm(options));
        return 0;
    }
}
