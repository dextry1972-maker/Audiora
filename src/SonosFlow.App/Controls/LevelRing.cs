using System.Windows;
using System.Windows.Media;

namespace SonosFlow.App.Controls;

/// <summary>Circular ring whose bright arc follows the audio level (0..1) of the stream.</summary>
public sealed class LevelRing : FrameworkElement
{
    public static readonly DependencyProperty LevelProperty = DependencyProperty.Register(
        nameof(Level), typeof(double), typeof(LevelRing), new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty IsLiveProperty = DependencyProperty.Register(
        nameof(IsLive), typeof(bool), typeof(LevelRing), new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));

    public double Level { get => (double)GetValue(LevelProperty); set => SetValue(LevelProperty, value); }
    public bool IsLive { get => (bool)GetValue(IsLiveProperty); set => SetValue(IsLiveProperty, value); }

    private static readonly Pen TrackPen = Frozen(new Pen(new SolidColorBrush(Color.FromArgb(0x55, 0x31, 0xD6, 0x7B)), 3));
    private static readonly Pen IdlePen = Frozen(new Pen(new SolidColorBrush(Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF)), 3));
    private static readonly Pen ArcPen = Frozen(new Pen(new LinearGradientBrush(Color.FromRgb(0x9C, 0xF7, 0xC6), Color.FromRgb(0x22, 0xC0, 0x6A), 90), 3.5)
    {
        StartLineCap = PenLineCap.Round,
        EndLineCap = PenLineCap.Round,
    });

    private static Pen Frozen(Pen p) { p.Freeze(); return p; }

    protected override void OnRender(DrawingContext dc)
    {
        double size = Math.Min(ActualWidth, ActualHeight);
        if (size <= 8) return;
        var center = new Point(ActualWidth / 2, ActualHeight / 2);
        double r = size / 2 - 2.5;
        dc.DrawEllipse(null, IsLive ? TrackPen : IdlePen, center, r, r);
        if (!IsLive) return;

        // A short arc is always visible while live; it grows with the level (up to ~85 % of the ring).
        double sweep = 40 + 270 * Math.Clamp(Level, 0, 1);
        double start = -90 - sweep / 2;
        Point P(double deg) => new(center.X + r * Math.Cos(deg * Math.PI / 180), center.Y + r * Math.Sin(deg * Math.PI / 180));
        var geo = new StreamGeometry();
        using (var ctx = geo.Open())
        {
            ctx.BeginFigure(P(start), false, false);
            ctx.ArcTo(P(start + sweep), new Size(r, r), 0, sweep > 180, SweepDirection.Clockwise, true, true);
        }
        geo.Freeze();
        dc.DrawGeometry(null, ArcPen, geo);
    }
}
