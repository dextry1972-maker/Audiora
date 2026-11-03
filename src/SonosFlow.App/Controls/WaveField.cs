using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using SonosFlow.Core.Audio;

namespace SonosFlow.App.Controls;

/// <summary>
/// Field of dots on both sides of the stream button, in seven bands of different depth.
/// <para>
/// Each band is drawn ONCE into a bitmap-cached layer whose pattern repeats every <see cref="Loop"/> pixels, and moving the waves is
/// just sliding that layer to the right with a transform animation: the GPU composes it, nothing is redrawn per frame, so it stays
/// smooth at the screen's refresh rate for almost no CPU. The animation exists only while audio is streaming, the window is visible
/// and Windows animations are on; its speed and the wave height follow the loudness (polled ~20 times a second, not per frame),
/// and the colours drift through the aurora palette because they travel with the dots (cyan → blue → violet → lavender).
/// When the sound stops it eases to a halt and every clock is released: an idle window costs nothing.
/// </para>
/// </summary>
public sealed class WaveField : Grid
{
    public static readonly DependencyProperty AnalyzerProperty = DependencyProperty.Register(
        nameof(Analyzer), typeof(SpectrumAnalyzer), typeof(WaveField),
        new PropertyMetadata(null, (d, _) => ((WaveField)d).UpdateMotion()));

    /// <summary>Source of the loudness; null when not streaming.</summary>
    public SpectrumAnalyzer? Analyzer
    {
        get => (SpectrumAnalyzer?)GetValue(AnalyzerProperty);
        set => SetValue(AnalyzerProperty, value);
    }

    private const int Bands = 7, PaletteSize = 96;
    private const double Period = 2 * Math.PI * 58;      // the main wave repeats every 364 px
    private const double Loop = 2 * Period;              // the colours and a slower swell repeat every two periods: the layer slides this far
    private const double DotSpacing = 9;
    private const double RestScale = 0.85, BasePixelsPerSecond = 120;

    private readonly DotLayer[] _layers = new DotLayer[Bands];
    private readonly float[] _spectrum = new float[48];
    private readonly DispatcherTimer _poll = new() { Interval = TimeSpan.FromMilliseconds(50) };
    private AnimationClock[]? _clocks;
    private double _level;
    private bool _paused;

    public WaveField()
    {
        IsHitTestVisible = false;
        ClipToBounds = true;
        // Dots fade towards the middle (nothing behind the stream button itself) and glow towards the edges: a fixed mask.
        OpacityMask = BuildMask();
        for (int band = 0; band < Bands; band++)
        {
            _layers[band] = new DotLayer(band) { HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Stretch };
            Children.Add(_layers[band]);
        }
        _poll.Tick += (_, _) => Poll();
        Loaded += (_, _) => { Rebuild(); UpdateMotion(); };
        Unloaded += (_, _) => StopMotion(settle: true);
        IsVisibleChanged += (_, _) => UpdateMotion();
        SizeChanged += (_, _) => Rebuild();
    }

    // ── layout and still picture ───────────────────────────────────────────────────────────────────

    private void Rebuild()
    {
        double w = ActualWidth;
        if (w < 40 || ActualHeight < 40) return;
        var palette = BuildPalette();
        double dpi = VisualTreeHelper.GetDpi(this).DpiScaleX;
        foreach (var layer in _layers)
        {
            layer.Width = w + Loop;
            layer.Margin = new Thickness(-Loop, 0, 0, 0);   // starts one loop to the left: sliding by Loop brings the same picture back
            layer.Setup(palette, dpi);
        }
    }

    private static Brush BuildMask()
    {
        // alpha(distance from the centre): faint at the middle, full from ~80 % of the way out
        var mask = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 0) };
        for (int i = 0; i <= 20; i++)
        {
            double pos = i / 20.0, dist = Math.Abs(pos - 0.5) * 2;
            double envelope = Math.Pow(Math.Min(1, dist * 1.25), 1.4);
            double alpha = Math.Min(1.0, 0.22 + 0.85 * envelope);
            mask.GradientStops.Add(new GradientStop(Color.FromArgb((byte)Math.Round(alpha * 255), 255, 255, 255), pos));
        }
        mask.Freeze();
        return mask;
    }

    /// <summary>The aurora loop from the theme: cyan → blue → violet → lavender → back to cyan.</summary>
    private Brush[] BuildPalette()
    {
        Color Find(string key, Color fallback) => TryFindResource(key) is Color c ? c : fallback;
        var stops = new[]
        {
            Find("C.Accent2", Color.FromRgb(0x37, 0xDD, 0xF3)),
            Color.FromRgb(0x6C, 0x8C, 0xFF),
            Find("C.Accent", Color.FromRgb(0x89, 0x57, 0xF5)),
            Find("C.AccentHi", Color.FromRgb(0xB6, 0xA5, 0xFF)),
        };
        var palette = new Brush[PaletteSize];
        for (int p = 0; p < PaletteSize; p++)
        {
            double pos = p / (double)PaletteSize * stops.Length;
            int i = (int)pos;
            double f = pos - i;
            var c0 = stops[i % stops.Length];
            var c1 = stops[(i + 1) % stops.Length];
            byte Mix(byte a, byte b) => (byte)Math.Round(a + (b - a) * f);
            var brush = new SolidColorBrush(Color.FromRgb(Mix(c0.R, c1.R), Mix(c0.G, c1.G), Mix(c0.B, c1.B)));
            brush.Freeze();
            palette[p] = brush;
        }
        return palette;
    }

    // ── motion ──────────────────────────────────────────────────────────────────────────────────────

    private void UpdateMotion()
    {
        if (IsVisible && Motion.Enabled && Analyzer is not null) StartMotion();
        else if (!IsVisible) StopMotion(settle: true);
    }

    private void StartMotion()
    {
        if (_clocks is not null || ActualWidth < 40) { _poll.Start(); return; }
        _clocks = new AnimationClock[Bands];
        for (int band = 0; band < Bands; band++)
        {
            double speed = BasePixelsPerSecond * (1 + band * 0.08);     // each band a little faster than the one before: depth
            var slide = new DoubleAnimation(0, Loop, TimeSpan.FromSeconds(Loop / speed)) { RepeatBehavior = RepeatBehavior.Forever };
            var clock = slide.CreateClock();
            _layers[band].Slide.ApplyAnimationClock(TranslateTransform.XProperty, clock);
            _clocks[band] = clock;
            // start paused: Poll() lets it run once there is sound
            clock.Controller?.Pause();
        }
        _paused = true;
        _poll.Start();
    }

    private void StopMotion(bool settle)
    {
        _poll.Stop();
        if (_clocks is null) return;
        for (int band = 0; band < Bands; band++)
        {
            // keep the pattern where it stopped (releasing a clock would snap it back to its starting position)
            double x = _layers[band].Slide.X;
            _layers[band].Slide.ApplyAnimationClock(TranslateTransform.XProperty, null);
            if (settle) _layers[band].Slide.X = x;
        }
        _clocks = null;
        _level = 0;
    }

    private void Poll()
    {
        float peak = 0;
        if (Analyzer is { } analyzer)
        {
            var (l, r) = analyzer.Snapshot(_spectrum);
            peak = Math.Max(l, r);
        }
        double db = peak > 0 ? 20 * Math.Log10(peak) : double.NegativeInfinity;
        double target = double.IsNegativeInfinity(db) ? 0 : Math.Clamp((db + 50) / 50, 0, 1);
        // fast attack, slow release, per 50 ms tick
        _level += (target - _level) * (target > _level ? 0.55 : 0.09);

        bool playing = _level > 0.02;
        if (_clocks is { } clocks)
        {
            if (playing)
            {
                double ratio = 0.5 + 3.0 * _level;                          // 0.5× on a whisper, 3.5× at full volume
                for (int band = 0; band < Bands; band++)
                {
                    if (clocks[band].Controller is { } controller) controller.SpeedRatio = ratio;
                    if (_paused) clocks[band].Controller?.Resume();
                    _layers[band].Scale.ScaleY = RestScale + 0.65 * _level;     // the waves rise with the music
                }
                _paused = false;
            }
            else if (!_paused)
            {
                foreach (var c in clocks) c.Controller?.Pause();
                _paused = true;
            }
        }

        // The sound is over (or never came back): settle and release everything.
        if (Analyzer is null && _level < 0.02)
        {
            foreach (var layer in _layers) layer.Scale.ScaleY = RestScale;
            StopMotion(settle: true);
        }
    }

    // ── one band ────────────────────────────────────────────────────────────────────────────────────

    private sealed class DotLayer : FrameworkElement
    {
        private readonly int _band;
        private Brush[] _palette = [];

        public TranslateTransform Slide { get; } = new();
        public ScaleTransform Scale { get; } = new(1, RestScale);

        public DotLayer(int band)
        {
            _band = band;
            IsHitTestVisible = false;
            SnapsToDevicePixels = false;
            Opacity = 1.0 - band * 0.05;                                    // the back bands are a little fainter
            RenderTransformOrigin = new Point(0, 0.5);
            RenderTransform = new TransformGroup { Children = { Scale, Slide } };
        }

        public void Setup(Brush[] palette, double dpiScale)
        {
            _palette = palette;
            CacheMode = new BitmapCache(Math.Max(1.0, dpiScale));           // rasterised once; sliding it is free
            InvalidateVisual();
        }

        protected override void OnRender(DrawingContext dc)
        {
            double w = ActualWidth, h = ActualHeight;
            if (w < 40 || h < 40 || _palette.Length == 0) return;
            double cy = h / 2, lift = (_band - 3) * 9, amp = 26 + _band * 7, phase = _band * 0.9;
            double hueShift = _band * 0.11;
            for (double x = 0; x <= w; x += DotSpacing)
            {
                // everything below repeats every Period (or Loop): so does the whole picture
                double u = 2 * Math.PI * x / Period;
                double swell = 0.82 + 0.18 * Math.Sin(2 * Math.PI * x / Loop + _band);
                double y = cy + lift + amp * swell * Math.Sin(u + phase) + 3 * Math.Sin(3 * u + _band);
                double s = Math.Sin(2 * u + _band * 1.7 + 1.0);
                double r = 2.3 * (0.55 + 0.45 * s * s);
                double hue = x / Loop + hueShift;
                hue -= Math.Floor(hue);
                dc.DrawEllipse(_palette[Math.Min((int)(hue * PaletteSize), PaletteSize - 1)], null, new Point(x, y), r, r);
            }
        }
    }
}
