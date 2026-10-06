using System.Globalization;
using System.Windows.Data;

namespace TeleSelfCloud.Desktop;

public sealed class ExplorerCompactConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value is double width && width < 640;
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}
