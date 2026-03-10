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

public class InverseBoolConverter : IValueConverter
{
    public static readonly InverseBoolConverter Instance = new();

    public object Convert(object value, Type t, object p, CultureInfo c) =>
        value is bool flag ? !flag : value;

    public object ConvertBack(object value, Type t, object p, CultureInfo c) =>
        value is bool flag ? !flag : value;
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

public class BoolToErrorColorConverter : IValueConverter
{
    public static readonly BoolToErrorColorConverter Instance = new();
    public object Convert(object value, Type t, object p, CultureInfo c) =>
        value is true
            ? new SolidColorBrush(Color.FromRgb(0xF4, 0x47, 0x47))
            : new SolidColorBrush(Color.FromRgb(0xD4, 0xD4, 0xD4));
    public object ConvertBack(object v, Type t, object p, CultureInfo c) => throw new NotImplementedException();
}

public class BoolToHighlightConverter : IValueConverter
{
    public static readonly BoolToHighlightConverter Instance = new();
    public object Convert(object value, Type t, object p, CultureInfo c) =>
        value is true
            ? new SolidColorBrush(Color.FromRgb(0x26, 0x4F, 0x78))
            : new SolidColorBrush(Colors.Transparent);
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

public class LevelToMarginConverter : IValueConverter
{
    public static readonly LevelToMarginConverter Instance = new();
    public object Convert(object value, Type t, object p, CultureInfo c) =>
        value is int level ? new Thickness(level * 16, 0, 0, 0) : new Thickness(0);
    public object ConvertBack(object v, Type t, object p, CultureInfo c) => throw new NotImplementedException();
}
