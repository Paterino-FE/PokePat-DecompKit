using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using TilesetEditor.App.Infrastructure;
using TilesetEditor.App.Shell;
using TilesetEditor.Core;
using TilesetEditor.Core.Platinum;
using TilesetEditor.Core.Project;

namespace TilesetEditor.App.Platinum;

/// <summary>
/// Shops of a Platinum project: the common list of every Poké Mart (with the badges from which an item is sold) and the
/// special shops (city specialties, Veilstone store, decorations, seal market). Items are added, sorted and removed; the
/// file keeps everything else as it is.
/// </summary>
public sealed class PlatinumShopsViewModel : ObservableObject, IToolkitModule
{
    private readonly ProjectContext _ctx;
    private readonly DeferredLoad _reload;
    private readonly ListBox _shops = new() { BorderThickness = new Thickness(0), HorizontalContentAlignment = HorizontalAlignment.Stretch };
    private readonly StackPanel _detail = new() { Margin = new Thickness(16, 0, 0, 0) };
    private readonly TextBlock _status = new() { Foreground = Brushes.Gray, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 6) };
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, BitmapSource?> _icons = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int?> _prices = new(StringComparer.Ordinal);
    private string _original = "";
    private List<MartList> _lists = [];
    private readonly HashSet<string> _changed = new(StringComparer.Ordinal);
    private Dictionary<string, List<string>> _usedBy = [];
    private IReadOnlyList<string> _items = [], _goods = [], _seals = [];

    public PlatinumShopsViewModel(ProjectContext ctx)
    {
        _ctx = ctx;
        _reload = new DeferredLoad(() => { if (!HasUnsavedChanges) Load(); });
        _ctx.PlatinumChanged += (_, _) => _reload.Request();
        View = BuildView();
        _reload.Attach(View);
    }

    public string Title => "Shops";
    public string Description => "Poké Mart items, city specialties";
    public bool IsAvailable => true;
    public override string ToString() => Title;
    public FrameworkElement View { get; }
    public bool HasUnsavedChanges => _changed.Count > 0;
    public string? UnsavedChangesDescription => HasUnsavedChanges ? $"Shops: {_changed.Count} list(s) changed, not saved yet." : null;

    private sealed record ShopItem(MartList List, string Display, string Sub)
    {
        public override string ToString() => Display;
    }

    private FrameworkElement BuildView()
    {
        var grid = new Grid { Margin = new Thickness(16) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(300) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var left = new DockPanel();
        var head = new StackPanel();
        head.Children.Add(Ui.Title("Shops"));
        head.Children.Add(_status);
        head.Children.Add(Ui.Row(Ui.Button("💾 Save", () => Save(), "Writes include/data/mart_items.h (shown as a file list first)."), Ui.Button("Discard", () => { _changed.Clear(); Load(); })));
        DockPanel.SetDock(head, Dock.Top);
        left.Children.Add(head);
        var stack = new FrameworkElementFactory(typeof(StackPanel));
        stack.SetValue(FrameworkElement.MarginProperty, new Thickness(0, 2, 0, 2));
        var a = new FrameworkElementFactory(typeof(TextBlock));
        a.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding(nameof(ShopItem.Display)));
        a.SetValue(TextBlock.FontWeightProperty, FontWeights.SemiBold);
        stack.AppendChild(a);
        var b = new FrameworkElementFactory(typeof(TextBlock));
        b.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding(nameof(ShopItem.Sub)));
        b.SetValue(TextBlock.FontSizeProperty, 10.0);
        b.SetValue(TextBlock.ForegroundProperty, Brushes.Gray);
        b.SetValue(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis);
        stack.AppendChild(b);
        _shops.ItemTemplate = new DataTemplate { VisualTree = stack };
        _shops.SelectionChanged += (_, _) => Show();
        left.Children.Add(_shops);
        grid.Children.Add(left);
        var right = new ScrollViewer { Content = _detail, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        Grid.SetColumn(right, 1);
        grid.Children.Add(right);
        _detail.Children.Add(Ui.Hint("Choose a shop on the left."));
        return grid;
    }

    private void Load()
    {
        var p = _ctx.Platinum;
        _changed.Clear();
        _icons.Clear();
        _prices.Clear();
        if (p == null || !PlatinumMarts.Exists(p.Root)) { _lists = []; _shops.ItemsSource = null; _status.Text = p == null ? "" : $"{PlatinumMarts.File} was not found."; return; }
        try
        {
            _original = File.ReadAllText(ProjectPaths.Combine(p.Root, PlatinumMarts.File), Encoding.UTF8);
            _lists = PlatinumMarts.Parse(_original);
            _usedBy = PlatinumMarts.UsedBy(p.Root, _lists);
        }
        catch (IOException ex) { _status.Text = ex.Message; return; }
        _items = p.Items.Where(i => i != "ITEM_NONE").ToList();
        _goods = p.ConstantList("goods").Where(g => g.StartsWith("UG_GOOD_", StringComparison.Ordinal)).ToList();
        _seals = p.ConstantList("seals").Where(g => g.Contains("_SEAL_", StringComparison.Ordinal)).ToList();
        Fill();
    }

    private void Fill()
    {
        var keep = (_shops.SelectedItem as ShopItem)?.List.Name;
        var items = _lists.Select(l => new ShopItem(l, PlatinumMarts.Title(l) + (_changed.Contains(l.Name) ? "  •" : ""),
            $"{l.Entries.Count} {(l.Kind == MartKind.Items ? "items" : l.Kind == MartKind.Goods ? "decorations" : "seals")}" +
            (_usedBy.TryGetValue(l.Name, out var maps) ? " · " + string.Join(", ", maps.Select(m => PlatinumScriptFlow.Pretty(m.ToUpperInvariant()))) : l.Common ? " · every Poké Mart" : ""))).ToList();
        _shops.ItemsSource = items;
        _shops.SelectedItem = items.FirstOrDefault(i => i.List.Name == keep) ?? items.FirstOrDefault();
        _status.Text = $"{_lists.Count} shop lists in {PlatinumMarts.File}" + (HasUnsavedChanges ? $" · {_changed.Count} changed – not saved yet" : "");
    }

    private IReadOnlyList<string> Choices(MartList l) => l.Kind switch { MartKind.Goods => _goods, MartKind.Seals => _seals, _ => _items };

    private void Show()
    {
        _detail.Children.Clear();
        if (_shops.SelectedItem is not ShopItem { List: var l }) return;
        _detail.Children.Add(Ui.Title(PlatinumMarts.Title(l)));
        _detail.Children.Add(Ui.Hint(l.Common
            ? "What the left counter of every Poké Mart sells. Each item has a badge level: it is sold once the player has that many badges (0 → start, 1–2, 3–4, 5–6, 7, 8)."
            : $"{l.Name}" + (l.Id != null ? $" · {l.Id}" : "") + (_usedBy.TryGetValue(l.Name, out var maps) ? " · used on " + string.Join(", ", maps) : ""), 760));
        foreach (var pr in PlatinumMarts.Check(l, Choices(l).ToHashSet()))
            _detail.Children.Add(new TextBlock { Text = "⚠ " + pr, Foreground = (Brush)Application.Current.FindResource("WarnTextBrush"), TextWrapping = TextWrapping.Wrap });

        // Add an item (after the selected row, or at the end).
        var add = new ComboBox { ItemsSource = Choices(l), IsEditable = true, IsTextSearchEnabled = true, Width = 320, Margin = new Thickness(0, 0, 8, 0) };
        add.ItemsPanel = new ItemsPanelTemplate(new FrameworkElementFactory(typeof(VirtualizingStackPanel)));
        var level = new ComboBox { ItemsSource = PlatinumMarts.Levels, SelectedIndex = 0, Width = 190, Margin = new Thickness(0, 0, 8, 0), Visibility = l.Common ? Visibility.Visible : Visibility.Collapsed };
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 8) };
        row.Children.Add(add);
        row.Children.Add(level);
        row.Children.Add(Ui.Button("+ Add", () =>
        {
            var item = add.Text.Trim().ToUpperInvariant();
            if (!Choices(l).Contains(item)) { ProjectWriter.Warn(Title, $"{item} is not in the list of this project."); return; }
            l.Entries = [.. l.Entries, (item, level.SelectedIndex + 1)];
            Changed(l);
        }));
        _detail.Children.Add(row);

        for (int i = 0; i < l.Entries.Count; i++)
        {
            int index = i;
            var (item, lv) = l.Entries[i];
            var line = new DockPanel { Margin = new Thickness(0, 1, 0, 1) };
            var icon = Ui.Picture();
            icon.Width = 32;
            icon.Height = 32;
            if (l.Kind == MartKind.Items && _ctx.Root is { } root)
                Imaging.LoadAsync(icon, () => PlatinumItemIcons.Of(root, ItemFile(item)) is { } ic ? PlatinumItemIcons.Render(root, ic) : null, _icons, item);
            DockPanel.SetDock(icon, Dock.Left);
            line.Children.Add(icon);
            var buttons = new StackPanel { Orientation = Orientation.Horizontal };
            DockPanel.SetDock(buttons, Dock.Right);
            Button Small(string t, string tip, Action act) { var bt = new Button { Content = t, Padding = new Thickness(6, 0, 6, 0), Margin = new Thickness(2, 0, 0, 0), ToolTip = tip }; bt.Click += (_, _) => act(); return bt; }
            buttons.Children.Add(Small("▲", "Earlier in the list", () => Swap(l, index, index - 1)));
            buttons.Children.Add(Small("▼", "Later in the list", () => Swap(l, index, index + 1)));
            buttons.Children.Add(Small("✕", "Remove", () => { var e = l.Entries.ToList(); e.RemoveAt(index); l.Entries = e; Changed(l); }));
            line.Children.Add(buttons);
            if (l.Common)
            {
                var lvBox = new ComboBox { ItemsSource = PlatinumMarts.Levels, SelectedIndex = Math.Clamp(lv, 1, 6) - 1, Width = 190, Margin = new Thickness(6, 0, 0, 0) };
                lvBox.SelectionChanged += (_, _) => { var e = l.Entries.ToList(); e[index] = (item, lvBox.SelectedIndex + 1); l.Entries = e; Changed(l, rebuild: false); };
                DockPanel.SetDock(lvBox, Dock.Right);
                line.Children.Add(lvBox);
            }
            var price = l.Kind == MartKind.Items ? Price(item) : null;
            line.Children.Add(new TextBlock
            {
                Text = PlatinumScriptFlow.Pretty(item.Replace("UG_GOOD_", "")) + (price is { } pv ? $"   ₽{pv}" : ""), VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(6, 0, 0, 0), ToolTip = item,
            });
            _detail.Children.Add(line);
        }
    }

    private static string ItemFile(string item) => $"res/items/data/{item[5..].ToLowerInvariant()}.json";

    private int? Price(string item)
    {
        if (_prices.TryGetValue(item, out var p)) return p;
        int? price = null;
        try
        {
            if (_ctx.Root is { } root && item.StartsWith("ITEM_", StringComparison.Ordinal) && File.Exists(ProjectPaths.Combine(root, ItemFile(item))))
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(ProjectPaths.Combine(root, ItemFile(item))));
                if (doc.RootElement.TryGetProperty("price", out var pr) && pr.TryGetInt32(out var v)) price = v;
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException) { }
        return _prices[item] = price;
    }

    private void Swap(MartList l, int i, int j)
    {
        if (j < 0 || j >= l.Entries.Count) return;
        var e = l.Entries.ToList();
        (e[i], e[j]) = (e[j], e[i]);
        l.Entries = e;
        Changed(l);
    }

    private void Changed(MartList l, bool rebuild = true)
    {
        _changed.Add(l.Name);
        Fill();
        if (rebuild) Show();
    }

    private bool Save()
    {
        if (!HasUnsavedChanges || _ctx.Root is not { } root) return true;
        var current = File.ReadAllText(ProjectPaths.Combine(root, PlatinumMarts.File), Encoding.UTF8);
        if (current != _original) { ProjectWriter.Warn(Title, $"{PlatinumMarts.File} was changed outside PokéPat – \"Discard\" reloads it."); return false; }
        var changed = _lists.Where(l => _changed.Contains(l.Name)).ToList();
        var problems = changed.SelectMany(l => PlatinumMarts.Check(l, Choices(l).ToHashSet())).ToList();
        if (problems.Count > 0 && AppDialog.Show(Application.Current.MainWindow, "Check these first:\n• " + string.Join("\n• ", problems) + "\n\nSave anyway?", Title, MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return false;
        var text = PlatinumMarts.Write(current, changed);
        if (!PlatinumModules.Write(_ctx, "Shops", [new PlatinumFileChange(PlatinumMarts.File, new UTF8Encoding(false).GetBytes(text), string.Join(", ", changed.Select(PlatinumMarts.Title)))],
                "Only the changed shop lists are written; the rest of the file stays as it is.", "Build the ROM afterwards.")) return false;
        _changed.Clear();
        return true;
    }
}
