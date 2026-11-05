using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using SonosFlow.App.ViewModels;

namespace SonosFlow.App;

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm;
    private int _pageRevision;
    private bool _watchingMotion;

    public MainWindow(MainViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        DataContext = vm;
        vm.PropertyChanged += OnVmChanged;
        _wasSourceReady = vm.SourceReady;
        Loaded += (_, _) => UpdateFlowMotion();
        Loaded += (_, _) => { if (!_watchingMotion) { SystemParameters.StaticPropertyChanged += OnMotionPreference; _watchingMotion = true; } };
        Unloaded += (_, _) => { SystemParameters.StaticPropertyChanged -= OnMotionPreference; _watchingMotion = false; };
        Closed += (_, _) => { SystemParameters.StaticPropertyChanged -= OnMotionPreference; vm.PropertyChanged -= OnVmChanged; };
        Loaded += (_, _) => MoveNavigation(false);
        NavTrack.SizeChanged += (_, _) => MoveNavigation(false);
        SourceInitialized += (_, _) => ApplyWindowsChrome();
        StateChanged += (_, _) =>
        {
            MaxButton.Content = WindowState == WindowState.Maximized ? "" : "";
            // WindowChrome windows overhang the screen when maximized; pad by the frame size.
            ((FrameworkElement)Content).Margin = WindowState == WindowState.Maximized ? new Thickness(7) : new Thickness(0);
        };
    }

    private void OnMotionPreference(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(SystemParameters.ClientAreaAnimation)) return;
        Dispatcher.InvokeAsync(() =>
        {
            if (Controls.Motion.Enabled) return;
            ++_pageRevision;
            MoveNavigation(false);
            foreach (var page in new[] { HomePage, SettingsPage })
            {
                bool selected = page == (_vm.Page == "settings" ? SettingsPage : HomePage);
                Controls.Motion.To(page, OpacityProperty, selected ? 1 : 0, Controls.Motion.Transition, false);
                if (page.RenderTransform is TranslateTransform move)
                {
                    Controls.Motion.To(move, TranslateTransform.YProperty, 0, Controls.Motion.Transition, false);
                    Controls.Motion.To(move, TranslateTransform.XProperty, 0, Controls.Motion.Transition, false);
                }
                page.Visibility = selected ? Visibility.Visible : Visibility.Collapsed;
            }
        });
    }

    private bool _wasSourceReady, _wavesDimmed;

    private void OnVmChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.Page)) ShowPage(_vm.Page);
        else if (e.PropertyName is nameof(MainViewModel.SourceReady) or nameof(MainViewModel.WavesDim)) UpdateFlowMotion();
    }

    /// <summary>
    /// Presentation of the guided flow, all finite and skipped when Windows animations are off: the waves step back while no source is
    /// chosen, and when a source becomes valid the confirmation fades in and the stream button gives one short glow.
    /// </summary>
    private void UpdateFlowMotion()
    {
        bool dim = _vm.WavesDim;
        if (dim != _wavesDimmed)
        {
            _wavesDimmed = dim;
            Controls.Motion.To(Waves, OpacityProperty, dim ? 0.3 : 1, Controls.Motion.Navigation, IsLoaded);
        }

        bool ready = _vm.SourceReady;
        if (ready == _wasSourceReady) return;
        _wasSourceReady = ready;
        if (!ready || !IsLoaded || _vm.State != UiState.Ready) return;
        Controls.Motion.To(GuideBlock, OpacityProperty, 1, Controls.Motion.Transition, false);
        if (Controls.Motion.Enabled) { GuideBlock.Opacity = 0; Controls.Motion.To(GuideBlock, OpacityProperty, 1, Controls.Motion.Navigation, true); }
        PlayReadyGlow();
    }

    /// <summary>One soft pulse behind the stream button when it becomes available. Not looped.</summary>
    private void PlayReadyGlow()
    {
        if (!Controls.Motion.Enabled || StreamButton.Template.FindName("ReadyGlow", StreamButton) is not System.Windows.Shapes.Ellipse glow) return;
        var scale = new ScaleTransform(0.92, 0.92);
        glow.RenderTransform = scale;
        var grow = new DoubleAnimation(0.92, 1.45, TimeSpan.FromMilliseconds(900)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, grow);
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, grow);
        var fade = new DoubleAnimationUsingKeyFrames();
        fade.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        fade.KeyFrames.Add(new LinearDoubleKeyFrame(0.85, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(180))));
        fade.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(900))));
        glow.BeginAnimation(OpacityProperty, fade);
    }

    /// <summary>Picking a speaker closes the device menu.</summary>
    private void Speaker_Click(object sender, RoutedEventArgs e) => DeviceToggle.IsChecked = false;

    public void ShowPage(string page)
    {
        var (show, hide) = page == "settings" ? (SettingsPage, HomePage) : (HomePage, SettingsPage);
        NavHome.IsChecked = page != "settings";
        NavSettings.IsChecked = page == "settings";
        MoveNavigation(Controls.Motion.PointerMotion);
        if (show.Visibility == Visibility.Visible && show.IsHitTestVisible && show.Opacity >= 0.999) return;

        int revision = ++_pageRevision;
        bool animate = Controls.Motion.PointerMotion;
        show.IsHitTestVisible = true;
        hide.IsHitTestVisible = false;
        bool entering = show.Visibility != Visibility.Visible;
        show.Visibility = Visibility.Visible;
        System.Windows.Controls.Panel.SetZIndex(show, 1);
        System.Windows.Controls.Panel.SetZIndex(hide, 0);
        if (entering) show.ScrollToTop();
        if (show.RenderTransform is not TranslateTransform move) show.RenderTransform = move = new TranslateTransform();
        if (entering) { show.Opacity = animate ? 0 : 1; move.X = animate ? (page == "settings" ? 24 : -24) * (FlowDirection == FlowDirection.RightToLeft ? -1 : 1) : 0; }
        Controls.Motion.To(show, OpacityProperty, 1, Controls.Motion.Transition, animate);
        Controls.Motion.To(move, TranslateTransform.XProperty, 0, Controls.Motion.Navigation, animate, spatial: true);
        Controls.Motion.To(hide, OpacityProperty, 0, Controls.Motion.Transition, animate, () =>
        {
            if (revision == _pageRevision) hide.Visibility = Visibility.Collapsed;
        });
    }

    private void MoveNavigation(bool animate)
    {
        var selected = _vm.Page == "settings" ? NavSettings : NavHome;
        if (!IsLoaded || selected.ActualHeight <= 0) return;
        NavSelection.Height = selected.ActualHeight;
        double y = selected.TranslatePoint(new Point(), NavTrack).Y;
        Controls.Motion.To((TranslateTransform)NavSelection.RenderTransform, TranslateTransform.YProperty,
            y, Controls.Motion.Navigation, animate, spatial: true);
    }

    /// <summary>Saves the rendered window as PNG (used for automated visual checks).</summary>
    public void SaveScreenshot(string path)
    {
        var root = (FrameworkElement)Content;
        var dpi = VisualTreeHelper.GetDpi(this);
        var bmp = new RenderTargetBitmap((int)(root.ActualWidth * dpi.DpiScaleX), (int)(root.ActualHeight * dpi.DpiScaleY),
            dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
        var bg = new DrawingVisual();
        using (var dc = bg.RenderOpen()) dc.DrawRectangle(Background, null, new Rect(0, 0, root.ActualWidth, root.ActualHeight));
        bmp.Render(bg);
        bmp.Render(root);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(Infrastructure.Snapshot.AsOnScreen(bmp, root)));
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        using var fs = File.Create(path);
        enc.Save(fs);
    }

    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void Maximize_Click(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    // Dark frame and rounded corners from DWM (Windows 11; silently ignored on Windows 10).
    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    private void ApplyWindowsChrome()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        int dark = 1, round = 2, border = 0x002C2620; // COLORREF 0x00BBGGRR
        DwmSetWindowAttribute(hwnd, 20, ref dark, sizeof(int));   // DWMWA_USE_IMMERSIVE_DARK_MODE
        DwmSetWindowAttribute(hwnd, 33, ref round, sizeof(int));  // DWMWA_WINDOW_CORNER_PREFERENCE = ROUND
        DwmSetWindowAttribute(hwnd, 34, ref border, sizeof(int)); // DWMWA_BORDER_COLOR
    }
}
