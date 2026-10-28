using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using FileSorter.Models;

namespace FileSorter.Converters;

public class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is true ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is Visibility.Visible;
}

public class InverseBoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is true ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is Visibility.Collapsed;
}

public class InverseBoolConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is bool b ? !b : value;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is bool b ? !b : value;
}

public class OrganizeMethodToStringConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is OrganizeMethod method
            ? method switch
            {
                OrganizeMethod.ByType => "Type",
                OrganizeMethod.ByExtension => "Extension",
                OrganizeMethod.ByDate => "Date",
                OrganizeMethod.ByAlphabet => "Alphabet",
                OrganizeMethod.BySize => "Size",
                OrganizeMethod.ByDateTaken => "Photo",
                OrganizeMethod.ByName => "Name",
                _ => value.ToString()!
            }
            : value?.ToString() ?? string.Empty;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

public class BoolToFolderIconConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is true ? "\uE8B7" : "\uE8A5";

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

public class BoolToFontWeightConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is true ? FontWeights.SemiBold : FontWeights.Normal;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

public class BoolToColorConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is true
            ? new SolidColorBrush(Color.FromRgb(255, 193, 7))
            : new SolidColorBrush(Color.FromRgb(160, 160, 184));

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

public class ColorToBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is RgbColor c ? new SolidColorBrush(Color.FromRgb(c.R, c.G, c.B)) : Brushes.Gray;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

public class TimestampToRelativeConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not DateTime ts) return "";
        var diff = DateTime.Now - ts;
        return diff.TotalMinutes switch
        {
            < 1 => "just now",
            < 60 => $"{(int)diff.TotalMinutes}m ago",
            < 1440 => $"{(int)diff.TotalHours}h ago",
            _ => ts.ToString("MMM dd, HH:mm")
        };
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

public class MethodMatchConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is OrganizeMethod current && parameter is string paramStr
            && Enum.TryParse<OrganizeMethod>(paramStr, out var target))
            return current == target;
        return false;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
