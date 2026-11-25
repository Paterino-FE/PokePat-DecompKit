using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Input;
using TilesetEditor.App.Infrastructure;
using TilesetEditor.App.Shell;
using TilesetEditor.Core.GameData;
using TilesetEditor.Core.Project;
using TilesetEditor.Export;

namespace TilesetEditor.App.Modules.GameData;

/// <summary>One value in the grid. Validation runs on every change; errors block saving, warnings do not.</summary>
public sealed class GameDataCellVm(GameDataTable table, GameDataRowVm row, int index, Action changed) : ObservableObject
{
    private string? _error, _warning;

    private GameDataCell Cell => row.Row.Cells[index];
    public GameDataColumn Column => table.Def.Columns[index];
    public bool IsEditable => Cell.Editable && row.Row.ReadOnlyReason == null;
    public IReadOnlyList<string>? Options => table.Options[index];
    public bool HasOptions => Options is { Count: > 0 };
    public bool IsChanged => Cell.IsChanged;
    public string? Error => _error;
    public bool HasError => _error != null;
    public bool HasWarning => _error == null && _warning != null;
    public string Original => Cell.Original;

    /// <summary>Numbers sort as numbers.</summary>
    public object SortValue => Column.Kind == GameDataKind.Number
        ? (long.TryParse(Cell.Value, NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : -1L)
        : Cell.Value;

    public string? ToolTip => _error ?? _warning
        ?? (row.Row.ReadOnlyReason != null && Cell.Editable ? row.Row.ReadOnlyReason : null)
        ?? Cell.Note ?? (IsEditable ? Column.Hint : null);

    public string Value
    {
        get => Cell.Value;
        set
        {
            if (!IsEditable) return;
            value ??= "";
            if (Column.Kind == GameDataKind.Text) value = value.Replace("\r\n", "\\n").Replace("\n", "\\n").Replace("\r", "");
            else value = value.Trim();
            if (value == Cell.Value) return;
            Cell.Value = value;
            Revalidate();
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsChanged));
            OnPropertyChanged(nameof(SortValue));
            row.Refresh();
            changed();
        }
    }

    public void Revalidate()
    {
        if (!IsChanged) { _error = null; _warning = null; }
        else
        {
            var c = table.Validate(row.Row, index);
            _error = c.Error;
            _warning = c.Warning;
        }
        OnPropertyChanged(nameof(HasError));
        OnPropertyChanged(nameof(HasWarning));
        OnPropertyChanged(nameof(Error));
        OnPropertyChanged(nameof(ToolTip));
    }

    public void Reset()
    {
        Cell.Value = Cell.Original;
        Revalidate();
        OnPropertyChanged(nameof(Value));
        OnPropertyChanged(nameof(IsChanged));
        OnPropertyChanged(nameof(SortValue));
    }
}

public sealed class GameDataRowVm : ObservableObject
{
    public GameDataRowVm(GameDataTable table, GameDataRow row, Action changed)
    {
        Row = row;
        Cells = Enumerable.Range(0, row.Cells.Length).Select(i => new GameDataCellVm(table, this, i, changed)).ToList();
    }

    public GameDataRow Row { get; }
    public string Key => Row.Key;
    public string Source => $"{Path.GetFileName(Row.File)}:{Row.Line}";
    public IReadOnlyList<GameDataCellVm> Cells { get; }
    public bool IsChanged => Row.IsChanged;
    public bool HasError => Cells.Any(c => c.HasError);
    public bool IsReadOnly => Row.ReadOnlyReason != null;

    public void Refresh()
    {
        OnPropertyChanged(nameof(IsChanged));
        OnPropertyChanged(nameof(HasError));
    }

    public bool Matches(string filter) =>
        Key.Contains(filter, StringComparison.OrdinalIgnoreCase) || Cells.Any(c => c.Value.Contains(filter, StringComparison.OrdinalIgnoreCase));
}

/// <summary>An entry of the category filter: rows whose <see cref="Column"/> has <see cref="Value"/> (Column -1 = all rows).</summary>
public sealed record GameDataCategory(string Label, int Column, string Value)
{
    public static readonly GameDataCategory All = new("All", -1, "");
    public override string ToString() => Label;
}

/// <summary>A one-click category button ("Poké Balls", "Fire" …); <see cref="Filter"/> null = all entries.</summary>
public sealed class QuickFilterVm(string label, int count, System.Windows.Media.Imaging.BitmapSource? icon, QuickFilter? filter, Action<QuickFilterVm, bool> toggled) : ObservableObject
{
    public string Label { get; } = label;
    public int Count { get; } = count;
    public System.Windows.Media.Imaging.BitmapSource? Icon { get; } = icon;
    public bool HasIcon => Icon != null;
    public QuickFilter? Filter { get; } = filter;

    private bool _isSelected;
    public bool IsSelected
    {
        get => _isSelected;
        set { if (Set(ref _isSelected, value)) toggled(this, value); }
    }

    /// <summary>Sets the state without notifying the view model (when another button is chosen, or when rebuilding the row).</summary>
    internal void SetQuietly(bool selected) { _isSelected = selected; OnPropertyChanged(nameof(IsSelected)); }
}

/// <summary>An entry of the left-hand list: one table with its loaded rows (or the load error).</summary>
public sealed class GameDataTableVm(GameDataTableDef def) : ObservableObject
{
    public GameDataTableDef Def { get; } = def;
    public GameDataTable? Table { get; set; }
    /// <summary>The table's one-click categories with their icons (loaded with the table).</summary>
    public List<QuickFilter> QuickFilters { get; set; } = [];
    public string? Error { get; set; }
    public List<GameDataRowVm> Rows { get; set; } = [];
    public int ChangedCount => Rows.Count(r => r.IsChanged);
    public string Display => Error != null ? $"{Def.Title}  (not found)" : ChangedCount > 0 ? $"{Def.Title}  ({Rows.Count}, {ChangedCount} changed)" : $"{Def.Title}  ({Rows.Count})";
    public void Refresh() => OnPropertyChanged(nameof(Display));
    public override string ToString() => Display;
}

/// <summary>
/// Game data kept in C tables (trainer classes, items, moves, abilities, Pokémon, types, natures) and the region map
/// section names. Values are changed in place – only the value text is replaced – and written through the shared file
/// preview. Config-dependent values (ternaries, #if blocks) show the current setting and become fixed values when changed;
/// family-wide macros stay read-only. Items, moves and Pokémon can be filtered by category (pocket, type …).
/// </summary>
public sealed class GameDataViewModel : ObservableObject, IToolkitModule
{
    private readonly ProjectContext _ctx;
    private bool _loading;

    public GameDataViewModel(ProjectContext ctx)
    {
        _ctx = ctx;
        _ctx.ProjectChanged += (_, _) => { if (!HasUnsavedChanges) _ = LoadAsync(); };
        foreach (var def in GameDataCatalog.Tables) Tables.Add(new GameDataTableVm(def));
        _selectedTable = Tables[0];
        SaveCommand = new RelayCommand(Save, () => HasUnsavedChanges && !_loading);
        ResetCommand = new RelayCommand(Discard, () => HasUnsavedChanges);
        AddTrainerClassCommand = new RelayCommand(AddTrainerClass, () => _ctx.Root != null && !_loading && SelectedTable?.Table != null);
        ShowInExplorerCommand = new RelayCommand(() =>
        {
            if (_ctx.Root == null) return;
            var file = Selected?.Row.File ?? SelectedTable?.Table?.Files.FirstOrDefault();
            if (file != null) Process.Start("explorer.exe", $"/select,\"{ProjectPaths.Combine(_ctx.Root, file)}\"");
        }, () => _ctx.Root != null && SelectedTable?.Table != null);
        View = new GameDataView { DataContext = this };
        _ = LoadAsync();
    }

    public string Title => "Game data";
    public string Description => "Names, prices, stats … in the C tables";
    public bool IsAvailable => true;
    public override string ToString() => Title;
    public FrameworkElement View { get; }
    public bool HasUnsavedChanges => Tables.Any(t => t.Rows.Any(r => r.IsChanged));
    public string? UnsavedChangesDescription => HasUnsavedChanges
        ? "Game data: " + string.Join(", ", Tables.Where(t => t.ChangedCount > 0).Select(t => $"{t.ChangedCount} {t.Def.Title.ToLowerInvariant()}")) + " changed, not saved yet."
        : null;

    public ICommand SaveCommand { get; }
    public ICommand ResetCommand { get; }
    public ICommand AddTrainerClassCommand { get; }
    public ICommand ShowInExplorerCommand { get; }

    public ObservableCollection<GameDataTableVm> Tables { get; } = [];
    public ObservableCollection<GameDataRowVm> Rows { get; } = [];

    /// <summary>Raised when the columns of the grid must be rebuilt (other table selected or reloaded).</summary>
    public event EventHandler? ColumnsChanged;

    private GameDataTableVm? _selectedTable;
    public GameDataTableVm? SelectedTable
    {
        get => _selectedTable;
        set
        {
            if (!Set(ref _selectedTable, value)) return;
            OnPropertyChanged(nameof(IsTrainerClasses));
            OnPropertyChanged(nameof(TableNote));
            ColumnsChanged?.Invoke(this, EventArgs.Empty);
            BuildQuickFilters();
            BuildCategories();
            ApplyFilter();
        }
    }

    /// <summary>The category buttons of the selected table, "All" first.</summary>
    public ObservableCollection<QuickFilterVm> QuickFilterButtons { get; } = [];
    public bool HasQuickFilters => QuickFilterButtons.Count > 1;
    private QuickFilter? _quickFilter;

    private void BuildQuickFilters()
    {
        var t = SelectedTable;
        var keep = _quickFilter?.Label;
        QuickFilterButtons.Clear();
        _quickFilter = null;
        if (t?.Table != null && t.QuickFilters.Count > 0)
        {
            QuickFilterButtons.Add(new QuickFilterVm("All", t.Rows.Count, null, null, ChooseQuickFilter));
            foreach (var f in t.QuickFilters)
                QuickFilterButtons.Add(new QuickFilterVm(f.Label, f.Count, f.Icon != null ? Imaging.ToBitmap(f.Icon) : null, f, ChooseQuickFilter));
            var selected = QuickFilterButtons.FirstOrDefault(b => b.Label == keep) ?? QuickFilterButtons[0];
            _quickFilter = selected.Filter;
            selected.SetQuietly(true);
        }
        OnPropertyChanged(nameof(HasQuickFilters));
    }

    private void ChooseQuickFilter(QuickFilterVm button, bool selected)
    {
        // Clicking the active button again switches back to "All".
        var chosen = selected ? button : QuickFilterButtons[0];
        foreach (var b in QuickFilterButtons) if (b != chosen && b.IsSelected) b.SetQuietly(false);
        if (!chosen.IsSelected) chosen.SetQuietly(true);
        _quickFilter = chosen.Filter;
        ApplyFilter();
    }

    public bool IsTrainerClasses => SelectedTable?.Def.Id == "trainer_classes";

    /// <summary>Pockets/kinds of items, types/categories of moves … with their entry counts; "All" first.</summary>
    public ObservableCollection<GameDataCategory> Categories { get; } = [];
    public bool HasCategories => Categories.Count > 1;

    private GameDataCategory _category = GameDataCategory.All;
    public GameDataCategory? Category
    {
        get => _category;
        set { if (Set(ref _category, value ?? GameDataCategory.All)) ApplyFilter(); }
    }

    private void BuildCategories()
    {
        var t = SelectedTable;
        var keep = _category;
        Categories.Clear();
        Categories.Add(GameDataCategory.All);
        if (t?.Table != null)
            foreach (var header in t.Def.CategoryColumns)
            {
                int col = t.Def.Columns.ToList().FindIndex(c => c.Header == header);
                if (col < 0) continue;
                var prefix = t.Def.Columns[col].ConstantPrefix ?? "";
                foreach (var g in t.Rows.Select(r => r.Row.Cells[col].Original).Where(v => v.Length > 0 && Regex.IsMatch(v, @"^\w+$"))
                             .GroupBy(v => v).OrderBy(g => g.Key, StringComparer.Ordinal))
                    Categories.Add(new GameDataCategory($"{header}: {Pretty(g.Key, prefix)}  ({g.Count()})", col, g.Key));
            }
        _category = Categories.FirstOrDefault(c => c.Column == keep.Column && c.Value == keep.Value) ?? GameDataCategory.All;
        OnPropertyChanged(nameof(Category));
        OnPropertyChanged(nameof(HasCategories));
    }

    /// <summary>POCKET_POKE_BALLS → "Poke Balls", TYPE_FIRE → "Fire".</summary>
    private static string Pretty(string constant, string prefix)
    {
        var s = constant.StartsWith(prefix, StringComparison.Ordinal) ? constant[prefix.Length..] : constant;
        return string.Join(' ', s.Split('_', StringSplitOptions.RemoveEmptyEntries)
            .Select(w => w.Length <= 2 && w.All(char.IsDigit) ? w : char.ToUpperInvariant(w[0]) + w[1..].ToLowerInvariant()));
    }
    public string TableNote => SelectedTable == null ? "" : SelectedTable.Error ?? SelectedTable.Def.Note;

    private string _filter = "", _summary = "", _details = "";
    private bool _changedOnly;
    public string Filter { get => _filter; set { if (Set(ref _filter, value)) ApplyFilter(); } }
    public bool ChangedOnly { get => _changedOnly; set { if (Set(ref _changedOnly, value)) ApplyFilter(); } }
    public string Summary { get => _summary; private set => Set(ref _summary, value); }
    public string Details { get => _details; private set => Set(ref _details, value); }

    private GameDataRowVm? _selected;
    public GameDataRowVm? Selected
    {
        get => _selected;
        set { if (Set(ref _selected, value)) UpdateDetails(); }
    }

    private async Task LoadAsync()
    {
        var root = _ctx.Root;
        _loading = true;
        try
        {
            if (root == null)
            {
                foreach (var t in Tables) { t.Table = null; t.Rows = []; t.Error = "No project is open."; t.Refresh(); }
            }
            else
            {
                var loaded = await Task.Run(() =>
                {
                    var text = GameText.Load(root);
                    return GameDataCatalog.Tables.Select(def =>
                    {
                        try
                        {
                            var table = GameDataCatalog.Load(root, def, text);
                            return (Table: table, Error: (string?)null, Quick: QuickFilters.For(table, root));
                        }
                        catch (Exception ex) when (ex is IOException or FormatException or UnauthorizedAccessException or InvalidOperationException)
                        {
                            return (Table: (GameDataTable?)null, Error: $"{def.Title} could not be read: {ex.Message}", Quick: new List<QuickFilter>());
                        }
                    }).ToList();
                });
                if (_ctx.Root != root) return;
                for (int i = 0; i < Tables.Count; i++)
                {
                    var vm = Tables[i];
                    vm.Table = loaded[i].Table;
                    vm.Error = loaded[i].Error;
                    vm.QuickFilters = loaded[i].Quick;
                    vm.Rows = vm.Table == null ? [] : vm.Table.Rows.Select(r => new GameDataRowVm(vm.Table, r, RowChanged)).ToList();
                    vm.Refresh();
                }
            }
        }
        finally
        {
            _loading = false;
        }
        OnPropertyChanged(nameof(TableNote));
        ColumnsChanged?.Invoke(this, EventArgs.Empty);
        BuildQuickFilters();
        BuildCategories();
        ApplyFilter();
        CommandManager.InvalidateRequerySuggested();
    }

    private void RowChanged()
    {
        SelectedTable?.Refresh();
        UpdateSummary();
        UpdateDetails();
        CommandManager.InvalidateRequerySuggested();
    }

    private void ApplyFilter()
    {
        var keep = Selected;
        Rows.Clear();
        var t = SelectedTable;
        if (t != null)
        {
            var f = Filter.Trim();
            var cat = _category;
            var quick = _quickFilter;
            foreach (var r in t.Rows)
                if ((!ChangedOnly || r.IsChanged) && (f.Length == 0 || r.Matches(f))
                    && (cat.Column < 0 || (cat.Column < r.Cells.Count && r.Cells[cat.Column].Value == cat.Value))
                    && (quick == null || t.Table == null || quick.Matches(c => QuickFilters.Cell(t.Table, r.Row, c))))
                    Rows.Add(r);
        }
        Selected = keep != null && Rows.Contains(keep) ? keep : null;
        UpdateSummary();
    }

    private void UpdateSummary()
    {
        var t = SelectedTable;
        if (t?.Table == null) { Summary = t?.Error ?? ""; return; }
        int editable = t.Table.Def.Columns.Count == 0 ? 0 : t.Rows.Sum(r => r.Cells.Count(c => c.IsEditable));
        int total = t.Rows.Count * t.Table.Def.Columns.Count;
        int errors = Tables.Sum(x => x.Rows.Count(r => r.HasError));
        var files = t.Table.Files;
        var source = files.Count > 3 ? $"{files[0]} … ({files.Count} files)" : string.Join(", ", files);
        Summary = $"{Rows.Count} of {t.Rows.Count} entries shown · {editable} of {total} values editable · source: {source}"
                  + (t.ChangedCount > 0 ? $" · {t.ChangedCount} changed, not saved yet" : "")
                  + (errors > 0 ? $" · {errors} with errors (red) – fix them before saving" : "");
    }

    private void UpdateDetails()
    {
        var r = Selected;
        var t = SelectedTable?.Table;
        if (r == null || t == null) { Details = ""; return; }
        var lines = new List<string> { $"{r.Key} – {r.Row.File}, line {r.Row.Line}" };
        if (r.Row.ReadOnlyReason != null) lines.Add("Read-only: " + r.Row.ReadOnlyReason);
        for (int i = 0; i < r.Cells.Count; i++)
        {
            var c = r.Cells[i];
            if (c.Error != null) lines.Add($"{c.Column.Header}: {c.Error}");
            else if (c.IsChanged) lines.Add($"{c.Column.Header}: {c.Original} → {c.Value}" + (c.HasWarning ? "  (" + c.ToolTip + ")" : ""));
            else if (c.ToolTip != null && !c.IsEditable) lines.Add($"{c.Column.Header}: {c.ToolTip}");
        }
        Details = string.Join("\n", lines);
    }

    private void Discard()
    {
        foreach (var t in Tables)
            foreach (var r in t.Rows.Where(r => r.IsChanged))
            {
                foreach (var c in r.Cells) c.Reset();
                r.Refresh();
            }
        foreach (var t in Tables) t.Refresh();
        ApplyFilter();
        UpdateDetails();
        CommandManager.InvalidateRequerySuggested();
    }

    private static string Describe(GameDataTable table, string file)
    {
        var parts = new List<string>();
        foreach (var r in table.ChangedRows.Where(r => r.File == file))
            for (int i = 0; i < r.Cells.Length; i++)
                if (r.Cells[i].IsChanged) parts.Add($"{r.Key} {table.Def.Columns[i].Header}: {Short(r.Cells[i].Original)} → {Short(r.Cells[i].Value)}");
        return parts.Count <= 6 ? string.Join("; ", parts) : string.Join("; ", parts.Take(6)) + $"; and {parts.Count - 6} more";
    }

    private static string Short(string s) => s.Length == 0 ? "(not set)" : s.Length > 40 ? s[..37] + "…" : s;

    private void Save()
    {
        var root = _ctx.Root;
        if (root == null) return;
        var errors = Tables.SelectMany(t => t.Rows.Where(r => r.IsChanged && r.HasError).Select(r => (t, r))).ToList();
        if (errors.Count > 0)
        {
            var (t0, r0) = errors[0];
            ProjectWriter.Warn("Save game data", $"{errors.Count} changed entr{(errors.Count == 1 ? "y has" : "ies have")} errors, e.g. {t0.Def.Title} {r0.Key}:\n{r0.Cells.First(c => c.HasError).Error}\n\nFix or discard them first.");
            SelectedTable = t0;
            Selected = r0;
            return;
        }
        var changed = Tables.Where(t => t.Table != null && t.Rows.Any(r => r.IsChanged)).Select(t => t.Table!).ToList();
        var files = new List<PlannedFile>();
        try
        {
            foreach (var g in changed.SelectMany(t => GameDataCatalog.ChangedFiles(t).Select(f => (Table: t, File: f))).GroupBy(x => x.File))
            {
                var list = g.ToList();
                files.Add(ProjectWriter.ModifyText(root, g.Key, text => list.Aggregate(text, (cur, x) => GameDataCatalog.Write(x.Table, g.Key, cur)),
                    string.Join(" | ", list.Select(x => Describe(x.Table, g.Key)))));
            }
        }
        catch (Exception ex) when (ex is GameDataConflictException or IOException or InvalidOperationException or FormatException)
        {
            ProjectWriter.Warn("Save game data", ex.Message);
            return;
        }
        var notes = new List<string>
        {
            "Only the changed values are replaced; every other character of these files stays as it is.",
            "Build the ROM afterwards to see the changes in-game.",
            "Names were checked against charmap.txt and the game's length limits. How long texts (descriptions) fit in the menus is not checked – look at them in-game.",
        };
        if (changed.Any(t => t.Def.Source == GameDataSource.MapSectionsJson))
            notes.Add("region_map_sections.json is turned into C code by the build (src/data/region_map/region_map_entries.h is generated).");
        if (!ProjectWriter.Apply(root, "Game data", "src", files, [.. notes])) return;
        _ = LoadAsync();
        _ctx.Reload();
    }

    private void AddTrainerClass()
    {
        var root = _ctx.Root;
        var classes = Tables.First(t => t.Def.Id == "trainer_classes");
        if (root == null || classes.Table == null) return;
        if (classes.ChangedCount > 0)
        {
            ProjectWriter.Warn("Add trainer class", "Save or discard the changed trainer classes first.");
            return;
        }
        var table = classes.Table;
        int maxName = table.MaxLength[0];
        var balls = table.Options[2] ?? [];
        string constant = "TRAINER_CLASS_", name = "", money = "", ball = "";
        while (true)
        {
            var dlg = new InputDialog("Add trainer class",
                "A new trainer class gets a constant in include/constants/trainers.h (TRAINER_CLASS_COUNT goes up by one) and an entry in gTrainerClasses (src/battle_main.c). " +
                "Picture and encounter music are chosen per trainer in the Trainers module.",
                ("Constant (TRAINER_CLASS_…)", constant),
                ($"Name shown in battle (at most {maxName} characters, e.g. SCIENTIST)", name),
                ("Prize money (0–255, empty = 5)", money),
                ("Poké Ball (e.g. BALL_ULTRA, empty = Poké Ball)", ball));
            if (dlg.ShowDialog() != true) return;
            (constant, name, money, ball) = (dlg.Values[0], dlg.Values[1], dlg.Values[2], dlg.Values[3]);
            constant = NormalizeConstant(constant);
            ball = ball.Trim().ToUpperInvariant();
            if (ball.Length > 0 && !ball.StartsWith("BALL_", StringComparison.Ordinal)) ball = "BALL_" + ball;
            string? problem;
            string constantsText;
            try { constantsText = GameDataCatalog.ReadText(root, TrainerClasses.ConstantsFile); }
            catch (IOException ex) { ProjectWriter.Warn("Add trainer class", ex.Message); return; }
            problem = TrainerClasses.CheckConstant(constantsText, constant)
                      ?? table.Text.Check(name, maxName, false, false, "The name").Error
                      ?? (money.Length > 0 && !(int.TryParse(money, NumberStyles.None, CultureInfo.InvariantCulture, out var mv) && mv <= 255) ? "Prize money must be a number from 0 to 255." : null)
                      ?? (ball.Length > 0 && balls.Count > 0 && !balls.Contains(ball) ? $"{ball} is not a known ball (e.g. {string.Join(", ", balls.Take(4))})." : null);
            if (problem == null) break;
            ProjectWriter.Warn("Add trainer class", problem);
        }
        int? moneyValue = money.Length > 0 ? int.Parse(money, CultureInfo.InvariantCulture) : null;
        string? ballValue = ball.Length > 0 ? ball : null;
        var files = new List<PlannedFile>();
        int value = -1;
        try
        {
            var tableText = GameDataCatalog.ReadText(root, TrainerClasses.TableFile);
            files.Add(ProjectWriter.ModifyText(root, TrainerClasses.ConstantsFile, c =>
            {
                var r = TrainerClasses.Add(c, tableText, constant, name, moneyValue, ballValue);
                value = r.Value;
                return r.Constants;
            }, $"#define {constant} (new class), TRAINER_CLASS_COUNT + 1"));
            files.Add(ProjectWriter.ModifyText(root, TrainerClasses.TableFile, t => TrainerClasses.AddEntry(t, constant, name, moneyValue, ballValue),
                $"gTrainerClasses: [{constant}] = \"{name}\""));
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or FormatException)
        {
            ProjectWriter.Warn("Add trainer class", ex.Message);
            return;
        }
        var notes = new List<string>
        {
            $"{constant} gets the number {value}; TRAINER_CLASS_COUNT becomes {value + 1}.",
            "Battle music, battle intro and battle background of some classes (gym leaders, champion, Team Aqua/Magma, Elite Four) are chosen in code (src/pokemon.c, src/battle_setup.c, src/battle_bg.c). The new class uses the normal trainer battle music and background.",
            "Battle Frontier and Trainer Hill use their own facility classes; they are not changed.",
            "Build the ROM afterwards. Then choose the class for trainers in the Trainers module.",
        };
        var others = TrainerClasses.OtherClassTables(root);
        if (others.Count > 0)
            notes.Add("Warning: these files also have tables indexed by trainer class and may need an entry for the new class: " + string.Join(", ", others));
        if (!ProjectWriter.Apply(root, "Add trainer class", "src", files, [.. notes])) return;
        _ = LoadAsync();
        _ctx.Reload();
    }

    private static string NormalizeConstant(string s)
    {
        s = string.Join("_", s.Trim().ToUpperInvariant().Split([' ', '-'], StringSplitOptions.RemoveEmptyEntries));
        if (s.Length > 0 && !s.StartsWith("TRAINER_CLASS_", StringComparison.Ordinal)) s = "TRAINER_CLASS_" + s;
        return s;
    }
}
