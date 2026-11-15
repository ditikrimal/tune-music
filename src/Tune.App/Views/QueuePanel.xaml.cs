using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Tune.App.ViewModels;

namespace Tune.App.Views;

public partial class QueuePanel : UserControl
{
    public QueuePanel() => InitializeComponent();

    private QueueViewModel? Vm => DataContext as QueueViewModel;

    private static TrackRowViewModel? Row(object sender) => (sender as FrameworkElement)?.DataContext as TrackRowViewModel;

    private void Row_Click(object sender, MouseButtonEventArgs e)
    {
        if (Row(sender) is { IsCurrent: false } row) Vm?.JumpCommand.Execute(row);
    }

    private void Remove_Click(object sender, RoutedEventArgs e) => Vm?.RemoveCommand.Execute(Row(sender));

    private void Row_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed && Row(sender) is { IsCurrent: false } row)
            DragDrop.DoDragDrop((DependencyObject)sender, row, DragDropEffects.Move);
    }

    private void Row_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(typeof(TrackRowViewModel)) is TrackRowViewModel dragged && Row(sender) is { IsCurrent: false } target && dragged != target)
            Vm?.Move(dragged, target);
    }
}
