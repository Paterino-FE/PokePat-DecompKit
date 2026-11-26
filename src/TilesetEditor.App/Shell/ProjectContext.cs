using TilesetEditor.App.Infrastructure;
using TilesetEditor.Core.Platinum;
using TilesetEditor.Core.Project;
using TilesetEditor.Toolkit;

namespace TilesetEditor.App.Shell;

public enum ProjectChangeKind
{
    /// <summary>Opened by the user (or at start-up).</summary>
    Opened,
    /// <summary>Re-read after the toolkit itself wrote files (e.g. export).</summary>
    Reloaded,
    /// <summary>Re-read because files changed outside the toolkit (Porymap, git, editor).</summary>
    ExternalChange,
}

/// <summary>The project opened in the toolkit. Shared by the shell and all modules.</summary>
public sealed class ProjectContext : ObservableObject, IDisposable
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(3);

    private readonly ToolkitSettingsStore _store = new();
    private readonly SynchronizationContext? _ui = SynchronizationContext.Current;
    private ProjectInfo? _project;
    private PlatinumProject? _platinum;
    private GameKind? _game;
    private ProjectEnvironment? _environment;
    private string? _loadError;
    private ProjectChangeMonitor? _monitor;

    public ProjectContext()
    {
        Settings = _store.Load();
    }

    public ToolkitSettings Settings { get; }

    /// <summary>The open pokeemerald(-expansion) project; null when none or a Platinum project is open.</summary>
    public ProjectInfo? Project { get => _project; private set { if (Set(ref _project, value)) OnPropertyChanged(nameof(IsOpen)); } }
    /// <summary>The open pokeplatinum project; null when none or an Emerald project is open.</summary>
    public PlatinumProject? Platinum { get => _platinum; private set { if (Set(ref _platinum, value)) OnPropertyChanged(nameof(IsOpen)); } }
    /// <summary>Game of the open project (null = none open).</summary>
    public GameKind? Game { get => _game; private set => Set(ref _game, value); }
    public ProjectEnvironment? Environment { get => _environment; private set => Set(ref _environment, value); }
    public string? LoadError { get => _loadError; private set => Set(ref _loadError, value); }
    public bool IsOpen => Project != null || Platinum != null;
    public string? Root => Project?.Root ?? Platinum?.Root;

    public ProjectToolSettings Tools => Root != null ? Settings.ForProject(Root) : new ProjectToolSettings();

    /// <summary>Why the project was last (re)loaded, and a description for external changes.</summary>
    public ProjectChangeKind LastChangeKind { get; private set; }
    public string? LastExternalChange { get; private set; }

    /// <summary>Raised after an Emerald project was opened or reloaded (the Emerald modules listen to it).</summary>
    public event EventHandler? ProjectChanged;
    /// <summary>Raised after a Platinum project was opened or reloaded (the Platinum modules listen to it).</summary>
    public event EventHandler? PlatinumChanged;
    /// <summary>Raised after any project was opened or reloaded (shell).</summary>
    public event EventHandler? AnyProjectChanged;

    /// <summary>Calls <paramref name="action"/> whenever a project of <paramref name="game"/> was opened or reloaded.</summary>
    public void ChangedFor(GameKind game, Action action)
    {
        if (game == GameKind.Platinum) PlatinumChanged += (_, _) => action();
        else ProjectChanged += (_, _) => action();
    }

    public bool Open(string root) => Load(root, ProjectChangeKind.Opened, null);

    /// <summary>Re-reads the project after the toolkit wrote to it. Does not count as an external change.</summary>
    public void Reload()
    {
        if (Root != null) Load(Root, ProjectChangeKind.Reloaded, null);
    }

    private bool Load(string root, ProjectChangeKind kind, string? externalDescription)
    {
        try
        {
            root = System.IO.Path.GetFullPath(root).TrimEnd('\\', '/');
            var game = Infrastructure.Perf.Time("detect game", () => GameDetector.Detect(root))
                       ?? throw new ProjectLoadException($"'{root}' is neither a pokeemerald(-expansion) nor a pokeplatinum project " +
                                                         "(include/fieldmap.h + src/data/tilesets/headers.h, or platinum.us/ + res/pokemon/ are missing).");
            ProjectInfo? info = null;
            PlatinumProject? platinum = null;
            if (game == GameKind.Platinum) platinum = Infrastructure.Perf.Time("load Platinum project", () => PlatinumProject.Load(root));
            else info = Infrastructure.Perf.Time("load Emerald project", () => ProjectLoader.Load(root));
            var env = Infrastructure.Perf.Time("detect environment", () => ProjectEnvironment.Detect(root));
            Project = info;
            Platinum = platinum;
            Game = game;
            Environment = env;
            LoadError = null;
            bool firstTime = !Settings.Projects.ContainsKey(ToolkitSettings.ProjectKey(root));
            var tools = Settings.ForProject(root);
            // First-time default: a project on a WSL share is built (and Porytiles run) in that distribution.
            if (firstTime && env.WslDistro != null)
            {
                tools.BuildWslDistro = env.WslDistro;
                tools.Porytiles.WslDistro = env.WslDistro;
            }
            // A new Platinum project gets the DS emulator, not the GBA one (ForProject filled in mGBA).
            if (firstTime && game == GameKind.Platinum) tools.EmulatorExe = Settings.DefaultNdsEmulatorExe;
            // pokeplatinum's plain "make" also runs the checksum test against the original ROM, which fails for every hack.
            if (game == GameKind.Platinum && tools.BuildCommand == ProjectToolSettings.DefaultBuildCommand) tools.BuildCommand = ProjectToolSettings.PlatinumBuildCommand;
            tools.Game = game.ToString();
            Settings.Touch(root);
            Infrastructure.Perf.Time("save settings", Save);
            Infrastructure.Perf.Time("start change monitor", () => StartMonitor(root));
            LastChangeKind = kind;
            LastExternalChange = externalDescription;
            if (game == GameKind.Platinum) Infrastructure.Perf.Raise(PlatinumChanged, this, $"{kind} Platinum");
            else Infrastructure.Perf.Raise(ProjectChanged, this, $"{kind} Emerald");
            Infrastructure.Perf.Raise(AnyProjectChanged, this, $"{kind} shell");
            return true;
        }
        catch (Exception ex) when (ex is ProjectLoadException or System.IO.IOException or UnauthorizedAccessException or ArgumentException or System.Text.Json.JsonException)
        {
            LoadError = ex.Message;
            return false;
        }
    }

    private void StartMonitor(string root)
    {
        // A fresh monitor takes the just-loaded state as baseline, so the toolkit's own writes before a reload are not reported.
        _monitor?.Dispose();
        _monitor = new ProjectChangeMonitor(root, PollInterval);
        _monitor.Changed += changed =>
        {
            var description = ProjectChangeMonitor.Describe(changed);
            void Apply(object? _)
            {
                if (Root == null || !string.Equals(Root, root, StringComparison.OrdinalIgnoreCase)) return;
                if (!Load(root, ProjectChangeKind.ExternalChange, description))
                {
                    // E.g. Porymap is in the middle of writing: report again on the next poll and retry then.
                    _monitor?.Invalidate(changed);
                }
            }
            if (_ui != null) _ui.Post(Apply, null); else Apply(null);
        };
    }

    public void Save() => _store.Save(Settings);

    public string RomRelativePath => Tools.RomPath
        ?? (Game == GameKind.Platinum ? Platinum?.RomPath ?? GameKind.Platinum.DefaultRomPath() : Environment?.RomFileName ?? "pokeemerald.gba");

    public void Dispose() => _monitor?.Dispose();
}
