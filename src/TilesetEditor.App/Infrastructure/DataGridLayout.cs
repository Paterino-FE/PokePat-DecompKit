using System.Windows;
using System.Windows.Controls;

namespace TilesetEditor.App.Infrastructure;

/// <summary>
/// WPF measures a DataGrid of a module that is not visible yet with almost no width and keeps the squeezed column widths
/// (with grouping even after it becomes visible). This puts the declared widths back whenever the grid becomes visible
/// or gets new items, so every column takes its intended room and the star column the rest.
/// </summary>
public static class DataGridLayout
{
    public static void KeepDeclaredColumnWidths(DataGrid grid)
    {
        var declared = grid.Columns.Select(c => (Column: c, Width: c.Width)).ToList();
        void Restore()
        {
            if (!grid.IsVisible || grid.ActualWidth <= 0) return;
            foreach (var (column, width) in declared)
            {
                // Re-assigning the same value is ignored: go through Auto to force a new layout pass of the column.
                column.Width = DataGridLength.Auto;
                column.Width = width;
            }
        }
        grid.IsVisibleChanged += (_, e) => { if (e.NewValue is true) grid.Dispatcher.BeginInvoke(Restore, System.Windows.Threading.DispatcherPriority.Loaded); };
        grid.Loaded += (_, _) => grid.Dispatcher.BeginInvoke(Restore, System.Windows.Threading.DispatcherPriority.Loaded);
    }
}
