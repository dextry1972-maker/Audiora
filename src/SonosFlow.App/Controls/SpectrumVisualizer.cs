using System.Windows;
using System.Windows.Media;
using SonosFlow.Core.Audio;

namespace SonosFlow.App.Controls;

/// <summary>
/// Real-time spectrum of the audio actually being streamed. Renders only while bars are moving,
/// so an idle window costs no CPU.
/// </summary>
public sealed class SpectrumVisualizer : FrameworkElement
{
    public static readonly DependencyProperty AnalyzerProperty = DependencyProperty.Register(
        nameof(Analyzer), typeof(SpectrumAnalyzer), typeof(SpectrumVisualizer),
        new PropertyMetadata(null, (d, _) => ((SpectrumVisualizer)d).UpdateRendering()));

    public static readonly DependencyProperty LevelProperty = DependencyProperty.Register(
        nameof(Level), typeof(double), typeof(SpectrumVisualizer), new PropertyMetadata(0.0));

    public static readonly DependencyProperty PeakDbfsProperty = DependencyProperty.Register(
        nameof(PeakDbfs), typeof(double), typeof(SpectrumVisualizer), new PropertyMetadata(double.NegativeInfinity));

    /// <summary>Source of bands; null when not streaming.</summary>
    public SpectrumAnalyzer? Analyzer
    {
        get => (SpectrumAnalyzer?)GetValue(AnalyzerProperty);
        set => SetValue(AnalyzerProperty, value);
    }

    /// <summary>Smoothed loudness 0..1 for ambient effects (speaker glow).</summary>
    public double Level
    {
        get => (double)GetValue(LevelProperty);
        private set => SetValue(LevelProperty, value);
    }

    /// <summary>Measured peak of the streamed audio over the last frame, in dBFS.</summary>
    public double PeakDbfs
    {
        get => (double)GetValue(PeakDbfsProperty);
        private set => SetValue(PeakDbfsProperty, value);
    }

    private const int Bars = 48;
    private readonly float[] _target = new float[Bars];
    private readonly double[] _shown = new double[Bars];
    private readonly Brush _barBrush;
    private readonly Brush _idleBrush;
    private bool _hooked;
    private TimeSpan _lastFrame;
    private double _peakHold = double.NegativeInfinity;
    private int _peakFrames;

    public SpectrumVisualizer()
    {
        var g = new LinearGradientBrush { StartPoint = new Point(0, 1), EndPoint = new Point(0, 0) };
        g.GradientStops.Add(new GradientStop(Color.FromRgb(0x13, 0x9A, 0x52), 0));
        g.GradientStops.Add(new GradientStop(Color.FromRgb(0x31, 0xD6, 0x7B), 0.55));
        g.GradientStops.Add(new GradientStop(Color.FromRgb(0x9C, 0xF7, 0xC6), 1));
        g.MappingMode = BrushMappingMode.RelativeToBoundingBox;
        g.Freeze();
        _barBrush = g;
        _idleBrush = new SolidColorBrush(Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF));
        _idleBrush.Freeze();
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

        // Fast attack, eased release — reads as fluid without lagging transients.
        double attack = 1 - Math.Exp(-dt / 0.03), release = 1 - Math.Exp(-dt / 0.18);
        bool moving = false;
        for (int i = 0; i < Bars; i++)
        {
            double t = _target[i];
            _shown[i] += (t - _shown[i]) * (t > _shown[i] ? attack : release);
            if (_shown[i] > 0.002) moving = true;
        }

        double db = peak > 0 ? 20 * Math.Log10(peak) : double.NegativeInfinity;
        if (db >= _peakHold || ++_peakFrames > 30) { _peakHold = db; _peakFrames = 0; }
        PeakDbfs = _peakHold;
        double lvl = double.IsNegativeInfinity(db) ? 0 : Math.Clamp((db + 50) / 50, 0, 1);
        Level += (lvl - Level) * (lvl > Level ? attack : release);

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
        double w = ActualWidth, h = ActualHeight;
        if (w <= 0 || h <= 0) return;
        double gap = Math.Max(2, w / Bars * 0.32);
        double barW = (w - gap * (Bars - 1)) / Bars;
        double radius = Math.Min(barW / 2, 3);
        for (int i = 0; i < Bars; i++)
        {
            double v = _shown[i];
            double bh = Math.Max(3, v * h);
            var rect = new Rect(i * (barW + gap), h - bh, barW, bh);
            dc.DrawRoundedRectangle(v > 0.01 ? _barBrush : _idleBrush, null, rect, radius, radius);
        }
    }
}
