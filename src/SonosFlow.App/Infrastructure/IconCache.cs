using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace SonosFlow.App.Infrastructure;

/// <summary>Application icons for the source list, extracted once per executable.</summary>
public static class IconCache
{
    private static readonly Dictionary<string, ImageSource?> Cache = new(StringComparer.OrdinalIgnoreCase);

    public static ImageSource? Get(string? exePath)
    {
        if (string.IsNullOrEmpty(exePath)) return null;
        if (Cache.TryGetValue(exePath, out var cached)) return cached;
        ImageSource? image = null;
        try
        {
            using var icon = System.Drawing.Icon.ExtractAssociatedIcon(exePath);
            if (icon is not null)
            {
                var src = Imaging.CreateBitmapSourceFromHIcon(icon.Handle, Int32Rect.Empty, BitmapSizeOptions.FromWidthAndHeight(32, 32));
                src.Freeze();
                image = src;
            }
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException or System.ComponentModel.Win32Exception) { }
        Cache[exePath] = image;
        return image;
    }
}
