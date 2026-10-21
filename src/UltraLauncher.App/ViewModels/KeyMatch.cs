using System.Globalization;
using System.Windows.Data;

namespace UltraLauncher.App.ViewModels;

/// <summary>Отмечает активный пункт навигации: значение == параметр.</summary>
public sealed class KeyMatch : IValueConverter
{
    public static readonly KeyMatch Instance = new();

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => Equals(value as string, parameter as string);

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}
