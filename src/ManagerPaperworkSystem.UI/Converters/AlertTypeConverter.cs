using System;
using System.Globalization;
using System.Windows.Data;
using ManagerPaperworkSystem.Core.Models;

namespace ManagerPaperworkSystem.UI.Converters;

/// <summary>
/// Converts PriceAlertType enum values to user-friendly display strings.
/// </summary>
public sealed class AlertTypeConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is PriceAlertType alertType)
        {
            return alertType switch
            {
                PriceAlertType.PriceChange => "Price Change",
                PriceAlertType.CrossVendorPrice => "Cross-Vendor Price",
                PriceAlertType.CrossVendorNew => "New Vendor",
                _ => alertType.ToString()
            };
        }
        return value?.ToString() ?? "";
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => System.Windows.Data.Binding.DoNothing;
}
