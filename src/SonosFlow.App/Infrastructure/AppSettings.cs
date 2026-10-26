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

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(AppPaths.SettingsFile))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(AppPaths.SettingsFile)) ?? new();
        }
        catch (Exception ex) when (ex is IOException or JsonException) { }
        return new();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(AppPaths.Roaming);
            File.WriteAllText(AppPaths.SettingsFile, JsonSerializer.Serialize(this, Json));
        }
        catch (IOException) { }
    }
}
