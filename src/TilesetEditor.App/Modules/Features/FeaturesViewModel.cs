using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Input;
using TilesetEditor.App.Infrastructure;
using TilesetEditor.App.Modules.Tilesets;
using TilesetEditor.App.Shell;
using TilesetEditor.Core.Project;
using TilesetEditor.Export;

namespace TilesetEditor.App.Modules.Features;

/// <summary>A category in the left-hand list (one config file, or all).</summary>
public sealed record FeatureCategory(string Title, string? File, int Count)
{
    public override string ToString() => $"{Title}  ({Count})";
}

/// <summary>One option in the table; editable where the format is unambiguous.</summary>
public sealed class ConfigRowVm(ConfigOption option, Action changed) : ObservableObject
{
    private string _value = option.Value;
    public ConfigOption Option { get; } = option;
    public string Name => Option.Name;
    public string KindText => Option.KindText;
    public bool Conditional => Option.Conditional;
    public string File => Option.File;
    public int Line => Option.Line;
    public string Section => Option.Section;
    public string Category => ConfigCatalog.CategoryOf(Option.File);
    public string GroupTitle => Option.Section.Length > 0 ? Option.Section : "No section";
    public string Description => Option.Description;

    /// <summary>Switches only where the branch has TRUE/FALSE; numbers and generations if not inside #if.</summary>
    public bool IsEditable => !Option.Conditional && Option.Kind is ConfigValueKind.Switch or ConfigValueKind.Number or ConfigValueKind.Generation;
    public bool IsSwitch => IsEditable && Option.Kind == ConfigValueKind.Switch;
    public bool IsGeneration => IsEditable && Option.Kind == ConfigValueKind.Generation;
    public bool IsNumber => IsEditable && Option.Kind == ConfigValueKind.Number;
    public bool IsReadOnly => !IsEditable;
    public IReadOnlyList<string> Generations => ConfigEditor.Generations.Contains(Option.Value) ? ConfigEditor.Generations : [.. ConfigEditor.Generations, Option.Value];

    public string Value
    {
        get => _value;
        set
        {
            value = (value ?? "").Trim();
            if (!IsEditable || _value == value) return;
            if (IsNumber && !long.TryParse(value, out _) && !value.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) return;
            _value = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsOn));
            OnPropertyChanged(nameof(IsChanged));
            changed();
        }
    }

    public bool IsOn
    {
        get => _value == "TRUE";
        set => Value = value ? "TRUE" : "FALSE";
    }

    public bool IsChanged => _value != Option.Value;

    public void Reset()
    {
        _value = Option.Value;
        OnPropertyChanged(nameof(Value));
        OnPropertyChanged(nameof(IsOn));
        OnPropertyChanged(nameof(IsChanged));
    }
}

/// <summary>
/// Feature configuration of the branch (include/config/*.h). Switches, numbers and generations can be changed in place;
/// the write goes through a file preview with conflict check. Options inside #if blocks stay read-only.
/// </summary>
public sealed class FeaturesViewModel : ObservableObject, IToolkitModule
{
    private readonly ProjectContext _ctx;
    private List<ConfigRowVm> _all = [];

    public FeaturesViewModel(ProjectContext ctx)
    {
        _ctx = ctx;
        _ctx.ProjectChanged += (_, _) => { if (!HasUnsavedChanges) _ = LoadAsync(); };
        ShowInExplorerCommand = new RelayCommand(() =>
        {
            if (_ctx.Root == null || Selected == null) return;
            Process.Start("explorer.exe", $"/select,\"{ProjectPaths.Combine(_ctx.Root, Selected.File)}\"");
        }, () => Selected != null);
        CopyCommand = new RelayCommand(() =>
        {
            if (Selected != null) Clipboard.SetText($"{Selected.Name} = {Selected.Value}  ({Selected.File}:{Selected.Line})");
        }, () => Selected != null);
        SaveCommand = new RelayCommand(Save, () => HasUnsavedChanges);
        ResetCommand = new RelayCommand(() => { foreach (var r in _all.Where(r => r.IsChanged)) r.Reset(); RowChanged(); }, () => HasUnsavedChanges);
        View = new FeaturesView { DataContext = this };
        _ = LoadAsync();
    }

    public string Title => "Features";
    public string Description => "Project options (include/config)";
    public bool IsAvailable => true;
    public override string ToString() => Title;
    public FrameworkElement View { get; }
    public bool HasUnsavedChanges => _all.Any(r => r.IsChanged);
    public string? UnsavedChangesDescription => HasUnsavedChanges ? $"Features: {_all.Count(r => r.IsChanged)} changed option(s) not saved yet." : null;

    public ICommand ShowInExplorerCommand { get; }
    public ICommand CopyCommand { get; }
    public ICommand SaveCommand { get; }
    public ICommand ResetCommand { get; }

    public ObservableCollection<ConfigRowVm> Options { get; } = [];
    /// <summary>Left-hand list: "All" plus one entry per config file with a display name and count.</summary>
    public ObservableCollection<FeatureCategory> Categories { get; } = [];
    public IReadOnlyList<string> Kinds { get; } = ["All", "Switch", "Generation", "Number", "Constant", "Expression", "Changed"];

    private string _filter = "", _kind = "All", _summary = "";
    private FeatureCategory? _category;
    public string Filter { get => _filter; set { if (Set(ref _filter, value)) Apply(); } }
    public FeatureCategory? Category { get => _category; set { if (Set(ref _category, value)) Apply(); } }
    public string Kind { get => _kind; set { if (Set(ref _kind, value)) Apply(); } }
    public string Summary { get => _summary; private set => Set(ref _summary, value); }

    private ConfigRowVm? _selected;
    public ConfigRowVm? Selected { get => _selected; set => Set(ref _selected, value); }

    private static readonly string[] CategoryOrder = ["general", "overworld", "pokemon", "battle", "item", "caps", "save", "debug", "species_enabled", "test"];
    private static int CategoryRank(string file)
    {
        int i = Array.IndexOf(CategoryOrder, Path.GetFileNameWithoutExtension(file));
        return i < 0 ? CategoryOrder.Length - 2 : i;
    }

    private static readonly string[] BulkFiles = ["include/config/species_enabled.h", "include/config/test.h"];

    private async Task LoadAsync()
    {
        var root = _ctx.Root;
        if (root == null) { _all = []; Apply(); return; }
        var options = await Task.Run(() => ConfigCatalog.Load(root));
        _all = options.Select(o => new ConfigRowVm(o, RowChanged)).ToList();
        var keep = Category?.File;
        Categories.Clear();
        Categories.Add(new FeatureCategory("All (without species/tests)", null, _all.Count(r => !BulkFiles.Contains(r.File))));
        foreach (var g in _all.GroupBy(r => r.File).OrderBy(g => CategoryRank(g.Key)).ThenBy(g => g.Key))
            Categories.Add(new FeatureCategory(ConfigCatalog.CategoryOf(g.Key), g.Key, g.Count()));
        _category = Categories.FirstOrDefault(c => c.File == keep) ?? Categories[0];
        OnPropertyChanged(nameof(Category));
        Apply();
    }

    private void RowChanged()
    {
        UpdateSummary();
        CommandManager.InvalidateRequerySuggested();
    }

    private void Apply()
    {
        var f = Filter.Trim();
        Options.Clear();
        IEnumerable<ConfigRowVm> q = _all;
        if (Category?.File is { } file) q = q.Where(o => o.File == file);
        else q = q.Where(o => !BulkFiles.Contains(o.File));
        if (Kind == "Changed") q = q.Where(o => o.IsChanged);
        else if (Kind != "All") q = q.Where(o => o.KindText == Kind);
        if (f.Length > 0)
            q = q.Where(o => o.Name.Contains(f, StringComparison.OrdinalIgnoreCase) || o.Value.Contains(f, StringComparison.OrdinalIgnoreCase)
                             || o.Description.Contains(f, StringComparison.OrdinalIgnoreCase) || o.Section.Contains(f, StringComparison.OrdinalIgnoreCase));
        foreach (var o in q) Options.Add(o);
        UpdateSummary();
    }

    private void UpdateSummary()
    {
        int changed = _all.Count(r => r.IsChanged);
        Summary = $"{Options.Count} of {_all.Count} options shown · {_all.Count(o => o.Option.IsToggle)} on/off switches · " +
                  $"{_all.Count(o => o.IsEditable)} directly editable · {_all.Count(o => o.Conditional)} conditional (#if, read-only)" +
                  (changed > 0 ? $" · {changed} changed, not saved yet" : "");
    }

    private void Save()
    {
        var root = _ctx.Root;
        if (root == null) return;
        var files = new List<PlannedFile>();
        try
        {
            foreach (var group in _all.Where(r => r.IsChanged).GroupBy(r => r.File))
            {
                var full = ProjectPaths.Combine(root, group.Key);
                var original = System.IO.File.ReadAllBytes(full);
                var text = System.Text.Encoding.UTF8.GetString(original);
                var updated = ConfigEditor.Apply(text, group.Select(r => new ConfigChange(r.Name, r.Line, r.Option.Value, r.Value)));
                files.Add(new PlannedFile(group.Key, PlannedAction.Modify, new System.Text.UTF8Encoding(false).GetBytes(updated),
                    string.Join(", ", group.Select(r => $"{r.Name}: {r.Option.Value} → {r.Value}")), Hashing.Sha256(original)));
            }
        }
        catch (Exception ex) when (ex is ConfigConflictException or IOException)
        {
            MessageBox.Show(Application.Current.MainWindow, ex.Message, "Save features", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        var plan = ExportPlan.ForFiles(root, "Features", "include/config", files);
        plan.Notes.Add("Only the value in the respective #define line is replaced; comments and all other lines stay unchanged.");
        plan.Notes.Add("Some options change the save data (see description, e.g. \"SAVE-BREAKING\"). Rebuild afterwards.");
        if (new ExportPlanWindow(plan) { Owner = Application.Current.MainWindow }.ShowDialog() != true) return;
        var result = new ExportApplier().Apply(plan);
        if (!result.Success)
        {
            MessageBox.Show(Application.Current.MainWindow, "Error: " + result.Error, "Save features", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }
        foreach (var r in _all) r.Reset();
        _ = LoadAsync();
        _ctx.Reload();
    }
}
