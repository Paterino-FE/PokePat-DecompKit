using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Input;
using TilesetEditor.App.Infrastructure;
using TilesetEditor.App.Shell;
using TilesetEditor.Core.Events;
using TilesetEditor.Core.Project;
using TilesetEditor.Export;

namespace TilesetEditor.App.Modules.Flags;

public sealed class FlagRowVm(FlagInfo info, int usages)
{
    public FlagInfo Info { get; } = info;
    public string Name => Info.Name;
    public string Hex => Info.Hex;
    public string Category => Info.Category;
    public string Comment => Info.Comment;
    public int Usages { get; } = usages;
    public string KindText => Info.Kind == FlagKind.Flag ? "Flag" : "Variable";
}

/// <summary>A person on the selected map; its visibility flag can be changed.</summary>
public sealed class ObjectFlagRowVm(MapObjectFlag obj, Action changed) : ObservableObject
{
    private string _flag = obj.Flag;
    public MapObjectFlag Object { get; } = obj;
    public string Person => $"#{Object.Index + 1} ({Object.X},{Object.Y}) {Object.Graphics}";
    public string Script => Object.Script;
    public string Flag
    {
        get => _flag;
        set
        {
            value = (value ?? "").Trim();
            if (value.Length == 0) value = "0";
            if (Set(ref _flag, value)) { OnPropertyChanged(nameof(IsChanged)); changed(); }
        }
    }
    public bool IsChanged => _flag != Object.Flag;
}

/// <summary>
/// Flags and variables: all defined constants with category and every use (scripts, C code, map.json), filtered by map.
/// Free slots can be claimed under a new name, descriptions edited and the visibility flags of persons changed.
/// </summary>
public sealed class FlagsViewModel : ObservableObject, IToolkitModule
{
    private const string AllMaps = "All maps";
    private const string AllCategories = "All categories";
    private readonly ProjectContext _ctx;
    private FlagCatalog? _catalog;

    public FlagsViewModel(ProjectContext ctx)
    {
        _ctx = ctx;
        _ctx.ProjectChanged += (_, _) => { if (!HasUnsavedChanges) _ = LoadAsync(); };
        NewFlagCommand = new RelayCommand(() => Claim(FlagKind.Flag), () => _catalog != null);
        NewVarCommand = new RelayCommand(() => Claim(FlagKind.Var), () => _catalog != null);
        MoreFlagsCommand = new RelayCommand(() => Expand(FlagKind.Flag), () => _catalog != null);
        MoreVarsCommand = new RelayCommand(() => Expand(FlagKind.Var), () => _catalog != null);
        SaveCommentCommand = new RelayCommand(SaveComment, () => Selected != null && (CommentText ?? "") != Selected.Comment);
        OpenUsageCommand = new RelayCommand(OpenUsage, () => SelectedUsage != null);
        SaveObjectsCommand = new RelayCommand(SaveObjectFlags, () => HasUnsavedChanges);
        ResetObjectsCommand = new RelayCommand(ShowObjects, () => HasUnsavedChanges);
        View = new FlagsView { DataContext = this };
        _ = LoadAsync();
    }

    public string Title => "Flags & Variables";
    public string Description => "Story flags, map flags, variables";
    public bool IsAvailable => true;
    public override string ToString() => Title;
    public FrameworkElement View { get; }
    public bool HasUnsavedChanges => Objects.Any(o => o.IsChanged);
    public string? UnsavedChangesDescription => HasUnsavedChanges ? "Flags & Variables: changed visibility flags not saved yet." : null;

    public ICommand NewFlagCommand { get; }
    public ICommand NewVarCommand { get; }
    public ICommand MoreFlagsCommand { get; }
    public ICommand MoreVarsCommand { get; }
    public ICommand SaveCommentCommand { get; }
    public ICommand OpenUsageCommand { get; }
    public ICommand SaveObjectsCommand { get; }
    public ICommand ResetObjectsCommand { get; }

    public ObservableCollection<FlagRowVm> Rows { get; } = [];
    public ObservableCollection<FlagUsage> SelectedUsages { get; } = [];
    public ObservableCollection<ObjectFlagRowVm> Objects { get; } = [];
    public ObservableCollection<string> Maps { get; } = [];
    public ObservableCollection<string> Categories { get; } = [];
    public ObservableCollection<string> FlagNames { get; } = [];
    public IReadOnlyList<string> Kinds { get; } = ["Flags and variables", "Flags only", "Variables only"];

    private string _filter = "", _map = AllMaps, _category = AllCategories, _kind = "Flags and variables", _summary = "Reading flags …", _commentText = "", _details = "";
    private bool _onlyUsed;
    public string Filter { get => _filter; set { if (Set(ref _filter, value)) Apply(); } }
    public string Map { get => _map; set { if (Set(ref _map, value)) { Apply(); ShowObjects(); } } }
    public string Category { get => _category; set { if (Set(ref _category, value)) Apply(); } }
    public string Kind { get => _kind; set { if (Set(ref _kind, value)) Apply(); } }
    public bool OnlyUsed { get => _onlyUsed; set { if (Set(ref _onlyUsed, value)) Apply(); } }
    public string Summary { get => _summary; private set => Set(ref _summary, value); }
    public string Details { get => _details; private set => Set(ref _details, value); }
    public string CommentText { get => _commentText; set { if (Set(ref _commentText, value)) CommandManager.InvalidateRequerySuggested(); } }
    public bool HasMap => Map != AllMaps;
    public string ObjectsTitle => HasMap ? $"Persons on {Map}: visibility" : "Persons: select a map above";

    private FlagRowVm? _selected;
    public FlagRowVm? Selected { get => _selected; set { if (Set(ref _selected, value)) ShowSelected(); } }
    private FlagUsage? _selectedUsage;
    public FlagUsage? SelectedUsage { get => _selectedUsage; set => Set(ref _selectedUsage, value); }

    private async Task LoadAsync()
    {
        var root = _ctx.Root;
        if (root == null) return;
        Summary = "Reading flags, variables and all uses …";
        var keepName = Selected?.Name;
        _catalog = await Task.Run(() => FlagCatalog.Load(root));
        var keepMap = Map;
        Maps.Clear();
        Maps.Add(AllMaps);
        foreach (var m in _catalog.MapIds.Keys.OrderBy(m => m, StringComparer.Ordinal)) Maps.Add(m);
        _map = Maps.Contains(keepMap) ? keepMap : AllMaps;
        OnPropertyChanged(nameof(Map));
        var keepCat = Category;
        Categories.Clear();
        Categories.Add(AllCategories);
        foreach (var c in _catalog.Entries.Select(e => e.Category).Distinct().OrderBy(c => c)) Categories.Add(c);
        _category = Categories.Contains(keepCat) ? keepCat : AllCategories;
        OnPropertyChanged(nameof(Category));
        FlagNames.Clear();
        FlagNames.Add("0");
        foreach (var e in _catalog.Entries.Where(e => e.Kind == FlagKind.Flag)) FlagNames.Add(e.Name);
        Apply();
        ShowObjects();
        if (keepName != null) Selected = Rows.FirstOrDefault(r => r.Name == keepName);
        CommandManager.InvalidateRequerySuggested();
    }

    private void Apply()
    {
        Rows.Clear();
        OnPropertyChanged(nameof(HasMap));
        OnPropertyChanged(nameof(ObjectsTitle));
        if (_catalog == null) return;
        var f = Filter.Trim();
        IEnumerable<FlagInfo> q = _catalog.Entries;
        if (Kind == "Flags only") q = q.Where(e => e.Kind == FlagKind.Flag);
        else if (Kind == "Variables only") q = q.Where(e => e.Kind == FlagKind.Var);
        if (Category != AllCategories) q = q.Where(e => e.Category == Category);
        if (HasMap) q = q.Where(e => _catalog.UsagesOf(e.Name).Any(u => u.Map == Map));
        if (OnlyUsed) q = q.Where(e => _catalog.UsagesOf(e.Name).Count > 0);
        if (f.Length > 0) q = q.Where(e => e.Name.Contains(f, StringComparison.OrdinalIgnoreCase) || e.Comment.Contains(f, StringComparison.OrdinalIgnoreCase) || e.Hex.Contains(f, StringComparison.OrdinalIgnoreCase));
        foreach (var e in q) Rows.Add(new FlagRowVm(e, _catalog.UsagesOf(e.Name).Count));
        int flags = _catalog.Entries.Count(e => e.Kind == FlagKind.Flag), vars = _catalog.Entries.Count - flags;
        Summary = $"{Rows.Count} shown · {flags} flags, {vars} variables · free: {_catalog.Entries.Count(e => e.IsClaimable && _catalog.IsUnusedAnywhere(e.Name))}" +
                  (HasMap ? $" · used on {Map}" : "");
    }

    private void ShowSelected()
    {
        SelectedUsages.Clear();
        var s = Selected;
        if (s == null || _catalog == null) { Details = ""; CommentText = ""; return; }
        var usages = _catalog.UsagesOf(s.Name);
        var maps = usages.Select(u => u.Map).Where(m => m.Length > 0).Distinct().ToList();
        Details = $"{s.Name} = {s.Info.ValueText}" + (s.Info.Value is { } v ? $"  ({s.Hex})" : "") +
                  $"\n{s.KindText} · {s.Category} · {s.Info.File}:{s.Info.Line}" +
                  $"\n{usages.Count} use(s)" + (maps.Count > 0 ? $" on {maps.Count} map(s): {string.Join(", ", maps.Take(8))}{(maps.Count > 8 ? " …" : "")}" : "") +
                  (s.Category.StartsWith("Temporary", StringComparison.Ordinal) ? "\nTemporary: reset on every map change – good for \"already talked to\" on one map." : "") +
                  (s.Info.IsClaimable && usages.Count == 0 ? "\nFree: can be used under your own name via \"New flag\" / \"New variable\"." : "");
        foreach (var u in usages.OrderBy(u => HasMap && u.Map != Map).ThenBy(u => u.Map.Length == 0).ThenBy(u => u.Map).ThenBy(u => u.File).ThenBy(u => u.Line))
            SelectedUsages.Add(u);
        CommentText = s.Comment;
    }

    private void ShowObjects()
    {
        Objects.Clear();
        if (_catalog != null && HasMap)
            foreach (var o in _catalog.Objects.Where(o => o.Map == Map)) Objects.Add(new ObjectFlagRowVm(o, () => CommandManager.InvalidateRequerySuggested()));
        CommandManager.InvalidateRequerySuggested();
    }

    private void OpenUsage()
    {
        if (SelectedUsage == null || _ctx.Root == null) return;
        var full = ProjectPaths.Combine(_ctx.Root, SelectedUsage.File);
        try { Process.Start(new ProcessStartInfo(full) { UseShellExecute = true }); }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Process.Start("explorer.exe", $"/select,\"{full}\"");
        }
    }

    private void Claim(FlagKind kind)
    {
        var root = _ctx.Root;
        if (root == null || _catalog == null) return;
        var free = _catalog.Entries.Where(e => e.Kind == kind && e.IsClaimable && _catalog.IsUnusedAnywhere(e.Name)).ToList();
        string what = kind == FlagKind.Flag ? "flag" : "variable";
        if (free.Count == 0)
        {
            if (AppDialog.Show(Application.Current.MainWindow,
                    $"There is no free {(kind == FlagKind.Flag ? "FLAG_UNUSED_…" : "VAR_UNUSED_…")} entry left.\n\nAdd more {what}s to the save data now?\n\n" + ExpandWarning(kind),
                    $"New {what}", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
            Expand(kind, askFirst: false);
            return;
        }
        var slot = free[0];
        var dlg = new InputDialog(kind == FlagKind.Flag ? "New flag" : "New variable",
            $"Uses the free entry {slot.Name} ({slot.Hex}) under a name of your choice. The value stays the same, so save files stay valid. " +
            $"Afterwards the {what} is available in every picker of the toolkit and in Porymap.",
            ("Name (e.g. \"Rival defeated\" or FLAG_RIVAL_DEFEATED)", ""), ("Description (optional)", ""));
        if (dlg.ShowDialog() != true) return;
        var name = FlagEditor.ToName(dlg.Values[0], kind);
        if (!FlagEditor.IsValidName(name, kind)) { ProjectWriter.Warn($"New {what}", $"\"{dlg.Values[0]}\" does not give a valid name."); return; }
        if (_catalog.Entries.Any(e => e.Name == name)) { ProjectWriter.Warn($"New {what}", $"{name} already exists."); return; }
        try
        {
            var file = ProjectWriter.ModifyText(root, slot.File, t => FlagEditor.Rewrite(t, slot.Name, name, dlg.Values[1].Length > 0 ? dlg.Values[1] : ""),
                $"{slot.Name} → {name}");
            if (!ProjectWriter.Apply(root, $"New {what} {name}", slot.File, [file],
                    $"Only the line of {slot.Name} ({slot.Hex}) is renamed; every other line stays unchanged.")) return;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.IO.IOException)
        {
            ProjectWriter.Warn($"New {what}", ex.Message);
            return;
        }
        Filter = name;
        _ctx.Reload();
    }

    private static string ExpandWarning(FlagKind kind) => kind == FlagKind.Flag
        ? $"This adds {FlagEditor.FlagBlock} new saved flags (the save data grows by {FlagEditor.FlagBlock / 8} bytes)."
          + "\n\nIMPORTANT: save files made before this change will not load correctly (everything after the flags in the save moves). Do it before releasing, or tell players to start a new game."
          + "\nIf the save data is full, the ROM build stops with a \"SaveBlock1\" size error – free space under Features → Save data (FREE_… options)."
        : $"This adds {FlagEditor.VarBlock} new saved variables (the save data grows by {FlagEditor.VarBlock * 2} bytes)."
          + "\n\nIMPORTANT: save files made before this change will not load correctly (everything after the variables in the save moves). Do it before releasing, or tell players to start a new game."
          + "\nIf the save data is full, the ROM build stops with a \"SaveBlock1\" size error – free space under Features → Save data (FREE_… options).";

    /// <summary>Adds a block of new free flags / variables (save-breaking), then they can be claimed as usual.</summary>
    private void Expand(FlagKind kind, bool askFirst = true)
    {
        var root = _ctx.Root;
        if (root == null) return;
        string what = kind == FlagKind.Flag ? "flags" : "variables";
        if (askFirst && AppDialog.Show(Application.Current.MainWindow, ExpandWarning(kind) + "\n\nContinue?", $"More {what}",
                MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        var rel = kind == FlagKind.Flag ? FlagCatalog.FlagsFile : FlagCatalog.VarsFile;
        List<string> names = [];
        try
        {
            var file = ProjectWriter.ModifyText(root, rel, t =>
            {
                var opponents = System.IO.File.ReadAllText(ProjectPaths.Combine(root, FlagCatalog.OpponentsFile));
                var (text, n) = kind == FlagKind.Flag ? FlagEditor.AddFlags(t, FlagEditor.FlagBlock, opponents) : FlagEditor.AddVars(t);
                names = n;
                return text;
            }, kind == FlagKind.Flag ? $"{FlagEditor.FlagBlock} new saved flags, FLAGS_COUNT raised" : $"{FlagEditor.VarBlock} new saved variables, VARS_END raised");
            if (!ProjectWriter.Apply(root, $"More {what}", rel, [file],
                    $"New free entries: {names.FirstOrDefault()} … {names.LastOrDefault()}. Use \"New {(kind == FlagKind.Flag ? "flag" : "variable")}…\" to give them names.",
                    "Save-breaking: old save files will not load correctly after this change.",
                    "Build the ROM afterwards; a SaveBlock1 size error means the save data is full (see Features → Save data).")) return;
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or System.IO.IOException)
        {
            ProjectWriter.Warn($"More {what}", ex.Message);
            return;
        }
        Filter = kind == FlagKind.Flag ? "FLAG_UNUSED" : "VAR_UNUSED";
        _ctx.Reload();
    }

    private void SaveComment()
    {
        var root = _ctx.Root;
        var s = Selected;
        if (root == null || s == null) return;
        try
        {
            var file = ProjectWriter.ModifyText(root, s.Info.File, t => FlagEditor.Rewrite(t, s.Name, s.Name, CommentText ?? ""), $"Description of {s.Name}");
            if (!ProjectWriter.Apply(root, $"Description {s.Name}", s.Info.File, [file], "Only the comment at the end of the #define line changes.")) return;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.IO.IOException)
        {
            ProjectWriter.Warn("Save description", ex.Message);
            return;
        }
        _ctx.Reload();
    }

    private void SaveObjectFlags()
    {
        var root = _ctx.Root;
        if (root == null || _catalog == null) return;
        var changed = Objects.Where(o => o.IsChanged).ToList();
        var unknown = changed.Where(o => o.Flag != "0" && !_catalog.Entries.Any(e => e.Name == o.Flag)).ToList();
        if (unknown.Count > 0) { ProjectWriter.Warn("Save visibility", "Unknown flag: " + string.Join(", ", unknown.Select(o => o.Flag))); return; }
        var files = new List<PlannedFile>();
        try
        {
            foreach (var g in changed.GroupBy(o => o.Object.MapJson))
                files.Add(ProjectWriter.ModifyText(root, g.Key, t =>
                {
                    foreach (var o in g) t = MapEventScripts.SetStringField(t, MapEventKind.Object, o.Object.Index, "flag", o.Flag);
                    return t;
                }, string.Join(", ", g.Select(o => $"Person #{o.Object.Index + 1}: {(o.Object.Flag.Length > 0 ? o.Object.Flag : "0")} → {o.Flag}"))));
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.IO.IOException)
        {
            ProjectWriter.Warn("Save visibility", ex.Message);
            return;
        }
        if (!ProjectWriter.Apply(root, "Visibility of persons", "data/maps", files,
                "A person is hidden while its flag is set (\"0\" = always visible).",
                "Only the person's \"flag\" field changes; Porymap shows the change after reloading.")) return;
        Objects.Clear();
        _ctx.Reload();
    }
}
