using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace SonosFlow.App.Controls;

/// <summary>
/// Decorative field of dots flowing in soft waves on both sides of the stream button (the reference's particle waves).
/// Drawn once (about a thousand dots); the flow is a slow horizontal slide of the whole cached layer.
/// </summary>
public sealed class WaveField : FrameworkElement
{
    private readonly TranslateTransform _slide = new();
    private Brush _dot = Brushes.White;

    public WaveField()
    {
        IsHitTestVisible = false;
        SnapsToDevicePixels = false;
        CacheMode = new BitmapCache();
        RenderTransform = _slide; // slides the cached layer: no redraw per frame
        Loaded += (_, _) =>
        {
            _dot = TryFindResource("B.AccentHi") as Brush ?? Brushes.LightGreen;
            InvalidateVisual();
            if (!SystemParameters.ClientAreaAnimation) return;
            var a = new DoubleAnimation(-14, 14, TimeSpan.FromSeconds(17))
            {
                AutoReverse = true,
                RepeatBehavior = RepeatBehavior.Forever,
                EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
            };
            Timeline.SetDesiredFrameRate(a, 20);
            _slide.BeginAnimation(TranslateTransform.XProperty, a);
        };
        Unloaded += (_, _) => _slide.BeginAnimation(TranslateTransform.XProperty, null);
    }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w < 40 || h < 40) return;
        double cx = w / 2, cy = h / 2;
        for (int band = 0; band < 7; band++)
        {
            double amp = 26 + band * 7, phase = band * 0.9, lift = (band - 3) * 9;
            for (double x = -20; x <= w + 20; x += 7)
            {
                double dist = Math.Abs(x - cx) / cx;                  // 0 at the centre, 1 at the edges
                double envelope = Math.Pow(Math.Min(1, dist * 1.25), 1.4);   // nothing behind the button itself
                double y = cy + lift + amp * Math.Sin(x / 58 + phase) * (0.35 + dist * 0.65) + Math.Sin(x / 21 + band) * 3;
                double r = (0.7 + 1.5 * envelope) * (0.55 + 0.45 * Math.Sin(x / 33 + band * 1.7 + 1.0) * Math.Sin(x / 33 + band * 1.7 + 1.0));
                double alpha = Math.Min(0.9, 0.16 + 0.7 * envelope * (1.0 - band * 0.06));
                dc.PushOpacity(alpha);
                dc.DrawEllipse(_dot, null, new Point(x, y), r, r);
                dc.Pop();
            }
        }
    }
}
