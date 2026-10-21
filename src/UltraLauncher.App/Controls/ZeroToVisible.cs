using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace UltraLauncher.App.Controls;

/// <summary>0 → Visible (для подписей «список пуст»).</summary>
public sealed class ZeroToVisible : IValueConverter
{
    public static readonly ZeroToVisible Instance = new();

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is 0 ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}
