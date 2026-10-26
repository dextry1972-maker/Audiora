using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace SonosFlow.App.Infrastructure;

public sealed class BoolToVisibilityConverter : IValueConverter
{
    public bool Invert { get; set; }

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        (value is true) ^ Invert ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

public sealed class InvertBoolConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value is not true;
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => value is not true;
}

/// <summary>Visible only when the bound count is 0 (empty-list hints).</summary>
public sealed class ZeroToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is 0 ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

public sealed class DbfsToTextConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is double d && !double.IsInfinity(d) && !double.IsNaN(d) ? $"{d:0.0} dBFS" : "— silencio";

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Maps a 0..1 level to an opacity range, for ambient glow.</summary>
public sealed class LevelToOpacityConverter : IValueConverter
{
    public double Min { get; set; } = 0.15;
    public double Max { get; set; } = 0.9;

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is double d ? Min + (Max - Min) * Math.Clamp(d, 0, 1) : Min;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
