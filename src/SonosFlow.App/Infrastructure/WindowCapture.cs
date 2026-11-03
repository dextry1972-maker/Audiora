using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace SonosFlow.App.Infrastructure;

/// <summary>Screenshot of a window for the automated visual checks (the same approach the other windows use).</summary>
internal static class WindowCapture
{
    public static void Save(Window window, string path)
    {
        var dpi = VisualTreeHelper.GetDpi(window);
        var bmp = new RenderTargetBitmap((int)(window.ActualWidth * dpi.DpiScaleX), (int)(window.ActualHeight * dpi.DpiScaleY),
            dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
        var bg = new DrawingVisual();
        using (var dc = bg.RenderOpen()) dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(0x2B, 0x3A, 0x4A)), null, new Rect(0, 0, window.ActualWidth, window.ActualHeight));
        bmp.Render(bg);
        bmp.Render(window);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(bmp));
        using var fs = File.Create(path);
        enc.Save(fs);
    }
}
