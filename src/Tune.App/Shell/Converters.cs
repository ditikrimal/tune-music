using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace Tune.App.Shell;

/// <summary>null / empty string → Collapsed, anything else → Visible.</summary>
public sealed class NullToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is null || value is string { Length: 0 } ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>"On" when the bound value equals the parameter (used to highlight the active sidebar entry).</summary>
public sealed class EqualsToTagConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        Equals(value?.ToString(), parameter?.ToString()) ? "On" : "Off";

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

public sealed class BoolToOnConverter : IValueConverter
{
    public static readonly BoolToOnConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value is true ? "On" : "Off";

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>[NavKey, playlist Id] → "On" when NavKey is "playlist:{Id}".</summary>
public sealed class PlaylistNavTagConverter : IMultiValueConverter
{
    public static readonly PlaylistNavTagConverter Instance = new();

    public object Convert(object?[] values, Type targetType, object? parameter, CultureInfo culture) =>
        values.Length == 2 && values[0] is string key && key == $"playlist:{values[1]}" ? "On" : "Off";

    public object[] ConvertBack(object? value, Type[] targetTypes, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

public sealed class SongCountConverter : IValueConverter
{
    public static readonly SongCountConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        Tune.App.ViewModels.ShellViewModel.Songs(value is int n ? n : 0);

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>TimeSpan? → "3:45" (empty when unknown).</summary>
public sealed class DurationConverter : IValueConverter
{
    public static readonly DurationConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is TimeSpan t ? Tune.App.ViewModels.PlayerBarViewModel.Format(t) : "";

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>[a, b] → true when equal (marks the selected swatch / tile).</summary>
public sealed class SameConverter : IMultiValueConverter
{
    public static readonly SameConverter Instance = new();

    public object Convert(object?[] values, Type targetType, object? parameter, CultureInfo culture) =>
        values.Length == 2 && Equals(values[0], values[1]);

    public object[] ConvertBack(object? value, Type[] targetTypes, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>Color → SolidColorBrush (accent swatches).</summary>
public sealed class ColorToBrushConverter : IValueConverter
{
    public static readonly ColorToBrushConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is System.Windows.Media.Color c ? new System.Windows.Media.SolidColorBrush(c) : System.Windows.Media.Brushes.Transparent;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>Uses <see cref="First"/> for the first item of a list and <see cref="Rest"/> for the others.</summary>
public sealed class FirstItemTemplateSelector : System.Windows.Controls.DataTemplateSelector
{
    public DataTemplate? First { get; set; }
    public DataTemplate? Rest { get; set; }

    public override DataTemplate? SelectTemplate(object? item, DependencyObject container)
    {
        var owner = System.Windows.Controls.ItemsControl.ItemsControlFromItemContainer(container);
        return owner is not null && owner.Items.Count > 0 && ReferenceEquals(owner.Items[0], item) ? First : Rest;
    }
}
