using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace TilesetEditor.App.Modules.SecretBases;

public partial class SecretBasesView : UserControl
{
    public SecretBasesView()
    {
        InitializeComponent();
        DataContextChanged += (_, e) =>
        {
            if (e.OldValue is INotifyPropertyChanged old) old.PropertyChanged -= OnVmChanged;
            if (e.NewValue is INotifyPropertyChanged vm) vm.PropertyChanged += OnVmChanged;
        };
    }

    /// <summary>Scrolls the selected entrance into the middle of the map view.</summary>
    private void OnVmChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is not (nameof(SecretBasesViewModel.LocationMarker) or nameof(SecretBasesViewModel.Zoom))) return;
        if (sender is not SecretBasesViewModel vm) return;
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            var p = vm.LocationMarker;
            LocationScroller.ScrollToHorizontalOffset(Math.Max(0, p.X * vm.Zoom - LocationScroller.ViewportWidth / 2));
            LocationScroller.ScrollToVerticalOffset(Math.Max(0, p.Y * vm.Zoom - LocationScroller.ViewportHeight / 2));
        });
    }
}
