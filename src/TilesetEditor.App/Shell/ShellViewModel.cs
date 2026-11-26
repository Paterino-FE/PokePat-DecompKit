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
using TilesetEditor.App.Platinum;
using TilesetEditor.Core.Project;
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
        // Pokémon Emerald (pokeemerald-expansion)
        _emeraldModules.Add(new ProjectOverviewViewModel(Context, Navigate));
        _tilesets = new TilesetEditorViewModel(Context);
        _emeraldModules.Add(_tilesets);
        _maps = new MapsViewModel(Context, NewTilesetForPrimary, map => OpenInPorymap(map));
        _emeraldModules.Add(_maps);
        _emeraldModules.Add(new SecretBasesViewModel(Context));
        _emeraldModules.Add(new EventsViewModel(Context));
        _emeraldModules.Add(new FlagsViewModel(Context));
        _emeraldModules.Add(new TrainersViewModel(Context));
        _emeraldModules.Add(new GameDataViewModel(Context));
        _emeraldModules.Add(new SpritesViewModel(Context));
        _emeraldModules.Add(new GraphicsViewModel(Context));
        _emeraldModules.Add(new FeaturesViewModel(Context));
        _emeraldModules.Add(new Modules.Mods.ModsViewModel(Context));
        _emeraldModules.Add(new BackupsViewModel(Context));
        foreach (var m in PlannedModule.Roadmap()) _emeraldModules.Add(m);
        // Pokémon Platinum (pokeplatinum)
        foreach (var m in PlatinumModules.Create(Context, Navigate)) _platinumModules.Add(m);
        ShowModulesFor(GameKinds.Parse(Context.Settings.PreferredGame));
        _selectedModule = Modules[0];

        // Porymap bridge: Porymap reports its open map via porymap.log (see PorymapBridge.cs).
        _ui = SynchronizationContext.Current;
        _porymapLog = new PorymapLogWatcher(PorymapLogWatcher.DefaultLogPath, TimeSpan.FromSeconds(1));
        _porymapLog.MessageReceived += m => _ui?.Post(_ => OnPorymapMessage(m), null);

        OpenProjectCommand = new RelayCommand(BrowseProject, () => !IsBuilding);
        SetupCommand = new RelayCommand(RunSetup, () => !IsBuilding);
        OpenRecentCommand = new RelayCommand(p => { if (p is string s) OpenProject(s); }, _ => !IsBuilding);
        OpenInPorymapCommand = new RelayCommand(OpenInPorymap, () => Context.IsOpen && Context.Game == GameKind.Emerald);
        BuildCommand = new AsyncRelayCommand(BuildAsync, () => Context.IsOpen && !IsBuilding);
        CancelBuildCommand = new RelayCommand(() => _buildCts?.Cancel(), () => IsBuilding);
        OpenInEmulatorCommand = new RelayCommand(OpenInEmulator, () => CanOpenInEmulator);
        ShowRomInExplorerCommand = new RelayCommand(() =>
        {
            if (ConfirmedRomPath != null) Process.Start("explorer.exe", $"/select,\"{ConfirmedRomPath}\"");
        }, () => ConfirmedRomPath != null);
        ToggleThemeCommand = new RelayCommand(() =>
        {
            var next = ThemeManager.IsDark ? AppTheme.Light : AppTheme.Dark;
            ThemeManager.Apply(next);
            Context.Settings.Theme = next.ToString();
            Context.Save();
            OnPropertyChanged(nameof(ThemeButtonText));
        });
        ToggleBuildPanelCommand = new RelayCommand(() => { _buildNumber++; ShowBuildPanel = !ShowBuildPanel; }); // a manual toggle cancels the auto-hide
        ShowProblemCommand = new RelayCommand(p => { if (p is BuildProblemVm b) ShowProblem(b); });
        OpenProblemFileCommand = new RelayCommand(p => { if (p is BuildProblemVm b) OpenProblemFile(b.Problem); });

        _flushTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(200), DispatcherPriority.Background, (_, _) => FlushOutput(), Dispatcher.CurrentDispatcher);
        Context.AnyProjectChanged += (_, _) =>
        {
            if (Context.Game is { } game) ShowModulesFor(game); // also shows/hides optional modules (mods switched on/off)
            UpdateGameLook();
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
    /// <summary>The navigation: the modules of the game of the open project.</summary>
    public ObservableCollection<IToolkitModule> Modules { get; } = [];
    private readonly List<IToolkitModule> _emeraldModules = [];
    private readonly List<IToolkitModule> _platinumModules = [];
    /// <summary>Every module of every game (unsaved changes are checked in all of them).</summary>
    public IEnumerable<IToolkitModule> AllModules => _emeraldModules.Concat(_platinumModules);

    private GameKind _shownGame;
    /// <summary>The game whose modules the navigation shows.</summary>
    public GameKind ShownGame { get => _shownGame; private set { if (Set(ref _shownGame, value)) { OnPropertyChanged(nameof(GameSubtitle)); OnPropertyChanged(nameof(IsEmerald)); } UpdateGameLook(); } }

    private System.Windows.Media.ImageSource? _gameLogo;
    /// <summary>The game's mascot from the open project's own sprites (Rayquaza / Giratina); null = the Pokéball logo.</summary>
    public System.Windows.Media.ImageSource? GameLogo { get => _gameLogo; private set => Set(ref _gameLogo, value); }
    public string GameLogoTip => Context.IsOpen ? $"{ShownGame.Title()} – picture from your project" : "PokéPat DecompKit";
    private string? _logoFor;

    /// <summary>Colours and logo of the shown game; the Pokémon Center look stays while no project is open.</summary>
    private void UpdateGameLook()
    {
        bool open = Context.IsOpen && Context.Game == ShownGame;
        Infrastructure.ThemeManager.ApplyStyle(!open ? Infrastructure.GameStyle.Center : ShownGame == GameKind.Platinum ? Infrastructure.GameStyle.Platinum : Infrastructure.GameStyle.Emerald);
        var key = open ? $"{ShownGame}|{Context.Root}" : null;
        if (key == _logoFor) return;
        _logoFor = key;
        GameLogo = null;
        OnPropertyChanged(nameof(GameLogoTip));
        if (!open || Context.Root is not { } root) return;
        var game = ShownGame;
        Task.Run(() => GameMascot.Load(root, game)).ContinueWith(t =>
        {
            if (_logoFor == key && t.IsCompletedSuccessfully && t.Result is { } img) GameLogo = Infrastructure.Imaging.ToBitmap(img);
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }
    public bool IsEmerald => ShownGame == GameKind.Emerald;
    /// <summary>Under the logo: which decomp the open project is based on.</summary>
    public string GameSubtitle => $"{ShownGame.Title()} · {ShownGame.DecompName()}";

    private void ShowModulesFor(GameKind game)
    {
        var list = (game == GameKind.Platinum ? _platinumModules : _emeraldModules).Where(m => m is not IOptionalModule o || o.IsShown).ToList();
        if (Modules.Count > 0 && Modules.SequenceEqual(list)) { ShownGame = game; return; }
        var keep = _selectedModule?.Title;
        Modules.Clear();
        foreach (var m in list) Modules.Add(m);
        ShownGame = game;
        SelectedModule = Modules.FirstOrDefault(m => m.Title == keep) ?? Modules[0];
    }

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
                   (Context.Project?.ExpansionVersion != null ? $"   ·   expansion {Context.Project.ExpansionVersion}" : "") +
                   (Context.Game == GameKind.Platinum ? "   ·   pokeplatinum" : "");
        }
    }

    private string _projectNotice = "";
    /// <summary>Shown under the top bar when the project was re-read because of outside changes.</summary>
    public string ProjectNotice { get => _projectNotice; private set { if (Set(ref _projectNotice, value)) OnPropertyChanged(nameof(HasProjectNotice)); } }
    public bool HasProjectNotice => ProjectNotice.Length > 0;
    public ICommand DismissNoticeCommand => new RelayCommand(() => ProjectNotice = "");

    public ICommand OpenProjectCommand { get; }
    public ICommand SetupCommand { get; }

    /// <summary>
    /// "Get started" (first start and "Setup…"): installs what is missing and opens the project. "Set paths manually" leads to
    /// the classic setup window for existing projects and own tool paths.
    /// </summary>
    public void RunSetup() => RunSetup(checkUpdates: false);

    public void RunSetup(bool checkUpdates)
    {
        var start = new GetStartedWindow(Context.Settings, checkUpdates: checkUpdates) { Owner = Application.Current.MainWindow };
        var ok = start.ShowDialog() == true;
        Context.Save();
        if (ok && start.ProjectRoot != null) { OpenProject(start.ProjectRoot); return; }
        if (start.WantsManualSetup) RunManualSetup();
    }

    /// <summary>Classic setup window: project folder required, tools optional; opens the chosen project.</summary>
    public void RunManualSetup()
    {
        var w = new SetupWindow(Context.Settings) { Owner = Application.Current.MainWindow };
        if (w.ShowDialog() != true || w.ProjectRoot == null) return;
        Context.Save();
        OpenProject(w.ProjectRoot);
    }

    public bool NeedsSetup => !Context.Settings.SetupCompleted;

    private string _updateNotice = "";
    public string UpdateNotice { get => _updateNotice; private set { if (Set(ref _updateNotice, value)) OnPropertyChanged(nameof(HasUpdateNotice)); } }
    public bool HasUpdateNotice => UpdateNotice.Length > 0;
    public ICommand DismissUpdateNoticeCommand => new RelayCommand(() => UpdateNotice = "");
    public ICommand ShowUpdatesCommand => new RelayCommand(() => { UpdateNotice = ""; RunSetup(checkUpdates: true); }, () => !IsBuilding);

    public static string AppVersion =>
        (System.Reflection.Assembly.GetExecutingAssembly().GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
            .FirstOrDefault() as System.Reflection.AssemblyInformationalVersionAttribute)?.InformationalVersion.Split('+')[0] ?? "0.0.0";

    // ------------------------------------------------------------------ PokéPat's own updates

    private TilesetEditor.Toolkit.Install.AppRelease? _appUpdate;
    /// <summary>A newer PokéPat release (found by the check on start or by "⟳").</summary>
    public TilesetEditor.Toolkit.Install.AppRelease? AppUpdate
    {
        get => _appUpdate;
        private set { if (Set(ref _appUpdate, value)) { OnPropertyChanged(nameof(HasAppUpdate)); OnPropertyChanged(nameof(AppUpdateButtonText)); OnPropertyChanged(nameof(AppUpdateTip)); } }
    }
    public bool HasAppUpdate => AppUpdate != null;
    public string AppUpdateButtonText => AppUpdate != null ? $"⬆ PokéPat {AppUpdate.Version}" : "⟳";
    public string AppUpdateTip => AppUpdate != null ? $"PokéPat {AppUpdate.Version} is available (you have {AppVersion}) – see what is new and update." : $"PokéPat {AppVersion} – check for a newer version";
    private bool _checkingApp;

    /// <summary>"⟳": checks now; "⬆": shows what is new and updates.</summary>
    public ICommand AppUpdateCommand => new RelayCommand(async () =>
    {
        if (AppUpdate is { } u) { ShowAppUpdate(u); return; }
        if (_checkingApp) return;
        _checkingApp = true;
        try
        {
            AppUpdate = await TilesetEditor.Toolkit.Install.AppSelfUpdate.CheckAsync(AppVersion);
            if (AppUpdate is { } found) ShowAppUpdate(found);
            else ProjectWriter.Warn("PokéPat", $"You have the newest version ({AppVersion}) – or GitHub cannot be reached right now.");
        }
        finally { _checkingApp = false; }
    }, () => !IsBuilding);

    private void ShowAppUpdate(TilesetEditor.Toolkit.Install.AppRelease u)
    {
        var unsaved = Modules.Concat(_emeraldModules).Concat(_platinumModules).Distinct().Where(m => m.HasUnsavedChanges).Select(m => m.UnsavedChangesDescription ?? m.Title).ToList();
        new AppUpdateWindow(u, AppVersion, unsaved) { Owner = System.Windows.Application.Current.MainWindow }.ShowDialog();
    }

    /// <summary>Check on start (option in Get started): PokéPat itself on every start, the tools at most once a day.</summary>
    public async void CheckUpdatesInBackground()
    {
        var s = Context.Settings;
        if (!s.CheckUpdatesOnStart || NeedsSetup) return;
        try
        {
            AppUpdate = await TilesetEditor.Toolkit.Install.AppSelfUpdate.CheckAsync(AppVersion);
            var names = new List<string>();
            if (AppUpdate != null) names.Add($"PokéPat {AppUpdate.Version}");
            if (s.LastUpdateCheckUtc <= DateTime.UtcNow.AddHours(-20))
            {
                var installer = new TilesetEditor.Toolkit.Install.Installer(TilesetEditor.Toolkit.Install.InstallOptions.FromSettings(s));
                var found = await Task.Run(() => installer.CheckAllUpdatesAsync());
                s.LastUpdateCheckUtc = DateTime.UtcNow;
                Context.Save();
                names.AddRange(found.Select(u => $"{TilesetEditor.Toolkit.Install.Installer.Title(u.Component, installer.Options.Game)} {u.Latest}"));
            }
            if (names.Count > 0) UpdateNotice = "Updates available: " + string.Join(", ", names) + ".";
        }
        catch (Exception ex) when (ex is IOException or System.Net.Http.HttpRequestException or InvalidOperationException or UnauthorizedAccessException)
        {
            // An update check must never get in the way.
        }
    }
    public ICommand OpenRecentCommand { get; }
    public ICommand OpenInPorymapCommand { get; }
    public ICommand BuildCommand { get; }
    public ICommand CancelBuildCommand { get; }
    public ICommand OpenInEmulatorCommand { get; }
    public ICommand ShowRomInExplorerCommand { get; }
    public ICommand ToggleBuildPanelCommand { get; }
    public ICommand ShowProblemCommand { get; }
    public ICommand OpenProblemFileCommand { get; }
    public ICommand ToggleThemeCommand { get; }
    /// <summary>Moon in the light theme, sun in the dark one.</summary>
    public string ThemeButtonText => ThemeManager.IsDark ? "🔆 Light" : "🌙 Dark";

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
        var dlg = new Microsoft.Win32.OpenFolderDialog { Title = "Select project folder (pokeemerald-expansion or pokeplatinum)", InitialDirectory = Context.Root };
        if (dlg.ShowDialog(Application.Current.MainWindow) == true) OpenProject(dlg.FolderName);
    }

    public void OpenProject(string root)
    {
        var dirty = AllModules.Where(m => m.HasUnsavedChanges).Select(m => m.UnsavedChangesDescription).ToList();
        if (Context.IsOpen && dirty.Count > 0 &&
            AppDialog.Show(Application.Current.MainWindow, "Unsaved changes will be lost when switching projects:\n\n" + string.Join("\n", dirty) + "\n\nSwitch anyway?",
                "Open project", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        if (!Context.Open(root))
            AppDialog.Show(Application.Current.MainWindow, Context.LoadError, "Open project", MessageBoxButton.OK, MessageBoxImage.Warning);
        OnPropertyChanged(nameof(ProjectHeader));
    }

    // ---- Porymap

    private void OpenInPorymap() => OpenInPorymap(null);

    /// <summary>Points Porymap at this project (and map) and starts it; see <see cref="PorymapLauncher.OpenProject"/>.</summary>
    public void OpenInPorymap(string? mapName)
    {
        var r = PorymapLauncher.OpenProject(Context.Tools.PorymapExe, Context.Root!, mapName);
        if (r.Started) { BuildStatus = r.Message; return; }
        AppDialog.Show(Application.Current.MainWindow, r.Message, "Open in Porymap", MessageBoxButton.OK, MessageBoxImage.Warning);
        if (string.IsNullOrWhiteSpace(Context.Tools.PorymapExe)) Navigate("Project");
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
            AppDialog.Show(Application.Current.MainWindow,
                "The build only uses the saved project files. The toolkit has unsaved changes that will NOT be included in the build:\n\n" +
                string.Join("\n", dirty) + "\n\nBuild anyway?", "Build ROM", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;

        ResetBuildResult();
        _buildNumber++; // a pending auto-hide of the previous build must not close this build's panel
        BuildOutput = "";
        BuildProblems.Clear();
        OnPropertyChanged(nameof(HasBuildProblems));
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
            if (!result.Success && !result.Cancelled) ListProblems();
            if (!result.Success && result.ErrorLines.Count > 0)
                AppendOutput("\n=== Error lines ===\n" + string.Join("\n", result.ErrorLines.TakeLast(15)));
            BuildStatus = sb.ToString();
            if (result.Success || result.Cancelled) _ = HideBuildPanelSoonAsync();
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

    /// <summary>
    /// After a successful (or cancelled) build the output panel closes by itself after a moment; the result stays in the
    /// status bar and "Build output" opens the log again. A failed build keeps the panel open for its error lines.
    /// </summary>
    private async Task HideBuildPanelSoonAsync()
    {
        int build = ++_buildNumber;
        await Task.Delay(TimeSpan.FromSeconds(2.5));
        if (build == _buildNumber && !IsBuilding) ShowBuildPanel = false;
    }

    private int _buildNumber;

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

    // ---- Build problems

    /// <summary>The problems of the last failed build, from its log (file and line where the tools say so).</summary>
    public ObservableCollection<BuildProblemVm> BuildProblems { get; } = [];
    public bool HasBuildProblems => BuildProblems.Count > 0;

    private void ListProblems()
    {
        BuildProblems.Clear();
        var found = TilesetEditor.Core.Project.BuildProblems.Parse(BuildOutput, Context.Root);
        // Errors first (in log order: the first one is usually the cause, later ones follow from it), then warnings.
        int n = 0;
        foreach (var p in found.Where(p => p.IsError).Concat(found.Where(p => !p.IsError)).Take(200))
            BuildProblems.Add(new BuildProblemVm(p, n++ == 0 && p.IsError, ShowTarget(p)));
        OnPropertyChanged(nameof(HasBuildProblems));
    }

    /// <summary>Where PokéPat itself can show the place of a problem (Platinum map scripts, events and texts), else null.</summary>
    private string? ShowTarget(BuildProblem p)
    {
        if (p.File == null || Context.Platinum is not { } plat) return null;
        if (plat.MapHeaders.FirstOrDefault(h => h.EventsFile == p.File || h.ScriptFile == p.File) is { } h)
            return h.EventsFile == p.File ? "Events & Dialogs: " + h.FriendlyName : "Events & Dialogs: " + h.FriendlyName + " (script)";
        if (plat.MapHeaders.FirstOrDefault(x => x.TextFile == p.File && x.EventsFile != null) is { } t) return "Texts of " + t.FriendlyName;
        return null;
    }

    private void ShowProblem(BuildProblemVm b)
    {
        var p = b.Problem;
        if (b.ShowIn == null || Context.Platinum is not { } plat) { OpenProblemFile(p); return; }
        Navigate("Events & Dialogs");
        if (SelectedModule is not PlatinumEventsViewModel events) return;
        if (plat.MapHeaders.FirstOrDefault(h => h.EventsFile == p.File || h.ScriptFile == p.File) is { } h)
        {
            if (h.EventsFile == p.File) events.OpenEvent(h.Id, "", -1);
            else events.OpenScriptAt(h.Id, p.Line);
            return;
        }
        var owners = plat.MapHeaders.Where(x => x.TextFile == p.File && x.EventsFile != null).ToList();
        var stem = Path.GetFileNameWithoutExtension(p.File!).ToUpperInvariant();
        var owner = owners.FirstOrDefault(x => x.Id == "MAP_HEADER_" + stem) ?? owners.FirstOrDefault();
        if (owner != null) events.OpenText(owner.Id, TextIdAt(Path.Combine(Context.Root!, p.File!), p.Line) ?? "");
    }

    /// <summary>The id of the text a line of a res/text/*.json belongs to (the last "id" at or before the line).</summary>
    private static string? TextIdAt(string file, int? line)
    {
        if (line == null || !File.Exists(file)) return null;
        string? id = null;
        int n = 0;
        foreach (var l in File.ReadLines(file))
        {
            if (++n > line) break;
            var m = System.Text.RegularExpressions.Regex.Match(l, "\"id\"\\s*:\\s*\"([^\"]+)\"");
            if (m.Success) id = m.Groups[1].Value;
        }
        return id;
    }

    private void OpenProblemFile(BuildProblem p)
    {
        if (p.File == null || Context.Root == null) return;
        var full = ProjectPaths.Combine(Context.Root, p.File);
        if (!File.Exists(full))
        {
            AppDialog.Show(Application.Current.MainWindow, $"{p.File} is not in the project (it is made by the build).\n\n{p.Message}", "Build problem", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var r = FileOpener.OpenAt(full, p.Line);
        if (r != null) BuildStatus = r;
    }

    private void OpenInEmulator()
    {
        if (ConfirmedRomPath == null) return;
        var r = EmulatorLauncher.Launch(Context.Tools.EmulatorExe, ConfirmedRomPath);
        if (!r.Started) AppDialog.Show(Application.Current.MainWindow, r.Message, "Emulator", MessageBoxButton.OK, MessageBoxImage.Warning);
    }
}
