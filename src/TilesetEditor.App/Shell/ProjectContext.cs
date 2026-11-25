using TilesetEditor.App.Infrastructure;
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
    private ProjectEnvironment? _environment;
    private string? _loadError;
    private ProjectChangeMonitor? _monitor;

    public ProjectContext()
    {
        Settings = _store.Load();
    }

    public ToolkitSettings Settings { get; }

    public ProjectInfo? Project { get => _project; private set { if (Set(ref _project, value)) OnPropertyChanged(nameof(IsOpen)); } }
    public ProjectEnvironment? Environment { get => _environment; private set => Set(ref _environment, value); }
    public string? LoadError { get => _loadError; private set => Set(ref _loadError, value); }
    public bool IsOpen => Project != null;
    public string? Root => Project?.Root;

    public ProjectToolSettings Tools => Root != null ? Settings.ForProject(Root) : new ProjectToolSettings();

    /// <summary>Why the project was last (re)loaded, and a description for external changes.</summary>
    public ProjectChangeKind LastChangeKind { get; private set; }
    public string? LastExternalChange { get; private set; }

    /// <summary>Raised after a project was opened or reloaded.</summary>
    public event EventHandler? ProjectChanged;

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
            var info = ProjectLoader.Load(root);
            var env = ProjectEnvironment.Detect(root);
            Project = info;
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
            Settings.Touch(root);
            Save();
            StartMonitor(root);
            LastChangeKind = kind;
            LastExternalChange = externalDescription;
            ProjectChanged?.Invoke(this, EventArgs.Empty);
            return true;
        }
        catch (Exception ex) when (ex is ProjectLoadException or System.IO.IOException or UnauthorizedAccessException or ArgumentException)
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

    public string RomRelativePath => Tools.RomPath ?? Environment?.RomFileName ?? "pokeemerald.gba";

    public void Dispose() => _monitor?.Dispose();
}
