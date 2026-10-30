using System.Text;
using System.Text.Json;
using SonosFlow.App.Infrastructure;

namespace SonosFlow.App.Tests;

public sealed class UiFeedbackTests
{
    [Theory]
    [InlineData(false, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, true, false)]
    [InlineData(true, false, true)]
    public void Sound_requires_opt_in_and_an_inactive_stream(bool enabled, bool streaming, bool expected) =>
        Assert.Equal(expected, UiFeedback.Allowed(enabled, streaming));

    [Fact]
    public void Preview_does_not_emit_feedback_beside_another_instance() =>
        Assert.False(UiFeedback.Allowed(true, false, otherInstance: true));

    [Fact]
    public void Legacy_settings_get_a_compatible_sound_default()
    {
        var settings = JsonSerializer.Deserialize<AppSettings>("{\"MuteLocal\":false,\"Port\":8790}")!;
        Assert.True(settings.InterfaceSounds);
        Assert.False(settings.MuteLocal);
        Assert.Equal(8790, settings.Port);
    }

    [Fact]
    public void Generated_confirmation_is_short_quiet_and_has_silent_edges()
    {
        byte[] wave = UiFeedback.CreateWave();
        Assert.Equal("RIFF", Encoding.ASCII.GetString(wave, 0, 4));
        Assert.Equal("WAVE", Encoding.ASCII.GetString(wave, 8, 4));
        Assert.Equal(wave.Length - 8, BitConverter.ToInt32(wave, 4));
        Assert.Equal(48000, BitConverter.ToInt32(wave, 24));
        Assert.Equal(1, BitConverter.ToInt16(wave, 22));
        Assert.Equal(16, BitConverter.ToInt16(wave, 34));
        int frames = (wave.Length - 44) / 2;
        Assert.InRange(frames / 48000.0, 0.04, 0.06);
        Assert.Equal(0, BitConverter.ToInt16(wave, 44));
        Assert.Equal(0, BitConverter.ToInt16(wave, wave.Length - 2));
        int peak = Enumerable.Range(0, frames).Max(i => Math.Abs((int)BitConverter.ToInt16(wave, 44 + i * 2)));
        Assert.InRange(peak, 1, 1147);
    }
}
