using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace SonosFlow.Setup;

public sealed class InstallOptions
{
    public string Directory { get; set; } = DefaultDirectory;
    public bool DesktopShortcut { get; set; } = true;
    public bool StartMenuShortcut { get; set; } = true;
    public bool Register { get; set; } = true;
    public bool Launch { get; set; } = true;

    public static string DefaultDirectory { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "SonosFlow");
}

/// <summary>Per-user install: no admin rights, only the user profile and HKCU are touched.</summary>
public static class Installer
{
    public const string ExeName = "SonosFlow.exe";
    private const string ShortcutName = "Sonos Flow.lnk";
    private const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\SonosFlow";

    public static bool HasPayload => typeof(Installer).Assembly.GetManifestResourceNames().Contains("payload.zip");

    public static string ExePath(InstallOptions o) => Path.Combine(o.Directory, ExeName);

    /// <summary>Returns a message when installing would be unsafe (app running, foreign folder), else null.</summary>
    public static string? CheckPreconditions(InstallOptions o)
    {
        if (!HasPayload) return "Este instalador no contiene la aplicación (se compiló sin el paquete).";

        var running = Process.GetProcessesByName("SonosFlow");
        if (running.Length > 0) return "Sonos Flow está abierto. Ciérralo (también desde la bandeja del sistema) y vuelve a intentarlo.";

        if (Directory.Exists(o.Directory) && Directory.EnumerateFileSystemEntries(o.Directory).Any()
            && !File.Exists(ExePath(o)))
            return $"La carpeta {o.Directory} ya existe y no contiene Sonos Flow. No se sobrescribirá.";
        return null;
    }

    public static void Install(InstallOptions o, IProgress<(int Percent, string Status)> progress)
    {
        progress.Report((2, "Preparando la carpeta…"));
        if (Directory.Exists(o.Directory)) Directory.Delete(o.Directory, recursive: true); // previous Sonos Flow only (checked above)
        Directory.CreateDirectory(o.Directory);

        using (var stream = typeof(Installer).Assembly.GetManifestResourceStream("payload.zip")!)
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Read))
        {
            var root = Path.GetFullPath(o.Directory) + Path.DirectorySeparatorChar;
            int total = zip.Entries.Count, done = 0;
            foreach (var entry in zip.Entries)
            {
                var target = Path.GetFullPath(Path.Combine(o.Directory, entry.FullName));
                if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Entrada de paquete no válida.");
                if (entry.FullName.EndsWith("/"))
                {
                    Directory.CreateDirectory(target);
                }
                else
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    entry.ExtractToFile(target, overwrite: true);
                }
                if (++done % 10 == 0 || done == total)
                    progress.Report((5 + done * 85 / total, $"Copiando archivos… {done}/{total}"));
            }
        }

        var exe = ExePath(o);
        if (!File.Exists(exe)) throw new FileNotFoundException("El paquete no contiene SonosFlow.exe.", exe);

        progress.Report((92, "Creando accesos directos…"));
        if (o.DesktopShortcut)
            CreateShortcut(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), ShortcutName), exe);
        if (o.StartMenuShortcut)
            CreateShortcut(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), ShortcutName), exe);

        if (o.Register)
        {
            progress.Report((96, "Registrando en Aplicaciones instaladas…"));
            Register(o, exe);
        }
        progress.Report((100, "Instalación completada"));
    }

    private static void CreateShortcut(string lnkPath, string exe)
    {
        var shellType = Type.GetTypeFromProgID("WScript.Shell") ?? throw new InvalidOperationException("WScript.Shell no disponible");
        dynamic shell = Activator.CreateInstance(shellType)!;
        try
        {
            dynamic lnk = shell.CreateShortcut(lnkPath);
            lnk.TargetPath = exe;
            lnk.WorkingDirectory = Path.GetDirectoryName(exe);
            lnk.IconLocation = exe + ",0";
            lnk.Description = "Transmite el audio del PC a tu Sonos";
            lnk.Save();
            Marshal.FinalReleaseComObject(lnk);
        }
        finally
        {
            Marshal.FinalReleaseComObject(shell);
        }
    }

    private static void Register(InstallOptions o, string exe)
    {
        using var key = Registry.CurrentUser.CreateSubKey(UninstallKey);
        long sizeKb = new DirectoryInfo(o.Directory).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length) / 1024;
        key.SetValue("DisplayName", "Sonos Flow");
        key.SetValue("DisplayVersion", typeof(Installer).Assembly.GetName().Version.ToString(3));
        key.SetValue("Publisher", "Sonos Flow (proyecto personal)");
        key.SetValue("DisplayIcon", exe + ",0");
        key.SetValue("InstallLocation", o.Directory);
        key.SetValue("UninstallString", $"\"{exe}\" --uninstall");
        key.SetValue("NoModify", 1, RegistryValueKind.DWord);
        key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
        key.SetValue("EstimatedSize", (int)Math.Min(int.MaxValue, sizeKb), RegistryValueKind.DWord);
        key.SetValue("InstallDate", DateTime.Now.ToString("yyyyMMdd"));
    }
}
