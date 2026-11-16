using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;

namespace Tune.App.Shell;

/// <summary>A small dark "name it" dialog (new playlist). Enter creates, Esc cancels.</summary>
public sealed class NameDialog : Window
{
    private readonly TextBox _box;

    private NameDialog(string title, string suggested, string okText)
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        SizeToContent = SizeToContent.WidthAndHeight;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        _box = new TextBox
        {
            Text = suggested,
            Style = (Style)Application.Current.FindResource("SearchBox"),
            Width = 360,
            Padding = new Thickness(16, 0, 16, 0),
        };
        _box.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) Accept();
        };

        var create = Pill(okText, accent: true);
        create.Click += (_, _) => Accept();
        var cancel = Pill("Cancel", accent: false);
        cancel.Click += (_, _) => DialogResult = false;

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 20, 0, 0) };
        buttons.Children.Add(cancel);
        buttons.Children.Add(create);

        var panel = new StackPanel();
        panel.Children.Add(new TextBlock
        {
            Text = title,
            FontSize = 20,
            FontWeight = FontWeights.Bold,
            Foreground = Brushes.White,
            FontFamily = (FontFamily)Application.Current.FindResource("UiFont"),
            Margin = new Thickness(0, 0, 0, 16),
        });
        panel.Children.Add(_box);
        panel.Children.Add(buttons);

        var card = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(0x24, 0x24, 0x24)),
            BorderBrush = (Brush)Application.Current.FindResource("AccentBorderBrush"),
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
        };
        Loaded += (_, _) =>
        {
            _box.Focus();
            _box.SelectAll();
        };
    }

    private static Button Pill(string text, bool accent)
    {
        var bg = accent ? (Brush)Application.Current.FindResource("AccentBrush") : new SolidColorBrush(Color.FromRgb(0x3A, 0x3A, 0x3A));
        var fg = accent ? (Brush)Application.Current.FindResource("OnAccentBrush") : Brushes.White;
        var border = new FrameworkElementFactory(typeof(Border));
        border.SetValue(Border.BackgroundProperty, bg);
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(17));
        border.SetValue(Border.PaddingProperty, new Thickness(20, 8, 20, 8));
        var content = new FrameworkElementFactory(typeof(ContentPresenter));
        content.SetValue(HorizontalAlignmentProperty, HorizontalAlignment.Center);
        border.AppendChild(content);
        return new Button
        {
            Content = text,
            Foreground = fg,
            FontWeight = FontWeights.SemiBold,
            FontSize = 13,
            Cursor = Cursors.Hand,
            Margin = new Thickness(8, 0, 0, 0),
            Template = new ControlTemplate(typeof(Button)) { VisualTree = border },
        };
    }

    private void Accept()
    {
        if (string.IsNullOrWhiteSpace(_box.Text)) return;
        DialogResult = true;
    }

    /// <summary>Shows the dialog over <paramref name="owner"/>; the trimmed name, or null when cancelled.</summary>
    public static string? Ask(Window? owner, string title, string suggested, string okText = "Create")
    {
        var d = new NameDialog(title, suggested, okText);
        if (owner is { IsVisible: true }) d.Owner = owner;
        else d.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        return d.ShowDialog() == true ? d._box.Text.Trim() : null;
    }
}
