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
    private Storyboard? _rings;

    public MainWindow(MainViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        DataContext = vm;
        vm.PropertyChanged += OnVmChanged;
        SourceInitialized += (_, _) => ApplyWindowsChrome();
        StateChanged += (_, _) =>
        {
            MaxButton.Content = WindowState == WindowState.Maximized ? "" : "";
            // WindowChrome windows overhang the screen when maximized; pad by the frame size.
            ((FrameworkElement)Content).Margin = WindowState == WindowState.Maximized ? new Thickness(7) : new Thickness(0);
        };
    }

    private void OnVmChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(MainViewModel.Page):
                ShowPage(_vm.Page);
                break;
            case nameof(MainViewModel.IsActive):
                if (_vm.IsActive) StartRings(); else StopRings();
                break;
        }
    }

    public void ShowPage(string page)
    {
        var (show, hide) = page == "settings" ? (SettingsPage, HomePage) : (HomePage, SettingsPage);
        NavHome.IsChecked = page != "settings";
        NavSettings.IsChecked = page == "settings";
        if (show.Visibility == Visibility.Visible) return;

        hide.Visibility = Visibility.Collapsed;
        show.Visibility = Visibility.Visible;
        show.ScrollToTop();
        var move = new TranslateTransform(0, 14);
        show.RenderTransform = move;
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        show.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(260)) { EasingFunction = ease });
        move.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(14, 0, TimeSpan.FromMilliseconds(320)) { EasingFunction = ease });
    }

    private void StartRings()
    {
        if (_rings is not null) return;
        _rings = new Storyboard { RepeatBehavior = RepeatBehavior.Forever };
        AddRing(_rings, Ring1, Ring1S, TimeSpan.Zero);
        AddRing(_rings, Ring2, Ring2S, TimeSpan.FromSeconds(1.1));
        _rings.Begin(this, true);
    }

    private static void AddRing(Storyboard sb, UIElement ring, ScaleTransform scale, TimeSpan delay)
    {
        var duration = TimeSpan.FromSeconds(2.2);
        var ease = new QuadraticEase { EasingMode = EasingMode.EaseOut };
        var op = new DoubleAnimationUsingKeyFrames { BeginTime = delay, Duration = duration + TimeSpan.FromSeconds(0.0) };
        op.KeyFrames.Add(new LinearDoubleKeyFrame(0.55, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        op.KeyFrames.Add(new EasingDoubleKeyFrame(0, KeyTime.FromTimeSpan(duration), ease));
        Storyboard.SetTarget(op, ring);
        Storyboard.SetTargetProperty(op, new PropertyPath(OpacityProperty));
        sb.Children.Add(op);
        foreach (var prop in new[] { ScaleTransform.ScaleXProperty, ScaleTransform.ScaleYProperty })
        {
            var a = new DoubleAnimation(1, 1.45, duration) { BeginTime = delay, EasingFunction = ease };
            Storyboard.SetTarget(a, ring);
            Storyboard.SetTargetProperty(a, new PropertyPath($"(UIElement.RenderTransform).({nameof(ScaleTransform)}.{(prop == ScaleTransform.ScaleXProperty ? "ScaleX" : "ScaleY")})"));
            sb.Children.Add(a);
        }
        // Keep both rings on the same period so the stagger stays constant.
        sb.Duration = duration + TimeSpan.FromSeconds(1.1);
    }

    private void StopRings()
    {
        if (_rings is null) return;
        _rings.Stop(this);
        _rings = null;
        Ring1.Opacity = Ring2.Opacity = 0;
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
        enc.Frames.Add(BitmapFrame.Create(bmp));
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
