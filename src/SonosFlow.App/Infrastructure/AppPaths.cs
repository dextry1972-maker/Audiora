namespace SonosFlow.App.Infrastructure;

/// <summary>Per-user locations. Nothing is written outside the user profile.</summary>
public static class AppPaths
{
    public static string Roaming { get; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SonosFlow");
    public static string Local { get; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SonosFlow");
    public static string SettingsFile => Path.Combine(Roaming, "settings.json");
    public static string GuardStateFile => Path.Combine(Local, "output-guard.json");
    public static string Logs => Path.Combine(Local, "logs");

    /// <summary>Where the installer puts the app (no admin rights needed).</summary>
    public static string InstallDir { get; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "SonosFlow");

    public static string ExePath => Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "SonosFlow.exe");
}
