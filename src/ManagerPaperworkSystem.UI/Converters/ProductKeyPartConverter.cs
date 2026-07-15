using System;
using System.Globalization;
using System.Windows.Data;

namespace ManagerPaperworkSystem.UI.Converters;

/// <summary>
/// Splits ProductKey formatted as "CODE|NAME" and returns requested part.
/// ConverterParameter: "Code" or "Name"
/// </summary>
public sealed class ProductKeyPartConverter : IValueConverter
{
    public object? Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var s = value as string ?? string.Empty;
        if (string.IsNullOrWhiteSpace(s)) return string.Empty;
        var parts = s.Split('|');
        if (parts.Length < 2) return s;
        var which = (parameter as string ?? "Code").Trim();
        return which.Equals("Name", StringComparison.OrdinalIgnoreCase) ? parts[1] : parts[0];
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => System.Windows.Data.Binding.DoNothing;
}
