using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using TilesetEditor.App.Infrastructure;
using TilesetEditor.App.Shell;
using TilesetEditor.Core.Platinum;
using TilesetEditor.Core.Project;

namespace TilesetEditor.App.Platinum;

/// <summary>A row of the flag list.</summary>
public sealed class VarFlagRowVm(PlatinumVarFlag flag, int uses)
{
    public PlatinumVarFlag Flag { get; } = flag;
    public string Name => Flag.Name;
    public string Hex => Flag.Hex;
    public string Group => Flag.Group;
    public int Uses { get; } = uses;
    public string UsesText => Uses < 0 ? "…" : Uses == 0 ? (Flag.Group == "Trainers defeated" ? "by number" : "unused") : $"{Uses}×";
}

/// <summary>A row of the trainer flag list.</summary>
public sealed class TrainerFlagRowVm(PlatinumTrainerFlag t)
{
    public string Trainer => t.Trainer;
    public string Number => t.Number.ToString();
    public string Flag => t.Flag ?? "(beyond the last flag)";
    public string State => t.Missing ? "✗ no own flag" : "✓";
    public bool Missing => t.Missing;
}

/// <summary>
/// Flags and variables of a Platinum project (generated/vars_flags.txt): every flag and variable by group, with its number
/// and every place that sets, clears or checks it; free flags for own events (and naming them); and the defeated-trainer
/// flags, which the game finds by the trainer's number – every trainer needs its own place there.
/// </summary>
public sealed class PlatinumFlagsViewModel : ObservableObject, IToolkitModule
{
    private readonly DeferredLoad _reload;
    private readonly ProjectContext _ctx;
    private readonly ListBox _groups = new() { BorderThickness = new Thickness(0) };
    private readonly TextBox _search = new() { Margin = new Thickness(0, 0, 0, 6), ToolTip = "Search a flag or variable (part of the name)" };
    private readonly DataGrid _grid = new() { AutoGenerateColumns = false, IsReadOnly = true, HeadersVisibility = DataGridHeadersVisibility.Column, SelectionMode = DataGridSelectionMode.Single };
    private readonly StackPanel _detail = new();
    private readonly TextBlock _status = new() { Foreground = Brushes.Gray, Margin = new Thickness(0, 0, 0, 6), TextWrapping = TextWrapping.Wrap };
    private readonly DataGrid _trainerGrid = new() { AutoGenerateColumns = false, IsReadOnly = true, HeadersVisibility = DataGridHeadersVisibility.Column };
    private readonly StackPanel _trainerHead = new();
    private List<PlatinumVarFlag> _all = [];
    private Dictionary<string, List<PlatinumVarFlagUse>>? _uses;
    private CancellationTokenSource? _scan;

    private readonly Action<string> _navigate;

    public PlatinumFlagsViewModel(ProjectContext ctx, Action<string> navigate)
    {
        _ctx = ctx;
        _navigate = navigate;
        _reload = new DeferredLoad(Load);
        _ctx.PlatinumChanged += (_, _) => _reload.Request();
        View = BuildView();
        _reload.Attach(View);
    }

    public string Title => "Flags & Variables";
    public string Description => "Story flags, free flags, trainer flags";
    public bool IsAvailable => true;
    public override string ToString() => Title;
    public FrameworkElement View { get; }
    public bool HasUnsavedChanges => false;
    public string? UnsavedChangesDescription => null;

    private FrameworkElement BuildView()
    {
        var tabs = new TabControl { Margin = new Thickness(8) };

        var grid = new Grid { Margin = new Thickness(8) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(230) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(420) });
        _groups.SelectionChanged += (_, _) => Filter();
        var left = new DockPanel();
        var actions = new StackPanel { Margin = new Thickness(0, 8, 0, 0) };
        actions.Children.Add(Ui.Button("+ New flag…", () => NewOne(true), "A flag for your own event: a free, unused place gets your name (its number stays, nothing else moves)."));
        actions.Children.Add(Ui.Button("+ New variable…", () => NewOne(false), "A variable for your own event (e.g. the state of a quest)."));
        actions.Children.Add(Ui.Button("🔍 Check: never used", CheckUnused, "Lists named flags and variables that no script, event or code uses, and flags that are set but never checked."));
        foreach (var b in actions.Children.OfType<Button>()) { b.HorizontalAlignment = HorizontalAlignment.Stretch; b.Margin = new Thickness(0, 0, 0, 4); }
        DockPanel.SetDock(actions, Dock.Bottom);
        left.Children.Add(actions);
        left.Children.Add(_groups);
        grid.Children.Add(left);

        var middle = new DockPanel { Margin = new Thickness(8, 0, 8, 0) };
        System.Windows.Automation.AutomationProperties.SetName(_search, "Search flags");
        _search.TextChanged += (_, _) => Filter();
        DockPanel.SetDock(_search, Dock.Top);
        middle.Children.Add(_search);
        DockPanel.SetDock(_status, Dock.Top);
        middle.Children.Add(_status);
        _grid.Columns.Add(new DataGridTextColumn { Header = "Name", Binding = new Binding(nameof(VarFlagRowVm.Name)), Width = new DataGridLength(1, DataGridLengthUnitType.Star), FontFamily = new FontFamily("Consolas") });
        _grid.Columns.Add(new DataGridTextColumn { Header = "Number", Binding = new Binding(nameof(VarFlagRowVm.Hex)), Width = 70, FontFamily = new FontFamily("Consolas") });
        _grid.Columns.Add(new DataGridTextColumn { Header = "Used", Binding = new Binding(nameof(VarFlagRowVm.UsesText)), Width = 80 });
        _grid.Columns.Add(new DataGridTextColumn { Header = "Group", Binding = new Binding(nameof(VarFlagRowVm.Group)), Width = 150 });
        VirtualizingPanel.SetIsVirtualizing(_grid, true);
        _grid.SelectionChanged += (_, _) => ShowDetail();
        middle.Children.Add(_grid);
        Grid.SetColumn(middle, 1);
        grid.Children.Add(middle);

        var right = new Border
        {
            BorderBrush = (Brush)Application.Current.FindResource("BorderBrush"), BorderThickness = new Thickness(1), Padding = new Thickness(10),
            Background = (Brush)Application.Current.FindResource("SurfaceBrush"),
            Child = new ScrollViewer { Content = _detail, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled },
        };
        Grid.SetColumn(right, 2);
        grid.Children.Add(right);
        tabs.Items.Add(new TabItem { Header = "Flags & variables", Content = grid });

        var t = new DockPanel { Margin = new Thickness(8) };
        DockPanel.SetDock(_trainerHead, Dock.Top);
        t.Children.Add(_trainerHead);
        _trainerGrid.Columns.Add(new DataGridTextColumn { Header = "No.", Binding = new Binding(nameof(TrainerFlagRowVm.Number)), Width = 50 });
        _trainerGrid.Columns.Add(new DataGridTextColumn { Header = "Trainer", Binding = new Binding(nameof(TrainerFlagRowVm.Trainer)), Width = new DataGridLength(1, DataGridLengthUnitType.Star), FontFamily = new FontFamily("Consolas") });
        _trainerGrid.Columns.Add(new DataGridTextColumn { Header = "Defeated flag", Binding = new Binding(nameof(TrainerFlagRowVm.Flag)), Width = new DataGridLength(1, DataGridLengthUnitType.Star), FontFamily = new FontFamily("Consolas") });
        _trainerGrid.Columns.Add(new DataGridTextColumn { Header = "", Binding = new Binding(nameof(TrainerFlagRowVm.State)), Width = 120 });
        VirtualizingPanel.SetIsVirtualizing(_trainerGrid, true);
        t.Children.Add(_trainerGrid);
        tabs.Items.Add(new TabItem { Header = "Trainer flags", Content = t });
        return tabs;
    }

    private void Load()
    {
        _scan?.Cancel();
        _uses = null;
        var root = _ctx.Root;
        if (root == null || _ctx.Platinum == null || !PlatinumFlags.Exists(root))
        {
            _all = [];
            _groups.ItemsSource = null;
            _grid.ItemsSource = null;
            _status.Text = root == null ? "" : $"{PlatinumFlags.File} was not found – older pokeplatinum versions keep flags elsewhere.";
            return;
        }
        try { _all = PlatinumFlags.Load(root); }
        catch (IOException ex) { _status.Text = ex.Message; return; }
        FillGroups();
        Filter();
        FillTrainers();
        Scan(root);
    }

    private sealed record GroupItem(string Key, string Text)
    {
        public override string ToString() => Text;
    }

    private void FillGroups()
    {
        var keep = (_groups.SelectedItem as GroupItem)?.Key;
        var items = new List<GroupItem> { new("", $"All ({_all.Count})"), new("free", $"Free for own events ({_all.Count(f => f.IsFree || f.IsUnknown)})") };
        if (_uses != null)
        {
            items.Add(new GroupItem("never", $"⚠ Never used ({PlatinumFlags.NeverUsed(_all, _uses).Count})"));
            items.Add(new GroupItem("setonly", $"⚠ Set, never checked ({PlatinumFlags.SetButNeverChecked(_all, _uses).Count})"));
        }
        foreach (var (g, _) in PlatinumFlags.Groups)
        {
            int n = _all.Count(f => f.Group == g);
            if (n > 0) items.Add(new GroupItem(g, $"{g} ({n})"));
        }
        _groups.ItemsSource = items;
        _groups.SelectedItem = items.FirstOrDefault(i => i.Key == keep) ?? items[0];
    }

    private async void Scan(string root)
    {
        var cts = new CancellationTokenSource();
        _scan = cts;
        var progress = new Progress<double>(p => { if (!cts.IsCancellationRequested) _status.Text = $"Looking where each flag is used … {p:P0}"; });
        try
        {
            var uses = await Task.Run(() => PlatinumFlags.FindUses(root, cts.Token, progress), cts.Token);
            if (cts.IsCancellationRequested) return;
            _uses = uses;
            FillGroups();
            Filter();
            ShowDetail();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { _status.Text = "The search for uses failed: " + ex.Message; }
    }

    private int UseCount(string name) => _uses == null ? -1 : _uses.TryGetValue(name, out var u) ? u.Count(x => !x.File.Equals(PlatinumFlags.File, StringComparison.Ordinal)) : 0;

    private void Filter()
    {
        var g = (_groups.SelectedItem as GroupItem)?.Key ?? "";
        var s = _search.Text.Trim();
        var source = g switch
        {
            "never" when _uses != null => PlatinumFlags.NeverUsed(_all, _uses),
            "setonly" when _uses != null => PlatinumFlags.SetButNeverChecked(_all, _uses),
            "free" => _all.Where(f => f.IsFree || f.IsUnknown).ToList(),
            "" => _all,
            _ => _all.Where(f => f.Group == g).ToList(),
        };
        var rows = source.Where(f => s.Length == 0 || f.Name.Contains(s, StringComparison.OrdinalIgnoreCase) || f.Hex.Contains(s, StringComparison.OrdinalIgnoreCase))
            .Select(f => new VarFlagRowVm(f, UseCount(f.Name))).ToList();
        if (g == "free" && _uses != null) rows = rows.Where(r => r.Uses == 0).ToList();
        var keep = (_grid.SelectedItem as VarFlagRowVm)?.Name;
        _grid.ItemsSource = rows;
        if (keep != null && rows.FirstOrDefault(r => r.Name == keep) is { } again) _grid.SelectedItem = again;
        var desc = g switch
        {
            "free" => "Free and unused: take one for your own event (the event builder offers FLAG_UNUSED_… first) or give it a name.",
            "never" => "Named, but no script, event or code uses them – left over, or meant for something that was never built. Safe to reuse after checking.",
            "setonly" => "Set somewhere, but nothing ever checks them – maybe a check is missing, or the flag is only kept for later.",
            _ => PlatinumFlags.Groups.FirstOrDefault(x => x.Group == g).Description,
        };
        _status.Text = (_uses == null ? "Looking where each flag is used … " : "") + $"{rows.Count} shown." + (desc != null ? " " + desc : "");
    }

    private void ShowDetail()
    {
        _detail.Children.Clear();
        if (_grid.SelectedItem is not VarFlagRowVm row)
        {
            _detail.Children.Add(Ui.Hint("Choose a flag or variable. Flags are on/off (SetFlag, ClearFlag, GoToIfSet); variables hold a number (SetVar, GoToIfEq …).", 380));
            return;
        }
        var f = row.Flag;
        _detail.Children.Add(new TextBox { Text = f.Name, IsReadOnly = true, FontFamily = new FontFamily("Consolas"), FontSize = 14, FontWeight = FontWeights.SemiBold, BorderThickness = new Thickness(0), Background = Brushes.Transparent, TextWrapping = TextWrapping.Wrap });
        _detail.Children.Add(new TextBlock { Text = $"{(f.IsFlag ? "Flag" : "Variable")} {f.Hex} ({f.Value}) · {f.Group}", Foreground = Brushes.Gray, Margin = new Thickness(0, 0, 0, 6) });
        var desc = PlatinumFlags.Groups.FirstOrDefault(x => x.Group == f.Group).Description;
        if (desc != null) _detail.Children.Add(Ui.Hint(desc, 380));
        if (_uses == null) { _detail.Children.Add(Ui.Hint("Still looking where it is used …")); return; }
        var uses = _uses.TryGetValue(f.Name, out var u) ? u.Where(x => x.File != PlatinumFlags.File).ToList() : [];
        if ((f.IsFree || f.IsUnknown) && uses.Count == 0)
        {
            _detail.Children.Add(new TextBlock { Text = "✓ Not used anywhere – free for your own event.", Foreground = (Brush)Application.Current.FindResource("SuccessBrush"), Margin = new Thickness(0, 6, 0, 4) });
            _detail.Children.Add(Ui.Row(Ui.Button("Give it a name…", () => Rename(f), "E.g. FLAG_GOT_MY_GIFT – the number stays, so nothing else changes.")));
        }
        if (f.Group == "Trainers defeated")
            _detail.Children.Add(Ui.Hint("The game finds this flag by the trainer's number (TRAINER_DEFEATED_FLAGS_START + number), not by its name. See the tab \"Trainer flags\".", 380));
        _detail.Children.Add(new TextBlock { Text = uses.Count == 0 ? "Not named in scripts, events or code." : $"Used {uses.Count}×" + (uses.Count >= 400 ? " (first 400)" : ""), FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 10, 0, 4) });
        foreach (var group in uses.GroupBy(x => x.File).Take(80))
        {
            var place = group.Key.StartsWith("res/field/maps/", StringComparison.Ordinal) ? PlatinumScriptFlow.Pretty(group.Key.Split('/')[3].ToUpperInvariant()) : group.Key;
            var card = new StackPanel { Margin = new Thickness(0, 0, 0, 6) };
            card.Children.Add(new TextBlock { Text = place, FontWeight = FontWeights.SemiBold, ToolTip = group.Key });
            foreach (var use in group.Take(8))
                card.Children.Add(new TextBlock { Text = $"{use.How,-12} line {use.Line}:  {use.Text}", FontFamily = new FontFamily("Consolas"), FontSize = 11, TextWrapping = TextWrapping.Wrap, Foreground = Brushes.Gray });
            _detail.Children.Add(card);
        }
    }

    private void CheckUnused()
    {
        if (_uses == null) { ProjectWriter.Warn(Title, "The search for uses is still running – try again in a moment."); return; }
        var never = PlatinumFlags.NeverUsed(_all, _uses);
        var setOnly = PlatinumFlags.SetButNeverChecked(_all, _uses);
        _search.Text = "";
        _groups.SelectedItem = ((List<GroupItem>)_groups.ItemsSource).FirstOrDefault(i => i.Key == "never");
        ProjectWriter.Warn(Title, $"{never.Count} named flag(s)/variable(s) are never used anywhere, and {setOnly.Count} flag(s) are set but never checked.\n\n" +
            "Both lists are on the left (⚠). Trainer, hidden-item, daily and map-local flags are left out – the game uses those by number.");
    }

    private void NewOne(bool flag)
    {
        var root = _ctx.Root;
        if (root == null || _all.Count == 0) return;
        if (_uses == null) { ProjectWriter.Warn(Title, "The search for uses is still running – try again in a moment."); return; }
        var prefix = flag ? "FLAG_" : "VAR_";
        var dlg = new InputDialog(flag ? "New flag" : "New variable",
            flag ? "Name of the flag, e.g. FLAG_GOT_REYAS_GIFT. A free, unused flag gets this name – its number stays, so nothing else moves." :
                   "Name of the variable, e.g. VAR_REYA_QUEST_STATE. A free, unused variable gets this name.", ("Name", prefix));
        if (dlg.ShowDialog() != true) return;
        try
        {
            var text = File.ReadAllText(ProjectPaths.Combine(root, PlatinumFlags.File));
            var free = PlatinumFlags.NextFree(text, flag, n => UseCount(n) > 0)
                       ?? throw new InvalidDataException($"No free {(flag ? "flag" : "variable")} is left (FLAG_UNUSED_/FLAG_UNK_ that nothing uses).");
            var name = dlg.Values[0].Trim().ToUpperInvariant();
            if (!name.StartsWith(prefix, StringComparison.Ordinal)) name = prefix + name;
            var changed = PlatinumFlags.Rename(text, free, name);
            PlatinumModules.Write(_ctx, flag ? "New flag" : "New variable", [new PlatinumFileChange(PlatinumFlags.File, PlatinumFlags.Encode(changed), $"{free.Name} ({free.Hex}) → {name}")],
                $"{name} uses the free place {free.Hex}; no other flag or variable changes its number.",
                "Use it in the event builder (blocks \"Set flag\", \"Only once\", \"Set variable\") after reloading.");
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException) { ProjectWriter.Warn(Title, ex.Message); }
    }

    private void Rename(PlatinumVarFlag f)
    {
        var root = _ctx.Root;
        if (root == null) return;
        var dlg = new InputDialog("Give it a name", $"New name for {f.Name} ({f.Hex}). Its number stays the same.", ("Name", f.IsFlag ? "FLAG_" : "VAR_"));
        if (dlg.ShowDialog() != true) return;
        try
        {
            var text = File.ReadAllText(ProjectPaths.Combine(root, PlatinumFlags.File));
            var changed = PlatinumFlags.Rename(text, f, dlg.Values[0].Trim().ToUpperInvariant());
            PlatinumModules.Write(_ctx, "Name a flag", [new PlatinumFileChange(PlatinumFlags.File, PlatinumFlags.Encode(changed), $"{f.Name} → {dlg.Values[0].Trim().ToUpperInvariant()}")],
                "Only the name changes; the number stays, so saves and other flags are not affected.", "Scripts can use the new name after the next build.");
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException) { ProjectWriter.Warn(Title, ex.Message); }
    }

    // ------------------------------------------------------------------ trainer flags

    private void FillTrainers()
    {
        _trainerHead.Children.Clear();
        var root = _ctx.Root;
        if (root == null) return;
        List<PlatinumTrainerFlag> list;
        try { list = PlatinumFlags.TrainerFlags(root, _all); }
        catch (IOException ex) { _trainerHead.Children.Add(Ui.Hint(ex.Message)); return; }
        int missing = list.Count(t => t.Missing);
        _trainerHead.Children.Add(Ui.Hint(
            "When the player beats a trainer, the game sets flag TRAINER_DEFEATED_FLAGS_START + the trainer's number (its place in generated/trainers.txt). " +
            "So the defeated flags must be in the same order as the trainers – a trainer added at the end needs the next flag after the last trainer flag.", 900));
        _trainerHead.Children.Add(new TextBlock
        {
            Text = missing == 0 ? $"✓ All {list.Count} trainers have their own flag." : $"✗ {missing} trainer(s) have no own flag – beating them would set another flag.",
            Foreground = missing == 0 ? (Brush)Application.Current.FindResource("SuccessBrush") : (Brush)Application.Current.FindResource("AccentDarkBrush"),
            FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 6, 0, 6),
        });
        var row = Ui.Row(Ui.Button("+ New trainer… (Trainers)", () => _navigate("Trainers"), "New trainers are made in the Trainers module – each gets its defeated flag automatically."));
        if (missing > 0) row.Children.Insert(0, Ui.Button("🛠 Give them their flags", () => FixTrainers(list), "Names the free flags at their places FLAG_DEFEATED_TRAINER_… – no other flag changes its number."));
        _trainerHead.Children.Add(row);
        _trainerGrid.ItemsSource = list.Select(t => new TrainerFlagRowVm(t)).OrderByDescending(r => r.Missing).ToList();
    }

    private void FixTrainers(List<PlatinumTrainerFlag> list)
    {
        var root = _ctx.Root;
        if (root == null) return;
        if (_uses == null) { ProjectWriter.Warn(Title, "Wait a moment until the search for uses is done (Flags & variables tab)."); return; }
        try
        {
            var text = File.ReadAllText(ProjectPaths.Combine(root, PlatinumFlags.File));
            var changed = PlatinumFlags.FixTrainerFlags(text, list, name => UseCount(name) > 0);
            PlatinumModules.Write(_ctx, "Trainer flags", [new PlatinumFileChange(PlatinumFlags.File, PlatinumFlags.Encode(changed), "defeated flags for the new trainers")],
                "Free flags at the trainers' places get the trainers' names; all numbers stay.", "Build the ROM afterwards.");
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException) { ProjectWriter.Warn(Title, ex.Message); }
    }
}
