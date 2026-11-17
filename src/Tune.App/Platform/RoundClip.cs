using System.Windows;
using System.Windows.Media;

namespace Tune.App.Platform;

/// <summary>Clips an element to a rounded rectangle that follows its size (Border's CornerRadius doesn't clip children).</summary>
public static class RoundClip
{
    public static readonly DependencyProperty RadiusProperty = DependencyProperty.RegisterAttached(
        "Radius", typeof(double), typeof(RoundClip), new PropertyMetadata(0.0, OnRadiusChanged));

    public static double GetRadius(DependencyObject d) => (double)d.GetValue(RadiusProperty);
    public static void SetRadius(DependencyObject d, double value) => d.SetValue(RadiusProperty, value);

    private static void OnRadiusChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement el) return;
        el.SizeChanged -= Update;
        el.SizeChanged += Update;
        Apply(el);
    }

    private static void Update(object sender, SizeChangedEventArgs e) => Apply((FrameworkElement)sender);

    private static void Apply(FrameworkElement el)
    {
        var r = GetRadius(el);
        var clip = new RectangleGeometry(new Rect(0, 0, el.ActualWidth, el.ActualHeight), r, r);
        clip.Freeze();
        el.Clip = clip;
    }
}

/// <summary>How many columns of at least <c>ConverterParameter</c> pixels fit in the bound width (at least 1).</summary>
public sealed class ColumnsConverter : System.Windows.Data.IValueConverter
{
    public static readonly ColumnsConverter Instance = new();

    public object Convert(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
    {
        var width = value is double w ? w : 0;
        var min = double.TryParse(parameter as string, System.Globalization.CultureInfo.InvariantCulture, out var m) ? m : 200;
        return Math.Max(1, (int)(width / min));
    }

    public object ConvertBack(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture) =>
        throw new NotSupportedException();
}
