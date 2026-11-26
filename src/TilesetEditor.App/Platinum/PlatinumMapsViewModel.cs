using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Text.Json.Nodes;
using System.Windows.Media;
using System.Windows.Shapes;
using TilesetEditor.App.Infrastructure;
using TilesetEditor.App.Shell;
using TilesetEditor.Core;
using TilesetEditor.Core.Imaging;
using TilesetEditor.Core.Platinum;
using TilesetEditor.Core.Project;

namespace TilesetEditor.App.Platinum;

/// <summary>A map header in the list, with its place name.</summary>
public sealed class MapHeaderItemVm(PlatinumMapHeader header, string? place)
{
    public PlatinumMapHeader Header { get; } = header;
    public string Display => place != null && !Header.FriendlyName.StartsWith(place, StringComparison.OrdinalIgnoreCase)
        ? $"{Header.FriendlyName}  ({place})" : Header.FriendlyName;
    public override string ToString() => Display;
}

/// <summary>
/// Map preview for Platinum: pokeplatinum maps are 3D models, and no map editor exists for them yet. The preview draws every
/// map from its land data top-down – walkable and blocked tiles, tile behaviors (grass, water, doors …), props and events.
/// </summary>
public sealed class PlatinumMapsViewModel : ObservableObject, IToolkitModule
{
    private readonly DeferredLoad _reload;
    private readonly ProjectContext _ctx;
    private readonly ListBox _list = new() { BorderThickness = new Thickness(0) };
    private readonly TextBox _filter = new();
    private readonly Canvas _canvas = new() { HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top };
    private readonly TextBlock _info = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBox _selection = new() { IsReadOnly = true, TextWrapping = TextWrapping.Wrap, FontFamily = new FontFamily("Consolas"), MinHeight = 40, BorderThickness = new Thickness(0) };
    private readonly CheckBox _showEvents = new() { Content = "Events", IsChecked = true, Margin = new Thickness(0, 0, 12, 0) };
    private readonly CheckBox _showSprites = new() { Content = "People as sprites", IsChecked = true, Margin = new Thickness(0, 0, 12, 0), ToolTip = "Draw people with their overworld sprite, looking in their starting direction." };
    private readonly Dictionary<(string, int), System.Windows.Media.Imaging.BitmapSource?> _sprites = [];
    private Dictionary<string, string?>? _spritePngs;
    private readonly CheckBox _showProps = new() { Content = "Props (buildings, trees …)", IsChecked = true, Margin = new Thickness(0, 0, 12, 0) };
    private readonly CheckBox _surroundings = new() { Content = "Surroundings", IsChecked = false, Margin = new Thickness(0, 0, 12, 0), ToolTip = "Also draw the neighbouring chunks of the map matrix." };
    private readonly Slider _zoom = new() { Minimum = 4, Maximum = 48, Value = 12, Width = 140, TickFrequency = 2, IsSnapToTickEnabled = true, VerticalAlignment = VerticalAlignment.Center };
    private readonly ComboBox _mode = new()
    {
        ItemsSource = new[] { "Textured (3D models from above)", "Textured + blocked tiles", "Collision & tile behaviors" },
        SelectedIndex = 0, Width = 230, Margin = new Thickness(0, 0, 12, 0), VerticalAlignment = VerticalAlignment.Center,
        ToolTip = "Textured: the map's 3D models with their textures, seen straight from above. Collision: walkable/blocked tiles and tile behaviors.",
    };
    private readonly TextBlock _busy = new() { Text = "Drawing …", Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, Visibility = Visibility.Collapsed };
    private int _renderGeneration;
    private PlatinumMaps? _maps;
    private List<MapHeaderItemVm> _all = [];

    private readonly Action<string, string, int>? _openInEvents;

    public PlatinumMapsViewModel(ProjectContext ctx, Action<string, string, int>? openInEvents = null)
    {
        _openInEvents = openInEvents;
        _ctx = ctx;
        _reload = new DeferredLoad(Load);
        _ctx.PlatinumChanged += (_, _) => _reload.Request();
        View = BuildView();
        _reload.Attach(View);
    }

    public string Title => "Maps";
    public string Description => "Preview of every map, edit people, warps, signs, triggers";
    public bool IsAvailable => true;
    public override string ToString() => Title;
    public FrameworkElement View { get; }
    public bool HasUnsavedChanges => _ev?.IsChanged == true || _extra.Values.Any(f => f.IsChanged);
    public string? UnsavedChangesDescription => HasUnsavedChanges ? $"Maps: the events of {_evHeader?.FriendlyName} are not saved yet." : null;

    private FrameworkElement BuildView()
    {
        var grid = new Grid { Margin = new Thickness(16) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(300) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        VirtualizingPanel.SetIsVirtualizing(_list, true);
        _list.SelectionChanged += (_, _) =>
        {
            if (_list.SelectedItem is MapHeaderItemVm m && _ev != null && _ev.IsChanged && m.Header != _evHeader)
            {
                var r = AppDialog.Show(Application.Current.MainWindow, $"The events of {_evHeader?.FriendlyName} were changed. Save them first?", Title, MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
                if (r == MessageBoxResult.Cancel || (r == MessageBoxResult.Yes && !SaveEvents())) { _list.SelectedItem = _all.FirstOrDefault(x => x.Header == _evHeader); return; }
                _ev = null;
                _extra.Clear();
            }
            Render();
        };
        _filter.TextChanged += (_, _) => ApplyFilter();
        grid.Children.Add(Ui.FilteredList(_list, _filter, "Filter maps"));

        var right = new DockPanel { Margin = new Thickness(16, 0, 0, 0) };
        var top = new StackPanel();
        DockPanel.SetDock(top, Dock.Top);
        top.Children.Add(Ui.Hint("The map's 3D models seen from above (or the collision layer). The terrain itself cannot be edited yet – but the events can, like in Porymap: " +
                                 "click one to select it, drag it to move it, drag the corner of a trigger to resize it; choose a \"+\" tool and click the map to add one."));
        top.Children.Add(BuildToolbar());
        var options = new WrapPanel { Margin = new Thickness(0, 6, 0, 6) };
        options.Children.Add(_mode);
        _mode.SelectionChanged += (_, _) => Render();
        options.Children.Add(_showEvents);
        options.Children.Add(_showSprites);
        options.Children.Add(_showProps);
        options.Children.Add(_surroundings);
        options.Children.Add(new TextBlock { Text = "Zoom", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 6, 0) });
        options.Children.Add(_zoom);
        options.Children.Add(_busy);
        top.Children.Add(options);
        top.Children.Add(_info);
        foreach (var c in new[] { _showEvents, _showSprites, _showProps, _surroundings }) { c.Checked += (_, _) => Render(); c.Unchecked += (_, _) => Render(); }
        _zoom.ValueChanged += (_, _) => Render();
        right.Children.Add(top);

        var bottom = new StackPanel();
        DockPanel.SetDock(bottom, Dock.Bottom);
        bottom.Children.Add(BuildLegend());
        bottom.Children.Add(_selection);
        right.Children.Add(bottom);

        var props = new Border
        {
            Width = 340, Margin = new Thickness(8, 6, 0, 6), Padding = new Thickness(10), BorderThickness = new Thickness(1),
            BorderBrush = (Brush)Application.Current.FindResource("BorderBrush"), Background = (Brush)Application.Current.FindResource("SurfaceBrush"),
            Child = new ScrollViewer { Content = _props, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled },
        };
        DockPanel.SetDock(props, Dock.Right);
        right.Children.Add(props);
        var scroll = new ScrollViewer { Content = _canvas, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Margin = new Thickness(0, 6, 0, 6) };
        // Ctrl+mouse wheel zooms (like the zoom slider).
        scroll.PreviewMouseWheel += (_, e) =>
        {
            if (!System.Windows.Input.Keyboard.Modifiers.HasFlag(System.Windows.Input.ModifierKeys.Control)) return;
            _zoom.Value = Math.Clamp(_zoom.Value + (e.Delta > 0 ? 2 : -2), _zoom.Minimum, _zoom.Maximum);
            e.Handled = true;
        };
        right.Children.Add(scroll);
        _canvas.Focusable = true;
        _canvas.MouseMove += CanvasMouseMove;
        _canvas.MouseLeftButtonUp += CanvasMouseUp;
        _canvas.KeyDown += CanvasKeyDown;
        ShowProps();
        Grid.SetColumn(right, 1);
        grid.Children.Add(right);
        return grid;
    }

    private static FrameworkElement BuildLegend()
    {
        var wrap = new WrapPanel { Margin = new Thickness(0, 2, 0, 4) };
        foreach (var g in PlatinumMaps.LegendGroups)
        {
            var c = PlatinumMaps.GroupColor(g);
            var item = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 12, 2) };
            item.Children.Add(new Border { Width = 12, Height = 12, Background = Imaging.Brush(c), BorderBrush = Brushes.Gray, BorderThickness = new Thickness(1), Margin = new Thickness(0, 0, 4, 0) });
            item.Children.Add(new TextBlock { Text = g, FontSize = 11 });
            wrap.Children.Add(item);
        }
        void Marker(string text, Brush b)
        {
            var item = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 12, 2) };
            item.Children.Add(new Ellipse { Width = 11, Height = 11, Fill = b, Stroke = Brushes.White, Margin = new Thickness(0, 0, 4, 0) });
            item.Children.Add(new TextBlock { Text = text, FontSize = 11 });
            wrap.Children.Add(item);
        }
        Marker("Person", EventBrush(PlatinumEventKind.Person));
        Marker("Warp", EventBrush(PlatinumEventKind.Warp));
        Marker("Sign / hidden item", EventBrush(PlatinumEventKind.Sign));
        Marker("Trigger", EventBrush(PlatinumEventKind.Trigger));
        Marker("Prop", PropBrush);
        wrap.Children.Add(new TextBlock { Text = "darker = blocked", FontSize = 11, FontStyle = FontStyles.Italic });
        return wrap;
    }

    private static readonly Brush PropBrush = Frozen(new SolidColorBrush(Color.FromRgb(120, 80, 40)));

    private static Brush Frozen(SolidColorBrush b) { b.Freeze(); return b; }

    private static Brush EventBrush(PlatinumEventKind k) => k switch
    {
        PlatinumEventKind.Person => Frozen(new SolidColorBrush(Color.FromRgb(230, 50, 50))),
        PlatinumEventKind.Warp => Frozen(new SolidColorBrush(Color.FromRgb(160, 60, 230))),
        PlatinumEventKind.Sign => Frozen(new SolidColorBrush(Color.FromRgb(250, 190, 30))),
        _ => Frozen(new SolidColorBrush(Color.FromArgb(110, 40, 200, 255))),
    };

    private void Load()
    {
        var p = _ctx.Platinum;
        _maps = p == null ? null : new PlatinumMaps(p.Root);
        _sprites.Clear();
        _spritePngs = null;
        if (p == null) { _all = []; ApplyFilter(); return; }
        var places = p.TextBank("location_names").ToDictionary(x => x.Id, x => x.Text, StringComparer.Ordinal);
        _all = p.MapHeaders.Select(h => new MapHeaderItemVm(h, h.LabelText != null && places.TryGetValue(h.LabelText, out var n) ? n : null)).ToList();
        ApplyFilter();
    }

    private void ApplyFilter()
    {
        var f = _filter.Text.Trim();
        var keep = (_list.SelectedItem as MapHeaderItemVm)?.Header.Id;
        _list.ItemsSource = f.Length == 0 ? _all : _all.Where(h => h.Display.Contains(f, StringComparison.OrdinalIgnoreCase) || h.Header.Id.Contains(f, StringComparison.OrdinalIgnoreCase)).ToList();
        if (keep != null) _list.SelectedItem = _all.FirstOrDefault(h => h.Header.Id == keep);
    }

    private async void Render()
    {
        int generation = ++_renderGeneration;
        _selection.Text = "";
        if (_list.SelectedItem is not MapHeaderItemVm item || _maps == null || _ctx.Platinum == null) { _canvas.Children.Clear(); _info.Text = ""; return; }
        var h = item.Header;
        var maps = _maps;
        var headers = _ctx.Platinum.MapHeaders.ToDictionary(x => x.Id, StringComparer.Ordinal);
        try
        {
            PlatinumMatrix? matrix;
            (int X, int Y, int W, int H)? area0;
            lock (maps)
            {
                matrix = h.Matrix != null ? maps.Matrix(h.Matrix) : null;
                area0 = matrix != null ? maps.Area(h, matrix) : null;
            }
            if (matrix == null) { _canvas.Children.Clear(); _info.Text = $"{h.Id}: the map matrix {h.Matrix} was not found."; return; }
            if (area0 == null) { _canvas.Children.Clear(); _info.Text = $"{h.Id}: no chunk of {matrix.Id} belongs to this map."; return; }
            var area = area0.Value;
            if (_surroundings.IsChecked == true)
            {
                int x0 = Math.Max(0, area.X - 1), y0 = Math.Max(0, area.Y - 1);
                int x1 = Math.Min(matrix.Width, area.X + area.W + 1), y1 = Math.Min(matrix.Height, area.Y + area.H + 1);
                area = (x0, y0, x1 - x0, y1 - y0);
            }
            int tile = (int)_zoom.Value;
            int mode = _mode.SelectedIndex;
            bool props = _showProps.IsChecked == true;
            // The cell's own map header decides its textures (neighbouring areas keep theirs).
            string? AreaOf(int x, int y) =>
                matrix.HeaderAt(x, y) is { Length: > 0 } hid && hid != "MAP_HEADER_EVERYWHERE" && hid != "MAP_HEADER_NOTHING" && headers.TryGetValue(hid, out var hh)
                    ? hh.AreaData : h.AreaData;
            _busy.Visibility = Visibility.Visible;
            var drawArea = area;
            var img = await Task.Run(() =>
            {
                lock (maps)
                {
                    if (mode == 2) return maps.Render(matrix, drawArea, tile);
                    var tex = maps.RenderTextured(matrix, drawArea, tile, AreaOf, props);
                    if (mode == 1) maps.OverlayCollision(tex, matrix, drawArea, tile);
                    return tex;
                }
            });
            if (generation != _renderGeneration) return;
            _busy.Visibility = Visibility.Collapsed;
            _canvas.Children.Clear();
            var bmp = Imaging.ToBitmap(img);
            var image = new Image { Source = bmp, Width = img.Width, Height = img.Height, Stretch = Stretch.None, SnapsToDevicePixels = true };
            RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.NearestNeighbor);
            _canvas.Width = img.Width;
            _canvas.Height = img.Height;
            _canvas.Children.Add(image);
            int size = PlatinumLandData.Size;
            int originX = area.X * size, originZ = area.Y * size;

            // Prop markers: in the textured views the props are drawn as models; the markers belong to the collision view.
            var placed = new List<(int Cx, int Cy, PlatinumProp Prop)>();
            lock (maps)
                for (int cy = 0; cy < area.H; cy++)
                    for (int cx = 0; cx < area.W; cx++)
                        if (maps.Land(matrix.MapAt(area.X + cx, area.Y + cy)) is { } land)
                            placed.AddRange(land.Props.Select(pr => (cx, cy, pr)));
            int propCount = placed.Count;
            if (_showProps.IsChecked == true && mode == 2)
                foreach (var (cx, cy, prop) in placed)
                        {
                            double px = (cx * size + prop.X) * tile, pz = (cy * size + prop.Z) * tile;
                            var d = new Rectangle { Width = Math.Max(6, tile * 0.7), Height = Math.Max(6, tile * 0.7), Fill = PropBrush, Stroke = Brushes.White, StrokeThickness = 1, RenderTransform = new RotateTransform(45) };
                            d.ToolTip = $"Prop {prop.ModelName ?? "#" + prop.ModelId} (model {prop.ModelId}) at tile {prop.X:0.#}, {prop.Z:0.#} · height {prop.Y:0.#}";
                            d.MouseLeftButtonUp += (_, _) => _selection.Text = (string)d.ToolTip;
                            Canvas.SetLeft(d, px);
                            Canvas.SetTop(d, pz - d.Height * 0.7);
                            _canvas.Children.Add(d);
                        }

            // Events: from the (editable) events file, drawn on their own layer above the map.
            _origin = (originX, originZ);
            _tile = tile;
            _imageSize = (img.Width, img.Height);
            var background = (Image)_canvas.Children[0];
            background.MouseLeftButtonDown += MapMouseDown;
            if (_ev == null || _evHeader != h) { _ev = LoadEvents(h); _evHeader = h; _multi.Clear(); _selected = null; ShowProps(); _history.Reset(State()); }
            _overlay.Width = img.Width;
            _overlay.Height = img.Height;
            Ui.Detach(_overlay);
            _canvas.Children.Add(_overlay);
            DrawEvents();
            int persons = Count("object_events"), warps = Count("warp_events"), total = PlatinumMapScripts.EventLists.Sum(Count);
            var events = Array.Empty<PlatinumMapEvent>();
            _info.Text = $"{h.Id} · matrix {matrix.Id} ({matrix.Width}×{matrix.Height} chunks), shown: {area.W}×{area.H} chunks from {area.X},{area.Y} · " +
                         $"events: {persons} people, {warps} warps, {total - persons - warps} other ({h.EventsFile}) · {propCount} props" +
                         $"\nWeather {Short(h.Weather, "OVERWORLD_WEATHER_")} · camera {Short(h.Camera, "CAMERA_TYPE_")} · music {Short(h.DayMusic, "SEQ_")} · encounters {Short(h.Encounters, "")} · area data {h.AreaData}" +
                         (mode < 2 ? $" · textures {System.IO.Path.GetFileNameWithoutExtension(maps.MapTextureSetFile(h.AreaData) ?? "–")}" : "");
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or System.Text.Json.JsonException or FormatException
                                       or IndexOutOfRangeException or ArgumentOutOfRangeException)
        {
            if (generation != _renderGeneration) return;
            _busy.Visibility = Visibility.Collapsed;
            _canvas.Children.Clear();
            _info.Text = $"{h.Id} could not be drawn: {ex.Message}";
        }
    }

    // ------------------------------------------------------------------ event editor (like Porymap's event tab)

    private enum Tool { Select, Person, Trainer, Sign, Warp, Trigger }

    private readonly Canvas _overlay = new();
    private readonly StackPanel _props = new();
    private readonly List<(Tool Tool, ToggleButton Button)> _toolButtons = [];
    private PlatinumJsonFile? _ev;
    /// <summary>Other maps' events changed together with this one (the way back of a two-way warp), saved with it.</summary>
    private readonly Dictionary<string, PlatinumJsonFile> _extra = new(StringComparer.Ordinal);
    private PlatinumMapHeader? _evHeader;
    private (string List, JsonObject Node)? _selected;
    private Tool _tool = Tool.Select;
    private (int X, int Z) _origin;
    private int _tile = 12;
    private (int W, int H) _imageSize;
    // dragging: the element, the event, the tile offset of the mouse inside it; resize = trigger corner
    private (FrameworkElement Element, JsonObject Node, double OffX, double OffZ, bool Resize, int StartX, int StartZ)? _drag;
    // several events: the selection, where each one's shape was drawn, the selection rectangle, copied events
    private readonly List<(string List, JsonObject Node)> _multi = [];
    private readonly Dictionary<JsonObject, (FrameworkElement Element, double Left, double Top)> _shapes = new(ReferenceEqualityComparer.Instance);
    private Rectangle? _band;
    private Point _bandStart;
    private (int X, int Z)? _hoverTile;
    private List<(string List, JsonObject Node)> _clipboard = [];

    private readonly UndoHistory<List<(string Path, string Json)>> _history = new((a, b) => a.SequenceEqual(b));

    private List<(string Path, string Json)> State() =>
        new[] { _ev }.Concat(_extra.Values).OfType<PlatinumJsonFile>().DistinctBy(f => f.Path).Select(f => (f.Path, f.Snapshot())).ToList();

    private void UndoRedo(bool redo)
    {
        var state = redo ? _history.Redo() : _history.Undo();
        if (state == null || _ev == null) { _selection.Text = redo ? "Nothing to redo." : "Nothing to undo (saved changes: Backups module)."; return; }
        foreach (var (path, json) in state)
        {
            if (path == _ev.Path) _ev = _ev.Restored(json);
            else if (_extra.TryGetValue(path, out var f)) _extra[path] = f.Restored(json);
        }
        _multi.Clear();
        SyncSelection();
        DrawEvents();
        _selection.Text = (redo ? "Redone." : "Undone.") + (HasUnsavedChanges ? " Not saved yet." : "");
        OnPropertyChanged(nameof(HasUnsavedChanges));
    }

    private bool IsSelected(JsonObject o) => _multi.Any(m => ReferenceEquals(m.Node, o));

    /// <summary>One selected event is edited on the side; several are moved, copied and deleted together.</summary>
    private void SyncSelection()
    {
        _selected = _multi.Count == 1 ? _multi[0] : null;
        ShowProps();
    }

    private int Count(string list) => _ev?.Root[list] is JsonArray a ? a.Count : 0;

    private FrameworkElement BuildToolbar()
    {
        var bar = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
        void T(Tool tool, string text, string tip)
        {
            var b = new ToggleButton { Content = text, Padding = new Thickness(8, 3, 8, 3), Margin = new Thickness(0, 0, 4, 4), ToolTip = tip, IsChecked = tool == Tool.Select };
            b.Click += (_, _) => SetTool(tool);
            _toolButtons.Add((tool, b));
            bar.Children.Add(b);
        }
        T(Tool.Select, "✋ Select / move", "Click an event to select it, drag it to move it; drag the corner of a trigger to resize it. Delete removes the selected event.");
        T(Tool.Person, "+ Person", "Click the map: a person stands there (give it dialog in Events & Dialogs).");
        T(Tool.Trainer, "+ Trainer", "Click the map: a trainer who battles the player walking into its sight.");
        T(Tool.Sign, "+ Sign", "Click the map: a sign the player can read.");
        T(Tool.Warp, "+ Warp", "Click the map: a door or exit to another map.");
        T(Tool.Trigger, "+ Trigger", "Click the map: a trigger tile (resize it by its corner).");
        bar.Children.Add(new Border { Width = 12 });
        bar.Children.Add(Ui.Button("🗑 Delete", DeleteSelected, "Removes the selected event (key: Delete)."));
        bar.Children.Add(Ui.Button("↶", () => UndoRedo(false), "Undo (Ctrl+Z) – changes that are not saved yet"));
        bar.Children.Add(Ui.Button("↷", () => UndoRedo(true), "Redo (Ctrl+Y)"));
        bar.Children.Add(Ui.Button("💾 Save", () => SaveEvents(), "Writes the events file of this map (shown as a file list first)."));
        bar.Children.Add(Ui.Button("Discard", () => { _ev = null; _extra.Clear(); _multi.Clear(); _selected = null; Render(); }, "Forget the unsaved changes of this map."));
        return bar;
    }

    private void SetTool(Tool tool)
    {
        _tool = tool;
        foreach (var (t, b) in _toolButtons) b.IsChecked = t == tool;
        _canvas.Cursor = tool == Tool.Select ? null : System.Windows.Input.Cursors.Cross;
    }

    private PlatinumJsonFile? LoadEvents(PlatinumMapHeader h)
    {
        if (_ctx.Root is not { } root || h.EventsFile == null || !File.Exists(ProjectPaths.Combine(root, h.EventsFile))) return null;
        if (_extra.TryGetValue(h.EventsFile, out var pending)) return pending;   // changed by a two-way warp, not saved yet
        try { return PlatinumJsonFile.Load(root, h.EventsFile, 4); }
        catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException or FormatException) { _info.Text = ex.Message; return null; }
    }

    private (int X, int Z) TileAt(Point p) => (_origin.X + (int)Math.Floor(p.X / _tile), _origin.Z + (int)Math.Floor(p.Y / _tile));

    private void Changed()
    {
        _history.Record(State());
        DrawEvents();
        _selection.Text = HasUnsavedChanges ? $"{_evHeader?.EventsFile} · changed – not saved yet" : "";
        OnPropertyChanged(nameof(HasUnsavedChanges));
    }

    private void DrawEvents()
    {
        _overlay.Children.Clear();
        _shapes.Clear();
        if (_ev == null || _showEvents.IsChecked != true) return;
        int tile = _tile;
        var lists = PlatinumMapScripts.EventLists;
        // Triggers first (below), then the others.
        foreach (var list in new[] { "coord_events", "bg_events", "warp_events", "object_events" })
        {
            if (_ev.Root[list] is not JsonArray arr) continue;
            foreach (var o in arr.OfType<JsonObject>())
            {
                var row = new EventRowVm(o, () => { });
                double ex = (row.X - _origin.X) * tile, ez = (row.Z - _origin.Z) * tile;
                if (ex < -2 * tile || ez < -2 * tile || ex > _imageSize.W + tile || ez > _imageSize.H + tile) continue;
                bool selected = IsSelected(o);
                var kind = list switch { "object_events" => PlatinumEventKind.Person, "warp_events" => PlatinumEventKind.Warp, "bg_events" => PlatinumEventKind.Sign, _ => PlatinumEventKind.Trigger };
                FrameworkElement shape;
                if (list == "coord_events")
                {
                    int w = Math.Max(1, PlatinumEncounters.Int(o["width"])), l = Math.Max(1, PlatinumEncounters.Int(o["length"]));
                    shape = new Rectangle { Width = w * tile, Height = l * tile, Fill = EventBrush(kind), Stroke = selected ? Brushes.Yellow : Brushes.DeepSkyBlue, StrokeThickness = selected ? 2.5 : 1 };
                }
                else if (list == "object_events" && _showSprites.IsChecked == true && Sprite(row["graphics_id"], PlatinumMaps.Direction(row["initial_dir"])) is { } spr)
                {
                    var pic = new Image { Source = spr, Width = 2 * tile, Height = 2 * tile, Stretch = Stretch.Fill };
                    RenderOptions.SetBitmapScalingMode(pic, BitmapScalingMode.NearestNeighbor);
                    if (selected) pic.Effect = new System.Windows.Media.Effects.DropShadowEffect { Color = Colors.Yellow, BlurRadius = 10, ShadowDepth = 0, Opacity = 1 };
                    shape = pic;
                    ex -= tile / 2.0;
                    ez -= tile;
                }
                else
                    shape = new Ellipse { Width = tile, Height = tile, Fill = EventBrush(kind), Stroke = selected ? Brushes.Yellow : Brushes.White, StrokeThickness = selected ? 3 : 1.5 };
                shape.ToolTip = Describe(list, o);
                shape.Cursor = System.Windows.Input.Cursors.SizeAll;
                var node = o;
                var listKey = list;
                double offX = list == "object_events" && shape is Image ? 0.5 : 0, offZ = list == "object_events" && shape is Image ? 1 : 0;
                shape.MouseLeftButtonDown += (_, e) => StartDrag(shape, listKey, node, e, offX, offZ, false);
                Canvas.SetLeft(shape, ex);
                Canvas.SetTop(shape, ez);
                _overlay.Children.Add(shape);
                _shapes[o] = (shape, ex, ez);
                if (list == "coord_events" && selected && _multi.Count == 1)
                {
                    // Resize handle at the bottom-right corner.
                    var handle = new Rectangle { Width = Math.Max(8, tile * 0.6), Height = Math.Max(8, tile * 0.6), Fill = Brushes.Yellow, Stroke = Brushes.Black, Cursor = System.Windows.Input.Cursors.SizeNWSE, ToolTip = "Drag to resize" };
                    handle.MouseLeftButtonDown += (_, e) => StartDrag(handle, listKey, node, e, 0, 0, true);
                    Canvas.SetLeft(handle, ex + ((FrameworkElement)shape).Width - handle.Width / 2);
                    Canvas.SetTop(handle, ez + ((FrameworkElement)shape).Height - handle.Height / 2);
                    _overlay.Children.Add(handle);
                }
            }
        }
    }

    private static string Describe(string list, JsonObject o)
    {
        var r = new EventRowVm(o, () => { });
        return list switch
        {
            "object_events" => PlatinumMapScripts.TrainerOf(o) is { } t ? $"Trainer {t.Trainer} · sight {t.Sight}" : $"{r["id"]} · {r["graphics_id"]} · script {r["script"]}",
            "warp_events" => $"Warp → {r["dest_header_id"]} (warp {r["dest_warp_id"]})",
            "bg_events" => o.ContainsKey("hidden_item") ? $"Hidden item {r["hidden_item"]}" : $"Sign · script {r["script"]}",
            _ => $"Trigger · {r["var"]} = {r["value"]} · script {r["script"]}",
        } + $" · tile {r.X}, {r.Z}";
    }

    private void StartDrag(FrameworkElement shape, string list, JsonObject node, System.Windows.Input.MouseButtonEventArgs e, double offX, double offZ, bool resize)
    {
        e.Handled = true;
        _canvas.Focus();
        if (_tool != Tool.Select) SetTool(Tool.Select);
        // Ctrl+click adds or removes an event from the selection.
        if ((System.Windows.Input.Keyboard.Modifiers & System.Windows.Input.ModifierKeys.Control) != 0 && !resize)
        {
            int i = _multi.FindIndex(m => ReferenceEquals(m.Node, node));
            if (i >= 0) _multi.RemoveAt(i); else _multi.Add((list, node));
            SyncSelection();
            DrawEvents();
            return;
        }
        // Select and drag in one go (like Porymap); a click on a selected event drags the whole selection.
        if (!IsSelected(node)) { _multi.Clear(); _multi.Add((list, node)); SyncSelection(); }
        var (tx, tz) = TileAt(e.GetPosition(_overlay));
        _drag = (shape, node, offX, offZ, resize, tx, tz);
        shape.CaptureMouse();
    }

    private void CanvasMouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        var p = e.GetPosition(_overlay);
        var (tx, tz) = TileAt(p);
        _hoverTile = (tx, tz);
        if (_band != null)
        {
            Canvas.SetLeft(_band, Math.Min(p.X, _bandStart.X));
            Canvas.SetTop(_band, Math.Min(p.Y, _bandStart.Y));
            _band.Width = Math.Abs(p.X - _bandStart.X);
            _band.Height = Math.Abs(p.Y - _bandStart.Y);
            return;
        }
        if (_drag is not { } d) { if (!HasUnsavedChanges) _selection.Text = $"tile {tx}, {tz}"; return; }
        if (d.Resize)
        {
            var r = new EventRowVm(d.Node, () => { });
            int w = Math.Max(1, tx - r.X + 1), l = Math.Max(1, tz - r.Z + 1);
            _selection.Text = $"size {w} × {l}";
            if (_shapes.TryGetValue(d.Node, out var rs) && rs.Element is Rectangle rect) { rect.Width = w * _tile; rect.Height = l * _tile; }
            Canvas.SetLeft(d.Element, (r.X - _origin.X + w) * _tile - d.Element.Width / 2);
            Canvas.SetTop(d.Element, (r.Z - _origin.Z + l) * _tile - d.Element.Height / 2);
            return;
        }
        int dx = tx - d.StartX, dz = tz - d.StartZ;
        foreach (var (_, node) in _multi)
            if (_shapes.TryGetValue(node, out var sh)) { Canvas.SetLeft(sh.Element, sh.Left + dx * _tile); Canvas.SetTop(sh.Element, sh.Top + dz * _tile); }
        _selection.Text = _multi.Count > 1 ? $"{_multi.Count} events → {dx:+0;-0;0}, {dz:+0;-0;0} tiles" : $"→ tile {tx}, {tz}";
    }

    private void CanvasMouseUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        var (tx, tz) = TileAt(e.GetPosition(_overlay));
        if (_band != null) { FinishBand(e.GetPosition(_overlay)); return; }
        if (_drag is not { } d) return;
        _drag = null;
        d.Element.ReleaseMouseCapture();
        if (d.Resize)
        {
            var r = new EventRowVm(d.Node, () => { });
            int w = Math.Max(1, tx - r.X + 1), l = Math.Max(1, tz - r.Z + 1);
            if (w != PlatinumEncounters.Int(d.Node["width"]) || l != PlatinumEncounters.Int(d.Node["length"])) { d.Node["width"] = w; d.Node["length"] = l; }
        }
        else MoveSelection(tx - d.StartX, tz - d.StartZ);
        Changed();
        ShowProps();
    }

    private void MoveSelection(int dx, int dz)
    {
        if (dx == 0 && dz == 0) return;
        foreach (var (_, node) in _multi)
        {
            var r = new EventRowVm(node, () => { });
            r["x"] = (r.X + dx).ToString();
            r["z"] = (r.Z + dz).ToString();
        }
    }

    private void FinishBand(Point end)
    {
        var band = _band!;
        _band = null;
        _overlay.Children.Remove(band);
        _canvas.ReleaseMouseCapture();
        bool add = (System.Windows.Input.Keyboard.Modifiers & System.Windows.Input.ModifierKeys.Control) != 0;
        if (!add) _multi.Clear();
        if (Math.Abs(end.X - _bandStart.X) > 4 || Math.Abs(end.Y - _bandStart.Y) > 4)
        {
            var (x0, z0) = TileAt(new Point(Math.Min(end.X, _bandStart.X), Math.Min(end.Y, _bandStart.Y)));
            var (x1, z1) = TileAt(new Point(Math.Max(end.X, _bandStart.X), Math.Max(end.Y, _bandStart.Y)));
            foreach (var list in PlatinumMapScripts.EventLists)
                foreach (var o in (_ev?.Root[list] as JsonArray)?.OfType<JsonObject>() ?? [])
                {
                    var r = new EventRowVm(o, () => { });
                    if (r.X >= x0 && r.X <= x1 && r.Z >= z0 && r.Z <= z1 && !IsSelected(o)) _multi.Add((list, o));
                }
        }
        SyncSelection();
        DrawEvents();
        if (_multi.Count > 1) _selection.Text = $"{_multi.Count} events selected – drag one to move all, arrows move them by a tile, Ctrl+C / Ctrl+V / Ctrl+D copy, paste, duplicate, Delete removes them.";
    }

    private void CanvasKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        bool ctrl = (System.Windows.Input.Keyboard.Modifiers & System.Windows.Input.ModifierKeys.Control) != 0;
        switch (e.Key)
        {
            case System.Windows.Input.Key.Delete: DeleteSelected(); break;
            case System.Windows.Input.Key.Escape: _multi.Clear(); SyncSelection(); DrawEvents(); break;
            case System.Windows.Input.Key.Left or System.Windows.Input.Key.Right or System.Windows.Input.Key.Up or System.Windows.Input.Key.Down when _multi.Count > 0:
                int step = (System.Windows.Input.Keyboard.Modifiers & System.Windows.Input.ModifierKeys.Shift) != 0 ? 5 : 1;
                MoveSelection(e.Key == System.Windows.Input.Key.Left ? -step : e.Key == System.Windows.Input.Key.Right ? step : 0,
                              e.Key == System.Windows.Input.Key.Up ? -step : e.Key == System.Windows.Input.Key.Down ? step : 0);
                Changed();
                ShowProps();
                break;
            case System.Windows.Input.Key.Z when ctrl: UndoRedo((System.Windows.Input.Keyboard.Modifiers & System.Windows.Input.ModifierKeys.Shift) != 0); break;
            case System.Windows.Input.Key.Y when ctrl: UndoRedo(true); break;
            case System.Windows.Input.Key.C when ctrl: Copy(); break;
            case System.Windows.Input.Key.V when ctrl: Paste(_hoverTile); break;
            case System.Windows.Input.Key.D when ctrl: Copy(); Paste(null); break;
            case System.Windows.Input.Key.A when ctrl:
                _multi.Clear();
                foreach (var list in PlatinumMapScripts.EventLists) foreach (var o in (_ev?.Root[list] as JsonArray)?.OfType<JsonObject>() ?? []) _multi.Add((list, o));
                SyncSelection();
                DrawEvents();
                break;
            default: return;
        }
        e.Handled = true;
    }

    private void Copy()
    {
        _clipboard = _multi.Select(m => (m.List, (JsonObject)JsonNode.Parse(m.Node.ToJsonString())!)).ToList();
        if (_clipboard.Count > 0) _selection.Text = $"{_clipboard.Count} event(s) copied – Ctrl+V pastes them where the mouse is.";
    }

    /// <summary>Pastes the copied events with the top-left one at <paramref name="at"/> (or one tile right of where they were).</summary>
    private void Paste((int X, int Z)? at)
    {
        if (_ev == null || _clipboard.Count == 0) return;
        int minX = _clipboard.Min(c => new EventRowVm(c.Node, () => { }).X), minZ = _clipboard.Min(c => new EventRowVm(c.Node, () => { }).Z);
        int dx = at is { } a ? a.X - minX : 1, dz = at is { } b ? b.Z - minZ : 0;
        _multi.Clear();
        foreach (var (list, node) in _clipboard)
        {
            var r = new EventRowVm(node, () => { });
            _multi.Add((list, PlatinumMapScripts.CopyEvent(_ev, list, node, r.X + dx, r.Z + dz)));
        }
        SyncSelection();
        Changed();
        _selection.Text = $"{_multi.Count} event(s) pasted." + (_clipboard.Any(c => c.List == "warp_events") ? " Check where pasted warps lead." : "");
    }

    private void MapMouseDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        _canvas.Focus();
        if (_ev == null || _ctx.Platinum is not { } p) return;
        var (x, z) = TileAt(e.GetPosition(_overlay));
        if (_tool == Tool.Select)
        {
            // A selection rectangle; a plain click (no drag) clears the selection.
            _bandStart = e.GetPosition(_overlay);
            _band = new Rectangle { Stroke = Brushes.Yellow, StrokeThickness = 1.5, StrokeDashArray = [4, 3], Fill = new SolidColorBrush(Color.FromArgb(40, 255, 255, 0)), IsHitTestVisible = false };
            Canvas.SetLeft(_band, _bandStart.X);
            Canvas.SetTop(_band, _bandStart.Y);
            _overlay.Children.Add(_band);
            _canvas.CaptureMouse();
            return;
        }
        JsonObject? added = null;
        string list = "object_events";
        try
        {
            switch (_tool)
            {
                case Tool.Person:
                    var gfx = p.ConstantList("object_events_gfx");
                    added = PlatinumMapScripts.AddPersonWithoutScript(_ev, gfx.FirstOrDefault(g => g == "OBJ_EVENT_GFX_YOUNGSTER") ?? gfx.First(), x, z);
                    break;
                case Tool.Trainer:
                    var trainers = p.ConstantList("trainers").Where(t => t != "TRAINER_NONE").ToList();
                    var first = trainers.FirstOrDefault() ?? throw new InvalidDataException("This project has no trainers.");
                    added = PlatinumMapScripts.AddTrainer(_ev, first, PlatinumMapScripts.GraphicsForClass("TRAINER_CLASS_YOUNGSTER", p.ConstantList("object_events_gfx")) ?? "OBJ_EVENT_GFX_YOUNGSTER", 1, 4, x, z);
                    break;
                case Tool.Sign:
                    list = "bg_events";
                    added = PlatinumMapScripts.AddSign(_ev, x, z, "", 0);
                    break;
                case Tool.Warp:
                    list = "warp_events";
                    added = NewWarp(p, x, z);
                    if (added == null) { SetTool(Tool.Select); return; }
                    break;
                case Tool.Trigger:
                    list = "coord_events";
                    var vars = p.ConstantList("vars_flags").Where(v => v.StartsWith("VAR_", StringComparison.Ordinal)).ToList();
                    added = PlatinumMapScripts.AddTrigger(_ev, x, z, vars.FirstOrDefault(v => v.StartsWith("VAR_UNUSED_", StringComparison.Ordinal)) ?? vars.First(), 0, "", 0);
                    break;
            }
        }
        catch (InvalidDataException ex) { ProjectWriter.Warn(Title, ex.Message); return; }
        if (added == null) return;
        _multi.Clear();
        _multi.Add((list, added));
        SyncSelection();
        SetTool(Tool.Select);
        Changed();
        ShowProps();
    }

    /// <summary>"+ Warp": destination map, and either an existing warp there or a new way back (two-way warp).</summary>
    private JsonObject? NewWarp(PlatinumProject p, int x, int z)
    {
        if (_ev == null || _evHeader == null) return null;
        var maps = p.MapHeaders.Where(h => h.EventsFile != null && h.Id != _evHeader.Id).Select(h => h.Id).ToList();
        var dest = new ComboBox { ItemsSource = maps, IsEditable = true, IsTextSearchEnabled = true, Width = 380, HorizontalAlignment = HorizontalAlignment.Left };
        dest.ItemsPanel = new ItemsPanelTemplate(new FrameworkElementFactory(typeof(VirtualizingStackPanel)));
        var existing = new RadioButton { Content = "Arrive at a warp that is already there", GroupName = "w", Margin = new Thickness(0, 8, 0, 2) };
        var targets = new ComboBox { Width = 380, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(20, 0, 0, 0) };
        var twoWay = new RadioButton { Content = "Also make the way back there (a warp on that map that leads here)", GroupName = "w", IsChecked = true, Margin = new Thickness(0, 8, 0, 2) };
        var bx = new TextBox { Width = 60 };
        var bz = new TextBox { Width = 60 };
        var pos = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(20, 0, 0, 0) };
        pos.Children.Add(new TextBlock { Text = "at x ", VerticalAlignment = VerticalAlignment.Center });
        pos.Children.Add(bx);
        pos.Children.Add(new TextBlock { Text = "  z ", VerticalAlignment = VerticalAlignment.Center });
        pos.Children.Add(bz);
        List<(string Value, string Display)> list = [];
        dest.SelectionChanged += (_, _) =>
        {
            if (dest.SelectedItem is not string id || p.MapHeaders.FirstOrDefault(h => h.Id == id) is not { } h) return;
            try { list = PlatinumMapScripts.WarpTargets(p.Root, h); } catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException) { list = []; }
            targets.ItemsSource = list.Select(t => t.Display).ToList();
            targets.SelectedIndex = list.Count > 0 ? 0 : -1;
            existing.IsEnabled = list.Count > 0;
            // A guess for the way back: one tile below the first warp there (e.g. inside a door); change it if needed.
            var first = list.Count > 0 ? System.Text.RegularExpressions.Regex.Match(list[0].Display, @"at (\d+), (\d+)") : null;
            bx.Text = first is { Success: true } ? first.Groups[1].Value : "0";
            bz.Text = first is { Success: true } ? (int.Parse(first.Groups[2].Value) + 1).ToString() : "0";
        };
        var panel = new StackPanel { Margin = new Thickness(14), Width = 440 };
        panel.Children.Add(new TextBlock { TextWrapping = TextWrapping.Wrap, Text = $"A warp at {x}, {z} on {_evHeader.FriendlyName}. Where does it lead?" });
        panel.Children.Add(new TextBlock { Text = "Destination map", Margin = new Thickness(0, 10, 0, 2) });
        panel.Children.Add(dest);
        panel.Children.Add(twoWay);
        panel.Children.Add(pos);
        panel.Children.Add(new TextBlock { Text = "Tip: open that map in Maps – moving the mouse over it shows the tile numbers.", Foreground = Brushes.Gray, FontSize = 11, Margin = new Thickness(20, 2, 0, 0), TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(existing);
        panel.Children.Add(targets);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
        var ok = new Button { Content = "Add", IsDefault = true, MinWidth = 90 };
        buttons.Children.Add(ok);
        buttons.Children.Add(new Button { Content = "Cancel", IsCancel = true, MinWidth = 80 });
        panel.Children.Add(buttons);
        var w = new Window { Title = "New warp", Content = panel, SizeToContent = SizeToContent.WidthAndHeight, ResizeMode = ResizeMode.NoResize, Owner = Application.Current.MainWindow, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        ok.Click += (_, _) =>
        {
            if (dest.SelectedItem is not string) { AppDialog.Show(w, "Choose the destination map.", w.Title); return; }
            if (twoWay.IsChecked == true && (!int.TryParse(bx.Text, out _) || !int.TryParse(bz.Text, out _))) { AppDialog.Show(w, "x and z of the way back are numbers (tiles).", w.Title); return; }
            if (existing.IsChecked == true && targets.SelectedIndex < 0) { AppDialog.Show(w, "Choose the warp to arrive at.", w.Title); return; }
            w.DialogResult = true;
        };
        if (w.ShowDialog() != true) return null;
        var destHeader = p.MapHeaders.First(h => h.Id == (string)dest.SelectedItem!);
        if (existing.IsChecked == true) return PlatinumMapScripts.AddWarp(_ev, x, z, destHeader.Id, list[targets.SelectedIndex].Value);
        var destFile = LoadEvents(destHeader) ?? throw new InvalidDataException($"{destHeader.Id} has no events file.");
        _extra[destFile.Path] = destFile;
        var (here, _) = PlatinumMapScripts.AddTwoWayWarp(_ev, _evHeader.Id, x, z, destFile, destHeader.Id, int.Parse(bx.Text), int.Parse(bz.Text));
        _selection.Text = $"The way back is on {destHeader.FriendlyName} at {bx.Text}, {bz.Text} – both maps are saved together.";
        return here;
    }

    private void ShowProps()
    {
        _props.Children.Clear();
        _props.Children.Add(Ui.Title("Event"));
        if (_ev != null && _multi.Count > 1)
        {
            _props.Children.Add(new TextBlock { Text = $"{_multi.Count} events selected", FontSize = 15, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 6) });
            foreach (var (list, node) in _multi.Take(20)) _props.Children.Add(new TextBlock { Text = "• " + Describe(list, node), FontSize = 11, TextWrapping = TextWrapping.Wrap });
            _props.Children.Add(Ui.Hint("Drag one of them to move all, arrow keys move by a tile (Shift: 5). Ctrl+C / Ctrl+V copy and paste (where the mouse is), Ctrl+D duplicates, Delete removes them, Esc clears the selection.", 300));
            _props.Children.Add(Ui.Row(Ui.Button("⧉ Duplicate", () => { Copy(); Paste(null); }), Ui.Button("🗑 Delete", DeleteSelected)));
            return;
        }
        if (_ev == null || _selected is not { } sel || _ctx.Platinum is not { } p)
        {
            _props.Children.Add(Ui.Hint(_ev == null ? "This map has no events file." : "Click an event on the map to select it, or choose a \"+\" tool above and click the map.", 300));
            return;
        }
        var kind = sel.List switch { "object_events" => PlatinumMapScripts.TrainerOf(sel.Node) != null ? "⚔ Trainer" : "👤 Person", "warp_events" => "🚪 Warp", "bg_events" => sel.Node.ContainsKey("hidden_item") ? "💎 Hidden item" : "🪧 Sign", _ => "👣 Trigger tile" };
        _props.Children.Add(new TextBlock { Text = kind, FontSize = 15, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 6) });
        var all = p.ConstantList("vars_flags");
        var scripts = _evHeader?.ScriptFile is { } sf && File.Exists(ProjectPaths.Combine(p.Root, sf))
            ? PlatinumMapScripts.Entries(File.ReadAllText(ProjectPaths.Combine(p.Root, sf))).Select(x => x.Label).Prepend("0").ToList() : null;
        var context = new EventEditContext(p, _ev, all.Where(f => f.StartsWith("FLAG_", StringComparison.Ordinal)).ToList(), all.Where(v => v.StartsWith("VAR_", StringComparison.Ordinal)).ToList(), scripts, Changed);
        _props.Children.Add(PlatinumEventEditor.Fields(sel.Node, context, narrow: true));
        bool noScript = sel.List != "warp_events" && !sel.Node.ContainsKey("hidden_item") && PlatinumMapScripts.TrainerOf(sel.Node) == null
                        && new EventRowVm(sel.Node, () => { })["script"] is "" or "0";
        if (noScript) _props.Children.Add(Ui.Hint("No script yet – it does nothing. Give it dialog or other blocks in Events & Dialogs (save first).", 300));
        if (_openInEvents != null && _evHeader != null && sel.List != "warp_events")
        {
            var idx = (_ev.Root[sel.List] as JsonArray)!.IndexOf(sel.Node);
            _props.Children.Add(Ui.Row(Ui.Button("💬 Open in Events & Dialogs →", () =>
            {
                if (HasUnsavedChanges && !SaveEvents()) return;
                _openInEvents(_evHeader.Id, sel.List, idx);
            }, "Script, texts and blocks of this event (saves the map first).")));
        }
        _props.Children.Add(Ui.Row(Ui.Button("🗑 Delete", DeleteSelected)));
    }

    private void DeleteSelected()
    {
        if (_ev == null || _multi.Count == 0) return;
        // A person's LOCALID may be used by the map's scripts – the build would fail without it.
        var script = _evHeader?.ScriptFile is { } sf && _ctx.Root is { } root && File.Exists(ProjectPaths.Combine(root, sf)) ? File.ReadAllText(ProjectPaths.Combine(root, sf)) : "";
        var used = _multi.Select(m => new EventRowVm(m.Node, () => { })["id"]).Where(id => id.StartsWith("LOCALID_", StringComparison.Ordinal)
            && System.Text.RegularExpressions.Regex.IsMatch(script, @"\b" + id + @"\b")).ToList();
        var text = _multi.Count == 1 ? $"Delete this event?\n{Describe(_multi[0].List, _multi[0].Node)}" : $"Delete these {_multi.Count} events?";
        if (used.Count > 0) text += $"\n\n{string.Join(", ", used)} {(used.Count == 1 ? "is" : "are")} used in {_evHeader?.ScriptFile} – remove those uses too, or the ROM does not build.";
        if (_multi.Any(m => m.List == "warp_events")) text += "\n\nOther maps lead to warps by name or number – check the warps that arrive here.";
        if (AppDialog.Show(Application.Current.MainWindow, text, Title, MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
        foreach (var (list, node) in _multi) (_ev.Root[list] as JsonArray)?.Remove(node);
        _multi.Clear();
        SyncSelection();
        Changed();
    }

    private bool SaveEvents()
    {
        if (_ctx.Root is not { } root || !HasUnsavedChanges) return true;
        var files = new[] { _ev }.Concat(_extra.Values).OfType<PlatinumJsonFile>().Where(f => f.IsChanged).DistinctBy(f => f.Path).ToList();
        var changes = new List<PlatinumFileChange>();
        foreach (var f in files)
        {
            var current = File.ReadAllText(ProjectPaths.Combine(root, f.Path), System.Text.Encoding.UTF8);
            if (current != f.OriginalText) { ProjectWriter.Warn(Title, $"{f.Path} was changed outside the Maps module (e.g. in Events & Dialogs) – \"Discard\" reloads it."); return false; }
            changes.Add(new PlatinumFileChange(f.Path, new System.Text.UTF8Encoding(false).GetBytes(f.Serialize()), ReferenceEquals(f, _ev) ? $"events of {_evHeader?.FriendlyName}" : "the way back of a two-way warp"));
        }
        if (!PlatinumModules.Write(_ctx, "Map events", changes, "Only the changed events change; the files keep their format.", "Build the ROM afterwards.")) return false;
        _ev = null;
        _extra.Clear();
        _multi.Clear();
        _selected = null;
        return true;
    }

    private System.Windows.Media.Imaging.BitmapSource? Sprite(string gfx, int dir)
    {
        if (_ctx.Root is not { } root) return null;
        if (_sprites.TryGetValue((gfx, dir), out var b)) return b;
        b = null;
        try
        {
            _spritePngs ??= PlatinumOverworld.List(root).GroupBy(x => x.Gfx).ToDictionary(g => g.Key, g => g.First().PngRel, StringComparer.Ordinal);
            if (_spritePngs.TryGetValue(gfx, out var png) && png != null && PlatinumOverworld.Facing(root, png, dir) is { } img) b = Imaging.ToBitmap(img);
        }
        catch (Exception ex) when (ex is IOException or PngFormatException or InvalidDataException or FormatException) { }
        return _sprites[(gfx, dir)] = b;
    }

    private static string Short(string? v, string prefix) => v == null ? "–" : PlatinumNames.Pretty(v, prefix);
}
