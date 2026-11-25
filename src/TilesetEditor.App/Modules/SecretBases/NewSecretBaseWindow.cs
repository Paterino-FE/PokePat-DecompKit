using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using TilesetEditor.App.Infrastructure;
using TilesetEditor.Core.Imaging;
using TilesetEditor.Core.Maps;
using TilesetEditor.Core.Project;
using TilesetEditor.Core.SecretBases;

namespace TilesetEditor.App.Modules.SecretBases;

public sealed record NewSecretBaseResult(string MapFolder, int X, int Y, int Elevation, SecretBaseCatalog.FreeId Id, string? Warning);

/// <summary>Pick a map tile, the base type and the interior; the next free id of that interior is used.</summary>
public sealed class NewSecretBaseWindow : Window
{
    private readonly SecretBaseData _data;
    private readonly Func<string, (RgbaImage Image, MapGrid Grid)?> _renderMap;
    private readonly ComboBox _map = new() { IsTextSearchEnabled = true, MinWidth = 220 };
    private readonly ComboBox _zoom = new() { Width = 70 };
    private readonly ComboBox _type = new();
    private readonly ComboBox _interior = new();
    private readonly TextBox _elevation = new() { Text = "0", Width = 60, HorizontalAlignment = HorizontalAlignment.Left };
    private readonly TextBlock _tileInfo = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 8) };
    private readonly TextBlock _idInfo = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 8), FontWeight = FontWeights.SemiBold };
    private readonly Image _image = new() { Stretch = Stretch.None, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top, Cursor = Cursors.Cross };
    private readonly ScrollViewer _scroller = new() { HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    private readonly Button _ok = new() { Content = "Create…", IsDefault = true, MinWidth = 90, Margin = new Thickness(0, 0, 8, 0), FontWeight = FontWeights.SemiBold };
    private (RgbaImage Image, MapGrid Grid)? _current;
    private (int X, int Y)? _tile;

    public NewSecretBaseResult? Result { get; private set; }

    public NewSecretBaseWindow(ProjectInfo project, SecretBaseData data, string? initialMap, Func<string, (RgbaImage Image, MapGrid Grid)?> renderMap)
    {
        _ = project;
        _data = data;
        _renderMap = renderMap;
        Title = "New secret base location";
        Width = 1150;
        Height = 760;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = (Brush)Application.Current.Resources["PanelBrush"];

        var interiors = data.Groups.Where(g => g.MapFolder != null).Select(g => g.MapFolder!).ToHashSet();
        var maps = data.MapFolders.Where(m => !interiors.Contains(m)).ToList();
        _map.ItemsSource = maps;
        _zoom.ItemsSource = new[] { "1×", "2×", "3×", "4×" };
        _zoom.SelectedIndex = 1;
        _type.ItemsSource = data.Types.OrderBy(t => t.Value).Select(t => t.Key).ToList();

        var side = new StackPanel { Width = 340, Margin = new Thickness(10, 0, 0, 0) };
        void Label(string text) => side.Children.Add(new TextBlock { Text = text, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 6, 0, 2) });
        Label("Map");
        side.Children.Add(_map);
        var zoomRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0) };
        zoomRow.Children.Add(new TextBlock { Text = "Zoom:", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) });
        zoomRow.Children.Add(_zoom);
        side.Children.Add(zoomRow);
        side.Children.Add(new TextBlock
        {
            Style = (Style)Application.Current.Resources["Hint"], TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0),
            Text = "Click the tile the player faces when using Secret Power (cave indent, tree, shrub). " +
                   "Green frames = secret base entrance metatiles on this map, orange = existing secret bases, red = your choice.",
        });
        Label("Selected tile");
        side.Children.Add(_tileInfo);
        Label("Type (decides which interiors fit)");
        side.Children.Add(_type);
        Label("Interior");
        side.Children.Add(_interior);
        Label("Elevation");
        side.Children.Add(_elevation);
        side.Children.Add(new TextBlock { Text = "0 = the entrance works from any elevation.", Style = (Style)Application.Current.Resources["Hint"] });
        side.Children.Add(_idInfo);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(_ok);
        buttons.Children.Add(new Button { Content = "Cancel", IsCancel = true, MinWidth = 80 });
        side.Children.Add(buttons);

        RenderOptions.SetBitmapScalingMode(_image, BitmapScalingMode.NearestNeighbor);
        _scroller.Content = _image;
        var dock = new DockPanel { Margin = new Thickness(10) };
        var sideScroll = new ScrollViewer { Content = side, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        DockPanel.SetDock(sideScroll, Dock.Right);
        dock.Children.Add(sideScroll);
        dock.Children.Add(new Border { BorderThickness = new Thickness(1), BorderBrush = (Brush)Application.Current.Resources["BorderBrush"], Background = new SolidColorBrush(Color.FromRgb(0x1F, 0x29, 0x37)), Child = _scroller });
        Content = dock;

        _map.SelectionChanged += (_, _) => LoadMap();
        _zoom.SelectionChanged += (_, _) => ApplyZoom();
        _type.SelectionChanged += (_, _) => FillInteriors();
        _interior.SelectionChanged += (_, _) => UpdateId();
        _elevation.TextChanged += (_, _) => UpdateId();
        _image.MouseLeftButtonDown += OnClick;
        _scroller.PreviewMouseWheel += (_, e) =>
        {
            if ((Keyboard.Modifiers & ModifierKeys.Control) == 0) return;
            _zoom.SelectedIndex = Math.Clamp(_zoom.SelectedIndex + (e.Delta > 0 ? 1 : -1), 0, 3);
            e.Handled = true;
        };
        _ok.Click += (_, _) => Confirm();

        _type.SelectedIndex = 0;
        _map.SelectedItem = initialMap != null && maps.Contains(initialMap) ? initialMap : maps.FirstOrDefault(m => data.Locations.Any(l => l.MapFolder == m)) ?? maps.FirstOrDefault();
        UpdateId();
    }

    private void ApplyZoom()
    {
        double z = _zoom.SelectedIndex + 1;
        _image.LayoutTransform = new ScaleTransform(z, z);
    }

    private void LoadMap()
    {
        _tile = null;
        var folder = _map.SelectedItem as string;
        Cursor = Cursors.Wait;
        try { _current = folder != null ? _renderMap(folder) : null; }
        finally { Cursor = null; }
        Redraw();
        UpdateTile();
    }

    private void Redraw()
    {
        if (_current is not { } map) { _image.Source = null; return; }
        var img = map.Image.Clone();
        var entrance = _data.EntranceMetatiles.SelectMany(e => new[] { e.Closed, e.Open }).OfType<int>().ToHashSet();
        for (int y = 0; y < map.Grid.Height; y++)
            for (int x = 0; x < map.Grid.Width; x++)
                if (entrance.Contains(map.Grid.MetatileId(x, y))) SecretBasePreview.MarkTile(img, x, y, SecretBasePreview.Entrance, fill: false);
        var folder = _map.SelectedItem as string;
        foreach (var l in _data.Locations.Where(l => l.MapFolder == folder)) SecretBasePreview.MarkTile(img, l.X, l.Y, SecretBasePreview.Other);
        if (_tile is { } t) SecretBasePreview.MarkTile(img, t.X, t.Y, SecretBasePreview.Selected);
        _image.Source = Imaging.ToBitmap(img);
        ApplyZoom();
    }

    private void OnClick(object sender, MouseButtonEventArgs e)
    {
        if (_current is not { } map) return;
        var p = e.GetPosition(_image);
        int x = (int)(p.X / MapRenderer.BlockSize), y = (int)(p.Y / MapRenderer.BlockSize);
        if (!map.Grid.InBounds(x, y)) return;
        _tile = (x, y);
        var type = SecretBaseCatalog.TypeOfMetatile(_data, map.Grid.MetatileId(x, y));
        if (type != null) _type.SelectedItem = type;
        Redraw();
        UpdateTile();
    }

    private string? TileWarning()
    {
        if (_tile is not { } t || _current is not { } map) return null;
        int metatile = map.Grid.MetatileId(t.X, t.Y);
        var type = SecretBaseCatalog.TypeOfMetatile(_data, metatile);
        var folder = _map.SelectedItem as string;
        if (_data.Locations.Any(l => l.MapFolder == folder && l.X == t.X && l.Y == t.Y))
            return "There already is a secret base entrance on this tile.";
        if (type == null)
            return $"Metatile 0x{metatile:X3} is not a secret base entrance metatile – Secret Power will not work here until you paint one of the entrance metatiles (cave indent, tree, shrub) on this tile in Porymap.";
        if (_type.SelectedItem is string chosen && chosen != type)
            return $"The tile is a {type} entrance, but a {chosen} interior was chosen.";
        return null;
    }

    private void UpdateTile()
    {
        if (_tile is not { } t || _current is not { } map) { _tileInfo.Text = "None – click a tile on the map."; UpdateId(); return; }
        int metatile = map.Grid.MetatileId(t.X, t.Y);
        var type = SecretBaseCatalog.TypeOfMetatile(_data, metatile);
        _tileInfo.Text = $"({t.X},{t.Y}) · metatile 0x{metatile:X3} · " + (type != null ? $"entrance metatile ({type}) ✓" : "not an entrance metatile");
        UpdateId();
    }

    private void FillInteriors()
    {
        var type = _type.SelectedItem as string;
        var groups = _data.Groups.Where(g => g.Type == type && g.MapFolder != null).OrderBy(g => g.Index).ToList();
        _interior.ItemsSource = groups;
        var free = type != null ? SecretBaseCatalog.NextFreeId(_data, type, null) : null;
        _interior.SelectedItem = free?.Group ?? groups.FirstOrDefault();
        UpdateId();
    }

    private SecretBaseCatalog.FreeId? CurrentId() =>
        _interior.SelectedItem is SecretBaseGroup g ? SecretBaseCatalog.NextFreeId(_data, g) : null;

    private void UpdateId()
    {
        var id = CurrentId();
        bool elevationOk = int.TryParse(_elevation.Text.Trim(), out var elev) && elev is >= 0 and <= 15;
        var warning = TileWarning();
        _idInfo.Text = (_interior.SelectedItem is not SecretBaseGroup g ? "Choose an interior." :
                           id == null ? $"{g.ShortName} has no free id left (ones digit 1–9 all used) – choose another interior of this type." :
                           $"New id: {id.Name} = {id.Value}\nInterior map: {g.MapFolder}")
                       + (elevationOk ? "" : "\nElevation: 0 … 15.")
                       + (warning != null ? "\n\n⚠ " + warning : "");
        _ok.IsEnabled = _tile != null && id != null && elevationOk;
    }

    private void Confirm()
    {
        if (_tile is not { } t || _map.SelectedItem is not string folder || CurrentId() is not { } id || !int.TryParse(_elevation.Text.Trim(), out var elev)) return;
        var warning = TileWarning();
        if (warning != null && MessageBox.Show(this, warning + "\n\nCreate the location anyway?", Title, MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;
        Result = new NewSecretBaseResult(folder, t.X, t.Y, elev, id, warning);
        DialogResult = true;
    }
}
