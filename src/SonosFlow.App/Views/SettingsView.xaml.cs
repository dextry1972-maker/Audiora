using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using SonosFlow.App.Infrastructure;
using SonosFlow.App.ViewModels;

namespace SonosFlow.App.Views;

public partial class SettingsView : UserControl
{
    private UiFeedback? _feedback;
    private MainViewModel? _observed;
    private bool _armed;

    public SettingsView()
    {
        InitializeComponent();
        PreviewMouseLeftButtonDown += (_, _) => _armed = true;
        PreviewKeyDown += (_, e) => _armed = e.Key is Key.Space or Key.Enter or Key.Up or Key.Down or Key.Left or Key.Right;
        AddHandler(ToggleButton.CheckedEvent, new RoutedEventHandler(Changed), true);
        AddHandler(ToggleButton.UncheckedEvent, new RoutedEventHandler(Changed), true);
        AddHandler(Selector.SelectionChangedEvent, new SelectionChangedEventHandler((s, e) => Changed(s, e)), true);
        Loaded += (_, _) => Observe();
        IsVisibleChanged += (_, _) => { if (!IsVisible) { _armed = false; _feedback?.Stop(); } };
        DataContextChanged += (_, _) => { if (IsLoaded) Observe(); };
        Unloaded += (_, _) => { Unobserve(); _feedback?.Dispose(); _feedback = null; _armed = false; };
    }

    private void Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded || !IsVisible || !_armed || e.OriginalSource is not (CheckBox or ComboBox)) return;
        _armed = false;
        // Confirm after the two-way binding has committed, including disabling the sounds themselves.
        Dispatcher.InvokeAsync(() =>
        {
            if (!IsLoaded || !IsVisible || DataContext is not MainViewModel vm) return;
            (_feedback ??= new UiFeedback()).Confirm(vm.InterfaceSounds, vm.IsStreaming);
        }, System.Windows.Threading.DispatcherPriority.Input);
    }

    private void Observe()
    {
        Unobserve();
        _observed = DataContext as MainViewModel;
        if (_observed is not null) _observed.StreamingChanged += StreamingChanged;
        StreamingChanged();
    }
    private void Unobserve()
    {
        if (_observed is not null) _observed.StreamingChanged -= StreamingChanged;
        _observed = null;
    }
    private void StreamingChanged() { if (_observed?.IsStreaming == true) { _feedback?.Stop(); _armed = false; } }
}
