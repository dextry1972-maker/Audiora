using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SonosFlow.App.Tests;

/// <summary>
/// Guards for the integration of the motion work with the "Audiora Enchanted" sounds: one playback engine, one setting,
/// one switch. Source-level checks, silent (no audio device is touched).
/// </summary>
public sealed class InterfaceSoundIntegrationTests
{
    private static string AppDir => Path.Combine(LocalizationCoverageTests.RepoRoot(), "src", "SonosFlow.App");

    private static IEnumerable<string> Sources(string pattern) =>
        Directory.EnumerateFiles(AppDir, pattern, SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                     && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"));

    [Fact]
    public void Only_the_sound_service_plays_audio_no_second_engine_exists()
    {
        var soundDir = Path.Combine(AppDir, "Sound") + Path.DirectorySeparatorChar;
        var offenders = Sources("*.cs")
            .Where(f => !f.StartsWith(soundDir, StringComparison.OrdinalIgnoreCase))
            .Where(f => Regex.IsMatch(File.ReadAllText(f), @"\bSoundPlayer\b|System\.Media\b|SystemSounds|Console\.Beep|MediaPlayer\b"))
            .Select(f => Path.GetRelativePath(AppDir, f))
            .ToList();
        Assert.True(offenders.Count == 0, "second sound engine in: " + string.Join(", ", offenders));
        Assert.False(File.Exists(Path.Combine(AppDir, "Infrastructure", "UiFeedback.cs")));
    }

    [Fact]
    public void InterfaceSounds_is_declared_once_and_has_a_single_switch()
    {
        var declarations = Sources("*.cs").Sum(f => Regex.Matches(File.ReadAllText(f), @"\bbool\s+InterfaceSounds\s*(\{|=>)").Count);
        Assert.Equal(2, declarations); // the stored setting (AppSettings) and its view-model property: nothing else
        var bindings = Sources("*.xaml").Sum(f => Regex.Matches(File.ReadAllText(f), @"\{Binding\s+InterfaceSounds\b").Count);
        Assert.Equal(1, bindings);
    }

    [Fact]
    public void Settings_screen_creates_no_sound_of_its_own()
    {
        var code = File.ReadAllText(Path.Combine(AppDir, "Views", "SettingsView.xaml.cs"));
        Assert.DoesNotContain("Feedback", code);
        Assert.DoesNotContain("Confirm(", code);
    }

    [Fact]
    public void The_retired_general_switch_strings_are_gone_from_every_language()
    {
        var files = Directory.GetFiles(Path.Combine(AppDir, "Localization", "Strings"), "settings.json", SearchOption.AllDirectories);
        Assert.Equal(12, files.Length);
        foreach (var file in files)
        {
            var text = File.ReadAllText(file);
            Assert.DoesNotContain("settings.general.sounds", text);
            Assert.Contains("settings.sounds.interface.title", text);
        }
    }
}
