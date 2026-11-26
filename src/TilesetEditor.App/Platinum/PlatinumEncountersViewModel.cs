using System.IO;
using System.Text;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using TilesetEditor.App.Infrastructure;
using TilesetEditor.App.Shell;
using TilesetEditor.Core.Imaging;
using TilesetEditor.Core.Platinum;
using TilesetEditor.Core.Project;

namespace TilesetEditor.App.Platinum;

/// <summary>
/// Wild Pokémon of a Platinum map: grass (12 slots), the lists that replace grass slots (time of day, swarm, Poké Radar,
/// GBA slot) and surfing / the three rods (5 slots with a level range). Every slot shows its fixed chance; a summary says
/// how often each Pokémon appears. Changes are written with "Save" (the JSON keeps its format).
/// </summary>
public sealed class PlatinumEncountersViewModel : ObservableObject, IToolkitModule
{
    private readonly ProjectContext _ctx;
    private readonly DeferredLoad _reload;
    private readonly ListBox _maps = new() { BorderThickness = new Thickness(0) };
    private readonly TextBox _filter = new();
    private readonly CheckBox _onlyWild = new() { Content = "Only maps with wild Pokémon", IsChecked = true, Margin = new Thickness(0, 4, 0, 4) };
    private readonly StackPanel _detail = new() { Margin = new Thickness(16, 0, 0, 0) };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, Foreground = Brushes.Gray, Margin = new Thickness(0, 0, 0, 6) };
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, BitmapSource?> _icons = new(StringComparer.Ordinal);
    private List<(PlatinumMapHeader Header, string File, string Display, bool Wild)> _all = [];
    private PlatinumJsonFile? _file;
    private PlatinumMapHeader? _header;
    private IReadOnlyList<string> _species = [];
    private bool _loadingMaps;

    public PlatinumEncountersViewModel(ProjectContext ctx)
    {
        _ctx = ctx;
        _reload = new DeferredLoad(Load);
        _ctx.PlatinumChanged += (_, _) => _reload.Request();
        View = BuildView();
        _reload.Attach(View);
    }

    public string Title => "Wild Pokémon";
    public string Description => "Grass, surfing and fishing per map";
    public bool IsAvailable => true;
    public override string ToString() => Title;
    public FrameworkElement View { get; }
    public bool HasUnsavedChanges => _file?.IsChanged == true;
    public string? UnsavedChangesDescription => HasUnsavedChanges ? $"Wild Pokémon of {_header?.FriendlyName} not saved yet." : null;

    private FrameworkElement BuildView()
    {
        var grid = new Grid { Margin = new Thickness(16) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(280) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var left = Ui.FilteredList(_maps, _filter, "Filter maps");
        DockPanel.SetDock(_onlyWild, Dock.Top);
        left.Children.Insert(0, _onlyWild);
        VirtualizingPanel.SetIsVirtualizing(_maps, true);
        _maps.SelectionChanged += (_, _) => { if (!_loadingMaps) Open(); };
        _filter.TextChanged += (_, _) => Filter();
        _onlyWild.Checked += (_, _) => Filter();
        _onlyWild.Unchecked += (_, _) => Filter();
        grid.Children.Add(left);
        var right = new DockPanel();
        var top = new DockPanel { Margin = new Thickness(16, 0, 0, 6) };
        var buttons = Ui.Row(Ui.Button("💾 Save", () => Save(), "Writes the encounters file of this map (shown as a file list first)."), Ui.Button("Discard changes", () => { _file = null; Open(force: true); }));
        DockPanel.SetDock(buttons, Dock.Right);
        top.Children.Add(buttons);
        top.Children.Add(_status);
        DockPanel.SetDock(top, Dock.Top);
        right.Children.Add(top);
        right.Children.Add(new ScrollViewer { Content = _detail, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled });
        Grid.SetColumn(right, 1);
        grid.Children.Add(right);
        _detail.Children.Add(Ui.Hint("Choose a map on the left."));
        return grid;
    }

    private void Load()
    {
        var p = _ctx.Platinum;
        _icons.Clear();
        if (p == null) { _all = []; Filter(); return; }
        _species = p.Species;
        var root = p.Root;
        var places = p.TextBank("location_names").ToDictionary(x => x.Id, x => x.Text, StringComparer.Ordinal);
        var list = new List<(PlatinumMapHeader, string, string, bool)>();
        // Files read in parallel (WSL share); "wild" = a rate above 0 with Pokémon.
        var headers = p.MapHeaders.Select(h => (h, file: PlatinumEncounters.FileOf(root, h))).Where(x => x.file != null).ToList();
        var wild = new bool[headers.Count];
        ParallelIo.For(headers.Count, k =>
        {
            try { wild[k] = PlatinumEncounters.HasAny(PlatinumEncounters.Load(root, headers[k].file!).Root); }
            catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException or FormatException) { }
        });
        for (int k = 0; k < headers.Count; k++)
        {
            var h = headers[k].h;
            var place = h.LabelText != null && places.TryGetValue(h.LabelText, out var n) ? n : null;
            var display = place != null && !h.FriendlyName.StartsWith(place, StringComparison.OrdinalIgnoreCase) ? $"{h.FriendlyName}  ({place})" : h.FriendlyName;
            list.Add((h, headers[k].file!, (wild[k] ? "🌿 " : "") + display, wild[k]));
        }
        _all = list;
        Filter();
    }

    private sealed record MapItem(PlatinumMapHeader Header, string File, string Display)
    {
        public override string ToString() => Display;
    }

    private void Filter()
    {
        var f = _filter.Text.Trim();
        var keep = (_maps.SelectedItem as MapItem)?.File;
        _loadingMaps = true;
        var items = _all.Where(m => (_onlyWild.IsChecked != true || m.Wild) && (f.Length == 0 || m.Display.Contains(f, StringComparison.OrdinalIgnoreCase)))
            .Select(m => new MapItem(m.Header, m.File, m.Display)).ToList();
        _maps.ItemsSource = items;
        _maps.SelectedItem = items.FirstOrDefault(i => i.File == keep);
        _loadingMaps = false;
        _status.Text = $"{_all.Count(m => m.Wild)} of {_all.Count} maps have wild Pokémon.";
    }

    private void Open(bool force = false)
    {
        if (_maps.SelectedItem is not MapItem item || _ctx.Root is not { } root) return;
        if (!force && _file != null && _file.IsChanged && _header != item.Header)
        {
            var r = AppDialog.Show(Application.Current.MainWindow, $"The wild Pokémon of {_header?.FriendlyName} were changed. Save them first?", Title, MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
            if (r == MessageBoxResult.Cancel || (r == MessageBoxResult.Yes && !Save())) { _loadingMaps = true; _maps.SelectedItem = (_maps.ItemsSource as List<MapItem>)?.FirstOrDefault(i => i.Header == _header); _loadingMaps = false; return; }
        }
        try
        {
            _file = PlatinumEncounters.Load(root, item.File);
            _header = item.Header;
            Show();
        }
        catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException or FormatException)
        {
            _detail.Children.Clear();
            _detail.Children.Add(Ui.Hint($"{item.File} could not be read: {ex.Message}"));
        }
    }

    private void Changed()
    {
        UpdateSummary();
        _status.Text = $"{_file?.Path}" + (HasUnsavedChanges ? "  ·  changed – not saved yet" : "");
        _status.Foreground = HasUnsavedChanges ? (Brush)Application.Current.FindResource("AccentDarkBrush") : Brushes.Gray;
    }

    private readonly StackPanel _summary = new();

    private void Show()
    {
        _detail.Children.Clear();
        if (_file == null || _header == null) return;
        var root = _file.Root;
        _detail.Children.Add(Ui.Title(_header.FriendlyName));
        _detail.Children.Add(Ui.Hint("Each slot has a fixed chance (the % on the card). A Pokémon in several slots adds up. Rate = how often an encounter happens per step (0 = none).", 900));
        Ui.Detach(_summary);
        _detail.Children.Add(_summary);

        Section(root, PlatinumEncounters.Land);
        var repl = new Expander { Header = "Replacing grass slots – time of day, swarms, Poké Radar, GBA slot", Margin = new Thickness(0, 6, 0, 0) };
        var rs = new StackPanel();
        foreach (var r in PlatinumEncounters.Replacements)
        {
            if (root[r.Key] is not JsonArray list) continue;
            rs.Children.Add(new TextBlock { Text = r.Title, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 8, 0, 0) });
            rs.Children.Add(Ui.Hint(r.Explanation, 800));
            var wrap = new WrapPanel();
            for (int i = 0; i < list.Count; i++)
            {
                int idx = i;
                wrap.Children.Add(SlotCard($"Replaces slot {r.ReplacesSlots.ElementAtOrDefault(i) + 1}", () => PlatinumEncounters.Str(list[idx]), v => list[idx] = v, null, null));
            }
            rs.Children.Add(wrap);
        }
        repl.Content = rs;
        _detail.Children.Add(repl);
        foreach (var t in PlatinumEncounters.Water) Section(root, t);
        Changed();
    }

    private void Section(JsonObject root, EncounterTable t)
    {
        if (root[t.Key] is not JsonArray slots) return;
        var head = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 14, 0, 4) };
        head.Children.Add(new TextBlock { Text = t.Title, FontSize = 15, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center });
        head.Children.Add(new TextBlock { Text = "   Rate ", VerticalAlignment = VerticalAlignment.Center, Foreground = Brushes.Gray });
        var rate = new TextBox { Text = PlatinumEncounters.Int(root[t.RateKey]).ToString(), Width = 50, ToolTip = "Encounter rate (0 = no encounters here; grass is usually 10–30)." };
        rate.TextChanged += (_, _) => { if (int.TryParse(rate.Text.Trim(), out var v) && v is >= 0 and <= 255) { root[t.RateKey] = v; Changed(); } };
        head.Children.Add(rate);
        _detail.Children.Add(head);
        var wrap = new WrapPanel();
        for (int i = 0; i < slots.Count; i++)
        {
            if (slots[i] is not JsonObject s) continue;
            var chance = i < t.Chances.Length ? t.Chances[i] : 0;
            if (t.HasLevelRange)
                wrap.Children.Add(SlotCard($"Slot {i + 1} · {chance} %", () => PlatinumEncounters.Str(s["species"]), v => s["species"] = v,
                    ("Lv.", () => PlatinumEncounters.Int(s["level_min"]), v => s["level_min"] = v), ("–", () => PlatinumEncounters.Int(s["level_max"]), v => s["level_max"] = v)));
            else
                wrap.Children.Add(SlotCard($"Slot {i + 1} · {chance} %", () => PlatinumEncounters.Str(s["species"]), v => s["species"] = v,
                    ("Lv.", () => PlatinumEncounters.Int(s["level"]), v => s["level"] = v), null));
        }
        _detail.Children.Add(wrap);
    }

    private FrameworkElement SlotCard(string title, Func<string> getSpecies, Action<string> setSpecies,
        (string Label, Func<int> Get, Action<int> Set)? level1, (string Label, Func<int> Get, Action<int> Set)? level2)
    {
        var card = new StackPanel { Width = 190 };
        card.Children.Add(new TextBlock { Text = title, FontSize = 10, Foreground = Brushes.Gray });
        var row = new DockPanel();
        var icon = Ui.Picture();
        icon.Width = 32;
        icon.Height = 32;
        DockPanel.SetDock(icon, Dock.Left);
        row.Children.Add(icon);
        var cb = new ComboBox { ItemsSource = _species, IsEditable = true, IsTextSearchEnabled = true, Text = getSpecies(), FontSize = 11, VerticalAlignment = VerticalAlignment.Center };
        cb.ItemsPanel = new ItemsPanelTemplate(new FrameworkElementFactory(typeof(VirtualizingStackPanel)));
        void SetIcon(string sp) { icon.Source = null; if (_ctx.Root is { } root && sp.StartsWith("SPECIES_", StringComparison.Ordinal) && sp != "SPECIES_NONE") Imaging.LoadAsync(icon, () => Icon(root, sp), _icons, sp); }
        void Apply(string v)
        {
            v = v.Trim().ToUpperInvariant();
            if (v == getSpecies() || (v.Length > 0 && !_species.Contains(v) && v != "SPECIES_NONE")) return;
            setSpecies(v.Length == 0 ? "SPECIES_NONE" : v);
            SetIcon(v);
            Changed();
        }
        cb.LostFocus += (_, _) => Apply(cb.Text);
        cb.SelectionChanged += (_, _) => { if (cb.SelectedItem is string sp) Apply(sp); };
        row.Children.Add(cb);
        card.Children.Add(row);
        if (level1 != null)
        {
            var lv = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(36, 2, 0, 0) };
            foreach (var l in new[] { level1, level2 })
            {
                if (l is not { } x) continue;
                lv.Children.Add(new TextBlock { Text = x.Label + " ", VerticalAlignment = VerticalAlignment.Center, FontSize = 11 });
                var tb = new TextBox { Text = x.Get().ToString(), Width = 38, FontSize = 11 };
                tb.TextChanged += (_, _) => { if (int.TryParse(tb.Text.Trim(), out var v) && v is >= 0 and <= 100 && v != x.Get()) { x.Set(v); Changed(); } };
                lv.Children.Add(tb);
                lv.Children.Add(new TextBlock { Text = " ", Width = 6 });
            }
            card.Children.Add(lv);
        }
        SetIcon(getSpecies());
        return Ui.Card(card, new Thickness(0, 0, 8, 8));
    }

    private static RgbaImage? Icon(string root, string species)
    {
        var path = ProjectPaths.Combine(root, $"{PlatinumProject.SpeciesDir}/{PlatinumNames.SpeciesFolder(species)}/icon.png");
        if (!File.Exists(path)) return null;
        var png = Png.Load(path);
        var img = new RgbaImage(32, 32);
        img.Blit(png.Rgba, 0, 0, 0, 0, 32, Math.Min(32, png.Height));
        if (png.Indices != null) for (int y = 0; y < 32; y++) for (int x = 0; x < 32; x++) if (png.Indices[y * png.Width + x] == 0) img[x, y] = Rgba32.Transparent;
        return img;
    }

    private void UpdateSummary()
    {
        _summary.Children.Clear();
        if (_file == null) return;
        var chances = PlatinumEncounters.GrassChances(_file.Root);
        if (chances.Count > 0 && PlatinumEncounters.Int(_file.Root[PlatinumEncounters.Land.RateKey]) > 0)
            _summary.Children.Add(new TextBlock
            {
                TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0),
                Text = "In the grass: " + string.Join(" · ", chances.Select(c => $"{PlatinumScriptFlow.Pretty(c.Species)} {c.Percent} % (Lv. {(c.MinLevel == c.MaxLevel ? c.MinLevel.ToString() : $"{c.MinLevel}–{c.MaxLevel}")})")),
            });
        foreach (var pr in PlatinumEncounters.Check(_file.Root, _species.ToHashSet()))
            _summary.Children.Add(new TextBlock { Text = "⚠ " + pr, Foreground = (Brush)Application.Current.FindResource("WarnTextBrush"), TextWrapping = TextWrapping.Wrap });
    }

    private bool Save()
    {
        if (_file == null || _ctx.Root is not { } root || !_file.IsChanged) return true;
        var current = File.ReadAllText(ProjectPaths.Combine(root, _file.Path), Encoding.UTF8);
        if (current != _file.OriginalText) { ProjectWriter.Warn(Title, $"{_file.Path} was changed outside PokéPat – discard your changes to reload it."); return false; }
        var problems = PlatinumEncounters.Check(_file.Root, _species.ToHashSet());
        if (problems.Count > 0 && AppDialog.Show(Application.Current.MainWindow, "Check these first:\n• " + string.Join("\n• ", problems) + "\n\nSave anyway?", Title, MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return false;
        var path = _file.Path;
        var bytes = new UTF8Encoding(false).GetBytes(_file.Serialize());
        if (!PlatinumModules.Write(_ctx, "Wild Pokémon", [new PlatinumFileChange(path, bytes, $"wild Pokémon of {_header?.FriendlyName}")], "Only the changed values change; the file keeps its format.", "Build the ROM afterwards."))
            return false;
        _file = null;
        return true;
    }
}
