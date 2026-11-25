using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using TilesetEditor.App.Shell;

namespace TilesetEditor.App;

public partial class MainWindow : Window
{
    public ShellViewModel ViewModel { get; }

    public MainWindow()
    {
        InitializeComponent();
        ViewModel = new ShellViewModel();
        DataContext = ViewModel;
        ViewModel.ActivationRequested += (_, _) =>
        {
            // Windows only lets the foreground app steal focus; briefly topmost brings the toolkit up anyway.
            if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
            Activate();
            Topmost = true;
            Topmost = false;
            Focus();
        };
    }

    private void OnRecentSelected(object sender, SelectionChangedEventArgs e)
    {
        if (Recent.SelectedItem is string path)
        {
            Recent.SelectedItem = null;
            ViewModel.OpenProject(path);
        }
    }

    private void OnBuildOutputChanged(object sender, TextChangedEventArgs e) => ((TextBox)sender).ScrollToEnd();

    protected override void OnClosing(CancelEventArgs e)
    {
        base.OnClosing(e);
        if (ViewModel.IsBuilding &&
            MessageBox.Show(this, "A build is still running. Closing will cancel it. Quit anyway?", "PokéPat DecompKit",
                MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
        {
            e.Cancel = true;
            return;
        }
        var dirty = ViewModel.Modules.Where(m => m.HasUnsavedChanges).Select(m => m.UnsavedChangesDescription).ToList();
        if (dirty.Count > 0 &&
            MessageBox.Show(this, "Unsaved changes will be lost:\n\n" + string.Join("\n", dirty) + "\n\nQuit anyway?", "PokéPat DecompKit",
                MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            e.Cancel = true;
        if (!e.Cancel && ViewModel.IsBuilding) ViewModel.CancelBuildCommand.Execute(null);
    }
}
