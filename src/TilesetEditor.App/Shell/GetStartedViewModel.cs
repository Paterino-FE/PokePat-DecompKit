using System.Collections.ObjectModel;
using System.Text;
using System.Windows;
using System.Windows.Input;
using TilesetEditor.App.Infrastructure;
using TilesetEditor.Compiler;
using TilesetEditor.Toolkit;
using TilesetEditor.Toolkit.Install;

namespace TilesetEditor.App.Shell;

/// <summary>One part of the setup in the list: what it is, its state and its own install button.</summary>
public sealed class InstallRowVm(InstallComponent component, GetStartedViewModel owner) : ObservableObject
{
    public InstallComponent Component { get; } = component;
    public string Title { get; } = Installer.Title(component);
    public string Description { get; } = Installer.Description(component);
    public string Icon { get; } = component switch
    {
        InstallComponent.Wsl => "🐧",
        InstallComponent.BuildTools => "🔧",
        InstallComponent.Expansion => "🎮",
        InstallComponent.Poryscript => "📜",
        InstallComponent.Porytiles => "🧱",
        InstallComponent.Porymap => "🗺",
        _ => "🕹",
    };

    private InstallState _state = InstallState.Unknown;
    private string _status = "Checking …";
    private bool _working;

    public InstallState State { get => _state; private set { if (Set(ref _state, value)) Changed(); } }
    public string Status { get => _status; private set => Set(ref _status, value); }
    public bool IsWorking { get => _working; set { if (Set(ref _working, value)) Changed(); } }

    /// <summary>Ready, Missing, Failed, NeedsUser, NotAvailable or Working – picks the color of the mark.</summary>
    public string Look => IsWorking ? "Working" : State.ToString();
    public string Mark => IsWorking ? "⟳" : State switch
    {
        InstallState.Ready => "✓",
        InstallState.Failed => "✗",
        InstallState.NeedsUser => "!",
        InstallState.NotAvailable => "–",
        InstallState.Unknown => "…",
        _ => "○",
    };
    public bool CanInstall => !IsWorking && State is InstallState.Missing or InstallState.Failed or InstallState.NeedsUser;
    public string ButtonText => Component switch
    {
        InstallComponent.Expansion => "Download",
        InstallComponent.BuildTools => "Install…",
        _ => "Install",
    };
    private ICommand? _install;
    public ICommand InstallCommand => _install ??= new RelayCommand(() => owner.InstallOne(this), () => CanInstall && !owner.IsBusy);

    private string _updateText = "";
    private bool _updateAvailable, _canUpdate;
    public string UpdateText { get => _updateText; private set { if (Set(ref _updateText, value)) OnPropertyChanged(nameof(HasUpdateText)); } }
    public bool HasUpdateText => UpdateText.Length > 0;
    public bool UpdateAvailable { get => _updateAvailable; private set => Set(ref _updateAvailable, value); }
    public bool CanUpdate { get => _canUpdate; private set { if (Set(ref _canUpdate, value)) CommandManager.InvalidateRequerySuggested(); } }
    private ICommand? _update;
    public ICommand UpdateCommand => _update ??= new RelayCommand(() => owner.UpdateOne(this), () => CanUpdate && !IsWorking && !owner.IsBusy);

    public void ApplyUpdate(UpdateInfo? u)
    {
        UpdateText = u?.Message ?? "";
        UpdateAvailable = u?.Available == true;
        CanUpdate = u is { Available: true, CanUpdate: true };
    }

    public void Apply(ComponentStatus s)
    {
        State = s.State;
        Status = s.Message;
    }

    private void Changed()
    {
        OnPropertyChanged(nameof(Look));
        OnPropertyChanged(nameof(Mark));
        OnPropertyChanged(nameof(CanInstall));
        CommandManager.InvalidateRequerySuggested();
    }
}

/// <summary>
/// "Get started": choose where the project and the programs go, see what is installed and install the rest with one click
/// (or one part at a time). Finishing stores everything as the current project.
/// </summary>
public sealed class GetStartedViewModel : ObservableObject
{
    private readonly ToolkitSettings _settings;
    private Installer _installer;
    private CancellationTokenSource _cts = new();
    private readonly StringBuilder _log = new();

    public GetStartedViewModel(ToolkitSettings settings)
    {
        _settings = settings;
        var current = InstallOptions.FromSettings(settings);
        _distro = current.Distro;
        _projectPath = current.ProjectPath;
        _toolsFolder = current.ToolsFolder;
        _installer = NewInstaller();
        foreach (var c in Installer.All) Rows.Add(new InstallRowVm(c, this));
        InstallAllCommand = new RelayCommand(InstallAll, () => !IsBusy);
        CheckCommand = new RelayCommand(() => _ = CheckAllAsync(), () => !IsBusy);
        CancelCommand = new RelayCommand(() => _cts.Cancel(), () => IsBusy);
        CheckUpdatesCommand = new RelayCommand(() => _ = CheckUpdatesAsync(), () => !IsBusy);
        OpenAppUpdateCommand = new RelayCommand(() =>
        {
            if (_appUpdatePage != null) System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(_appUpdatePage) { UseShellExecute = true });
        });
    }

    public ICommand CheckUpdatesCommand { get; }
    public ICommand OpenAppUpdateCommand { get; }

    public bool CheckUpdatesOnStart
    {
        get => _settings.CheckUpdatesOnStart;
        set { _settings.CheckUpdatesOnStart = value; OnPropertyChanged(); }
    }

    private string? _appUpdatePage;
    private string _appUpdateText = "";
    public string AppUpdateText { get => _appUpdateText; private set { if (Set(ref _appUpdateText, value)) OnPropertyChanged(nameof(HasAppUpdate)); } }
    public bool HasAppUpdate => AppUpdateText.Length > 0;

    /// <summary>Asks GitHub for the newest version of every installed part (and of PokéPat) and shows the result per row.</summary>
    public async Task CheckUpdatesAsync()
    {
        if (IsBusy) return;
        await RunAsync(async ct =>
        {
            Log("== Checking for updates");
            foreach (var r in Rows)
            {
                r.IsWorking = true;
                try
                {
                    var u = await Task.Run(() => _installer.CheckUpdateAsync(r.Component, ct), ct);
                    r.ApplyUpdate(u);
                    if (u != null) Log($"   {r.Title}: {u.Message}");
                }
                finally { r.IsWorking = false; }
            }
            var app = await Installer.CheckAppUpdateAsync(ShellViewModel.AppVersion, ct);
            _appUpdatePage = app?.PageUrl;
            AppUpdateText = app != null ? $"PokéPat {app.Version} is available (you have {ShellViewModel.AppVersion})." : "";
            _settings.LastUpdateCheckUtc = DateTime.UtcNow;
            int n = Rows.Count(r => r.UpdateAvailable) + (app != null ? 1 : 0);
            Log(n == 0 ? "   Everything is up to date." : $"   {n} update(s) available.");
        });
        Summary = Rows.Any(r => r.UpdateAvailable) || HasAppUpdate ? "Updates available – see the marked parts." : "Everything is up to date.";
    }

    public async void UpdateOne(InstallRowVm row)
    {
        if (IsBusy) return;
        await RunAsync(async ct =>
        {
            Log($"== Update {row.Title}");
            row.IsWorking = true;
            try
            {
                var s = await Task.Run(() => _installer.UpdateAsync(row.Component, Log, ct), ct);
                Show(row.Component, s);
                Log("   " + s.Message);
                if (s.IsReady)
                {
                    StoreProgramPaths();
                    row.ApplyUpdate(await Task.Run(() => _installer.CheckUpdateAsync(row.Component, ct), ct));
                    if (row.Component == InstallComponent.Expansion) row.ApplyUpdate(new UpdateInfo(row.Component, null, null, false, false, s.Message));
                }
                else row.ApplyUpdate(new UpdateInfo(row.Component, null, null, true, false, s.Message));
            }
            finally { row.IsWorking = false; }
        });
    }

    /// <summary>A new Porymap/mGBA is used right away, also when the window is closed without "Open my project".</summary>
    private void StoreProgramPaths()
    {
        if (_installer.PorymapExe is { } porymap) _settings.DefaultPorymapExe = porymap;
        if (_installer.EmulatorExe is { } emulator) _settings.DefaultEmulatorExe = emulator;
        if (_settings.LastProject is { } last && _settings.Projects.TryGetValue(ToolkitSettings.ProjectKey(last), out var t))
        {
            if (_installer.PorymapExe != null) t.PorymapExe = _installer.PorymapExe;
            if (_installer.EmulatorExe != null) t.EmulatorExe = _installer.EmulatorExe;
        }
    }

    private Installer NewInstaller() => new(new InstallOptions
    {
        Distro = Distro,
        ProjectPath = ProjectPath.Trim().Length > 0 ? ProjectPath.Trim() : "~/pokeemerald-expansion",
        ToolsFolder = ToolsFolder.Trim().Length > 0 ? ToolsFolder.Trim() : InstallOptions.DefaultToolsFolder,
        ExistingPorymap = InstallOptions.FromSettings(_settings).ExistingPorymap,
        ExistingEmulator = InstallOptions.FromSettings(_settings).ExistingEmulator,
    });

    public ObservableCollection<InstallRowVm> Rows { get; } = [];
    public ObservableCollection<string> Distros { get; } = [];
    public bool UsesWsl => OperatingSystem.IsWindows();

    private string _projectPath, _toolsFolder, _distro;
    public string ProjectPath { get => _projectPath; set { if (Set(ref _projectPath, value)) SettingsChanged(); } }
    public string ToolsFolder { get => _toolsFolder; set { if (Set(ref _toolsFolder, value)) SettingsChanged(); } }
    public string Distro { get => _distro; set { if (Set(ref _distro, value ?? "Ubuntu")) SettingsChanged(); } }

    private string _projectHint = "";
    public string ProjectHint { get => _projectHint; private set => Set(ref _projectHint, value); }

    private bool _busy;
    public bool IsBusy
    {
        get => _busy;
        private set
        {
            if (!Set(ref _busy, value)) return;
            CommandManager.InvalidateRequerySuggested();
        }
    }

    private string _summary = "";
    public string Summary { get => _summary; private set => Set(ref _summary, value); }

    public string LogText => _log.ToString();
    public bool ProjectReady => Rows.First(r => r.Component == InstallComponent.Expansion).State == InstallState.Ready;

    public ICommand InstallAllCommand { get; }
    public ICommand CheckCommand { get; }
    public ICommand CancelCommand { get; }

    /// <summary>Set by the window: a log line was added (scroll to the end).</summary>
    public event Action? LogChanged;

    private void SettingsChanged()
    {
        if (IsBusy) return;
        _installer = NewInstaller();
        _ = CheckAllAsync();
    }

    private string? _progressPrefix;

    public void Log(string line)
    {
        Application.Current.Dispatcher.Invoke(() =>
        {
            // git and downloads report progress on one line again and again: replace instead of append.
            var prefix = line.Contains('%') && line.Contains(':') ? line[..line.IndexOf(':')] : line.TrimStart().EndsWith(" MB") ? "MB" : null;
            if (prefix != null && prefix == _progressPrefix)
            {
                var text = _log.ToString().TrimEnd('\n');
                int cut = text.LastIndexOf('\n');
                _log.Length = cut + 1;
            }
            _progressPrefix = prefix;
            _log.Append(line).Append('\n');
            OnPropertyChanged(nameof(LogText));
            LogChanged?.Invoke();
        });
    }

    private void Show(InstallComponent c, ComponentStatus s)
    {
        Application.Current.Dispatcher.Invoke(() =>
        {
            Rows.First(r => r.Component == c).Apply(s);
            if (c == InstallComponent.Expansion)
            {
                OnPropertyChanged(nameof(ProjectReady));
                ProjectHint = _installer.ProjectHostPath is { } host ? "Opened by PokéPat, Porymap and Windows as " + host : "";
            }
            UpdateSummary();
        });
    }

    private void UpdateSummary()
    {
        var relevant = Rows.Where(r => r.State != InstallState.NotAvailable).ToList();
        int ready = relevant.Count(r => r.State == InstallState.Ready);
        Summary = IsBusy ? "Working – this can take a few minutes …"
            : ready == relevant.Count ? "Everything is ready. Click \"Open my project\" to start."
            : $"{ready} of {relevant.Count} ready.";
    }

    public async Task LoadAsync()
    {
        if (UsesWsl)
        {
            var list = await Installer.ListDistrosAsync();
            Distros.Clear();
            foreach (var d in list) Distros.Add(d);
            if (!list.Contains(Distro, StringComparer.OrdinalIgnoreCase))
            {
                var pick = list.FirstOrDefault(d => d.StartsWith("Ubuntu", StringComparison.OrdinalIgnoreCase));
                if (pick != null) { _distro = pick; OnPropertyChanged(nameof(Distro)); _installer = NewInstaller(); }
                else if (!Distros.Contains(Distro)) Distros.Add(Distro);
            }
        }
        await CheckAllAsync();
    }

    private int _checkGeneration;

    public async Task CheckAllAsync()
    {
        int gen = ++_checkGeneration;
        var installer = _installer;
        foreach (var r in Rows) r.Apply(new ComponentStatus(InstallState.Unknown, "Checking …"));
        foreach (var c in Installer.All)
        {
            var s = await Task.Run(() => installer.CheckAsync(c));
            if (gen != _checkGeneration) return;
            Show(c, s);
        }
    }

    public async void InstallOne(InstallRowVm row)
    {
        if (IsBusy) return;
        await RunAsync(async ct =>
        {
            Log($"== {row.Title}");
            row.IsWorking = true;
            try
            {
                var s = await Task.Run(() => _installer.InstallAsync(row.Component, Log, newWindow: true, ct), ct);
                Show(row.Component, s);
                Log("   " + s.Message);
            }
            finally { row.IsWorking = false; }
            // Later parts depend on earlier ones (e.g. Poryscript on the project): refresh them.
            foreach (var c in Installer.All.Where(c => c > row.Component)) Show(c, await Task.Run(() => _installer.CheckAsync(c, ct), ct));
        });
    }

    private async void InstallAll()
    {
        if (IsBusy) return;
        await RunAsync(async ct =>
        {
            bool ok = await Task.Run(() => _installer.InstallAllAsync(Log, (c, s) =>
            {
                Show(c, s);
                Application.Current.Dispatcher.Invoke(() =>
                {
                    foreach (var r in Rows) r.IsWorking = false;
                    if (s.State is InstallState.Missing or InstallState.Failed) Rows.First(r => r.Component == c).IsWorking = true;
                });
            }, newWindow: true, ct), ct);
            Log(ok ? "Everything is installed." : "Stopped – see the marked step.");
        });
    }

    private async Task RunAsync(Func<CancellationToken, Task> work)
    {
        _cts = new CancellationTokenSource();
        IsBusy = true;
        UpdateSummary();
        try { await work(_cts.Token); }
        catch (OperationCanceledException) { Log("Cancelled."); }
        finally
        {
            foreach (var r in Rows) r.IsWorking = false;
            IsBusy = false;
            UpdateSummary();
        }
    }

    /// <summary>Stores project and programs in the settings; returns the project folder to open.</summary>
    public async Task<string?> FinishAsync()
    {
        foreach (var c in new[] { InstallComponent.Expansion, InstallComponent.Porytiles, InstallComponent.Porymap, InstallComponent.Emulator })
            Show(c, await Task.Run(() => _installer.CheckAsync(c)));
        if (!ProjectReady) return null;
        _installer.ApplyTo(_settings);
        return _installer.ProjectHostPath;
    }
}
