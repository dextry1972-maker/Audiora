using System.Text.Json;

namespace SonosFlow.App.Infrastructure;

public sealed class AppSettings
{
    public string? LastSpeakerUdn { get; set; }
    public string? CaptureDeviceId { get; set; }
    public bool MuteLocal { get; set; } = true;
    public bool FullLevel { get; set; }
    public bool MinimizeToTray { get; set; } = true;
    public bool ResumeOnLaunch { get; set; }
    public bool WasStreaming { get; set; }
    public int Port { get; set; } = 8780;
    /// <summary>"Margen anti-cortes" in ms (0 = pure live). Absorbs short Wi-Fi pauses at the cost of that much latency.</summary>
    public int PrebufferMs { get; set; } = 1000;
    /// <summary>System-wide Ctrl+Alt shortcuts for the Sonos volume, pause and the mini player.</summary>
    public bool GlobalHotkeys { get; set; } = true;
    public bool InterfaceSounds { get; set; } = true;

    // "Audiora Enchanted" sounds (Settings → Sonidos). Played locally, never sent to the Sonos.
    public bool NotificationSounds { get; set; } = true;
    public bool ConnectionSounds { get; set; } = true;
    public bool ConnectionLossSounds { get; set; } = true;
    public bool ErrorSounds { get; set; } = true;
    /// <summary>Independent volume of the effects, 0–100.</summary>
    public int SoundsVolume { get; set; } = 60;
    /// <summary>Render endpoint for the effects; null = the system default.</summary>
    public string? SoundsDeviceId { get; set; }
    /// <summary>Sonos Flow source folder for "Copias de seguridad" (auto-detected when running from the project).</summary>
    public string? ProjectPath { get; set; }
    public string? BackupRoot { get; set; }

    // Source: "system" (whole output) or "app" (one application, remembered by executable).
    public string SourceMode { get; set; } = "system";
    public string? SourceAppExe { get; set; }
    public bool SourceAppIsolated { get; set; }

    // Mini player.
    public bool MiniVisible { get; set; }
    public bool MiniTopmost { get; set; } = true;
    public bool MiniRememberPosition { get; set; } = true;
    public double? MiniLeft { get; set; }
    public double? MiniTop { get; set; }
    public bool MiniDimWhenIdle { get; set; } = true;
    public bool MinimizeToMini { get; set; }
    /// <summary>Closing the main window: "tray", "mini" or "exit". Null = follow the older MinimizeToTray switch.</summary>
    public string? CloseAction { get; set; }

    /// <summary>UI language (BCP 47, see Localization/Languages.cs). Null in settings written before languages existed.</summary>
    public string? Language { get; set; }
    /// <summary>The first-launch language choice was made (or skipped); the welcome screen never shows again.</summary>
    public bool LanguageOnboardingDone { get; set; }

    /// <summary>Name for the welcome ("Welcome back, …"). Null/empty = none. Stays on this PC: never logged, never in diagnostics.</summary>
    public string? UserName { get; set; }
    /// <summary>Greet with the name when the app opens (the greeting itself is skipped for automatic starts).</summary>
    public bool PersonalizedWelcome { get; set; } = true;
    /// <summary>The first-launch "what should we call you?" step was answered or skipped.</summary>
    public bool NameOnboardingDone { get; set; }

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    /// <summary>
    /// Whether the last <see cref="Load"/> found a settings file. False only on a genuinely fresh install: the file
    /// survives updates and uninstall, so a reinstall keeps the user's choices.
    /// </summary>
    public static bool LastLoadFoundFile { get; private set; }

    /// <summary>The last <see cref="Load"/> found a file it could not read; a copy was kept next to it.</summary>
    public static bool LastLoadUnreadable { get; private set; }

    public static AppSettings Load()
    {
        LastLoadFoundFile = File.Exists(AppPaths.SettingsFile);
        LastLoadUnreadable = false;
        try
        {
            if (LastLoadFoundFile)
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(AppPaths.SettingsFile)) ?? new();
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            LastLoadUnreadable = true;
            // Keep the damaged file for inspection; it is about to be replaced by the defaults.
            try { File.Copy(AppPaths.SettingsFile, Path.ChangeExtension(AppPaths.SettingsFile, $".unreadable-{DateTime.Now:yyyyMMdd-HHmmss}.json")); }
            catch (Exception copy) when (copy is IOException or UnauthorizedAccessException) { }
        }
        return new();
    }

    /// <summary>False in automated test runs, so they never change the user's settings.</summary>
    public static bool PersistenceEnabled { get; set; } = true;

    public void Save()
    {
        if (!PersistenceEnabled) return;
        try
        {
            Directory.CreateDirectory(AppPaths.Roaming);
            File.WriteAllText(AppPaths.SettingsFile, JsonSerializer.Serialize(this, Json));
        }
        catch (IOException) { }
    }
}
