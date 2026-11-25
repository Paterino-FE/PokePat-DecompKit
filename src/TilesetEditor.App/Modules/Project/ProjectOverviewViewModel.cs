using System.IO;
using System.Windows;
using System.Windows.Input;
using Microsoft.Win32;
using TilesetEditor.App.Infrastructure;
using TilesetEditor.App.Shell;
using TilesetEditor.Compiler;
using TilesetEditor.Toolkit;

namespace TilesetEditor.App.Modules.Project;

/// <summary>Start page: detected project state, tool paths and the entry into the editors.</summary>
public sealed class ProjectOverviewViewModel : ObservableObject, IToolkitModule
{
    private readonly ProjectContext _ctx;
    private readonly Action<string> _navigate;

    public ProjectOverviewViewModel(ProjectContext ctx, Action<string> navigate)
    {
        _ctx = ctx;
        _navigate = navigate;
        _ctx.ProjectChanged += (_, _) => Refresh();
        BrowsePorymapCommand = new RelayCommand(() => Browse("porymap.exe|porymap.exe|Programs|*.exe", v => PorymapExe = v));
        BrowseEmulatorCommand = new RelayCommand(() => Browse("Programs|*.exe", v => EmulatorExe = v));
        SaveCommand = new RelayCommand(Save, () => _ctx.IsOpen);
        CheckPorytilesCommand = new AsyncRelayCommand(CheckPorytilesAsync, () => _ctx.IsOpen);
        OpenTilesetsCommand = new RelayCommand(() => _navigate("Tilesets"), () => _ctx.IsOpen);
        InstallBridgeCommand = new RelayCommand(() => ChangeBridge(install: true), () => _ctx.IsOpen);
        UninstallBridgeCommand = new RelayCommand(() => ChangeBridge(install: false), () => _ctx.IsOpen && BridgeInstalled);
        View = new ProjectOverviewView { DataContext = this };
        Refresh();
    }

    public string Title => "Project";
    public string Description => "Overview and tools";
    public bool IsAvailable => true;
    public override string ToString() => Title;
    public FrameworkElement View { get; }
    public bool HasUnsavedChanges => _dirty;
    public string? UnsavedChangesDescription => _dirty ? "Project: tool settings are not saved yet." : null;

    public ICommand BrowsePorymapCommand { get; }
    public ICommand BrowseEmulatorCommand { get; }
    public ICommand SaveCommand { get; }
    public ICommand CheckPorytilesCommand { get; }
    public ICommand OpenTilesetsCommand { get; }
    public ICommand InstallBridgeCommand { get; }
    public ICommand UninstallBridgeCommand { get; }

    private string _bridgeStatus = "";
    public string BridgeStatus { get => _bridgeStatus; private set => Set(ref _bridgeStatus, value); }
    public bool BridgeInstalled { get; private set; }

    private void UpdateBridgeStatus()
    {
        if (_ctx.Root == null) { BridgeStatus = ""; return; }
        var script = PorymapBridgeScript.DefaultScriptPath;
        BridgeInstalled = PorymapBridgeInstaller.IsInstalled(_ctx.Root, script);
        BridgeStatus = !BridgeInstalled
            ? "Not set up. With the bridge, Porymap reports the open map to the toolkit and gets two entries in its Tools menu."
            : PorymapBridgeInstaller.ScriptIsCurrent(script)
                ? $"Set up ({script}). Watching Porymap's log: {TilesetEditor.Toolkit.PorymapLogWatcher.DefaultLogPath}"
                : "Set up, but the script is outdated or missing – please set it up again.";
    }

    private void ChangeBridge(bool install)
    {
        var root = _ctx.Root;
        if (root == null) return;
        BridgeInstallPlan plan;
        try
        {
            plan = install ? PorymapBridgeInstaller.PlanInstall(root) : PorymapBridgeInstaller.PlanUninstall(root);
        }
        catch (ArgumentException ex)
        {
            MessageBox.Show(Application.Current.MainWindow, ex.Message, "Porymap bridge", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (plan.NothingToDo)
        {
            MessageBox.Show(Application.Current.MainWindow, "Nothing to change.", "Porymap bridge", MessageBoxButton.OK, MessageBoxImage.Information);
            UpdateBridgeStatus();
            return;
        }
        bool porymapRunning = System.Diagnostics.Process.GetProcessesByName("porymap").Length > 0;
        var body = new System.Text.StringBuilder();
        body.AppendLine($"=== {plan.UserConfigPath}  ({(plan.UserConfigBefore == null ? "new" : "modified")}; backup: porymap.user.cfg.toolkit-backup)");
        body.AppendLine("--- before");
        body.AppendLine(plan.UserConfigBefore ?? "(file does not exist)");
        body.AppendLine("--- after");
        body.AppendLine(plan.UserConfigAfter.Length == 0 ? "(empty)" : plan.UserConfigAfter);
        if (plan.ScriptChanged)
        {
            body.AppendLine($"=== {plan.ScriptPath}  (script, outside the project)");
            body.AppendLine(plan.ScriptContent);
        }
        var intro = (install
                        ? "The bridge adds a Porymap script to this project's user-local porymap.user.cfg (excluded from the repository by .gitignore). It reports opened maps via porymap.log and writes no project files."
                        : "The bridge entry is removed from porymap.user.cfg. Other Porymap scripts are kept.")
                    + (porymapRunning ? "\n\nWARNING: Porymap is currently running. Please close Porymap first – it overwrites porymap.user.cfg when it saves its settings." : "");
        var dlg = new TilesetEditor.App.Shell.TextPreviewWindow(install ? "Set up Porymap bridge" : "Remove Porymap bridge", intro, body.ToString())
        {
            Owner = Application.Current.MainWindow,
        };
        if (dlg.ShowDialog() != true) return;
        try
        {
            PorymapBridgeInstaller.Apply(plan);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(Application.Current.MainWindow, ex.Message, "Porymap bridge", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        UpdateBridgeStatus();
        if (install)
            MessageBox.Show(Application.Current.MainWindow, "Set up. Takes effect the next time the project is opened in Porymap (or in Porymap: Options → Custom Scripts… → Refresh Scripts).",
                "Porymap bridge", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    public ProjectContext Context => _ctx;
    public IReadOnlyList<BuildEnvironment> BuildEnvironments { get; } = Enum.GetValues<BuildEnvironment>();
    public IReadOnlyList<PorytilesMode> PorytilesModes { get; } = Enum.GetValues<PorytilesMode>();

    private bool _dirty;
    private string _facts = "", _porytilesStatus = "", _porymapStatus = "";
    public string Facts { get => _facts; private set => Set(ref _facts, value); }
    public string PorytilesStatus { get => _porytilesStatus; private set => Set(ref _porytilesStatus, value); }
    public string PorymapStatus { get => _porymapStatus; private set => Set(ref _porymapStatus, value); }

    // Editable tool settings (bound to the active project's settings)
    private string? _porymapExe, _emulatorExe, _buildCommand, _buildDistro, _romPath, _porytilesExe, _porytilesDistro;
    private BuildEnvironment _buildEnv;
    private PorytilesMode _porytilesMode;
    public string? PorymapExe { get => _porymapExe; set { if (Set(ref _porymapExe, value)) Dirty(); } }
    public string? EmulatorExe { get => _emulatorExe; set { if (Set(ref _emulatorExe, value)) Dirty(); } }
    public string? BuildCommand { get => _buildCommand; set { if (Set(ref _buildCommand, value)) Dirty(); } }
    public string? BuildDistro { get => _buildDistro; set { if (Set(ref _buildDistro, value)) Dirty(); } }
    public string? RomPath { get => _romPath; set { if (Set(ref _romPath, value)) Dirty(); } }
    public BuildEnvironment BuildEnv { get => _buildEnv; set { if (Set(ref _buildEnv, value)) Dirty(); } }
    public string? PorytilesExe { get => _porytilesExe; set { if (Set(ref _porytilesExe, value)) Dirty(); } }
    public string? PorytilesDistro { get => _porytilesDistro; set { if (Set(ref _porytilesDistro, value)) Dirty(); } }
    public PorytilesMode PorytilesMode { get => _porytilesMode; set { if (Set(ref _porytilesMode, value)) Dirty(); } }

    private void Dirty()
    {
        _dirty = true;
        OnPropertyChanged(nameof(SaveHint));
    }

    public string SaveHint => _dirty ? "Unsaved changes" : "";

    private void Refresh()
    {
        var p = _ctx.Project;
        var env = _ctx.Environment;
        if (p == null || env == null)
        {
            Facts = _ctx.LoadError ?? "No project open. Choose \"Open project…\" at the top (folder with Makefile and include/fieldmap.h).";
            return;
        }
        var c = p.Constants;
        var lines = new List<string>
        {
            $"Folder:            {p.Root}",
            $"Git branch:        {(env.IsGitRepository ? $"{env.GitBranch} ({env.GitCommit})" : "not a Git repository")}",
            $"Base:              {(p.ExpansionVersion != null ? $"pokeemerald-expansion {p.ExpansionVersion}" : "pokeemerald (no expansion version found)")}",
            $"ROM:               {_ctx.RomRelativePath}{(env.HasMakefile ? " (FILE_NAME from the Makefile)" : " – no Makefile found")}",
            $"Location:          {(env.WslDistro != null ? $"WSL distribution {env.WslDistro}" : "Windows file system")}",
            $"Tilesets:          {p.PrimaryTilesets.Count()} Primary, {p.SecondaryTilesets.Count()} Secondary",
            $"Layer mode:        {c.LayerMode} ({c.NumTilesPerMetatile} tiles/metatile), attributes {p.Attributes.SizeBytes} bytes",
            $"Secondary limits:  {c.MaxSecondaryTiles} tiles, {c.MaxSecondaryMetatiles} metatiles, palettes {c.NumPalsInPrimary}–{c.NumPalsTotal - 1}",
            $"Maps / Layouts:    {p.Layouts.Count} Layouts in data/layouts/layouts.json",
            $"Poryscript:        {(env.UsesPoryscript ? "in use (.pory)" : "not detected")}",
        };
        lines.AddRange(p.Warnings.Select(w => "Note: " + w));
        Facts = string.Join("\n", lines);

        var t = _ctx.Tools;
        _porymapExe = t.PorymapExe; _emulatorExe = t.EmulatorExe; _buildCommand = t.BuildCommand; _buildDistro = t.BuildWslDistro;
        _romPath = t.RomPath; _buildEnv = t.BuildEnvironment; _porytilesExe = t.Porytiles.Executable; _porytilesDistro = t.Porytiles.WslDistro;
        _porytilesMode = t.Porytiles.Mode;
        foreach (var n in new[] { nameof(PorymapExe), nameof(EmulatorExe), nameof(BuildCommand), nameof(BuildDistro), nameof(RomPath), nameof(BuildEnv),
                     nameof(PorytilesExe), nameof(PorytilesDistro), nameof(PorytilesMode) })
            OnPropertyChanged(n);
        _dirty = false;
        OnPropertyChanged(nameof(SaveHint));
        UpdatePorymapStatus();
        UpdateBridgeStatus();
    }

    private void UpdatePorymapStatus()
    {
        if (string.IsNullOrWhiteSpace(PorymapExe)) { PorymapStatus = "Not configured."; return; }
        if (!File.Exists(PorymapExe)) { PorymapStatus = "File not found."; return; }
        var (recent, reopen, _) = PorymapLauncher.ReadPorymapState();
        PorymapStatus = "Found. Porymap 6.x does not accept a project path on startup." +
                        (recent == null ? "" : PorymapLauncher.SamePath(recent, _ctx.Root) && reopen
                            ? " Porymap last opened this project."
                            : $" Porymap last opened: {recent}");
    }

    private void Save()
    {
        var t = _ctx.Tools;
        t.PorymapExe = Blank(PorymapExe);
        t.EmulatorExe = Blank(EmulatorExe);
        t.BuildCommand = string.IsNullOrWhiteSpace(BuildCommand) ? ProjectToolSettings.DefaultBuildCommand : BuildCommand.Trim();
        t.BuildWslDistro = string.IsNullOrWhiteSpace(BuildDistro) ? "Ubuntu" : BuildDistro.Trim();
        t.RomPath = Blank(RomPath);
        t.BuildEnvironment = BuildEnv;
        t.Porytiles.Executable = string.IsNullOrWhiteSpace(PorytilesExe) ? PorytilesSettings.DefaultWslExecutable : PorytilesExe.Trim();
        t.Porytiles.WslDistro = string.IsNullOrWhiteSpace(PorytilesDistro) ? "Ubuntu" : PorytilesDistro.Trim();
        t.Porytiles.Mode = PorytilesMode;
        // Porymap and emulator are usually the same for every project.
        _ctx.Settings.DefaultPorymapExe ??= t.PorymapExe;
        _ctx.Settings.DefaultEmulatorExe ??= t.EmulatorExe;
        _ctx.Save();
        _dirty = false;
        OnPropertyChanged(nameof(SaveHint));
        UpdatePorymapStatus();
        Refresh();
    }

    private static string? Blank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    private async Task CheckPorytilesAsync()
    {
        if (_dirty) Save();
        PorytilesStatus = "Checking …";
        try
        {
            var v = await Task.Run(() => new PorytilesClient(_ctx.Tools.Porytiles).GetVersionAsync(CancellationToken.None));
            PorytilesStatus = v.Matches(PorytilesSettings.PinnedVersion)
                ? $"OK: porytiles {v.Version} (pinned version)."
                : $"Found: porytiles {v.Version} – pinned version is {PorytilesSettings.PinnedVersion}. Output may differ.";
        }
        catch (PorytilesUnavailableException ex)
        {
            PorytilesStatus = ex.Message;
        }
    }

    private static void Browse(string filter, Action<string> set)
    {
        var dlg = new OpenFileDialog { Filter = filter };
        if (dlg.ShowDialog(Application.Current.MainWindow) == true) set(dlg.FileName);
    }
}
