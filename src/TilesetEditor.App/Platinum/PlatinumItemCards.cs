using System.Collections.Specialized;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using TilesetEditor.App.Infrastructure;
using TilesetEditor.App.Modules.GameData;
using TilesetEditor.App.Shell;
using TilesetEditor.Core.Imaging;
using TilesetEditor.Core.Platinum;

namespace TilesetEditor.App.Platinum;

/// <summary>
/// Items of a Platinum project as cards (icon, name, price, pocket, effect, description) instead of table rows – the same
/// values as the table, so search, filters, checks and "Save changes" work as before. Icons are changed from the card.
/// </summary>
public sealed class PlatinumItemCards
{
    private const int Page = 120;
    private readonly GameDataViewModel _vm;
    private readonly ProjectContext _ctx;
    private readonly WrapPanel _cards = new();
    private readonly TextBlock _more = new() { Margin = new Thickness(0, 4, 0, 8), Foreground = Brushes.Gray };
    private readonly Button _showAll = new() { Content = "Show all", Margin = new Thickness(8, 0, 0, 0) };
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, BitmapSource?> _icons = new(StringComparer.Ordinal);
    private bool _hiddenChange;
    private bool _all, _pending;

    public FrameworkElement View { get; }

    public PlatinumItemCards(GameDataViewModel vm, ProjectContext ctx)
    {
        _vm = vm;
        _ctx = ctx;
        _showAll.Click += (_, _) => { _all = true; Rebuild(); };
        vm.Rows.CollectionChanged += (_, _) => Schedule();
        _ctx.PlatinumChanged += (_, _) => _icons.Clear();
        var panel = new StackPanel();
        var bar = new StackPanel { Orientation = Orientation.Horizontal };
        bar.Children.Add(_more);
        bar.Children.Add(_showAll);
        panel.Children.Add(bar);
        panel.Children.Add(_cards);
        View = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        // Cards are only built while they are on screen (background loading of the game data must not build them).
        View.IsVisibleChanged += (_, e) => { if (e.NewValue is true && _hiddenChange) { _hiddenChange = false; Schedule(); } };
        Schedule();
    }

    /// <summary>The filter fills Rows one by one: rebuild once afterwards.</summary>
    private void Schedule()
    {
        if (_pending) return;
        _pending = true;
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, () =>
        {
            _pending = false;
            if (!View.IsVisible) { _hiddenChange = true; return; }
            _all = false;
            Rebuild();
        });
    }

    private void Rebuild()
    {
        _cards.Children.Clear();
        var table = _vm.SelectedTable?.Table;
        if (table == null || _vm.SelectedTable?.Def.Id != "pl_items") return;
        var rows = _vm.Rows.ToList();
        var shown = _all ? rows : rows.Take(Page).ToList();
        _more.Text = rows.Count > shown.Count ? $"{shown.Count} of {rows.Count} items shown – search or filter to find others, or" : $"{rows.Count} items";
        _showAll.Visibility = rows.Count > shown.Count ? Visibility.Visible : Visibility.Collapsed;
        int Col(string header) => table.Def.Columns.ToList().FindIndex(c => c.Header == header);
        foreach (var row in shown) _cards.Children.Add(Card(row, Col));
    }

    /// <summary>The item's icon, read in the background (cached).</summary>
    private void Icon(Image target, string itemFile)
    {
        if (_ctx.Root is not { } root) return;
        Imaging.LoadAsync(target, () =>
        {
            try { return PlatinumItemIcons.Of(root, itemFile) is { } icon ? PlatinumItemIcons.Render(root, icon) : null; }
            catch (System.Text.Json.JsonException) { return null; }
        }, _icons, itemFile);
    }

    private FrameworkElement Card(GameDataRowVm row, Func<string, int> col)
    {
        var grid = new Grid { Width = 380, DataContext = row };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(76) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var left = new StackPanel();
        var img = Ui.Picture(2);
        Icon(img, row.Row.File);
        left.Children.Add(new Border { Child = img, Width = 68, Height = 68, Background = (Brush)Application.Current.FindResource("PanelBrush") });
        var change = new Button { Content = "Icon…", FontSize = 11, Margin = new Thickness(0, 4, 0, 0), ToolTip = "New icon (PNG 32×32, up to 15 colours)" };
        change.Click += (_, _) => ChangeIcon(row, img);
        left.Children.Add(change);
        left.Children.Add(new TextBlock { Text = row.Key.Replace("ITEM_", ""), FontSize = 9, Foreground = Brushes.Gray, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0) });
        grid.Children.Add(left);

        var fields = new StackPanel();
        Grid.SetColumn(fields, 1);
        FrameworkElement? Editor(string header, bool combo = false, bool multiline = false, double width = double.NaN)
        {
            int i = col(header);
            if (i < 0) return null;
            var cell = row.Cells[i];
            Control c;
            if (combo)
            {
                var cb = new ComboBox { ItemsSource = cell.Options, IsEditable = true, IsTextSearchEnabled = true, FontSize = 11 };
                cb.SetBinding(ComboBox.TextProperty, new Binding($"Cells[{i}].Value") { UpdateSourceTrigger = UpdateSourceTrigger.LostFocus });
                c = cb;
            }
            else
            {
                var tb = new TextBox { FontSize = multiline ? 11 : 12, TextWrapping = multiline ? TextWrapping.Wrap : TextWrapping.NoWrap, AcceptsReturn = false, Width = width, HorizontalAlignment = HorizontalAlignment.Left };
                tb.SetBinding(TextBox.TextProperty, new Binding($"Cells[{i}].Value") { UpdateSourceTrigger = UpdateSourceTrigger.LostFocus });
                if (multiline) tb.MinHeight = 46;
                c = tb;
            }
            c.IsEnabled = cell.IsEditable;
            c.Margin = new Thickness(0, 1, 0, 1);
            void Mark()
            {
                c.BorderBrush = cell.HasError ? Brushes.Firebrick : cell.HasWarning ? Brushes.DarkOrange : cell.IsChanged ? Brushes.SeaGreen : (Brush)Application.Current.FindResource("BorderBrush");
                c.ToolTip = cell.ToolTip ?? header;
            }
            ((INotifyPropertyChanged)cell).PropertyChanged += (_, _) => Mark();
            Mark();
            return c;
        }
        void Labeled(string label, FrameworkElement? e)
        {
            if (e == null) return;
            var d = new DockPanel();
            var l = new TextBlock { Text = label, Width = 70, FontSize = 11, VerticalAlignment = VerticalAlignment.Center, Foreground = (Brush)Application.Current.FindResource("MutedBrush") };
            DockPanel.SetDock(l, Dock.Left);
            d.Children.Add(l);
            d.Children.Add(e);
            fields.Children.Add(d);
        }
        if (Editor("Name") is { } name) { ((TextBox)name).FontWeight = FontWeights.SemiBold; fields.Children.Add(name); }
        var priceRow = new StackPanel { Orientation = Orientation.Horizontal };
        if (Editor("Price", width: 70) is { } price) { priceRow.Children.Add(new TextBlock { Text = "Price ₽ ", FontSize = 11, VerticalAlignment = VerticalAlignment.Center }); priceRow.Children.Add(price); }
        if (Editor("Fling power", width: 40) is { } fling) { priceRow.Children.Add(new TextBlock { Text = "   Fling ", FontSize = 11, VerticalAlignment = VerticalAlignment.Center }); priceRow.Children.Add(fling); }
        fields.Children.Add(priceRow);
        Labeled("Pocket", Editor("Pocket", combo: true));
        Labeled("Hold effect", Editor("Hold effect", combo: true));
        Labeled("Plural", Editor("Plural"));
        if (Editor("Description", multiline: true) is { } desc) fields.Children.Add(desc);
        grid.Children.Add(fields);

        return new Border
        {
            Child = grid, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(4), Padding = new Thickness(8), Margin = new Thickness(0, 0, 8, 8),
            BorderBrush = (Brush)Application.Current.FindResource("BorderBrush"), Background = (Brush)Application.Current.FindResource("SurfaceBrush"),
        };
    }

    private void ChangeIcon(GameDataRowVm row, Image img)
    {
        var root = _ctx.Root;
        if (root == null) return;
        var icon = PlatinumItemIcons.Of(root, row.Row.File);
        if (icon == null) { ProjectWriter.Warn("Item icon", "This item has no icon entry in its JSON file."); return; }
        var dlg = new Microsoft.Win32.OpenFileDialog { Filter = "PNG pictures|*.png", Title = $"New icon for {row.Key} (32×32)" };
        if (dlg.ShowDialog(Application.Current.MainWindow) != true) return;
        try
        {
            var picture = Png.Load(dlg.FileName).Rgba;
            var users = PlatinumItemIcons.Users(root, icon.Sprite);
            List<PlatinumFileChange> files;
            if (users.Count <= 1 || users.All(u => u.Palette == icon.Palette))
            {
                var others = users.Where(u => u.File != row.Row.File).Select(u => Path.GetFileNameWithoutExtension(u.File)).ToList();
                var r = others.Count == 0 ? MessageBoxResult.Yes : AppDialog.Show(Application.Current.MainWindow,
                    $"{others.Count} other item(s) use the same icon ({string.Join(", ", others.Take(6))}{(others.Count > 6 ? " …" : "")}).\n\nYes – change it for all of them.\nNo – give {row.Key} its own icon.",
                    "Item icon", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
                if (r == MessageBoxResult.Cancel) return;
                files = r == MessageBoxResult.Yes ? PlatinumItemIcons.PlanReplaceShared(root, icon, picture) : PlatinumItemIcons.PlanOwnIcon(root, icon, picture);
            }
            else
            {
                // The picture is shared with different palettes (e.g. all TMs): only an own icon keeps the others right.
                files = PlatinumItemIcons.PlanOwnIcon(root, icon, picture);
            }
            if (PlatinumModules.Write(_ctx, "Item icon", files, "Icons are 32×32 with 16 colours; transparent or the top-left colour is the background.", "Build the ROM afterwards."))
            {
                _icons.Clear();
                Icon(img, row.Row.File);
            }
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or PngFormatException or System.Text.Json.JsonException) { ProjectWriter.Warn("Item icon", ex.Message); }
    }
}
