using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace SonosFlow.App.Controls;

/// <summary>Slow drift of the two light pools; off when Windows has animations turned off or the window is hidden.</summary>
public partial class AuroraBackground : UserControl
{
    private Storyboard? _drift;

    public AuroraBackground()
    {
        InitializeComponent();
        Loaded += (_, _) => Start();
        Unloaded += (_, _) => _drift?.Stop(this);
        IsVisibleChanged += (_, e) => { if ((bool)e.NewValue) Start(); else _drift?.Pause(this); };
    }

    private void Start()
    {
        if (!SystemParameters.ClientAreaAnimation) return; // "reduce animations": stay still
        if (_drift is null)
        {
            _drift = new Storyboard();
            Timeline.SetDesiredFrameRate(_drift, 20); // slow drift: 20 fps looks the same and costs a third
            Add(_drift, "GlowAShift", TranslateTransform.XProperty, -70, 90, 23);
            Add(_drift, "GlowAShift", TranslateTransform.YProperty, -30, 50, 29);
            Add(_drift, "GlowBShift", TranslateTransform.XProperty, 60, -80, 27);
            Add(_drift, "GlowBShift", TranslateTransform.YProperty, 30, -40, 31);
            _drift.Begin(this, true);
        }
        else _drift.Resume(this);
    }

    private static void Add(Storyboard sb, string target, DependencyProperty property, double from, double to, double seconds)
    {
        var a = new DoubleAnimation(from, to, TimeSpan.FromSeconds(seconds))
        {
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
        };
        Storyboard.SetTargetName(a, target);
        Storyboard.SetTargetProperty(a, new PropertyPath(property));
        sb.Children.Add(a);
    }
}
