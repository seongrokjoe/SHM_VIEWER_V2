using ShmViewer.ViewModels;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace ShmViewer;

public class FileNameConverter : IValueConverter
{
    public static readonly FileNameConverter Instance = new();
    public object Convert(object value, Type t, object p, CultureInfo c) =>
        value is string path ? Path.GetFileName(path) : value;
    public object ConvertBack(object v, Type t, object p, CultureInfo c) => throw new NotImplementedException();
}

public class BoolToVisConverter : IValueConverter
{
    public static readonly BoolToVisConverter Instance = new();
    public object Convert(object value, Type t, object p, CultureInfo c) =>
        value is true ? Visibility.Visible : Visibility.Collapsed;
    public object ConvertBack(object v, Type t, object p, CultureInfo c) => throw new NotImplementedException();
}

public class SpareColorConverter : IValueConverter
{
    public static readonly SpareColorConverter Instance = new();
    public object Convert(object value, Type t, object p, CultureInfo c) =>
        value is true
            ? new SolidColorBrush(Color.FromRgb(0x66, 0x66, 0x66))
            : new SolidColorBrush(Color.FromRgb(0xD4, 0xD4, 0xD4));
    public object ConvertBack(object v, Type t, object p, CultureInfo c) => throw new NotImplementedException();
}

public class EnumToBoolConverter : IValueConverter
{
    public static readonly EnumToBoolConverter Instance = new();

    public object Convert(object value, Type t, object p, CultureInfo c)
    {
        if (value == null || p == null) return false;
        return value.ToString() == p.ToString();
    }

    public object ConvertBack(object value, Type t, object p, CultureInfo c)
    {
        if (value is true && p != null)
        {
            if (Enum.TryParse(typeof(RefreshMode), p.ToString(), out var result))
                return result;
        }
        return Binding.DoNothing;
    }
}
