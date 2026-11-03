using System.Windows;
using System.Windows.Input;
using SonosFlow.App.Infrastructure;
using SonosFlow.App.ViewModels;

namespace SonosFlow.App.Views;

/// <summary>First-launch step after the language: asks for the name used by the welcome. Skipping is always possible.</summary>
public partial class NameWindow : Window
{
    /// <summary>The cleaned name, or null when skipped or left empty.</summary>
    public string? Chosen { get; private set; }

    public NameWindow()
    {
        InitializeComponent();
        Loaded += (_, _) => NameBox.Focus();
    }

    /// <summary>Shows the window modally; returns the name or null.</summary>
    public static string? Ask()
    {
        var w = new NameWindow();
        w.ShowDialog();
        return w.Chosen;
    }

    private void Confirm_Click(object sender, RoutedEventArgs e)
    {
        var name = MainViewModel.CleanUserName(NameBox.Text);
        Chosen = name.Length == 0 ? null : name;
        Close();
    }

    private void Skip_Click(object sender, RoutedEventArgs e) => Close();

    private void Root_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed && e.OriginalSource is System.Windows.Controls.Border) DragMove();
    }

    /// <summary>For automated visual checks (optionally with a name already typed).</summary>
    public void SaveScreenshot(string path, string? typed = null)
    {
        if (typed is not null) NameBox.Text = typed;
        WindowCapture.Save(this, path);
    }
}
