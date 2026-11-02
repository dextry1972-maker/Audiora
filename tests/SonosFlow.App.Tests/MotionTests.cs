using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Threading;
using SonosFlow.App.Controls;

namespace SonosFlow.App.Tests;

public sealed class MotionTests
{
    private static Task OnSta(Action test)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try { test(); done.SetResult(); }
            catch (Exception ex) { done.SetException(ex); }
            finally { Dispatcher.CurrentDispatcher.InvokeShutdown(); }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return done.Task.WaitAsync(TimeSpan.FromSeconds(10));
    }

    private static void Pump(int milliseconds)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(milliseconds) };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
        timer.Start();
        Dispatcher.PushFrame(frame);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task Reversing_a_running_clock_preserves_its_current_value(bool spatial) => OnSta(() =>
    {
        var move = new TranslateTransform(0, 6);
        var host = new Window { Width = 1, Height = 1, Left = -32000, Top = -32000, ShowActivated = false, ShowInTaskbar = false, WindowStyle = WindowStyle.None, Content = new Border { RenderTransform = move } };
        host.Show();
        try {
        Pump(40);
        Motion.To(move, TranslateTransform.YProperty, 0, Motion.Transition, true, spatial: spatial);
        // Reverse while it is genuinely in flight. A fixed Pump(100) can overshoot the whole transition when the machine is
        // busy (the clock follows wall time: seen 0.0002, i.e. already finished), so pump in small steps until it has moved.
        var inFlight = Stopwatch.StartNew();
        do Pump(5); while (Motion.Enabled && move.Y > 4 && inFlight.ElapsedMilliseconds < 1000);
        double current = move.Y;
        if (Motion.Enabled) Assert.InRange(current, 0.001, 5.999);
        Motion.To(move, TranslateTransform.YProperty, 6, Motion.Transition, true, spatial: spatial);
        if (Motion.Enabled) Assert.InRange(Math.Abs(move.Y - current), 0, 0.001);
        Pump(240);
        Assert.Equal(6, move.Y, precision: 3);
        } finally { host.Close(); }
    });
    [Fact]
    public Task Immediate_path_cancels_an_in_progress_transition() => OnSta(() =>
    {
        var move = new TranslateTransform(0, 6);
        Motion.To(move, TranslateTransform.YProperty, 0, Motion.Transition, true);
        Motion.To(move, TranslateTransform.YProperty, 9, Motion.Transition, false);
        Assert.Equal(9, move.Y);
        Assert.False(move.HasAnimatedProperties);
    });

    [Fact]
    public Task Presentation_does_not_remove_an_existing_binding() => OnSta(() =>
    {
        var source = new Border { Opacity = 0.75 };
        var target = new Border();
        BindingOperations.SetBinding(target, UIElement.OpacityProperty, new Binding("Opacity") { Source = source });
        Motion.To(target, UIElement.OpacityProperty, 1, Motion.Transition, false);
        Assert.True(BindingOperations.IsDataBound(target, UIElement.OpacityProperty));
        source.Opacity = 0.4;
        Assert.Equal(0.4, target.Opacity);
    });

    [Fact]
    public Task Rapid_reversals_settle_to_the_latest_destination() => OnSta(() =>
    {
        var scale = new ScaleTransform(1, 1);
        for (int i = 0; i < 100; i++)
            Motion.To(scale, ScaleTransform.ScaleXProperty, i % 2 == 0 ? 0.97 : 1, Motion.Feedback, true);
        Motion.To(scale, ScaleTransform.ScaleXProperty, 1, Motion.Feedback, false);
        Assert.Equal(1, scale.ScaleX);
        Assert.False(scale.HasAnimatedProperties);
    });

    [Fact]
    public Task Switch_template_tracks_repeated_checked_changes_without_loading_animation() => OnSta(() =>
    {
        var resources = (ResourceDictionary)Application.LoadComponent(new Uri("/SonosFlow;component/Theme/Theme.xaml", UriKind.Relative));
        var toggle = new CheckBox { Style = (Style)resources["Switch"] };
        toggle.Resources.MergedDictionaries.Add(resources);
        toggle.ApplyTemplate();
        var knob = (FrameworkElement)toggle.Template.FindName("Knob", toggle);
        for (int i = 0; i < 100; i++) toggle.IsChecked = i % 2 == 0;
        Assert.Equal(-8, ((TranslateTransform)knob.RenderTransform).X);
        toggle.IsChecked = true;
        Assert.Equal(8, ((TranslateTransform)knob.RenderTransform).X);
        Assert.False(((TranslateTransform)knob.RenderTransform).HasAnimatedProperties);
    });
}
