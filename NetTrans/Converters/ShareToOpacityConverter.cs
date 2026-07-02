using Microsoft.UI.Xaml.Data;

namespace NetTrans.Converters;

/// <summary>.source__dot { opacity: 0.3 + share/100 } — busier mirrors render a more solid dot.</summary>
public sealed class ShareToOpacityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language) =>
        value is int share ? 0.3 + share / 100.0 : 0.3;

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();
}
