using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Tune.App.Views;

public partial class NowPlayingView : UserControl
{
    public NowPlayingView() => InitializeComponent();

    /// <summary>Rounded corners for the panel (children are clipped too).</summary>
    private void Panel_SizeChanged(object sender, SizeChangedEventArgs e) =>
        Panel.Clip = new RectangleGeometry(new Rect(e.NewSize), 24, 24);
}
