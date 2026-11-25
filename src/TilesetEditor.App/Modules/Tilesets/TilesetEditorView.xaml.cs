using System.Windows;
using System.Windows.Controls;

namespace TilesetEditor.App.Modules.Tilesets;

public partial class TilesetEditorView : UserControl
{
    public TilesetEditorView()
    {
        InitializeComponent();
    }

    private void OnDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] files && DataContext is TilesetEditorViewModel vm)
            vm.ImportFiles(files);
    }

    private void OnLogChanged(object sender, TextChangedEventArgs e) => ((TextBox)sender).ScrollToEnd();
}
