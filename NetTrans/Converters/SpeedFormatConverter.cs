using Microsoft.UI.Xaml.Data;
using NetTrans.Services;

namespace NetTrans.Converters;

public sealed class SpeedFormatConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language) =>
        value is double d ? FormatHelpers.Speed(d) : "—";

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();
}
