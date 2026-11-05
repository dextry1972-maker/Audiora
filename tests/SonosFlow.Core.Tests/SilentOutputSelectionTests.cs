using SonosFlow.Core.Audio;

namespace SonosFlow.Core.Tests;

/// <summary>
/// 02/11/2026: after Brave was restarted the new instance was born on the silent output (Windows keeps the per-app output per
/// executable), the silent output was excluded as "the app's own device", no destination was left, and "Silenciar el PC"
/// fell back to muting the silent output while the browser played on the real speakers.
/// </summary>
public sealed class SilentOutputSelectionTests
{
    private static readonly OutputDeviceInfo Real = new("{real}", "BenQ EX270QM (NVIDIA High Definition Audio)", IsDefault: true);
    private static readonly OutputDeviceInfo Steam = new("{steam}", "Speakers (Steam Streaming Speakers)", IsDefault: false);
    private static readonly OutputDeviceInfo Cable = new("{cable}", "CABLE Input (VB-Audio Virtual Cable)", IsDefault: false);

    [Fact]
    public void The_silent_output_is_found_even_when_the_app_is_already_on_it()
    {
        // The app was found ON the silent output: it must still be the destination (nothing else to pick, nothing to exclude).
        Assert.Same(Steam, AudioDevices.PickSilentOutput([Real, Steam]));
    }

    [Fact]
    public void A_real_output_or_the_system_default_is_never_chosen()
    {
        Assert.Null(AudioDevices.PickSilentOutput([Real, Cable]));                                  // a cable is routed somewhere audible: not "silent"
        Assert.Null(AudioDevices.PickSilentOutput([Real with { IsDefault = false }, Steam with { IsDefault = true }])); // never the default one
        Assert.Null(AudioDevices.PickSilentOutput([]));
    }

    [Theory]
    [InlineData("Speakers (Steam Streaming Speakers)", true)]
    [InlineData("steam streaming microphone", true)]
    [InlineData("BenQ EX270QM (NVIDIA High Definition Audio)", false)]
    [InlineData("CABLE Input (VB-Audio Virtual Cable)", false)]
    public void Only_known_virtual_outputs_count_as_silent(string name, bool expected) =>
        Assert.Equal(expected, AudioDevices.IsSilentOutputName(name));
}
