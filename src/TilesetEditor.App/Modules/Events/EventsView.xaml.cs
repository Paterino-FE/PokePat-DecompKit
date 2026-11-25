using System.Windows;
using System.Windows.Controls;

namespace TilesetEditor.App.Modules.Events;

public partial class EventsView : UserControl
{
    public EventsView() => InitializeComponent();

    // TreeView.SelectedItem is read-only, so the selection is forwarded here.
    private void OnTreeSelection(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (DataContext is EventsViewModel vm) vm.SelectedNode = e.NewValue as NodeVm;
    }
}
