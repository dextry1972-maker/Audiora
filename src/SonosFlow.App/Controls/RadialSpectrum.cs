using System.Windows;
using System.Windows.Media;
using SonosFlow.Core.Audio;

namespace SonosFlow.App.Controls;

/// <summary>
/// The real spectrum of the audio being streamed (the same 48 log bands as <see cref="SpectrumVisualizer"/>),
/// drawn as a ring around the stream button: low frequencies at the bottom, rising symmetrically to the highs at
/// the top. Without a stream it shows a still ring of dots — nothing is animated or invented. Renders only while
/// bars move, so an idle window costs no CPU.
/// </summary>
public sealed class RadialSpectrum : FrameworkElement
{
    public static readonly DependencyProperty AnalyzerProperty = DependencyProperty.Register(
        nameof(Analyzer), typeof(SpectrumAnalyzer), typeof(RadialSpectrum),
        new PropertyMetadata(null, (d, _) => ((RadialSpectrum)d).UpdateRendering()));

    public static readonly DependencyProperty PeakDbfsProperty = DependencyProperty.Register(
        nameof(PeakDbfs), typeof(double), typeof(RadialSpectrum), new PropertyMetadata(double.NegativeInfinity));

    public static readonly DependencyProperty LevelProperty = DependencyProperty.Register(
        nameof(Level), typeof(double), typeof(RadialSpectrum), new PropertyMetadata(0.0));

    /// <summary>Source of bands; null when not streaming.</summary>
    public SpectrumAnalyzer? Analyzer
    {
        get => (SpectrumAnalyzer?)GetValue(AnalyzerProperty);
        set => SetValue(AnalyzerProperty, value);
    }

    /// <summary>Measured peak of the streamed audio (held ~0.5 s), in dBFS.</summary>
    public double PeakDbfs
    {
        get => (double)GetValue(PeakDbfsProperty);
        private set => SetValue(PeakDbfsProperty, value);
    }

    /// <summary>Smoothed loudness 0..1, for the ambient light behind the ring.</summary>
    public double Level
    {
        get => (double)GetValue(LevelProperty);
        private set => SetValue(LevelProperty, value);
    }

    private const int Bands = 48;
    private readonly float[] _target = new float[Bands];
    private readonly double[] _shown = new double[Bands];
    private readonly Pen[] _barPens = new Pen[16];
    private readonly Brush _idleDot;
    private readonly Pen _baseRing;
    private bool _hooked;
    private TimeSpan _lastFrame;
    private double _peakHold = double.NegativeInfinity;
    private int _peakFrames;

    public RadialSpectrum()
    {
        // Pens by intensity: quiet bands are deep violet and translucent, loud ones bright cyan.
        for (int i = 0; i < _barPens.Length; i++)
        {
            double t = i / (double)(_barPens.Length - 1);
            var c = Color.FromArgb((byte)(110 + 145 * t), (byte)(0x6C + (0x37 - 0x6C) * t), (byte)(0x4B + (0xDD - 0x4B) * t), (byte)(0xDB + (0xF3 - 0xDB) * t));
            var pen = new Pen(new SolidColorBrush(c), 2.2) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
            pen.Freeze();
            _barPens[i] = pen;
        }
        _idleDot = new SolidColorBrush(Color.FromArgb(0x38, 0xFF, 0xFF, 0xFF));
        _idleDot.Freeze();
        _baseRing = new Pen(new SolidColorBrush(Color.FromArgb(0x16, 0xFF, 0xFF, 0xFF)), 1);
        _baseRing.Freeze();
        IsVisibleChanged += (_, _) => UpdateRendering();
        Unloaded += (_, _) => Unhook();
    }

    private void UpdateRendering()
    {
        if (IsVisible && (Analyzer is not null || _shown.Any(v => v > 0.002))) Hook();
        InvalidateVisual();
    }

    private void Hook()
    {
        if (_hooked) return;
        _hooked = true;
        CompositionTarget.Rendering += OnFrame;
    }

    private void Unhook()
    {
        if (!_hooked) return;
        _hooked = false;
        CompositionTarget.Rendering -= OnFrame;
    }

    private void OnFrame(object? sender, EventArgs e)
    {
        var now = ((RenderingEventArgs)e).RenderingTime;
        if (now == _lastFrame) return;
        double dt = _lastFrame == TimeSpan.Zero ? 1 / 60.0 : Math.Min(0.1, (now - _lastFrame).TotalSeconds);
        _lastFrame = now;

        float peak = 0;
        if (Analyzer is { } a)
        {
            var (l, r) = a.Snapshot(_target);
            peak = Math.Max(l, r);
        }
        else
        {
            Array.Clear(_target);
        }

        // Same response as the bar spectrum: fast attack, eased release.
        double attack = 1 - Math.Exp(-dt / 0.03), release = 1 - Math.Exp(-dt / 0.2);
        bool moving = false;
        for (int i = 0; i < Bands; i++)
        {
            double t = _target[i];
            _shown[i] += (t - _shown[i]) * (t > _shown[i] ? attack : release);
            if (_shown[i] > 0.002) moving = true;
        }

        double db = peak > 0 ? 20 * Math.Log10(peak) : double.NegativeInfinity;
        if (db >= _peakHold || ++_peakFrames > 30) { _peakHold = db; _peakFrames = 0; }
        PeakDbfs = _peakHold;
        double lvl = double.IsNegativeInfinity(db) ? 0 : Math.Clamp((db + 50) / 50, 0, 1);
        Level += (lvl - Level) * (lvl > Level ? attack : release * 0.5);

        InvalidateVisual();
        if (Analyzer is null && !moving)
        {
            Level = 0;
            PeakDbfs = double.NegativeInfinity;
            Unhook();
        }
    }

    protected override void OnRender(DrawingContext dc)
    {
        double size = Math.Min(ActualWidth, ActualHeight);
        if (size < 40) return;
        var c = new Point(ActualWidth / 2, ActualHeight / 2);
        double inner = size * 0.30;          // just outside the stream button
        double span = size * 0.16;           // longest bar
        dc.DrawEllipse(null, _baseRing, c, inner - 7, inner - 7);

        // 48 bands mirrored left/right = 96 spokes; band 0 (40 Hz) at the bottom, band 47 (16 kHz) at the top.
        const int spokes = Bands * 2;
        bool any = _shown.Any(v => v > 0.01);
        for (int s = 0; s < spokes; s++)
        {
            int band = s < Bands ? s : spokes - 1 - s;
            double angle = Math.PI / 2 - (s + 0.5) * (2 * Math.PI / spokes); // start at the bottom, go round
            double cos = Math.Cos(angle), sin = Math.Sin(angle);
            var p0 = new Point(c.X + cos * inner, c.Y + sin * inner);
            if (!any)
            {
                if (s % 2 == 0) dc.DrawEllipse(_idleDot, null, p0, 1.3, 1.3);
                continue;
            }
            double v = Math.Clamp(_shown[band], 0, 1);
            double len = 2 + v * span;
            var p1 = new Point(c.X + cos * (inner + len), c.Y + sin * (inner + len));
            dc.DrawLine(_barPens[(int)Math.Round(v * (_barPens.Length - 1))], p0, p1);
        }
    }
}
