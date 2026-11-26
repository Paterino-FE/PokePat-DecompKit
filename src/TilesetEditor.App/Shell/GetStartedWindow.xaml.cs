using TilesetEditor.App.Infrastructure;
using System.Windows;
using Microsoft.Win32;
using TilesetEditor.Toolkit;

namespace TilesetEditor.App.Shell;

/// <summary>First start (and "Setup…"): installs what a new hacker needs and opens the project. See <see cref="GetStartedViewModel"/>.</summary>
public partial class GetStartedWindow : Window
{
    private readonly GetStartedViewModel _vm;

    public GetStartedWindow(ToolkitSettings settings, bool check = true, bool checkUpdates = false)
    {
        InitializeComponent();
        Height = Math.Min(Height, SystemParameters.WorkArea.Height - 16);
        _vm = new GetStartedViewModel(settings);
        DataContext = _vm;
        _vm.LogChanged += () =>
        {
            LogExpander.IsExpanded = true;
            LogBox.ScrollToEnd();
        };
        if (check) Loaded += async (_, _) =>
        {
            await _vm.LoadAsync();
            if (checkUpdates) await _vm.CheckUpdatesAsync();
        };
        Closing += (_, e) =>
        {
            if (!_vm.IsBusy) return;
            if (AppDialog.Show(this, "PokéPat is still installing. Stop and close?", Title, MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
                e.Cancel = true;
            else _vm.CancelCommand.Execute(null);
        };
    }

    public GetStartedViewModel ViewModel => _vm;

    /// <summary>The project to open after "Open my project".</summary>
    public string? ProjectRoot { get; private set; }

    /// <summary>The user chose "set paths manually".</summary>
    public bool WantsManualSetup { get; private set; }

    private void BrowseTools(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog { Title = "Folder for Porymap and mGBA" };
        if (System.IO.Directory.Exists(_vm.ToolsFolder)) dlg.InitialDirectory = _vm.ToolsFolder;
        if (dlg.ShowDialog(this) == true) _vm.ToolsFolder = dlg.FolderName;
    }

    private async void Finish(object sender, RoutedEventArgs e)
    {
        FinishButton.IsEnabled = false;
        ProjectRoot = await _vm.FinishAsync();
        if (ProjectRoot != null) DialogResult = true;
        else FinishButton.IsEnabled = true;
    }

    private void Advanced(object sender, RoutedEventArgs e)
    {
        if (_vm.IsBusy) return;
        WantsManualSetup = true;
        DialogResult = false;
    }
}
