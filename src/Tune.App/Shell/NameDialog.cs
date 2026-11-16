using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;

namespace Tune.App.Shell;

/// <summary>
/// A small dark "name it" dialog (new playlist). Enter creates, Esc cancels. For a new playlist it also offers a
/// small link button that switches to importing a YouTube Music playlist from its link.
/// </summary>
public sealed class NameDialog : Window
{
    private readonly TextBox _box;
    private bool _linkMode;

    private NameDialog(string title, string suggested, string okText, bool allowLink = false)
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
            Tag = "", // no hint
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

        var buttons = new DockPanel { Margin = new Thickness(0, 20, 0, 0), LastChildFill = false };
        DockPanel.SetDock(create, Dock.Right);
        DockPanel.SetDock(cancel, Dock.Right);
        buttons.Children.Add(create);
        buttons.Children.Add(cancel);

        var heading = new TextBlock
        {
            Text = title,
            FontSize = 20,
            FontWeight = FontWeights.Bold,
            Foreground = Brushes.White,
            FontFamily = (FontFamily)Application.Current.FindResource("UiFont"),
            Margin = new Thickness(0, 0, 0, 16),
        };
        var hint = new TextBlock
        {
            Text = "Paste a YouTube Music playlist link. Its songs become a new playlist.",
            Foreground = (Brush)Application.Current.FindResource("SubtleTextBrush"),
            FontSize = 12,
            Margin = new Thickness(4, 10, 0, 0),
            Visibility = Visibility.Collapsed,
        };
        if (allowLink)
        {
            // quiet link icon bottom-left: switch between naming a new playlist and importing one from a link
            var link = new Button
            {
                Style = (Style)Application.Current.FindResource("IconButton"),
                Width = 34, Height = 34,
                ToolTip = "Import from a YouTube Music link",
                Content = new Tune.App.Platform.Icon { Data = (Geometry)Application.Current.FindResource("IcoLink"), Size = 20 },
            };
            DockPanel.SetDock(link, Dock.Left);
            buttons.Children.Add(link);
            link.Click += (_, _) =>
            {
                _linkMode = !_linkMode;
                heading.Text = _linkMode ? "Import a playlist" : title;
                create.Content = _linkMode ? "Import" : okText;
                hint.Visibility = _linkMode ? Visibility.Visible : Visibility.Collapsed;
                link.Foreground = _linkMode ? (Brush)Application.Current.FindResource("AccentBrush") : (Brush)Application.Current.FindResource("SubtleTextBrush");
                _box.Text = _linkMode ? (Clipboard.ContainsText() && Tune.Core.YtMusic.PlaylistLinks.Parse(Clipboard.GetText()) is not null ? Clipboard.GetText().Trim() : "") : suggested;
                _box.Tag = _linkMode ? "https://music.youtube.com/playlist?list=\u2026" : "";
                _box.Focus();
                _box.SelectAll();
            };
        }

        var panel = new StackPanel();
        panel.Children.Add(heading);
        panel.Children.Add(_box);
        panel.Children.Add(hint);
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

    /// <summary>New playlist: a name to create, or (via the link button) a playlist link to import; null when cancelled.</summary>
    public static (string Text, bool IsLink)? AskNewPlaylist(Window? owner, string suggested)
    {
        var d = new NameDialog("New playlist", suggested, "Create", allowLink: true);
        if (owner is { IsVisible: true }) d.Owner = owner;
        else d.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        return d.ShowDialog() == true ? (d._box.Text.Trim(), d._linkMode) : null;
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
