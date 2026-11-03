using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using SonosFlow.App.ViewModels;

namespace SonosFlow.App.Views;

/// <summary>Settings page. Binds to the shared <see cref="ViewModels.MainViewModel"/> inherited from the window.</summary>
public partial class SettingsView : UserControl
{
    public SettingsView()
    {
        InitializeComponent();
        ShowPane("profile");
        // Plugged-in devices show up each time the page opens.
        Loaded += (_, _) => (DataContext as MainViewModel)?.RefreshSoundDevices();
        // A short sample at the new level once the volume control is released (mouse drag or keyboard).
        SoundsVolumeSlider.AddHandler(Thumb.DragCompletedEvent, new DragCompletedEventHandler((_, _) => PlayVolumeSample()));
        SoundsVolumeSlider.PreviewKeyUp += (_, e) =>
        {
            if (e.Key is Key.Left or Key.Right or Key.Up or Key.Down or Key.Home or Key.End or Key.PageUp or Key.PageDown) PlayVolumeSample();
        };
    }

    /// <summary>Immediate by design: switching category is a view change, there is nothing to animate (and reduced motion stays honoured).</summary>
    private void Tab_Checked(object sender, System.Windows.RoutedEventArgs e)
    {
        if (sender is RadioButton { Tag: string tag }) ShowPane(tag);
    }

    private void ShowPane(string tag)
    {
        // Checked fires while the XAML is still loading, before every pane exists.
        if (PaneProfile is null || PaneGeneral is null || PaneAudio is null || PaneSounds is null || PaneMini is null || PaneAdvanced is null) return;
        foreach (var pane in new[] { PaneProfile, PaneGeneral, PaneAudio, PaneSounds, PaneMini, PaneAdvanced })
            pane.Visibility = (string)pane.Tag == tag ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
    }

    private void PlayVolumeSample() => (DataContext as MainViewModel)?.PlaySoundsVolumeSample();
}
