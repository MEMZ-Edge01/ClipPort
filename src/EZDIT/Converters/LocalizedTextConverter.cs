using EZDIT.Services;
using Microsoft.UI.Xaml.Data;

namespace EZDIT.Converters;

/// <summary>
/// XAML binding converter that resolves resource keys via ResourceService.
/// The binding source value is treated as a resource key.
/// </summary>
public sealed class LocalizedTextConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language) =>
        value is string text ? ResourceService.GetString(text) : value;

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();
}
