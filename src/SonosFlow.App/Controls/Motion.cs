using System.ComponentModel;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace SonosFlow.App.Controls;

/// <summary>Finite, replaceable presentation clocks. Commands and bindings remain synchronous.</summary>
public static class Motion
{
    public static readonly TimeSpan Feedback = TimeSpan.FromMilliseconds(120);
    public static readonly TimeSpan Toggle = TimeSpan.FromMilliseconds(160);
    public static readonly TimeSpan Transition = TimeSpan.FromMilliseconds(180);
    public static bool Enabled => SystemParameters.ClientAreaAnimation;
    public static bool PointerMotion => Enabled && InputManager.Current.MostRecentInputDevice is MouseDevice;

    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<DependencyObject, Dictionary<DependencyProperty, long>> Revisions = new();

    public static void To(IAnimatable target, DependencyProperty property, double value, TimeSpan duration,
        bool animate, Action? completed = null)
    {
        var owner = (DependencyObject)target;
        var revisions = Revisions.GetOrCreateValue(owner);
        long revision = revisions.GetValueOrDefault(property) + 1;
        revisions[property] = revision;
        double current = (double)owner.GetValue(property);
        if (!animate || !Enabled || Math.Abs(current - value) < 0.0001)
        {
            target.BeginAnimation(property, null);
            owner.SetCurrentValue(property, value);
            completed?.Invoke();
            return;
        }
        var animation = new DoubleAnimation(current, value, duration) { EasingFunction = new InterfaceEase() };
        animation.Completed += (_, _) =>
        {
            if (revisions[property] != revision) return;
            target.BeginAnimation(property, null);
            owner.SetCurrentValue(property, value);
            completed?.Invoke();
        };
        target.BeginAnimation(property, animation, HandoffBehavior.SnapshotAndReplace);
    }
    private sealed class InterfaceEase : EasingFunctionBase
    {
        private readonly KeySpline _curve = new(0.23, 1, 0.32, 1);
        public InterfaceEase() => EasingMode = EasingMode.EaseIn;
        protected override double EaseInCore(double normalizedTime) => _curve.GetSplineProgress(normalizedTime);
        protected override Freezable CreateInstanceCore() => new InterfaceEase();
    }

    public static readonly DependencyProperty PressProperty = DependencyProperty.RegisterAttached("Press", typeof(bool), typeof(Motion), new PropertyMetadata(false, Attach));
    public static void SetPress(DependencyObject o, bool v) => o.SetValue(PressProperty, v);
    public static bool GetPress(DependencyObject o) => (bool)o.GetValue(PressProperty);
    public static readonly DependencyProperty EnterProperty = DependencyProperty.RegisterAttached("Enter", typeof(bool), typeof(Motion), new PropertyMetadata(false, Attach));
    public static void SetEnter(DependencyObject o, bool v) => o.SetValue(EnterProperty, v);
    public static bool GetEnter(DependencyObject o) => (bool)o.GetValue(EnterProperty);
    public static readonly DependencyProperty OffsetXProperty = DependencyProperty.RegisterAttached("OffsetX", typeof(double), typeof(Motion), new PropertyMetadata(0d, OffsetChanged));
    public static void SetOffsetX(DependencyObject o, double v) => o.SetValue(OffsetXProperty, v);
    public static double GetOffsetX(DependencyObject o) => (double)o.GetValue(OffsetXProperty);
    private static readonly DependencyProperty StateProperty = DependencyProperty.RegisterAttached("State", typeof(TargetState), typeof(Motion));
    private static TargetState State(FrameworkElement element)
    {
        if (element.GetValue(StateProperty) is TargetState state) return state;
        state = new TargetState(element);
        element.SetValue(StateProperty, state);
        return state;
    }
    private static void Attach(DependencyObject o, DependencyPropertyChangedEventArgs e)
    { if (o is FrameworkElement element) State(element).Refresh(); }
    private static void OffsetChanged(DependencyObject o, DependencyPropertyChangedEventArgs e)
    {
        if (o is not FrameworkElement element) return;
        State(element).Refresh();
        if (element.RenderTransform is not TranslateTransform move) element.RenderTransform = move = new TranslateTransform();
        To(move, TranslateTransform.XProperty, (double)e.NewValue, Toggle, element.IsLoaded && PointerMotion);
    }

    private sealed class TargetState
    {
        private readonly FrameworkElement _element;
        private ButtonBase? _button;
        private DependencyPropertyDescriptor? _pressed;
        private bool _watching, _pointerPress;
        public TargetState(FrameworkElement element)
        {
            _element = element;
            element.Loaded += (_, _) =>
            {
                Refresh();
                if (GetEnter(element) && PointerMotion)
                {
                    element.Opacity = 0;
                    element.RenderTransform = new TranslateTransform(0, 4);
                    To(element, UIElement.OpacityProperty, 1, Transition, true);
                    To((TranslateTransform)element.RenderTransform, TranslateTransform.YProperty, 0, Transition, true);
                }
            };
            element.Unloaded += (_, _) => Detach();
        }
        public void Refresh()
        {
            if (!_element.IsLoaded) return;
            if (!_watching) { SystemParameters.StaticPropertyChanged += PreferencesChanged; _watching = true; }
            if (GetPress(_element) && _button is null && _element.TemplatedParent is ButtonBase button)
            {
                _button = button;
                _pressed = DependencyPropertyDescriptor.FromProperty(ButtonBase.IsPressedProperty, button.GetType());
                _pressed.AddValueChanged(button, PressChanged);
            }
        }
        private void PressChanged(object? sender, EventArgs e)
        {
            if (_button is null) return;
            if (_button.IsPressed) _pointerPress = Mouse.LeftButton == MouseButtonState.Pressed;
            if (_element.RenderTransform is not ScaleTransform scale) _element.RenderTransform = scale = new ScaleTransform(1, 1);
            _element.RenderTransformOrigin = new Point(0.5, 0.5);
            bool animate = _pointerPress && Enabled;
            double target = _button.IsPressed && _pointerPress && Enabled ? 0.97 : 1;
            To(scale, ScaleTransform.ScaleXProperty, target, Feedback, animate);
            To(scale, ScaleTransform.ScaleYProperty, target, Feedback, animate);
            if (!_button.IsPressed) _pointerPress = false;
        }
        private void PreferencesChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName != nameof(SystemParameters.ClientAreaAnimation)) return;
            _element.Dispatcher.InvokeAsync(() => { if (!Enabled) Reset(); });
        }
        private void Reset()
        {
            if (_element.RenderTransform is ScaleTransform scale)
            {
                To(scale, ScaleTransform.ScaleXProperty, 1, Feedback, false);
                To(scale, ScaleTransform.ScaleYProperty, 1, Feedback, false);
            }
            if (_element.RenderTransform is TranslateTransform move)
            {
                To(move, TranslateTransform.XProperty, GetOffsetX(_element), Toggle, false);
                if (GetEnter(_element)) To(move, TranslateTransform.YProperty, 0, Transition, false);
            }
            if (GetEnter(_element)) To(_element, UIElement.OpacityProperty, 1, Transition, false);
        }
        private void Detach()
        {
            if (_watching) SystemParameters.StaticPropertyChanged -= PreferencesChanged;
            _watching = false;
            if (_button is not null) _pressed?.RemoveValueChanged(_button, PressChanged);
            _button = null;
            _pressed = null;
            Reset();
        }
    }
}
