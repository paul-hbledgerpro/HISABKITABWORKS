using System;
using System.Globalization;
using System.Text.RegularExpressions;
using System.Windows.Data;

namespace ManagerPaperworkSystem.UI.Converters;

/// <summary>
/// Converts enum values like "SalesSummaryByDate" to "Sales Summary by Date".
/// </summary>
public sealed class EnumDisplayConverter : IValueConverter
{
    private static readonly Regex SplitCaps = new Regex("(?<=[a-z])([A-Z])", RegexOptions.Compiled);

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is null) return string.Empty;
        var s = value.ToString() ?? string.Empty;
        if (s.Length == 0) return s;

        // Insert spaces between camel-case parts
        s = SplitCaps.Replace(s, " $1");

        // Small readability tweaks
        s = s.Replace(" By ", " by ");
        return s;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => System.Windows.Data.Binding.DoNothing;
}
