using EZDIT.Services;
using Microsoft.UI.Xaml.Data;

namespace EZDIT.Converters;

public sealed class LocalizedTextConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language) =>
        value is string text ? LocalizationService.Text(text) : value;

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();
}
