using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace SonosFlow.App.Infrastructure;

/// <summary>Helpers for the PNG captures used by the automated visual checks.</summary>
internal static class Snapshot
{
    /// <summary>
    /// Rendering a window's content element on its own drops the mirroring its right-to-left window applies
    /// (the screen itself is correct), so the PNG comes out flipped; flip it back so it matches what the user sees.
    /// Not needed when the whole Window is rendered.
    /// </summary>
    public static BitmapSource AsOnScreen(BitmapSource bitmap, FrameworkElement content) =>
        content is not Window && content.FlowDirection == FlowDirection.RightToLeft
            ? new TransformedBitmap(bitmap, new ScaleTransform(-1, 1))
            : bitmap;
}
