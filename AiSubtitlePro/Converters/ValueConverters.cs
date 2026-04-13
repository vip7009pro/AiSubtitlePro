using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using AiSubtitlePro.Core.Models;

namespace AiSubtitlePro.Converters;

/// <summary>
/// Converts null to Visibility (null = Collapsed, non-null = Visible)
/// Use ConverterParameter="invert" to reverse behavior
/// </summary>
public class NullToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var isNull = value == null;
        var invert = parameter?.ToString()?.ToLower() == "invert";
        
        if (invert)
            isNull = !isNull;
        
        return isNull ? Visibility.Collapsed : Visibility.Visible;
    }
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}

/// <summary>
/// Converts boolean to Visibility
/// </summary>
public class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var boolValue = value is bool b && b;
        var invert = parameter?.ToString()?.ToLower() == "invert";
        
        if (invert)
            boolValue = !boolValue;
            
        return boolValue ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        return value is Visibility v && v == Visibility.Visible;
    }
}

/// <summary>
/// Inverts a boolean value
/// </summary>
public class InverseBoolConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        return value is bool b ? !b : false;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        return value is bool b ? !b : false;
    }
}

/// <summary>
/// Formats a double value to a specific precision
/// </summary>
public class DoubleFormatConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is double d)
        {
            var format = parameter?.ToString() ?? "F2";
            var suffix = string.Empty;
            
            // Check if we have a suffix after the format specifier (e.g., "F2x")
            if (format.Length > 2 && char.IsLetter(format[format.Length - 1]))
            {
                suffix = format[format.Length - 1].ToString();
                format = format.Substring(0, format.Length - 1);
            }
            
            return d.ToString($"{format}", culture) + suffix;
        }
        return value?.ToString() ?? "";
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        return Binding.DoNothing;
    }
}

public class AssColorToBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var s = value?.ToString();
        if (string.IsNullOrWhiteSpace(s))
            return Brushes.Transparent;

        var c = SubtitleStyle.AssToColor(s);
        return new SolidColorBrush(Color.FromArgb(c.A, c.R, c.G, c.B));
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        return Binding.DoNothing;
    }
}

