using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace SonosFlow.App;

/// <summary>
/// Brief on-screen indicator for the global shortcuts (volume, pause). Never activates or takes focus
/// (WS_EX_NOACTIVATE | WS_EX_TRANSPARENT), so it doesn't interrupt games; it shows over borderless/windowed
/// games but not over exclusive-fullscreen ones (Windows does not allow it).
/// </summary>
public sealed class VolumeHudWindow : Window
{
    private readonly TextBlock _icon = new() { FontSize = 20, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _text = new() { FontSize = 14, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0) };
    private readonly ProgressBar _bar = new() { Height = 4, Minimum = 0, Maximum = 1, Margin = new Thickness(0, 10, 0, 0), BorderThickness = new Thickness(0) };
    private readonly DispatcherTimer _hide = new() { Interval = TimeSpan.FromSeconds(1.6) };

    public VolumeHudWindow()
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        Topmost = true;
        ShowActivated = false;
        ShowInTaskbar = false;
        ResizeMode = ResizeMode.NoResize;
        SizeToContent = SizeToContent.WidthAndHeight;
        Focusable = false;

        var text = Res("B.Text", Colors.White);
        _icon.FontFamily = TryFindResource("F.Icons") as FontFamily ?? new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets");
        _icon.Foreground = Res("B.Accent", Color.FromRgb(0x89, 0x57, 0xF5));
        _text.Foreground = text;
        _bar.Foreground = Res("B.Accent", Color.FromRgb(0x89, 0x57, 0xF5));
        _bar.Background = new SolidColorBrush(Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF));

        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(_icon);
        row.Children.Add(_text);
        var panel = new StackPanel { MinWidth = 220 };
        panel.Children.Add(row);
        panel.Children.Add(_bar);
        Content = new Border
        {
            Margin = new Thickness(16), // room for the shadow
            Padding = new Thickness(18, 14, 18, 14),
            CornerRadius = new CornerRadius(18),
            Background = new LinearGradientBrush(Color.FromArgb(0xF2, 0x14, 0x1E, 0x48), Color.FromArgb(0xF7, 0x0A, 0x10, 0x30), new Point(0, 0), new Point(1, 1)), // emerald glass, as the mini player
            BorderBrush = new LinearGradientBrush(Color.FromArgb(0x66, 0xFF, 0xFF, 0xFF), Color.FromArgb(0x55, 0xB6, 0xA5, 0xFF), new Point(0, 0), new Point(1, 1)),
            BorderThickness = new Thickness(1),
            Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 18, ShadowDepth = 3, Opacity = 0.45 },
            Child = panel,
        };

        _hide.Tick += (_, _) =>
        {
            _hide.Stop();
            Hide(); // Frequent keyboard feedback has no presentation delay.
        };
        SourceInitialized += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            const int GwlExStyle = -20, WsExNoActivate = 0x08000000, WsExToolWindow = 0x80, WsExTransparent = 0x20;
            SetWindowLong(hwnd, GwlExStyle, GetWindowLong(hwnd, GwlExStyle) | WsExNoActivate | WsExToolWindow | WsExTransparent);
        };
    }

    private SolidColorBrush Res(string key, Color fallback) => TryFindResource(key) as SolidColorBrush ?? new SolidColorBrush(fallback);

    /// <summary>Shows (or refreshes) the indicator near the bottom centre of the screen with the mouse.</summary>
    public void Flash(string glyph, string text, double? fraction)
    {
        _icon.Text = glyph;
        _text.Text = text;
        _bar.Visibility = fraction is null ? Visibility.Collapsed : Visibility.Visible;
        _bar.Value = Math.Clamp(fraction ?? 0, 0, 1);

        BeginAnimation(OpacityProperty, null);
        Opacity = 1;
        if (!IsVisible) Show();
        UpdateLayout();
        var screen = System.Windows.Forms.Screen.FromPoint(System.Windows.Forms.Cursor.Position).WorkingArea;
        var toDip = PresentationSource.FromVisual(this)?.CompositionTarget?.TransformFromDevice ?? Matrix.Identity;
        var bottomCentre = toDip.Transform(new Point(screen.Left + screen.Width / 2.0, screen.Bottom));
        Left = bottomCentre.X - ActualWidth / 2;
        Top = bottomCentre.Y - ActualHeight - 90;
        _hide.Stop();
        _hide.Start();
    }

    /// <summary>Renders the indicator to PNG over a dark backdrop, for automated visual checks.</summary>
    public void SaveScreenshot(string path)
    {
        var dpi = VisualTreeHelper.GetDpi(this);
        var bmp = new RenderTargetBitmap((int)Math.Ceiling(ActualWidth * dpi.DpiScaleX), (int)Math.Ceiling(ActualHeight * dpi.DpiScaleY),
            dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
        var backdrop = new DrawingVisual();
        using (var dc = backdrop.RenderOpen()) dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(0x2B, 0x3A, 0x4A)), null, new Rect(0, 0, ActualWidth, ActualHeight));
        bmp.Render(backdrop);
        bmp.Render(this);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(bmp));
        using var fs = File.Create(path);
        enc.Save(fs);
    }

    [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr hwnd, int index);
    [DllImport("user32.dll")] private static extern int SetWindowLong(IntPtr hwnd, int index, int value);
}
