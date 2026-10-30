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
        // Plugged-in devices show up each time the page opens.
        Loaded += (_, _) => (DataContext as MainViewModel)?.RefreshSoundDevices();
        // A short sample at the new level once the volume control is released (mouse drag or keyboard).
        SoundsVolumeSlider.AddHandler(Thumb.DragCompletedEvent, new DragCompletedEventHandler((_, _) => PlayVolumeSample()));
        SoundsVolumeSlider.PreviewKeyUp += (_, e) =>
        {
            if (e.Key is Key.Left or Key.Right or Key.Up or Key.Down or Key.Home or Key.End or Key.PageUp or Key.PageDown) PlayVolumeSample();
        };
    }

    private void PlayVolumeSample() => (DataContext as MainViewModel)?.PlaySoundsVolumeSample();
}
