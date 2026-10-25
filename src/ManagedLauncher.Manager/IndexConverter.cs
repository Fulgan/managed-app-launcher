using System.Globalization;
using System.Windows.Controls;
using System.Windows.Data;

namespace ManagedLauncher.Manager;

/// <summary>Shows a list item's position, which is also its place on the deck.</summary>
public sealed class IndexConverter : IValueConverter
{
    public static readonly IndexConverter Instance = new();

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is ListBoxItem item && ItemsControl.ItemsControlFromItemContainer(item) is { } owner
            ? (owner.ItemContainerGenerator.IndexFromContainer(item) + 1).ToString(culture)
            : "";

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
