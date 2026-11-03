using System.Windows;
using System.Windows.Media.Animation;
using System.Windows.Input;
using System.Windows.Threading;
using SonosFlow.App.Controls;
using SonosFlow.App.Infrastructure;
using SonosFlow.App.Localization;

namespace SonosFlow.App.Views;

/// <summary>"Welcome back, Elian." for a moment, then the main window. Never blocks for long and honours reduced motion.</summary>
public partial class WelcomeBackWindow : Window
{
    private readonly DispatcherTimer _timer;

    public WelcomeBackWindow(string name)
    {
        InitializeComponent();
        // The name is isolated (first-strong isolate) so it stays whole inside right-to-left text.
        Greeting.Text = Loc.Instance.F("onboarding.back.title", "⁨" + name + "⁩");
        // Shown just long enough to read; with animations off (Windows setting) it is shorter and appears at once.
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(Motion.Enabled ? 1700 : 1100) };
        _timer.Tick += (_, _) => Close();
        Loaded += (_, _) =>
        {
            if (Motion.Enabled)
            {
                Opacity = 0;
                BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(240)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
            }
            _timer.Start();
        };
        Closed += (_, _) => _timer.Stop();
    }

    /// <summary>Shows it modally; returns when it closes by itself, by a click or by a key.</summary>
    public static void ShowFor(string name) => new WelcomeBackWindow(name).ShowDialog();

    private void Dismiss(object sender, MouseButtonEventArgs e) => Close();

    private void Dismiss_Key(object sender, KeyEventArgs e) => Close();

    /// <summary>For automated visual checks.</summary>
    public void SaveScreenshot(string path) => WindowCapture.Save(this, path);
}
