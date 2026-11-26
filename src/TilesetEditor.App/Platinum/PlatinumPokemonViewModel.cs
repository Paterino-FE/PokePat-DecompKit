using System.Collections.ObjectModel;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using TilesetEditor.App.Infrastructure;
using TilesetEditor.App.Shell;
using TilesetEditor.Core.Imaging;
using TilesetEditor.Core.Platinum;
using TilesetEditor.Core.Project;

namespace TilesetEditor.App.Platinum;

/// <summary>A species in the list: constant, name from the Pokédex data, menu icon.</summary>
public sealed class SpeciesItemVm(string constant, string name, BitmapSource? icon)
{
    public string Constant { get; } = constant;
    public string Name { get; } = name;
    public BitmapSource? Icon { get; } = icon;
    public string Display => $"{Name}  ·  {Constant}";
    public override string ToString() => Display;
}

/// <summary>One row of the level-up learnset grid.</summary>
public sealed class LearnsetRowVm : ObservableObject
{
    private string _level = "1", _move = "MOVE_TACKLE";
    public string Level { get => _level; set => Set(ref _level, value?.Trim() ?? ""); }
    public string Move { get => _move; set => Set(ref _move, value?.Trim().ToUpperInvariant() ?? ""); }
}

/// <summary>
/// Pokémon of a Platinum project: battle sprites (front/back, normal/shiny, female variants) with replacing from a PNG,
/// level-up learnset and evolutions. Stats and texts are in Game data.
/// </summary>
public sealed class PlatinumPokemonViewModel : ObservableObject, IToolkitModule
{
    private readonly DeferredLoad _reload;
    private readonly ProjectContext _ctx;
    private readonly Action<string> _navigate;
    private readonly ListBox _list = new() { BorderThickness = new Thickness(0) };
    private readonly TextBox _filter = new();
    private readonly StackPanel _detail = new() { Margin = new Thickness(16, 0, 0, 0) };
    private List<SpeciesItemVm> _all = [];
    private string? _learnsetSpecies;
    private string? _learnsetOriginal;

    public PlatinumPokemonViewModel(ProjectContext ctx, Action<string> navigate)
    {
        _ctx = ctx;
        _navigate = navigate;
        _reload = new DeferredLoad(() => HasUnsavedChanges ? null : LoadAsync(), prefetch: true);
        _ctx.PlatinumChanged += (_, _) => _reload.Request();
        Learnset.CollectionChanged += (_, _) => MarkLearnsetDirty();
        View = BuildView();
        _reload.Attach(View);
    }

    public string Title => "Pokémon";
    public string Description => "Sprites, shiny colours, learnsets";
    public bool IsAvailable => true;
    public override string ToString() => Title;
    public FrameworkElement View { get; }

    public ObservableCollection<LearnsetRowVm> Learnset { get; } = [];
    private bool _learnsetDirty;
    public bool HasUnsavedChanges => _learnsetDirty;
    public string? UnsavedChangesDescription => _learnsetDirty ? $"Pokémon: learnset of {_learnsetSpecies} not saved yet." : null;

    private FrameworkElement BuildView()
    {
        var grid = new Grid { Margin = new Thickness(16) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(280) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var itemTemplate = new DataTemplate();
        var row = new FrameworkElementFactory(typeof(StackPanel));
        row.SetValue(StackPanel.OrientationProperty, Orientation.Horizontal);
        var icon = new FrameworkElementFactory(typeof(Image));
        icon.SetBinding(Image.SourceProperty, new Binding(nameof(SpeciesItemVm.Icon)));
        icon.SetValue(FrameworkElement.WidthProperty, 32.0);
        icon.SetValue(FrameworkElement.HeightProperty, 32.0);
        icon.SetValue(RenderOptions.BitmapScalingModeProperty, BitmapScalingMode.NearestNeighbor);
        row.AppendChild(icon);
        var text = new FrameworkElementFactory(typeof(TextBlock));
        text.SetBinding(TextBlock.TextProperty, new Binding(nameof(SpeciesItemVm.Display)));
        text.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        text.SetValue(FrameworkElement.MarginProperty, new Thickness(6, 0, 0, 0));
        row.AppendChild(text);
        itemTemplate.VisualTree = row;
        _list.ItemTemplate = itemTemplate;
        VirtualizingPanel.SetIsVirtualizing(_list, true);
        _list.SelectionChanged += (_, _) => { if (_list.SelectedItem is SpeciesItemVm s) Select(s); };
        _filter.TextChanged += (_, _) => ApplyFilter();
        grid.Children.Add(Ui.FilteredList(_list, _filter, "Filter Pokémon"));
        var scroll = new ScrollViewer { Content = _detail, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        Grid.SetColumn(scroll, 1);
        grid.Children.Add(scroll);
        _detail.Children.Add(Ui.Hint("Choose a Pokémon on the left."));
        return grid;
    }

    private async Task LoadAsync()
    {
        var p = _ctx.Platinum;
        if (p == null) { _all = []; ApplyFilter(); return; }
        var root = p.Root;
        var items = await Task.Run(() => p.Species.AsParallel().AsOrdered().WithDegreeOfParallelism(ParallelIo.Degree).Select(c =>
        {
            var folder = ProjectPaths.Combine(root, $"{PlatinumProject.SpeciesDir}/{PlatinumNames.SpeciesFolder(c)}");
            string name = PlatinumNames.Pretty(c, "SPECIES_");
            RgbaImage? iconImg = null;
            try
            {
                var data = Path.Combine(folder, "data.json");
                if (File.Exists(data))
                {
                    using var doc = JsonDocument.Parse(File.ReadAllText(data));
                    if (doc.RootElement.TryGetProperty("pokedex_data", out var pd) && pd.TryGetProperty("en", out var en) && en.TryGetProperty("name", out var n))
                        name = n.GetString() ?? name;
                }
                var iconPath = Path.Combine(folder, "icon.png");
                if (File.Exists(iconPath))
                {
                    var png = Png.Load(iconPath);
                    iconImg = new RgbaImage(32, 32);
                    iconImg.Blit(png.Rgba, 0, 0, 0, 0, 32, Math.Min(32, png.Height));
                    // Index 0 is the transparent colour of the icon.
                    if (png.IsIndexed && png.Indices != null)
                        for (int y = 0; y < 32; y++) for (int x = 0; x < 32; x++) if (png.Indices[y * png.Width + x] == 0) iconImg[x, y] = Rgba32.Transparent;
                }
            }
            catch (Exception ex) when (ex is IOException or JsonException or PngFormatException) { }
            return (c, name, iconImg);
        }).ToList());
        if (_ctx.Platinum != p) return;
        _all = items.Select(x => new SpeciesItemVm(x.c, x.name, x.iconImg != null ? Imaging.ToBitmap(x.iconImg) : null)).ToList();
        ApplyFilter();
    }

    private void ApplyFilter()
    {
        var f = _filter.Text.Trim();
        var keep = _list.SelectedItem as SpeciesItemVm;
        _list.ItemsSource = f.Length == 0 ? _all : _all.Where(s => s.Display.Contains(f, StringComparison.OrdinalIgnoreCase)).ToList();
        if (keep != null) _list.SelectedItem = _all.FirstOrDefault(s => s.Constant == keep.Constant);
    }

    private void Select(SpeciesItemVm s)
    {
        if (_learnsetDirty && _learnsetSpecies != s.Constant)
        {
            var r = AppDialog.Show(Application.Current.MainWindow, $"The learnset of {_learnsetSpecies} was changed. Save it first?", "Pokémon",
                MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
            if (r == MessageBoxResult.Cancel) { _list.SelectedItem = _all.FirstOrDefault(x => x.Constant == _learnsetSpecies); return; }
            if (r == MessageBoxResult.Yes && !SaveLearnset()) { _list.SelectedItem = _all.FirstOrDefault(x => x.Constant == _learnsetSpecies); return; }
            _learnsetDirty = false;
        }
        ShowDetail(s);
    }

    private void ShowDetail(SpeciesItemVm s)
    {
        _detail.Children.Clear();
        var p = _ctx.Platinum;
        if (p == null) return;
        var root = p.Root;
        var folder = ProjectPaths.Combine(root, $"{PlatinumProject.SpeciesDir}/{PlatinumNames.SpeciesFolder(s.Constant)}");
        _detail.Children.Add(Ui.Title($"{s.Name}  ({s.Constant})"));

        // ---- sprites
        PlatinumSpriteSet set;
        try { set = PlatinumSpriteSet.Load(root, s.Constant); }
        catch (Exception ex) when (ex is IOException or FormatException or InvalidDataException)
        {
            _detail.Children.Add(Ui.Hint("The sprites could not be read: " + ex.Message));
            return;
        }
        var sprites = new WrapPanel();
        void AddSprite(string label, PlatinumSpritePart part)
        {
            var box = new StackPanel { Margin = new Thickness(0, 0, 14, 6) };
            box.Children.Add(new TextBlock { Text = label, FontWeight = FontWeights.SemiBold });
            var pics = new StackPanel { Orientation = Orientation.Horizontal };
            foreach (var shiny in new[] { false, true })
                for (int frame = 0; frame < 2; frame++)
                {
                    var img = Ui.Picture();
                    var frameImg = set.Frame(part, frame, shiny);
                    if (frameImg != null) img.Source = Imaging.ToBitmap(frameImg);
                    img.ToolTip = $"{(shiny ? "Shiny" : "Normal")}, frame {frame + 1}";
                    var border = new Border
                    {
                        Child = img, Background = Imaging.Checkerboard(8), Margin = new Thickness(0, 2, 4, 2),
                        BorderBrush = (Brush)Application.Current.FindResource("BorderBrush"), BorderThickness = new Thickness(1),
                    };
                    pics.Children.Add(border);
                }
            box.Children.Add(pics);
            box.Children.Add(new TextBlock { Text = "normal ×2 · shiny ×2", FontSize = 10, Foreground = (Brush)Application.Current.FindResource("MutedBrush") });
            sprites.Children.Add(box);
        }
        AddSprite("Front", PlatinumSpritePart.MaleFront);
        AddSprite("Back", PlatinumSpritePart.MaleBack);
        if (set.HasFemaleDifference(false)) AddSprite("Front (female)", PlatinumSpritePart.FemaleFront);
        if (set.HasFemaleDifference(true)) AddSprite("Back (female)", PlatinumSpritePart.FemaleBack);
        _detail.Children.Add(Ui.Card(sprites));
        _detail.Children.Add(Ui.Row(
            Ui.Button("Replace front…", () => ReplaceSprite(s, set, back: false), "PNG, 80×80 (one frame) or 160×80 (two frames side by side). Up to 15 colours; transparent or the top-left colour = background."),
            Ui.Button("Replace back…", () => ReplaceSprite(s, set, back: true), "PNG, 80×80 or 160×80. Front and back share one 16-colour palette."),
            Ui.Button("Show folder", () => Ui.OpenFolder(Path.Combine(folder, "male_front.png")))));

        // ---- data summary
        string dataPath = Path.Combine(folder, "data.json");
        string? dataText = File.Exists(dataPath) ? File.ReadAllText(dataPath, Encoding.UTF8) : null;
        if (dataText == null) { _detail.Children.Add(Ui.Hint("data.json is missing.")); return; }
        try
        {
            using var doc = JsonDocument.Parse(dataText);
            var r = doc.RootElement;
            string Stat(string n) => r.TryGetProperty("base_stats", out var b) && b.TryGetProperty(n, out var v) ? v.GetRawText() : "?";
            string List(string n) => r.TryGetProperty(n, out var a) && a.ValueKind == JsonValueKind.Array ? string.Join(" / ", a.EnumerateArray().Select(x => PlatinumNames.Pretty(x.GetString() ?? "", x.GetString()?.Split('_')[0] + "_"))) : "?";
            var summary = $"Types: {List("types")}    Abilities: {List("abilities")}\n" +
                          $"HP {Stat("hp")} · Atk {Stat("attack")} · Def {Stat("defense")} · SpA {Stat("special_attack")} · SpD {Stat("special_defense")} · Spe {Stat("speed")}";
            var evo = PlatinumLearnsets.Evolutions(dataText);
            summary += "\nEvolves: " + (evo.Count == 0 ? "–" : string.Join(";  ", evo));
            _detail.Children.Add(Ui.Card(new TextBlock { Text = summary, TextWrapping = TextWrapping.Wrap }));
            _detail.Children.Add(Ui.Row(Ui.Button("Edit stats, types, abilities… in Game data →", () => _navigate("Game data"))));
        }
        catch (JsonException ex) { _detail.Children.Add(Ui.Hint("data.json could not be read: " + ex.Message)); return; }

        // ---- learnset
        _detail.Children.Add(Ui.Title("Level-up moves"));
        _suppressDirty = true;
        Learnset.Clear();
        foreach (var e in PlatinumLearnsets.Read(dataText)) Learnset.Add(Track(new LearnsetRowVm { Level = e.Level.ToString(), Move = e.Move }));
        _suppressDirty = false;
        _learnsetSpecies = s.Constant;
        _learnsetOriginal = dataText;
        _learnsetDirty = false;
        var dg = new DataGrid
        {
            ItemsSource = Learnset, AutoGenerateColumns = false, CanUserAddRows = false, CanUserDeleteRows = true, HeadersVisibility = DataGridHeadersVisibility.Column,
            MaxHeight = 380, Width = 420, HorizontalAlignment = HorizontalAlignment.Left,
        };
        dg.Columns.Add(new DataGridTextColumn { Header = "Level", Binding = new Binding(nameof(LearnsetRowVm.Level)) { UpdateSourceTrigger = UpdateSourceTrigger.LostFocus }, Width = 70 });
        dg.Columns.Add(PlatinumGrid.ComboColumn("Move", nameof(LearnsetRowVm.Move), p.Moves, 300));
        _detail.Children.Add(dg);
        _detail.Children.Add(Ui.Row(
            Ui.Button("+ Add move", () => { var l = Learnset.LastOrDefault()?.Level ?? "1"; Learnset.Add(Track(new LearnsetRowVm { Level = l })); }),
            Ui.Button("− Remove selected", () => { if (dg.SelectedItem is LearnsetRowVm row) Learnset.Remove(row); }),
            Ui.Button("Sort by level", () =>
            {
                var sorted = Learnset.OrderBy(x => int.TryParse(x.Level, out var v) ? v : 999).ToList();
                Learnset.Clear();
                foreach (var x in sorted) Learnset.Add(x);
            }),
            Ui.Button("💾 Save learnset", () => SaveLearnset()),
            Ui.Button("Discard", () => { _learnsetDirty = false; ShowDetail(s); })));
        _detail.Children.Add(Ui.Hint("At most 20 moves are recommended (the game reads the list up to its end marker). A Pokémon knows the last 4 moves learned up to its level."));
    }

    private LearnsetRowVm Track(LearnsetRowVm row)
    {
        row.PropertyChanged += (_, _) => MarkLearnsetDirty();
        return row;
    }

    private bool _suppressDirty;
    private void MarkLearnsetDirty() { if (!_suppressDirty) _learnsetDirty = true; }

    private bool SaveLearnset()
    {
        var p = _ctx.Platinum;
        if (p == null || _learnsetSpecies == null || _learnsetOriginal == null) return false;
        var entries = new List<PlatinumLearnsets.Entry>();
        foreach (var row in Learnset)
        {
            if (!int.TryParse(row.Level, out var lvl) || lvl < 1 || lvl > 100) { ProjectWriter.Warn("Learnset", $"\"{row.Level}\" is not a level from 1 to 100."); return false; }
            if (!p.Moves.Contains(row.Move) || row.Move == "MOVE_NONE") { ProjectWriter.Warn("Learnset", $"\"{row.Move}\" is not a move."); return false; }
            entries.Add(new PlatinumLearnsets.Entry(lvl, row.Move));
        }
        var rel = $"{PlatinumProject.SpeciesDir}/{PlatinumNames.SpeciesFolder(_learnsetSpecies)}/data.json";
        string current;
        try { current = File.ReadAllText(ProjectPaths.Combine(p.Root, rel), Encoding.UTF8); }
        catch (IOException ex) { ProjectWriter.Warn("Learnset", ex.Message); return false; }
        if (current != _learnsetOriginal) { ProjectWriter.Warn("Learnset", $"{rel} was changed outside PokéPat. Choose the Pokémon again to reload it."); return false; }
        string updated;
        try { updated = PlatinumLearnsets.Write(current, entries); }
        catch (Exception ex) when (ex is InvalidOperationException or JsonException) { ProjectWriter.Warn("Learnset", ex.Message); return false; }
        if (updated == current) { _learnsetDirty = false; return true; }
        var ok = PlatinumModules.Write(_ctx, "Learnset", [new PlatinumFileChange(rel, new UTF8Encoding(false).GetBytes(updated), $"learnset.by_level: {entries.Count} moves")],
            "Only learnset.by_level is replaced; the rest of data.json stays as it is.", "Build the ROM afterwards.");
        if (ok) { _learnsetDirty = false; _learnsetOriginal = updated; }
        return ok;
    }

    private void ReplaceSprite(SpeciesItemVm s, PlatinumSpriteSet set, bool back)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog { Filter = "PNG pictures|*.png", Title = $"New {(back ? "back" : "front")} sprite for {s.Name} (80×80 or 160×80)" };
        if (dlg.ShowDialog(Application.Current.MainWindow) != true) return;
        bool femaleToo = false;
        if (set.HasFemaleDifference(back))
            femaleToo = AppDialog.Show(Application.Current.MainWindow, $"{s.Name} has its own female sprite. Use the new picture for the female sprite too?",
                "Replace sprite", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;
        try
        {
            var png = Png.Load(dlg.FileName);
            var (files, notes) = PlatinumSpriteImport.Replace(set, back, png.Rgba, femaleToo);
            if (PlatinumModules.Write(_ctx, "Replace sprite", files, [.. notes, "The .png.key files stay as they are (they only scramble the data in the ROM).", "Build the ROM afterwards."]))
                ShowDetail(s);
        }
        catch (Exception ex) when (ex is IOException or PngFormatException or InvalidDataException)
        {
            ProjectWriter.Warn("Replace sprite", ex.Message);
        }
    }
}

/// <summary>DataGrid columns shared by the Platinum modules.</summary>
internal static class PlatinumGrid
{
    /// <summary>A column edited with a searchable, editable combo box of constants.</summary>
    public static DataGridTemplateColumn ComboColumn(string header, string path, IReadOnlyList<string> items, double width)
    {
        var show = new FrameworkElementFactory(typeof(TextBlock));
        show.SetBinding(TextBlock.TextProperty, new Binding(path));
        show.SetValue(FrameworkElement.MarginProperty, new Thickness(4, 0, 4, 0));
        show.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        var edit = new FrameworkElementFactory(typeof(ComboBox));
        edit.SetValue(ItemsControl.ItemsSourceProperty, items);
        edit.SetValue(ComboBox.IsEditableProperty, true);
        edit.SetValue(ItemsControl.IsTextSearchEnabledProperty, true);
        edit.SetBinding(ComboBox.TextProperty, new Binding(path) { UpdateSourceTrigger = UpdateSourceTrigger.LostFocus });
        return new DataGridTemplateColumn
        {
            Header = header,
            Width = width,
            CellTemplate = new DataTemplate { VisualTree = show },
            CellEditingTemplate = new DataTemplate { VisualTree = edit },
            SortMemberPath = path,
        };
    }
}
