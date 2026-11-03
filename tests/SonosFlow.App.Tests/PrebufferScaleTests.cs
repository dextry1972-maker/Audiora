using SonosFlow.App.ViewModels;
using Xunit;

namespace SonosFlow.App.Tests;

public class PrebufferScaleTests
{
    [Theory]
    [InlineData(0, 0)]
    [InlineData(4, 0)]
    [InlineData(6, 10)]
    [InlineData(300, 300)]
    [InlineData(304, 300)]
    [InlineData(996, 1000)]
    [InlineData(1000, 1000)]
    [InlineData(5000, 1000)]
    [InlineData(-50, 0)]
    public void Slider_values_are_clamped_and_snapped(double slider, int expected)
    {
        int actual = PrebufferScale.FromSlider(slider);
        Assert.Equal(expected, actual);
        Assert.Equal(0, actual % PrebufferScale.SliderStep);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(250, 250)]
    [InlineData(1000, 1000)]
    [InlineData(2000, 1000)]   // saved by an older version: shown at the end of the slider, the saved value itself is not touched
    [InlineData(3000, 1000)]
    [InlineData(-1, 0)]
    public void Saved_values_map_to_the_slider(int ms, double expected) => Assert.Equal(expected, PrebufferScale.ToSlider(ms));

    [Theory]
    [InlineData(0, "0")]
    [InlineData(100, "100")]
    [InlineData(300, "300")]
    [InlineData(1000, "1000")]
    [InlineData(50, "custom")]
    [InlineData(2000, "custom")]
    public void Presets_match_only_exact_values(int ms, string key) => Assert.Equal(key, PrebufferScale.KeyFor(ms));

    [Fact]
    public void Every_preset_is_reachable_with_the_slider() =>
        Assert.All(PrebufferScale.Presets, p => Assert.Equal(p, PrebufferScale.FromSlider(PrebufferScale.ToSlider(p))));
}
