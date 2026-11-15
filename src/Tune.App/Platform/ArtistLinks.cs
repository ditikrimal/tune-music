using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using Tune.Core.Models;

namespace Tune.App.Platform;

/// <summary>
/// "Artist A, Artist B" where each name with a YouTube Music page is a link (underlined on hover)
/// that opens the artist page in the main window. Keeps the look of whatever text style it is given.
/// </summary>
public sealed class ArtistLinks : TextBlock
{
    public static readonly DependencyProperty ArtistsProperty = DependencyProperty.Register(
        nameof(Artists), typeof(IEnumerable<ArtistRef>), typeof(ArtistLinks),
        new PropertyMetadata(null, (d, _) => ((ArtistLinks)d).Rebuild()));

    public IEnumerable<ArtistRef>? Artists
    {
        get => (IEnumerable<ArtistRef>?)GetValue(ArtistsProperty);
        set => SetValue(ArtistsProperty, value);
    }

    private bool _building;

    public ArtistLinks()
    {
        TextTrimming = TextTrimming.CharacterEllipsis;
    }

    static ArtistLinks()
    {
        // a style trigger ("Loading…") that ends resets Text to empty, which wipes the links: put them back
        TextProperty.OverrideMetadata(typeof(ArtistLinks), new FrameworkPropertyMetadata(string.Empty, (d, e) =>
        {
            var self = (ArtistLinks)d;
            if (!self._building && string.IsNullOrEmpty(e.NewValue as string))
                self.Dispatcher.BeginInvoke(self.Rebuild);
        }));
    }

    private void Rebuild()
    {
        _building = true;
        try
        {
            Inlines.Clear();
            var first = true;
            foreach (var a in Artists ?? [])
            {
                if (!first) Inlines.Add(new Run(", "));
                first = false;
                if (a.BrowseId is null)
                {
                    Inlines.Add(new Run(a.Name));
                    continue;
                }
                var link = new Hyperlink(new Run(a.Name))
                {
                    TextDecorations = null,
                    Cursor = Cursors.Hand,
                    Focusable = false,
                    ToolTip = $"Go to {a.Name}",
                };
                link.SetBinding(TextElement.ForegroundProperty, new Binding(nameof(Foreground)) { Source = this });
                link.MouseEnter += (_, _) =>
                {
                    link.TextDecorations = System.Windows.TextDecorations.Underline;
                    link.Foreground = Brushes.White;
                };
                link.MouseLeave += (_, _) =>
                {
                    link.TextDecorations = null;
                    link.SetBinding(TextElement.ForegroundProperty, new Binding(nameof(Foreground)) { Source = this });
                };
                var artist = a;
                link.Click += (_, e) =>
                {
                    e.Handled = true;
                    App.ShowMain();
                    App.Shell.OpenArtistCommand.Execute(artist);
                };
                Inlines.Add(link);
            }
        }
        finally
        {
            _building = false;
        }
    }
}
