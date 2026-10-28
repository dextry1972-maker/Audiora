using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace SonosFlow.App.Localization;

/// <summary>
/// Like HTML's dir="auto" for text that is not ours (song titles, artists, language names): the TextBlock takes the
/// direction of its first strong letter whatever the UI direction, so "(Video Oficial)" or "Português (Brasil)" don't
/// get their brackets moved around in the Arabic layout. Pair it with HorizontalAlignment="Left" (the start edge in
/// the parent's direction) so the text still sits where the layout puts it:
/// <c>FlowDirection="{Binding Text, RelativeSource={RelativeSource Self}, Converter={x:Static loc:AutoDirection.Instance}}"</c>.
/// </summary>
public sealed class AutoDirection : IValueConverter
{
    public static readonly AutoDirection Instance = new();

    public static FlowDirection Of(string? text)
    {
        foreach (var c in text ?? string.Empty)
        {
            if (IsRightToLeftLetter(c)) return FlowDirection.RightToLeft;
            if (char.IsLetter(c)) return FlowDirection.LeftToRight;
        }
        return Loc.Instance.FlowDirection; // digits and punctuation only: follow the UI
    }

    private static bool IsRightToLeftLetter(char c) =>
        c is >= '֐' and <= 'ࣿ' or >= 'יִ' and <= '﷿' or >= 'ﹰ' and <= '﻿';

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => Of(value as string);

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}
