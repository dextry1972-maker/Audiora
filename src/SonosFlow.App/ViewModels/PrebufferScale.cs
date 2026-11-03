namespace SonosFlow.App.ViewModels;

/// <summary>Maps the "anti-dropout margin" between the slider (0–1000 ms in steps of 10), the quick presets and the saved milliseconds.</summary>
public static class PrebufferScale
{
    public const int SliderMax = 1000, SliderStep = 10;
    public const string CustomKey = "custom";
    public static readonly int[] Presets = [0, 100, 300, 1000];

    /// <summary>Slider value → milliseconds: clamped to the slider's range and snapped to its step.</summary>
    public static int FromSlider(double value) => (int)Math.Round(Math.Clamp(value, 0, SliderMax) / SliderStep) * SliderStep;

    /// <summary>Milliseconds → slider position. Older settings may hold more than the slider's end (up to 3 s): they show at the end.</summary>
    public static double ToSlider(int ms) => Math.Min(Math.Max(0, ms), SliderMax);

    /// <summary>The preset that matches these milliseconds exactly, or "custom".</summary>
    public static string KeyFor(int ms) => Array.IndexOf(Presets, ms) >= 0 ? ms.ToString(System.Globalization.CultureInfo.InvariantCulture) : CustomKey;
}
