using TilesetEditor.App.Infrastructure;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
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

    private void OnProblemClicked(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject d && FindParent<Button>(d) != null) return;   // the 📄 button has its own command
        if (sender is ListBoxItem { DataContext: BuildProblemVm p } && DataContext is ShellViewModel vm) vm.ShowProblemCommand.Execute(p);
    }

    private void OnProblemKey(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || sender is not ListBoxItem { DataContext: BuildProblemVm p } || DataContext is not ShellViewModel vm) return;
        vm.ShowProblemCommand.Execute(p);
        e.Handled = true;
    }

    private static T? FindParent<T>(DependencyObject d) where T : DependencyObject
    {
        for (var x = d; x != null; x = x is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(x) : LogicalTreeHelper.GetParent(x))
            if (x is T t) return t;
        return null;
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        base.OnClosing(e);
        if (ViewModel.IsBuilding &&
            AppDialog.Show(this, "A build is still running. Closing will cancel it. Quit anyway?", "PokéPat DecompKit",
                MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
        {
            e.Cancel = true;
            return;
        }
        var dirty = ViewModel.AllModules.Where(m => m.HasUnsavedChanges).Select(m => m.UnsavedChangesDescription).ToList();
        if (dirty.Count > 0 &&
            AppDialog.Show(this, "Unsaved changes will be lost:\n\n" + string.Join("\n", dirty) + "\n\nQuit anyway?", "PokéPat DecompKit",
                MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            e.Cancel = true;
        if (!e.Cancel && ViewModel.IsBuilding) ViewModel.CancelBuildCommand.Execute(null);
    }
}
