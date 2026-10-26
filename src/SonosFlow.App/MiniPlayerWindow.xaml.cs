using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using SonosFlow.App.ViewModels;

namespace SonosFlow.App;

/// <summary>
/// Floating capsule player. Shares <see cref="MainViewModel"/> with the main window (one session,
/// one Sonos connection). Never takes focus (WS_EX_NOACTIVATE), so it doesn't interrupt games.
/// </summary>
public partial class MiniPlayerWindow : Window
{
    private const double IdleOpacity = 0.72;
    private readonly MainViewModel _vm;
    private readonly DispatcherTimer _levelTimer;
    private readonly DispatcherTimer _idleTimer;
    private double _level;
    private bool _allowClose;

    /// <summary>Real close, only when Audiora exits.</summary>
    public void CloseForShutdown()
    {
        _allowClose = true;
        _levelTimer.Stop();
        _idleTimer.Stop();
        Close();
    }

    public MiniPlayerWindow(MainViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        DataContext = vm;

        // ~15 fps level updates only while visible and streaming: a layered window repaints in software.
        _levelTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(66) };
        _levelTimer.Tick += (_, _) => UpdateLevel();
        _idleTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2.5) };
        _idleTimer.Tick += (_, _) => { _idleTimer.Stop(); FadeTo(_vm.MiniDimWhenIdle && !_vm.MiniSourcesExpanded ? IdleOpacity : 1); };

        SourceInitialized += (_, _) => MakeNonActivating();
        IsVisibleChanged += (_, _) => UpdateTimers();
        MouseEnter += (_, _) => { _idleTimer.Stop(); FadeTo(1); };
        MouseLeave += (_, _) => _idleTimer.Start();
        LocationChanged += (_, _) => { if (IsLoaded) _vm.SaveMiniPosition(Left, Top); };
        vm.PropertyChanged += OnVmChanged;
        Closing += (s, e) => { if (_allowClose) return; e.Cancel = true; _vm.MiniVisible = false; }; // Alt+F4 hides, never quits
    }

    private void OnVmChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MainViewModel.IsActive) or nameof(MainViewModel.IsPaused)) UpdateTimers();
        if (e.PropertyName == nameof(MainViewModel.MiniSourcesExpanded) && _vm.MiniSourcesExpanded) FadeTo(1);
        if (e.PropertyName == nameof(MainViewModel.MiniSourcesExpanded)) Dispatcher.BeginInvoke(EnsureOnScreen, DispatcherPriority.Loaded);
    }

    /// <summary>Shows at the remembered position (or bottom centre), always fully reachable on screen.</summary>
    public void ShowAtSavedPosition()
    {
        var (left, top) = _vm.MiniPosition;
        var area = SystemParameters.WorkArea;
        if (_vm.MiniRememberPosition && left is { } l && top is { } t)
        {
            Left = l;
            Top = t;
        }
        else
        {
            Left = area.Left + (area.Width - 804) / 2; // capsule + shadow margins
            Top = area.Bottom - 170;
        }
        Show();
        EnsureOnScreen();
        Opacity = 1;
        _idleTimer.Start();
    }

    /// <summary>Keeps the capsule inside the visible area of some monitor (e.g. after unplugging one).</summary>
    public void EnsureOnScreen()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero || !GetWindowRect(hwnd, out var r)) return;
        var rect = new System.Drawing.Rectangle(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top);
        var screen = System.Windows.Forms.Screen.FromRectangle(rect);
        var work = screen.WorkingArea;
        int x = Math.Clamp(rect.Left, work.Left - 20, Math.Max(work.Left, work.Right - rect.Width + 20));
        int y = Math.Clamp(rect.Top, work.Top - 20, Math.Max(work.Top, work.Bottom - rect.Height + 20));
        if (x == rect.Left && y == rect.Top) return;
        var toDip = PresentationSource.FromVisual(this)?.CompositionTarget?.TransformFromDevice ?? Matrix.Identity;
        var p = toDip.Transform(new Point(x, y));
        Left = p.X;
        Top = p.Y;
    }

    private void UpdateTimers()
    {
        bool run = IsVisible && _vm.IsActive && !_vm.IsPaused;
        if (run) _levelTimer.Start();
        else
        {
            _levelTimer.Stop();
            _level = 0;
            Ring.Level = 0;
        }
    }

    private void UpdateLevel()
    {
        var peak = _vm.Analyzer?.PeekLevel() ?? 0;
        double db = peak > 0 ? 20 * Math.Log10(peak) : -90;
        double target = Math.Clamp((db + 50) / 50, 0, 1);
        _level += (target - _level) * (target > _level ? 0.6 : 0.15);
        Ring.Level = _level;
    }

    private void FadeTo(double opacity) =>
        BeginAnimation(OpacityProperty, new DoubleAnimation(opacity, TimeSpan.FromMilliseconds(220)));

    private void Capsule_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState != MouseButtonState.Pressed) return;
        try { DragMove(); } catch (InvalidOperationException) { return; }
        EnsureOnScreen();
        _vm.SaveMiniPosition(Left, Top);
    }

    private void Hide_Click(object sender, RoutedEventArgs e) => _vm.MiniVisible = false;

    /// <summary>Renders the window (with its transparency) to PNG, for automated visual checks.</summary>
    public void SaveScreenshot(string path, Color? background = null)
    {
        var dpi = VisualTreeHelper.GetDpi(this);
        var root = this; // the whole window, including the transparent shadow margin
        int w = (int)Math.Ceiling(root.ActualWidth * dpi.DpiScaleX), h = (int)Math.Ceiling(root.ActualHeight * dpi.DpiScaleY);
        var bmp = new RenderTargetBitmap(w, h, dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
        if (background is { } bg)
        {
            // Light, patterned backdrop to show what the transparent parts let through.
            var backdrop = new DrawingVisual();
            using (var dc = backdrop.RenderOpen())
            {
                dc.DrawRectangle(new SolidColorBrush(bg), null, new Rect(0, 0, root.ActualWidth, root.ActualHeight));
                var stripe = new SolidColorBrush(Color.FromArgb(60, 120, 140, 160));
                for (double x = -root.ActualHeight; x < root.ActualWidth; x += 28)
                    dc.DrawGeometry(stripe, null, new PathGeometry([new PathFigure(new Point(x, 0),
                        [new LineSegment(new Point(x + 12, 0), false), new LineSegment(new Point(x + 12 + root.ActualHeight, root.ActualHeight), false),
                         new LineSegment(new Point(x + root.ActualHeight, root.ActualHeight), false)], true)]));
            }
            bmp.Render(backdrop);
        }
        bmp.Render(root);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(bmp));
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        using var fs = File.Create(path);
        enc.Save(fs);
    }

    private void MakeNonActivating()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        const int GwlExStyle = -20, WsExNoActivate = 0x08000000, WsExToolWindow = 0x00000080;
        SetWindowLong(hwnd, GwlExStyle, GetWindowLong(hwnd, GwlExStyle) | WsExNoActivate | WsExToolWindow);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect32 { public int Left, Top, Right, Bottom; }

    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out Rect32 rect);
    [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr hwnd, int index);
    [DllImport("user32.dll")] private static extern int SetWindowLong(IntPtr hwnd, int index, int value);
}
