// WPF Value Converters for file size, types, and UI bindings

using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace GrandCrossExtractor.UI;

public class FileSizeConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is long l)
            return FormatBytes(l);
        if (value is uint u)
            return FormatBytes(u);
        if (value is int i)
            return FormatBytes(i);
        return "0 B";
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotImplementedException();

    public static string FormatBytes(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{(bytes / 1024.0):F1} KB";
        if (bytes < 1024 * 1024 * 1024) return $"{(bytes / (1024.0 * 1024.0)):F2} MB";
        return $"{(bytes / (1024.0 * 1024.0 * 1024.0)):F2} GB";
    }
}

public class RatioConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        if (values.Length >= 2 && values[0] is uint size && values[1] is uint unpacked)
        {
            if (unpacked == 0) return "100%";
            double ratio = (double)size / unpacked * 100.0;
            return $"{ratio:F0}%";
        }
        return "-";
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) => throw new NotImplementedException();
}

public class NullToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        bool notNull = value != null;
        if (parameter as string == "Inverse")
            notNull = !notNull;
        return notNull ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotImplementedException();
}
