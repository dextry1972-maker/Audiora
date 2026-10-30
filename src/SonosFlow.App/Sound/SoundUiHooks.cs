using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;
using SonosFlow.App.Views;

namespace SonosFlow.App.Sound;

/// <summary>
/// Interface feedback for the controls themselves: switches and the drop-downs of the settings page. Only real user
/// actions play (a Click, or a selection made while the drop-down is open or focused), never a value that changes
/// because a binding refreshed.
/// </summary>
public static class SoundUiHooks
{
    private static Func<SoundService?>? _service;
    private static bool _registered;

    public static void Register(Func<SoundService?> service)
    {
        _service = service;
        if (_registered) return;
        _registered = true;
        EventManager.RegisterClassHandler(typeof(CheckBox), ButtonBase.ClickEvent, new RoutedEventHandler(OnCheckBoxClick), handledEventsToo: true);
        EventManager.RegisterClassHandler(typeof(ComboBox), Selector.SelectionChangedEvent, new SelectionChangedEventHandler(OnComboChanged), handledEventsToo: true);
    }

    private static void OnCheckBoxClick(object sender, RoutedEventArgs e)
    {
        if (sender is not CheckBox box || _service?.Invoke() is not { } sounds) return;
        bool? clicked = box.IsChecked;
        // Confirm once the binding has settled: a setter that refuses the change bounces the value back.
        box.Dispatcher.BeginInvoke(DispatcherPriority.Background, (Action)(() =>
        {
            if (box.IsChecked == clicked) sounds.Play(clicked == true ? SoundId.ToggleOn : SoundId.ToggleOff);
        }));
    }

    private static void OnComboChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!ReferenceEquals(e.OriginalSource, sender) || sender is not ComboBox combo) return;
        if (e.RemovedItems.Count == 0 || e.AddedItems.Count == 0) return;          // initial population
        if (!combo.IsDropDownOpen && !combo.IsKeyboardFocusWithin) return;          // changed by code, not by the user
        if (!IsInsideSettings(combo)) return;
        _service?.Invoke()?.Play(SoundId.ConfigChanged);
    }

    private static bool IsInsideSettings(DependencyObject element)
    {
        for (var node = element; node is not null; node = VisualTreeHelper.GetParent(node))
            if (node is SettingsView) return true;
        return false;
    }
}
