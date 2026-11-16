using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;

namespace Tune.App.Shell;

/// <summary>
/// "Are you sure?" before anything that removes or deletes (downloads, songs from a playlist / Liked songs,
/// playlists). Same dark card as <see cref="NameDialog"/>; Enter confirms, Esc cancels.
/// </summary>
public sealed class ConfirmDialog : Window
{
    private ConfirmDialog(string title, string message, string okText)
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        SizeToContent = SizeToContent.WidthAndHeight;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var font = (FontFamily)Application.Current.FindResource("UiFont");
        var heading = new TextBlock { Text = title, FontSize = 20, FontWeight = FontWeights.Bold, Foreground = Brushes.White, FontFamily = font };
        var text = new TextBlock
        {
            Text = message, FontSize = 14, FontFamily = font, TextWrapping = TextWrapping.Wrap, MaxWidth = 380, Margin = new Thickness(0, 10, 0, 0),
            Foreground = (Brush)Application.Current.FindResource("SubtleTextBrush"),
        };

        var ok = Pill(okText, new SolidColorBrush(Color.FromRgb(0xE5, 0x48, 0x4D)), Brushes.White);
        ok.Click += (_, _) => DialogResult = true;
        var cancel = Pill("Cancel", new SolidColorBrush(Color.FromRgb(0x3A, 0x3A, 0x3A)), Brushes.White);
        cancel.Click += (_, _) => DialogResult = false;
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 22, 0, 0) };
        buttons.Children.Add(cancel);
        buttons.Children.Add(ok);

        var panel = new StackPanel { MinWidth = 340 };
        panel.Children.Add(heading);
        panel.Children.Add(text);
        panel.Children.Add(buttons);

        var card = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(0x24, 0x24, 0x24)),
            BorderBrush = (Brush)Application.Current.FindResource("DividerBrush"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(18),
            Padding = new Thickness(24),
            Margin = new Thickness(24),
            Child = panel,
            Effect = new DropShadowEffect { BlurRadius = 30, ShadowDepth = 6, Opacity = 0.6 },
        };
        card.MouseLeftButtonDown += (_, _) => { try { DragMove(); } catch (InvalidOperationException) { } };
        Content = card;
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) DialogResult = false;
            else if (e.Key == Key.Enter) DialogResult = true;
        };
        Loaded += (_, _) => cancel.Focus(); // the safe choice has focus
    }

    private static Button Pill(string text, Brush background, Brush foreground)
    {
        var border = new FrameworkElementFactory(typeof(Border));
        border.SetValue(Border.BackgroundProperty, background);
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(17));
        border.SetValue(Border.PaddingProperty, new Thickness(20, 8, 20, 8));
        var content = new FrameworkElementFactory(typeof(ContentPresenter));
        content.SetValue(HorizontalAlignmentProperty, HorizontalAlignment.Center);
        border.AppendChild(content);
        return new Button
        {
            Content = text, Foreground = foreground, FontWeight = FontWeights.SemiBold, FontSize = 13, Cursor = Cursors.Hand,
            Margin = new Thickness(8, 0, 0, 0), Template = new ControlTemplate(typeof(Button)) { VisualTree = border },
        };
    }

    /// <summary>True when the user confirms.</summary>
    public static bool Ask(Window? owner, string title, string message, string okText)
    {
        var d = new ConfirmDialog(title, message, okText);
        if (owner is { IsVisible: true }) d.Owner = owner;
        else d.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        return d.ShowDialog() == true;
    }
}
