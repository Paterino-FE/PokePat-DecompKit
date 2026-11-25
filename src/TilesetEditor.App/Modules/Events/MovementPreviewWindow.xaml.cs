using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using TilesetEditor.App.Infrastructure;
using TilesetEditor.Core.Gba;
using TilesetEditor.Core.Imaging;
using TilesetEditor.Core.Maps;
using TilesetEditor.Core.Project;
using TilesetEditor.Core.Sprites;

namespace TilesetEditor.App.Modules.Events;

/// <summary>Element that lets the window paint the map, persons and paths itself (crisp at every zoom).</summary>
public sealed class MapDrawingSurface : FrameworkElement
{
    public Action<DrawingContext>? Painter { get; set; }
    protected override void OnRender(DrawingContext dc) => Painter?.Invoke(dc);
}

/// <summary>
/// Shows a map with its persons and draws where people walk in an event: one colored path per person (and the
/// player) with step numbers, the end positions, warnings, and an animated play-through.
/// </summary>
public partial class MovementPreviewWindow : Window
{
    private static readonly Color[] TrackColors =
    [
        Color.FromRgb(0x25, 0x63, 0xEB), Color.FromRgb(0xDC, 0x26, 0x26), Color.FromRgb(0x16, 0xA3, 0x4A), Color.FromRgb(0x93, 0x33, 0xEA),
        Color.FromRgb(0xEA, 0x58, 0x0C), Color.FromRgb(0x08, 0x91, 0xB2), Color.FromRgb(0xDB, 0x27, 0x77), Color.FromRgb(0x65, 0xA3, 0x0D),
        Color.FromRgb(0x4F, 0x46, 0xE5), Color.FromRgb(0xCA, 0x8A, 0x04),
    ];
    private static readonly Dictionary<string, List<OverworldSprite>> SpriteLists = new(StringComparer.OrdinalIgnoreCase);

    private readonly ProjectInfo _project;
    private readonly MapScene _scene;
    private readonly IReadOnlyList<MovementPhase> _phases;
    private readonly IReadOnlyList<string> _notes;
    private readonly MapObject? _talker;
    private readonly IReadOnlySet<string>? _known;
    private readonly BitmapSource? _map;
    private readonly (int X, int Y, Facing F) _defaultPlayer;
    private readonly Dictionary<(string, Facing), BitmapSource?> _sprites = [];
    private readonly Dictionary<string, Color> _colors = [];
    private readonly DispatcherTimer _timer;
    private readonly Stopwatch _clock = new();
    private readonly bool _ready;
    private int _px, _py;
    private Facing _pf;
    private SimulationResult _sim = new();
    private double _zoom = 2;
    private double _frame;
    private double _clockStartFrame;
    private string? _playerGfx;

    /// <param name="scriptLabel">Script whose movements are shown: its person is VAR_LAST_TALKED and the default player start.</param>
    /// <param name="personIndex">Person (object_events index) that runs the script, if known without map.json (pending assignment).</param>
    /// <param name="triggerIndex">Trigger (coord_events index) that runs the script, if known without map.json.</param>
    public MovementPreviewWindow(ProjectInfo project, string mapFolder, IReadOnlyList<MovementPhase> phases, string caption,
        string? scriptLabel = null, IEnumerable<string>? notes = null, int? personIndex = null, int? triggerIndex = null)
    {
        InitializeComponent();
        _project = project;
        _phases = phases;
        _notes = notes?.ToList() ?? [];
        _scene = MapScene.Load(project, mapFolder);
        _talker = personIndex is { } pi && pi >= 0 && pi < _scene.Objects.Count ? _scene.Objects[pi]
            : scriptLabel != null ? _scene.Objects.FirstOrDefault(o => o.Script == scriptLabel) : null;
        _known = LoadKnownActions(project.Root);
        try
        {
            if (MapRenderer.Render(project, _scene) is { } img) _map = Imaging.ToBitmap(img);
            else _scene.Problems.Add("The map image could not be drawn (layout or blockdata missing).");
        }
        catch (Exception ex) when (ex is IOException or FormatException or InvalidOperationException or ArgumentException)
        {
            _scene.Problems.Add($"The map image could not be drawn: {ex.Message}");
        }
        var trigger = _talker != null ? null
            : triggerIndex is { } ti && ti >= 0 && ti < _scene.Triggers.Count ? _scene.Triggers[ti]
            : scriptLabel != null ? _scene.Triggers.FirstOrDefault(t => t.Script == scriptLabel) : null;
        _defaultPlayer = _talker != null ? (_talker.X, _talker.Y + 1, Facing.Up)
            : trigger != null ? (trigger.X, trigger.Y, Facing.Up)
            : (Math.Max(0, _scene.Width / 2), Math.Max(0, _scene.Height / 2), Facing.Down);

        Title = $"Movements on the map – {mapFolder}";
        Header.Text = caption;
        SubHeader.Text = $"{mapFolder}  ·  {_scene.Width}×{_scene.Height} blocks  ·  {_scene.Objects.Count} persons" +
                         (_talker != null ? $"  ·  event person: {_talker.Name} at ({_talker.X},{_talker.Y})" : "") +
                         (trigger != null ? $"  ·  trigger tile at ({trigger.X},{trigger.Y})" : "");
        RenderOptions.SetBitmapScalingMode(Surface, BitmapScalingMode.NearestNeighbor);
        Surface.Painter = Paint;
        PlayerFacing.ItemsSource = Enum.GetValues<Facing>();
        ZoomChoice.ItemsSource = new[] { "1×", "2×", "3×", "4×" };
        _timer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(15) };
        _timer.Tick += OnTick;
        Closed += (_, _) => _timer.Stop();
        Loaded += (_, _) => Dispatcher.BeginInvoke(new Action(ScrollToAction), DispatcherPriority.Loaded);
        _ready = true;
        ZoomChoice.SelectedIndex = 1;
        SetPlayer(_defaultPlayer.X, _defaultPlayer.Y, _defaultPlayer.F);
    }

    private static IReadOnlySet<string>? LoadKnownActions(string root)
    {
        var f = System.IO.Path.Combine(root, "asm", "macros", "movement.inc");
        if (!File.Exists(f)) return null;
        return Regex.Matches(File.ReadAllText(f), @"create_movement_action\s+(\w+)").Select(m => m.Groups[1].Value).ToHashSet(StringComparer.Ordinal);
    }

    // ---------------------------------------------------------------- simulation

    private static string KeyOf(MapObject o) => $"obj{o.Index}";

    private ActorStart ToActor(MapObject o) => new(KeyOf(o), o.Name, o.X, o.Y, o.InitialFacing);

    private ActorStart? Resolve(string target)
    {
        if (MapScene.IsPlayer(target)) return new ActorStart(MovementSimulator.PlayerKey, "Player", _px, _py, _pf, true);
        if (_scene.FindObject(target, _talker) is not { } o) return null;
        var actor = ToActor(o);
        // Scripts name people by LOCALID_…; show that name first (the graphics may be a placeholder).
        var t = target.Trim();
        return t.StartsWith("LOCALID_", StringComparison.Ordinal) ? actor with { Name = $"{t[8..]} ({o.Name})" } : actor;
    }

    private void SetPlayer(int x, int y, Facing f)
    {
        _px = x;
        _py = y;
        _pf = f;
        if (!Equals(PlayerFacing.SelectedItem, f)) PlayerFacing.SelectedItem = f;
        Resimulate();
    }

    private void Resimulate()
    {
        _sim = MovementSimulator.Simulate(_phases, Resolve, _scene.Grid, _scene.Objects.Select(ToActor), _known);
        _colors.Clear();
        _colors[MovementSimulator.PlayerKey] = TrackColors[0];
        foreach (var t in _sim.Tracks)
            if (!_colors.ContainsKey(t.Key)) _colors[t.Key] = TrackColors[_colors.Count % TrackColors.Length];
        TimeSlider.Maximum = Math.Max(1, _sim.TotalFrames);
        _frame = Math.Min(_frame, _sim.TotalFrames);
        bool inside = _scene.Grid?.InBounds(_px, _py) ?? true;
        PlayerInfo.Text = $"({_px},{_py}), facing {_pf.ToString().ToLowerInvariant()}" + (inside ? "" : "  – outside the map!") +
                          (_scene.Grid is { } g && inside && g.Collision(_px, _py) != 0 ? "  – on a blocked tile" : "");
        BuildLegend();
        BuildWarnings();
        UpdateTimeText();
        Surface.InvalidateVisual();
    }

    private static string Collapse(IEnumerable<string> steps)
    {
        var list = steps.ToList();
        var parts = new List<string>();
        for (int i = 0; i < list.Count;)
        {
            int j = i;
            while (j < list.Count && list[j] == list[i]) j++;
            parts.Add(j - i > 1 ? $"{list[i]} ×{j - i}" : list[i]);
            i = j;
        }
        return parts.Count == 0 ? "(no steps)" : string.Join(", ", parts);
    }

    private void BuildLegend()
    {
        Legend.Children.Clear();
        if (_phases.Count == 0)
        {
            Legend.Children.Add(new TextBlock { Text = "No movements to show.", Style = (Style)FindResource("Hint") });
            return;
        }
        foreach (var phase in _sim.Phases)
        {
            var title = _sim.Phases.Count > 1 ? $"Phase {phase.Index + 1}" : "All at the same time";
            if (phase.Caption.Length > 0 && !phase.Caption.StartsWith("Phase ", StringComparison.Ordinal)) title += $" – {phase.Caption}";
            Legend.Children.Add(new TextBlock
            {
                Text = $"{title}   (≈ {phase.Frames / 60.0:0.0} s)", FontWeight = FontWeights.SemiBold, FontSize = 11,
                TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 1),
            });
            foreach (var t in phase.Tracks)
            {
                var row = new DockPanel { Margin = new Thickness(6, 0, 0, 2) };
                var swatch = new Rectangle { Width = 12, Height = 12, Fill = new SolidColorBrush(_colors[t.Key]), Margin = new Thickness(0, 2, 6, 0), VerticalAlignment = VerticalAlignment.Top };
                DockPanel.SetDock(swatch, Dock.Left);
                row.Children.Add(swatch);
                row.Children.Add(new TextBlock
                {
                    TextWrapping = TextWrapping.Wrap, FontSize = 11,
                    Text = $"{t.Name}: {Collapse(t.Steps.Select(s => s.Action))}  →  ends at ({t.EndX},{t.EndY}) facing {t.EndFacing.ToString().ToLowerInvariant()}",
                });
                Legend.Children.Add(row);
            }
        }
    }

    private void BuildWarnings()
    {
        WarningList.Children.Clear();
        var all = _notes.Select(n => (n, false)).Concat(_scene.Problems.Concat(_sim.AllWarnings).Select(w => (w, true))).ToList();
        if (all.Count == 0)
        {
            var ok = new TextBlock { Text = "None – every step stays on walkable tiles of the map.", TextWrapping = TextWrapping.Wrap, FontSize = 11 };
            ok.SetResourceReference(TextBlock.ForegroundProperty, "SuccessBrush");
            WarningList.Children.Add(ok);
            return;
        }
        foreach (var (text, isWarning) in all)
        {
            var line = new TextBlock { Text = (isWarning ? "⚠ " : "ℹ ") + text, TextWrapping = TextWrapping.Wrap, FontSize = 11, Margin = new Thickness(0, 0, 0, 2) };
            line.SetResourceReference(TextBlock.ForegroundProperty, isWarning ? "ErrorBrush" : "InkBrush");
            WarningList.Children.Add(line);
        }
    }

    // ---------------------------------------------------------------- sprites

    private List<OverworldSprite> Sprites()
    {
        lock (SpriteLists)
        {
            if (!SpriteLists.TryGetValue(_project.Root, out var list))
            {
                try { list = OverworldSprites.Load(_project.Root); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { list = []; }
                SpriteLists[_project.Root] = list;
            }
            return list;
        }
    }

    private string? PlayerGfx => _playerGfx ??= new[] { "OBJ_EVENT_GFX_BRENDAN_NORMAL", "OBJ_EVENT_GFX_MAY_NORMAL" }
        .FirstOrDefault(g => Sprites().Any(s => s.GfxId == g)) ?? "";

    private string? _graphicsH;

    /// <summary>
    /// Pics whose INCBIN lists several files (e.g. the player: walking.4bpp + running.4bpp) have no PngPath in the sprite
    /// list; the first file holds the standing frames.
    /// </summary>
    private string? FirstIncbinPng(string picSymbol)
    {
        _graphicsH ??= File.Exists(_project.FullPath("src/data/object_events/object_event_graphics.h"))
            ? File.ReadAllText(_project.FullPath("src/data/object_events/object_event_graphics.h")) : "";
        // INCBIN (built path → .png) or INCGFX (expansion 1.13+, names the .png directly).
        return GraphicsIncludes.BySymbol(_graphicsH).TryGetValue(picSymbol, out var inc) && inc.SourcePath.EndsWith(".png", StringComparison.Ordinal)
            ? _project.FullPath(inc.SourcePath) : null;
    }

    /// <summary>First frame for the facing (sAnimTable_Standard: south, north, west; east = west mirrored).</summary>
    private BitmapSource? Sprite(string gfx, Facing facing)
    {
        if (gfx.Length == 0) return null;
        if (_sprites.TryGetValue((gfx, facing), out var cached)) return cached;
        BitmapSource? result = null;
        try
        {
            var s = Sprites().FirstOrDefault(x => x.GfxId == gfx);
            var png = s is { PngPath.Length: > 0 } && File.Exists(_project.FullPath(s.PngPath)) ? _project.FullPath(s.PngPath)
                : s?.PicSymbol is { Length: > 0 } pic ? FirstIncbinPng(pic) : null;
            if (s != null && png != null && File.Exists(png) && s.Width > 0 && s.Height > 0)
            {
                var pal = s.PalettePath != null && File.Exists(_project.FullPath(s.PalettePath)) ? JascPalette.Load(_project.FullPath(s.PalettePath)) : null;
                var sheet = OverworldSprites.Render(Png.Load(png), pal);
                if (sheet.Width >= s.Width && sheet.Height >= s.Height)
                {
                    int frames = sheet.Width / s.Width;
                    int idx = facing switch { Facing.Down => 0, Facing.Up => 1, _ => 2 };
                    if (idx >= frames) idx = 0;
                    bool mirror = facing == Facing.Right && idx == 2;
                    var img = new RgbaImage(s.Width, s.Height);
                    for (int y = 0; y < s.Height; y++)
                        for (int x = 0; x < s.Width; x++)
                            img[x, y] = sheet[idx * s.Width + (mirror ? s.Width - 1 - x : x), y];
                    result = Imaging.ToBitmap(img);
                }
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            result = null; // fall back to a marker
        }
        _sprites[(gfx, facing)] = result;
        return result;
    }

    // ---------------------------------------------------------------- drawing

    private int MapW => Math.Max(1, _scene.Width);
    private int MapH => Math.Max(1, _scene.Height);
    private double T => MapRenderer.BlockSize * _zoom;

    private void UpdateSize()
    {
        Surface.Width = MapW * T;
        Surface.Height = MapH * T;
        Surface.InvalidateVisual();
    }

    private Point Centre(double x, double y) => new((x + 0.5) * T, (y + 0.5) * T);

    /// <summary>Centres the view on the paths (or the player) so large maps open where the action is.</summary>
    private void ScrollToAction()
    {
        var tracks = _sim.Tracks.ToList();
        var points = tracks.SelectMany(t => t.Steps.Select(s => (X: (double)s.X, Y: (double)s.Y)).Prepend((t.StartX, t.StartY))).ToList();
        if (points.Count == 0) points.Add((_px, _py));
        var c = Centre(points.Average(p => p.X), points.Average(p => p.Y));
        Scroller.ScrollToHorizontalOffset(Math.Max(0, c.X - Scroller.ViewportWidth / 2));
        Scroller.ScrollToVerticalOffset(Math.Max(0, c.Y - Scroller.ViewportHeight / 2));
    }

    private FormattedText Text(string s, double size, Brush brush, bool bold = false) =>
        new(s, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, bold ? FontWeights.Bold : FontWeights.Normal, FontStretches.Normal),
            size, brush, VisualTreeHelper.GetDpi(this).PixelsPerDip);

    private static SolidColorBrush Frozen(Color c, byte alpha = 255)
    {
        var b = new SolidColorBrush(Color.FromArgb(alpha, c.R, c.G, c.B));
        b.Freeze();
        return b;
    }

    private void Paint(DrawingContext dc)
    {
        dc.DrawRectangle(Frozen(Color.FromRgb(0x1F, 0x29, 0x37)), null, new Rect(0, 0, MapW * T, MapH * T));
        if (_map != null) dc.DrawImage(_map, new Rect(0, 0, _map.PixelWidth * _zoom, _map.PixelHeight * _zoom));
        if (ShowBlocked.IsChecked == true && _scene.Grid is { } g)
        {
            var red = Frozen(Colors.Red, 70);
            for (int y = 0; y < g.Height; y++)
                for (int x = 0; x < g.Width; x++)
                    if (g.Collision(x, y) != 0) dc.DrawRectangle(red, null, new Rect(x * T, y * T, T, T));
        }

        var moving = _sim.Actors.Keys.ToHashSet();
        if (ShowPeople.IsChecked == true)
            foreach (var o in _scene.Objects.Where(o => !moving.Contains(KeyOf(o))))
                DrawActor(dc, o.GraphicsId, o.X, o.Y, o.InitialFacing, null, Colors.White, 1.0);

        // Paths, step numbers, warnings, end tiles.
        int trackNo = 0;
        foreach (var t in _sim.Tracks)
        {
            var color = _colors[t.Key];
            var brush = Frozen(color);
            double off = ((trackNo++ % 3) - 1) * 2.5; // parallel paths stay distinguishable
            var pen = new Pen(brush, 3) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
            var dashed = new Pen(brush, 3) { DashStyle = DashStyles.Dash };
            foreach (var s in t.Steps)
            {
                if (s.FromX == s.X && s.FromY == s.Y) continue;
                var a = Centre(s.FromX, s.FromY) + new Vector(off, off);
                var b = Centre(s.X, s.Y) + new Vector(off, off);
                dc.DrawLine(s.Kind == StepKind.Move ? pen : dashed, a, b);
                if (s.Kind != StepKind.Place) Arrow(dc, brush, a, b);
            }
            if (ShowNumbers.IsChecked == true)
                foreach (var s in t.Steps.Where(s => s.FromX != s.X || s.FromY != s.Y))
                    Number(dc, brush, Centre(s.X, s.Y) + new Vector(off - T * 0.22, off - T * 0.22), s.Index.ToString());
            foreach (var s in t.Steps.Where(s => s.Warning != null))
            {
                var c = Centre(s.X, s.Y);
                var warn = new Pen(Frozen(Colors.Red), 3);
                double r = T * 0.3;
                dc.DrawLine(warn, c + new Vector(-r, -r), c + new Vector(r, r));
                dc.DrawLine(warn, c + new Vector(-r, r), c + new Vector(r, -r));
            }
            bool final = ReferenceEquals(t, _sim.Tracks.Last(x => x.Key == t.Key));
            var endPen = new Pen(brush, final ? 3 : 1.5) { DashStyle = final ? DashStyles.Solid : DashStyles.Dot };
            dc.DrawRoundedRectangle(Frozen(color, final ? (byte)60 : (byte)25), endPen, new Rect(t.EndX * T + 1, t.EndY * T + 1, T - 2, T - 2), 3, 3);
            if (final) FacingMark(dc, brush, t.EndX, t.EndY, t.EndFacing);
        }

        // Ghosts at the end positions while the play-through has not reached them.
        if (_frame < _sim.TotalFrames)
            foreach (var key in _sim.Tracks.Select(t => t.Key).Distinct())
            {
                var (ex, ey, ef) = _sim.EndOf(key);
                DrawActor(dc, GfxOf(key), ex, ey, ef, null, _colors.GetValueOrDefault(key, Colors.White), 0.4);
            }

        // Actors at the current frame.
        foreach (var key in moving.OrderBy(k => k == MovementSimulator.PlayerKey ? 1 : 0))
        {
            var (x, y, f) = _sim.StateAt(key, _frame);
            DrawActor(dc, GfxOf(key), x, y, f, _sim.Actors[key].Name, _colors.GetValueOrDefault(key, Colors.White), 1.0);
        }
    }

    private string GfxOf(string key)
    {
        if (key == MovementSimulator.PlayerKey) return PlayerGfx ?? "";
        return int.TryParse(key.AsSpan(3), out var i) && i < _scene.Objects.Count ? _scene.Objects[i].GraphicsId : "";
    }

    private void DrawActor(DrawingContext dc, string gfx, double x, double y, Facing facing, string? label, Color color, double opacity)
    {
        if (opacity < 1) dc.PushOpacity(opacity);
        var sprite = Sprite(gfx, facing);
        if (sprite != null)
        {
            double w = sprite.PixelWidth * _zoom, h = sprite.PixelHeight * _zoom;
            dc.DrawImage(sprite, new Rect((x * 16 + 8) * _zoom - w / 2, (y * 16 + 16) * _zoom - h, w, h));
        }
        else
        {
            var c = Centre(x, y);
            dc.DrawEllipse(Frozen(color), new Pen(Brushes.Black, 1.5), c, T * 0.35, T * 0.35);
            FacingMark(dc, Brushes.Black, x, y, facing);
        }
        if (opacity < 1) dc.Pop();
        if (label == null) return;
        var text = Text(label, Math.Max(9, 4.5 * _zoom), Brushes.White, bold: true);
        var origin = new Point((x + 0.5) * T - text.Width / 2, y * T - T * (sprite != null && sprite.PixelHeight > 16 ? 1.0 : 0.05) - text.Height);
        dc.DrawRoundedRectangle(Frozen(color, 220), null, new Rect(origin.X - 3, origin.Y - 1, text.Width + 6, text.Height + 2), 3, 3);
        dc.DrawText(text, origin);
    }

    private void FacingMark(DrawingContext dc, Brush brush, double x, double y, Facing f)
    {
        var c = Centre(x, y);
        var (dx, dy) = f.Delta();
        var tip = c + new Vector(dx, dy) * T * 0.45;
        var side = new Vector(-dy, dx) * T * 0.15;
        var basePt = c + new Vector(dx, dy) * T * 0.25;
        Triangle(dc, brush, tip, basePt + side, basePt - side);
    }

    private void Arrow(DrawingContext dc, Brush brush, Point a, Point b)
    {
        var v = b - a;
        if (v.Length < 1) return;
        v.Normalize();
        var mid = a + (b - a) / 2;
        var side = new Vector(-v.Y, v.X) * T * 0.16;
        var tip = mid + v * T * 0.16;
        var back = mid - v * T * 0.12;
        Triangle(dc, brush, tip, back + side, back - side);
    }

    private static void Triangle(DrawingContext dc, Brush brush, Point p1, Point p2, Point p3)
    {
        var geo = new StreamGeometry();
        using (var g = geo.Open())
        {
            g.BeginFigure(p1, true, true);
            g.LineTo(p2, false, false);
            g.LineTo(p3, false, false);
        }
        geo.Freeze();
        dc.DrawGeometry(brush, null, geo);
    }

    private void Number(DrawingContext dc, Brush brush, Point centre, string n)
    {
        var text = Text(n, Math.Max(8, 4 * _zoom), Brushes.White, bold: true);
        double r = Math.Max(text.Width, text.Height) / 2 + 1.5;
        dc.DrawEllipse(brush, new Pen(Brushes.White, 1), centre, r, r);
        dc.DrawText(text, new Point(centre.X - text.Width / 2, centre.Y - text.Height / 2));
    }

    // ---------------------------------------------------------------- interaction

    private (int X, int Y)? TileAt(MouseEventArgs e)
    {
        var p = e.GetPosition(Surface);
        int x = (int)Math.Floor(p.X / T), y = (int)Math.Floor(p.Y / T);
        return x >= 0 && y >= 0 && x < MapW && y < MapH ? (x, y) : null;
    }

    private void OnMapClick(object sender, MouseButtonEventArgs e)
    {
        if (TileAt(e) is not { } t) return;
        SetPlayer(t.X, t.Y, _pf);
    }

    private void OnMapMove(object sender, MouseEventArgs e)
    {
        if (TileAt(e) is not { } t) { HoverText.Text = ""; return; }
        var info = $"Tile ({t.X},{t.Y})";
        if (_scene.Grid is { } g)
            info += $"  ·  metatile 0x{g.MetatileId(t.X, t.Y):X3}  ·  {(g.Collision(t.X, t.Y) != 0 ? "blocked" : "walkable")}  ·  elevation {g.Elevation(t.X, t.Y)}";
        var here = _scene.Objects.Where(o => o.X == t.X && o.Y == t.Y).Select(o => o.Name).ToList();
        if (here.Count > 0) info += "  ·  " + string.Join(", ", here);
        HoverText.Text = info + "   (click = player starts here)";
    }

    private void OnPlayerFacingChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready || PlayerFacing.SelectedItem is not Facing f || f == _pf) return;
        SetPlayer(_px, _py, f);
    }

    private void OnResetPlayer(object sender, RoutedEventArgs e) => SetPlayer(_defaultPlayer.X, _defaultPlayer.Y, _defaultPlayer.F);

    private void OnZoomChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready) return;
        _zoom = ZoomChoice.SelectedIndex + 1;
        UpdateSize();
    }

    private void OnWheel(object sender, MouseWheelEventArgs e)
    {
        if ((Keyboard.Modifiers & ModifierKeys.Control) == 0) return;
        ZoomChoice.SelectedIndex = Math.Clamp(ZoomChoice.SelectedIndex + (e.Delta > 0 ? 1 : -1), 0, 3);
        e.Handled = true;
    }

    private void OnViewChanged(object sender, RoutedEventArgs e)
    {
        if (_ready) Surface.InvalidateVisual();
    }

    private void OnPlay(object sender, RoutedEventArgs e)
    {
        if (_timer.IsEnabled) { StopPlaying(); return; }
        if (_frame >= _sim.TotalFrames) _frame = 0;
        _clockStartFrame = _frame;
        _clock.Restart();
        _timer.Start();
        PlayButton.Content = "⏸ Pause";
    }

    private void StopPlaying()
    {
        _timer.Stop();
        _clock.Stop();
        PlayButton.Content = "▶ Play";
    }

    private void OnRewind(object sender, RoutedEventArgs e)
    {
        StopPlaying();
        TimeSlider.Value = 0;
        _frame = 0;
        UpdateTimeText();
        Surface.InvalidateVisual();
    }

    private void OnTick(object? sender, EventArgs e)
    {
        double speed = SpeedChoice.SelectedItem is ComboBoxItem { Tag: string tag } && double.TryParse(tag, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : 1;
        _frame = Math.Min(_sim.TotalFrames, _clockStartFrame + _clock.Elapsed.TotalSeconds * 60 * speed);
        TimeSlider.Value = _frame;
        if (_frame >= _sim.TotalFrames) StopPlaying();
        UpdateTimeText();
        Surface.InvalidateVisual();
    }

    private void OnTimeChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_ready) return;
        if (!_timer.IsEnabled) _frame = e.NewValue;
        UpdateTimeText();
        Surface.InvalidateVisual();
    }

    private void UpdateTimeText()
    {
        var phase = _sim.Phases.LastOrDefault(p => p.StartFrame <= _frame);
        TimeText.Text = $"Frame {_frame:0} of {_sim.TotalFrames} (≈ {_frame / 60:0.0} of {_sim.TotalFrames / 60.0:0.0} s)" +
                        (phase != null && _sim.Phases.Count > 1 ? $"  ·  phase {phase.Index + 1}" : "");
    }
}
