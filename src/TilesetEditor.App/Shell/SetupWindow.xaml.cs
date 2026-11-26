using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;
using TilesetEditor.Compiler;
using TilesetEditor.Toolkit;

namespace TilesetEditor.App.Shell;

/// <summary>
/// First-run setup (also reachable via "Setup…"): project folder (required), build environment, Porymap, emulator and
/// Porytiles (all optional). Writes the same settings the Project module edits.
/// </summary>
public partial class SetupWindow : Window
{
    private readonly ToolkitSettings _settings;
    private bool _loading = true;

    public SetupWindow(ToolkitSettings settings)
    {
        InitializeComponent();
        _settings = settings;
        var project = settings.LastProject ?? "";
        var tools = project.Length > 0 && settings.Projects.TryGetValue(ToolkitSettings.ProjectKey(project), out var t) ? t : new ProjectToolSettings
        {
            PorymapExe = settings.DefaultPorymapExe,
            EmulatorExe = settings.DefaultEmulatorExe,
        };
        ProjectBox.Text = project;
        Select(BuildEnvBox, tools.BuildEnvironment == BuildEnvironment.Windows ? "Windows" : "Wsl");
        DistroBox.Text = tools.BuildWslDistro;
        BuildCommandBox.Text = tools.BuildCommand;
        PorymapBox.Text = tools.PorymapExe ?? "";
        EmulatorBox.Text = tools.EmulatorExe ?? "";
        Select(PorytilesModeBox, tools.Porytiles.Mode.ToString());
        PorytilesBox.Text = tools.Porytiles.Executable;
        _loading = false;
        Validate();
    }

    public string? ProjectRoot { get; private set; }

    private static void Select(ComboBox box, string tag) =>
        box.SelectedItem = box.Items.OfType<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == tag) ?? box.Items[0];

    private static string TagOf(ComboBox box) => (box.SelectedItem as ComboBoxItem)?.Tag as string ?? "";

    private static void Show(TextBlock block, SetupCheck check)
    {
        block.Text = (check.Ok ? (check.Skipped ? "– " : "✓ ") : "✗ ") + check.Message;
        block.Foreground = check.Ok ? (check.Skipped ? Brushes.Gray : Brushes.DarkGreen) : Brushes.Firebrick;
    }

    private PorytilesSettings Porytiles() => new()
    {
        Mode = TagOf(PorytilesModeBox) == "Native" ? PorytilesMode.Native : PorytilesMode.Wsl,
        WslDistro = DistroBox.Text.Trim().Length > 0 ? DistroBox.Text.Trim() : "Ubuntu",
        Executable = PorytilesBox.Text.Trim(),
    };

    private bool Validate()
    {
        if (_loading) return false;
        var project = ToolSetup.CheckProject(ProjectBox.Text);
        Show(ProjectStatus, project);
        var porymap = ToolSetup.CheckProgram(PorymapBox.Text, "Porymap");
        Show(PorymapStatus, porymap);
        var emulator = ToolSetup.CheckProgram(EmulatorBox.Text, "Emulator");
        Show(EmulatorStatus, emulator);
        var porytiles = ToolSetup.CheckPorytiles(Porytiles());
        Show(PorytilesStatus, porytiles);
        DistroBox.IsEnabled = TagOf(BuildEnvBox) == "Wsl" || TagOf(PorytilesModeBox) == "Wsl";
        bool ok = project.Ok && porymap.Ok && emulator.Ok && porytiles.Ok;
        FinishButton.IsEnabled = ok;
        return ok;
    }

    private void Changed(object sender, RoutedEventArgs e) => Validate();

    private void BrowseProject(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog { Title = "Folder of your pokeemerald-expansion or pokeplatinum project (contains the Makefile)" };
        if (ProjectBox.Text.Trim().Length > 0) dlg.InitialDirectory = ProjectBox.Text.Trim();
        if (dlg.ShowDialog(this) == true) ProjectBox.Text = dlg.FolderName;
    }

    private void BrowseExe(object sender, RoutedEventArgs e)
    {
        var which = (string)((Button)sender).Tag;
        var dlg = new OpenFileDialog { Filter = "Programs|*.exe|All files|*.*", Title = which == "Porymap" ? "porymap.exe" : "Emulator (mGBA.exe or melonDS.exe)" };
        if (dlg.ShowDialog(this) != true) return;
        (which == "Porymap" ? PorymapBox : EmulatorBox).Text = dlg.FileName;
    }

    private void Skip(object sender, RoutedEventArgs e)
    {
        switch ((string)((Button)sender).Tag)
        {
            case "Porymap": PorymapBox.Text = ""; break;
            case "Emulator": EmulatorBox.Text = ""; break;
            default: PorytilesBox.Text = ""; break;
        }
    }

    private void Finish(object sender, RoutedEventArgs e)
    {
        if (!Validate()) return;
        var root = ProjectBox.Text.Trim().Trim('"');
        var tools = _settings.ForProject(root);
        tools.BuildEnvironment = TagOf(BuildEnvBox) == "Windows" ? BuildEnvironment.Windows : BuildEnvironment.Wsl;
        tools.BuildWslDistro = DistroBox.Text.Trim().Length > 0 ? DistroBox.Text.Trim() : "Ubuntu";
        tools.BuildCommand = BuildCommandBox.Text.Trim().Length > 0 ? BuildCommandBox.Text.Trim() : ProjectToolSettings.DefaultBuildCommand;
        tools.PorymapExe = PorymapBox.Text.Trim().Length > 0 ? PorymapBox.Text.Trim() : null;
        tools.EmulatorExe = EmulatorBox.Text.Trim().Length > 0 ? EmulatorBox.Text.Trim() : null;
        var pt = Porytiles();
        tools.Porytiles.Mode = pt.Mode;
        tools.Porytiles.WslDistro = pt.WslDistro;
        tools.Porytiles.Executable = pt.Executable;
        ToolSetup.Apply(_settings, root, tools);
        ProjectRoot = root;
        DialogResult = true;
    }
}
