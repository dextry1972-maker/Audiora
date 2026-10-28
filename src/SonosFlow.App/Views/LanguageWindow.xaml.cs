using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using SonosFlow.App.Localization;

namespace SonosFlow.App.Views;

/// <summary>
/// First-launch language choice (fresh installs only, see <see cref="LanguageStartup"/>). Highlighting a language
/// previews it: this window switches language and direction live, so the user sees the result before confirming.
/// Closing without confirming keeps the suggestion; it can be changed any time in Settings.
/// </summary>
public partial class LanguageWindow : Window
{
    public sealed record Row(LanguageInfo Info, bool IsSuggested)
    {
        public string Display => Info.Display;
    }

    private readonly List<Row> _rows;
    private readonly string _suggested;

    /// <summary>The confirmed language, or null if the window was closed without confirming.</summary>
    public string? Chosen { get; private set; }

    public LanguageWindow(string suggested)
    {
        InitializeComponent();
        _suggested = suggested;
        // Suggested first, then the rest in registry order.
        _rows = [.. Languages.All.OrderBy(l => l.Code == suggested ? 0 : 1).Select(l => new Row(l, l.Code == suggested))];
        List.ItemsSource = _rows;
        List.SelectedIndex = 0;
        Loaded += (_, _) => Search.Focus();
    }

    /// <summary>Shows the window modally; returns the chosen code or null.</summary>
    public static string? Ask(string suggested)
    {
        var w = new LanguageWindow(suggested);
        w.ShowDialog();
        return w.Chosen;
    }

    private void Search_TextChanged(object sender, TextChangedEventArgs e)
    {
        var q = Search.Text.Trim();
        SearchHint.Visibility = q.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        var shown = q.Length == 0 ? _rows : _rows.Where(r =>
            r.Info.NativeName.Contains(q, StringComparison.CurrentCultureIgnoreCase)
            || r.Info.EnglishName.Contains(q, StringComparison.OrdinalIgnoreCase)
            || r.Info.Code.StartsWith(q, StringComparison.OrdinalIgnoreCase)).ToList();
        List.ItemsSource = shown;
        NoResults.Visibility = shown.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (shown.Count > 0) List.SelectedIndex = 0;
    }

    private void Search_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        // Arrow keys from the search box move through the list; Enter confirms.
        if (e.Key is Key.Down or Key.Up && List.Items.Count > 0)
        {
            int i = Math.Clamp(List.SelectedIndex + (e.Key == Key.Down ? 1 : -1), 0, List.Items.Count - 1);
            List.SelectedIndex = i;
            List.ScrollIntoView(List.SelectedItem);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape) { Close(); e.Handled = true; }
    }

    private void List_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        Confirm.IsEnabled = List.SelectedItem is Row;
        if (List.SelectedItem is Row r) Loc.Instance.SetLanguage(r.Info.Code); // live preview
    }

    private void List_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) Close();
    }

    private void List_MouseDoubleClick(object sender, MouseButtonEventArgs e) => Confirm_Click(sender, e);

    private void Confirm_Click(object sender, RoutedEventArgs e)
    {
        if (List.SelectedItem is not Row r) return;
        Chosen = r.Info.Code;
        Close();
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    protected override void OnClosed(EventArgs e)
    {
        if (Chosen is null) Loc.Instance.SetLanguage(_suggested); // undo the preview: closing keeps the suggestion
        base.OnClosed(e);
    }

    private void Root_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed && e.OriginalSource is Border) DragMove();
    }

    /// <summary>For automated visual checks.</summary>
    public void SaveScreenshot(string path)
    {
        var dpi = System.Windows.Media.VisualTreeHelper.GetDpi(this);
        var root = (FrameworkElement)Content;
        var bmp = new System.Windows.Media.Imaging.RenderTargetBitmap((int)(ActualWidth * dpi.DpiScaleX), (int)(ActualHeight * dpi.DpiScaleY),
            dpi.PixelsPerInchX, dpi.PixelsPerInchY, System.Windows.Media.PixelFormats.Pbgra32);
        var bg = new System.Windows.Media.DrawingVisual();
        using (var dc = bg.RenderOpen()) dc.DrawRectangle(new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x2B, 0x3A, 0x4A)), null, new Rect(0, 0, ActualWidth, ActualHeight));
        bmp.Render(bg);
        bmp.Render(this);
        var enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
        enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bmp));
        using var fs = File.Create(path);
        enc.Save(fs);
    }
}
