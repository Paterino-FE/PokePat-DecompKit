using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using TilesetEditor.App.Infrastructure;
using TilesetEditor.App.Modules.Events;
using TilesetEditor.App.Modules.Features;
using TilesetEditor.App.Modules.Flags;
using TilesetEditor.App.Modules.GameData;
using TilesetEditor.App.Modules.Graphics;
using TilesetEditor.App.Modules.Maps;
using TilesetEditor.App.Modules.Project;
using TilesetEditor.App.Modules.SecretBases;
using TilesetEditor.App.Modules.Sprites;
using TilesetEditor.App.Modules.Tilesets;
using TilesetEditor.App.Modules.Trainers;
using TilesetEditor.Toolkit;

namespace TilesetEditor.App.Shell;

/// <summary>Main window: one entry point for all modules plus project-wide actions (Porymap, ROM build, emulator).</summary>
public sealed class ShellViewModel : ObservableObject
{
    private readonly StringBuilder _pendingOutput = new();
    private readonly DispatcherTimer _flushTimer;
    private CancellationTokenSource? _buildCts;

    public ShellViewModel()
    {
        Context = new ProjectContext();
        Modules.Add(new ProjectOverviewViewModel(Context, Navigate));
        _tilesets = new TilesetEditorViewModel(Context);
        Modules.Add(_tilesets);
        _maps = new MapsViewModel(Context, NewTilesetForPrimary);
        Modules.Add(_maps);
        Modules.Add(new SecretBasesViewModel(Context));
        Modules.Add(new EventsViewModel(Context));
        Modules.Add(new FlagsViewModel(Context));
        Modules.Add(new TrainersViewModel(Context));
        Modules.Add(new GameDataViewModel(Context));
        Modules.Add(new SpritesViewModel(Context));
        Modules.Add(new GraphicsViewModel(Context));
        Modules.Add(new FeaturesViewModel(Context));
        foreach (var m in PlannedModule.Roadmap()) Modules.Add(m);
        _selectedModule = Modules[0];

        // Porymap bridge: Porymap reports its open map via porymap.log (see PorymapBridge.cs).
        _ui = SynchronizationContext.Current;
        _porymapLog = new PorymapLogWatcher(PorymapLogWatcher.DefaultLogPath, TimeSpan.FromSeconds(1));
        _porymapLog.MessageReceived += m => _ui?.Post(_ => OnPorymapMessage(m), null);

        OpenProjectCommand = new RelayCommand(BrowseProject, () => !IsBuilding);
        SetupCommand = new RelayCommand(RunSetup, () => !IsBuilding);
        OpenRecentCommand = new RelayCommand(p => { if (p is string s) OpenProject(s); }, _ => !IsBuilding);
        OpenInPorymapCommand = new RelayCommand(OpenInPorymap, () => Context.IsOpen);
        BuildCommand = new AsyncRelayCommand(BuildAsync, () => Context.IsOpen && !IsBuilding);
        CancelBuildCommand = new RelayCommand(() => _buildCts?.Cancel(), () => IsBuilding);
        OpenInEmulatorCommand = new RelayCommand(OpenInEmulator, () => CanOpenInEmulator);
        ShowRomInExplorerCommand = new RelayCommand(() =>
        {
            if (ConfirmedRomPath != null) Process.Start("explorer.exe", $"/select,\"{ConfirmedRomPath}\"");
        }, () => ConfirmedRomPath != null);
        ToggleBuildPanelCommand = new RelayCommand(() => ShowBuildPanel = !ShowBuildPanel);

        _flushTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(200), DispatcherPriority.Background, (_, _) => FlushOutput(), Dispatcher.CurrentDispatcher);
        Context.ProjectChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(WindowTitle));
            OnPropertyChanged(nameof(ProjectHeader));
            OnPropertyChanged(nameof(RecentProjects));
            bool hadRom = ConfirmedRomPath != null;
            // A different project, or changed sources, invalidate the previous build result.
            ResetBuildResult();
            ProjectNotice = Context.LastChangeKind == ProjectChangeKind.ExternalChange
                ? $"{DateTime.Now:HH:mm} Project reloaded: {Context.LastExternalChange}." +
                  (hadRom ? " The last built ROM no longer matches the sources – please rebuild." : "")
                : "";
        };

        if (Context.Settings.LastProject is { } last && Directory.Exists(last)) Context.Open(last);
    }

    public ProjectContext Context { get; }
    public ObservableCollection<IToolkitModule> Modules { get; } = [];

    private readonly TilesetEditorViewModel _tilesets;
    private readonly MapsViewModel _maps;
    private readonly PorymapLogWatcher _porymapLog;
    private readonly SynchronizationContext? _ui;

    /// <summary>Raised when the toolkit should come to the foreground (requests from Porymap).</summary>
    public event EventHandler? ActivationRequested;

    private string _porymapStatus = "";
    /// <summary>What Porymap currently has open, as reported by the bridge script.</summary>
    public string PorymapStatus { get => _porymapStatus; private set => Set(ref _porymapStatus, value); }

    private void NewTilesetForPrimary(string primary)
    {
        Navigate("Tilesets");
        _tilesets.PreparePrimary(primary);
    }

    private void OnPorymapMessage(PorymapBridgeMessage m)
    {
        bool sameProject = m.Project == null || PorymapLauncher.SamePath(m.Project, Context.Root);
        if (!sameProject)
        {
            PorymapStatus = $"Porymap: different project open ({m.Project})";
            _maps.PorymapMap = null;
            return;
        }
        switch (m.Event)
        {
            case "project":
                PorymapStatus = "Porymap: project open";
                break;
            case "closed":
                PorymapStatus = "Porymap: project closed";
                _maps.PorymapMap = null;
                break;
            case "map":
            case "layout":
                PorymapStatus = $"Porymap: {m.Map ?? m.Layout} · {m.Primary} / {m.Secondary}";
                _maps.PorymapMap = m.Map ?? m.Layout;
                break;
            case "tileset":
                PorymapStatus = $"Porymap: tileset {m.Get("tileset")} saved";
                break;
            case "action" when m.Action == "show-map":
                Navigate("Maps");
                if (!_maps.SelectMap(m.Map ?? "") && m.Layout != null) _maps.Filter = m.Layout;
                ActivationRequested?.Invoke(this, EventArgs.Empty);
                break;
            case "action" when m.Action == "new-tileset" && m.Primary != null:
                NewTilesetForPrimary(m.Primary);
                ActivationRequested?.Invoke(this, EventArgs.Empty);
                break;
        }
    }

    /// <summary>For tests and the CLI-free smoke test: feed a bridge line as if Porymap had logged it.</summary>
    public void SimulatePorymapLine(string line)
    {
        if (PorymapBridgeMessage.TryParse(line) is { } m) OnPorymapMessage(m);
    }

    private IToolkitModule _selectedModule;
    public IToolkitModule SelectedModule { get => _selectedModule; set => Set(ref _selectedModule, value); }

    public IReadOnlyList<string> RecentProjects => Context.Settings.RecentProjects;

    public string WindowTitle => Context.IsOpen ? $"PokéPat DecompKit – {Path.GetFileName(Context.Root)}" : "PokéPat DecompKit";

    public string ProjectHeader
    {
        get
        {
            if (!Context.IsOpen) return Context.LoadError != null ? "Could not open project: " + Context.LoadError : "No project open";
            var env = Context.Environment!;
            return $"{Context.Root}   ·   Branch: {(env.IsGitRepository ? env.GitBranch : "–")}" +
                   (Context.Project!.ExpansionVersion != null ? $"   ·   expansion {Context.Project.ExpansionVersion}" : "");
        }
    }

    private string _projectNotice = "";
    /// <summary>Shown under the top bar when the project was re-read because of outside changes.</summary>
    public string ProjectNotice { get => _projectNotice; private set { if (Set(ref _projectNotice, value)) OnPropertyChanged(nameof(HasProjectNotice)); } }
    public bool HasProjectNotice => ProjectNotice.Length > 0;
    public ICommand DismissNoticeCommand => new RelayCommand(() => ProjectNotice = "");

    public ICommand OpenProjectCommand { get; }
    public ICommand SetupCommand { get; }

    /// <summary>First-run setup window (project folder required, tools optional); opens the chosen project.</summary>
    public void RunSetup()
    {
        var w = new SetupWindow(Context.Settings) { Owner = Application.Current.MainWindow };
        if (w.ShowDialog() != true || w.ProjectRoot == null) return;
        Context.Save();
        OpenProject(w.ProjectRoot);
    }

    public bool NeedsSetup => !Context.Settings.SetupCompleted;
    public ICommand OpenRecentCommand { get; }
    public ICommand OpenInPorymapCommand { get; }
    public ICommand BuildCommand { get; }
    public ICommand CancelBuildCommand { get; }
    public ICommand OpenInEmulatorCommand { get; }
    public ICommand ShowRomInExplorerCommand { get; }
    public ICommand ToggleBuildPanelCommand { get; }

    public void ImportIntoTilesetEditor(IReadOnlyList<string> files)
    {
        Navigate("Tilesets");
        (SelectedModule as TilesetEditorViewModel)?.ImportFiles(files);
    }

    private void Navigate(string title)
    {
        var m = Modules.FirstOrDefault(x => x.Title == title);
        if (m != null) SelectedModule = m;
    }

    // ---- Project

    private void BrowseProject()
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog { Title = "Select pokeemerald project folder", InitialDirectory = Context.Root };
        if (dlg.ShowDialog(Application.Current.MainWindow) == true) OpenProject(dlg.FolderName);
    }

    public void OpenProject(string root)
    {
        var dirty = Modules.Where(m => m.HasUnsavedChanges).Select(m => m.UnsavedChangesDescription).ToList();
        if (Context.IsOpen && dirty.Count > 0 &&
            MessageBox.Show(Application.Current.MainWindow, "Unsaved changes will be lost when switching projects:\n\n" + string.Join("\n", dirty) + "\n\nSwitch anyway?",
                "Open project", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        if (!Context.Open(root))
            MessageBox.Show(Application.Current.MainWindow, Context.LoadError, "Open project", MessageBoxButton.OK, MessageBoxImage.Warning);
        OnPropertyChanged(nameof(ProjectHeader));
    }

    // ---- Porymap

    private void OpenInPorymap()
    {
        var r = PorymapLauncher.Launch(Context.Tools.PorymapExe, Context.Root!);
        if (r.Started) Clipboard.SetText(Context.Root!);
        MessageBox.Show(Application.Current.MainWindow, r.Message + (r.Started ? "\n\n(The project path was copied to the clipboard.)" : ""),
            "Open in Porymap", MessageBoxButton.OK, r.Started ? MessageBoxImage.Information : MessageBoxImage.Warning);
        if (!r.Started) Navigate("Project");
    }

    // ---- Build

    private bool _isBuilding, _showBuildPanel, _buildSucceeded;
    private string _buildOutput = "", _buildStatus = "";
    private string? _confirmedRomPath;

    public bool IsBuilding { get => _isBuilding; private set { if (Set(ref _isBuilding, value)) CommandManager.InvalidateRequerySuggested(); } }
    public bool ShowBuildPanel { get => _showBuildPanel; set => Set(ref _showBuildPanel, value); }
    public string BuildOutput { get => _buildOutput; private set => Set(ref _buildOutput, value); }
    public string BuildStatus { get => _buildStatus; private set => Set(ref _buildStatus, value); }
    public bool BuildSucceeded { get => _buildSucceeded; private set => Set(ref _buildSucceeded, value); }
    /// <summary>Only set after a successful build whose ROM was verified. Cleared when a new build starts.</summary>
    public string? ConfirmedRomPath
    {
        get => _confirmedRomPath;
        private set
        {
            if (!Set(ref _confirmedRomPath, value)) return;
            OnPropertyChanged(nameof(CanOpenInEmulator));
            OnPropertyChanged(nameof(HasEmulator));
        }
    }

    public bool HasEmulator => !string.IsNullOrWhiteSpace(Context.Tools.EmulatorExe); // read live from the saved settings
    public bool CanOpenInEmulator => ConfirmedRomPath != null && HasEmulator && !IsBuilding;

    private void ResetBuildResult()
    {
        ConfirmedRomPath = null;
        BuildSucceeded = false;
        BuildStatus = "";
    }

    private async Task BuildAsync()
    {
        if (!Context.IsOpen) return;
        var dirty = Modules.Where(m => m.HasUnsavedChanges).Select(m => m.UnsavedChangesDescription).ToList();
        if (dirty.Count > 0 &&
            MessageBox.Show(Application.Current.MainWindow,
                "The build only uses the saved project files. The toolkit has unsaved changes that will NOT be included in the build:\n\n" +
                string.Join("\n", dirty) + "\n\nBuild anyway?", "Build ROM", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;

        ResetBuildResult();
        BuildOutput = "";
        lock (_pendingOutput) _pendingOutput.Clear();
        ShowBuildPanel = true;
        IsBuilding = true;
        BuildStatus = "Building …";
        OnPropertyChanged(nameof(CanOpenInEmulator));
        _flushTimer.Start();
        _buildCts = new CancellationTokenSource();
        var request = new BuildRequest { ProjectRoot = Context.Root!, Settings = Context.Tools, RomRelativePath = Context.RomRelativePath };
        try
        {
            var result = await Task.Run(() => new BuildRunner().RunAsync(request, AppendOutput, _buildCts.Token));
            FlushOutput();
            BuildSucceeded = result.Success;
            ConfirmedRomPath = result.Success ? result.ConfirmedRomPath : null;
            var sb = new StringBuilder(result.Message);
            sb.Append($"  ({result.Duration.TotalSeconds:0}s)");
            if (!result.Success && result.ErrorLines.Count > 0)
                AppendOutput("\n=== Error lines ===\n" + string.Join("\n", result.ErrorLines.TakeLast(15)));
            BuildStatus = sb.ToString();
        }
        catch (BuildAlreadyRunningException ex)
        {
            BuildStatus = ex.Message;
        }
        catch (ArgumentException ex)
        {
            BuildStatus = "Cannot build: " + ex.Message;
        }
        finally
        {
            FlushOutput();
            _flushTimer.Stop();
            IsBuilding = false;
            _buildCts.Dispose();
            _buildCts = null;
            OnPropertyChanged(nameof(CanOpenInEmulator));
        }
    }

    private void AppendOutput(string line)
    {
        lock (_pendingOutput) _pendingOutput.AppendLine(line);
    }

    private void FlushOutput()
    {
        string add;
        lock (_pendingOutput)
        {
            if (_pendingOutput.Length == 0) return;
            add = _pendingOutput.ToString();
            _pendingOutput.Clear();
        }
        var text = BuildOutput + add;
        if (text.Length > 300_000) text = "… (truncated)\n" + text[^250_000..];
        BuildOutput = text;
    }

    private void OpenInEmulator()
    {
        if (ConfirmedRomPath == null) return;
        var r = EmulatorLauncher.Launch(Context.Tools.EmulatorExe, ConfirmedRomPath);
        if (!r.Started) MessageBox.Show(Application.Current.MainWindow, r.Message, "Emulator", MessageBoxButton.OK, MessageBoxImage.Warning);
    }
}
