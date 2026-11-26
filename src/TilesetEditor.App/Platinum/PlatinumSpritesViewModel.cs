using System.IO;
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
/// Sprites of a Platinum project: the battle pictures of all trainer classes (replace, new class) and the overworld
/// characters (preview of every frame, replace, new character). Pokémon sprites are in the Pokémon module.
/// </summary>
public sealed class PlatinumSpritesViewModel : ObservableObject, IToolkitModule
{
    private readonly DeferredLoad _reload;
    private readonly ProjectContext _ctx;
    private readonly WrapPanel _trainers = new();
    private readonly WrapPanel _overworld = new();
    private readonly TextBox _owFilter = new() { Width = 260, Margin = new Thickness(0, 0, 8, 0) };
    private readonly StackPanel _owDetail = new() { Margin = new Thickness(0, 8, 0, 8) };
    private readonly TextBlock _trainerHint = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 8) };
    private List<OverworldSprite> _sprites = [];
    private readonly Dictionary<string, BitmapSource?> _owCache = new(StringComparer.Ordinal);
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, BitmapSource?> _owTiles = new(StringComparer.Ordinal);

    public PlatinumSpritesViewModel(ProjectContext ctx)
    {
        _ctx = ctx;
        _reload = new DeferredLoad(Refresh);
        _ctx.PlatinumChanged += (_, _) => _reload.Request();
        _owFilter.TextChanged += (_, _) => FillOverworld();
        View = BuildView();
        _reload.Attach(View);
    }

    public string Title => "Sprites";
    public string Description => "Trainer pictures, overworld characters";
    public bool IsAvailable => true;
    public override string ToString() => Title;
    public FrameworkElement View { get; }
    public bool HasUnsavedChanges => false;
    public string? UnsavedChangesDescription => null;

    private FrameworkElement BuildView()
    {
        var tabs = new TabControl { Margin = new Thickness(16) };
        tabs.Items.Add(new TabItem { Header = "Player character", Content = new ScrollViewer { Content = _player, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Padding = new Thickness(0, 8, 0, 0) } });
        var t = new StackPanel();
        t.Children.Add(Ui.Row(Ui.Button("+ New trainer class…", NewClass, "A new class with its own name and battle picture.")));
        t.Children.Add(_trainerHint);
        t.Children.Add(_trainers);
        tabs.Items.Add(new TabItem { Header = "Trainer pictures", Content = new ScrollViewer { Content = t, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Padding = new Thickness(0, 8, 0, 0) } });

        var o = new StackPanel();
        System.Windows.Automation.AutomationProperties.SetName(_owFilter, "Filter overworld sprites");
        o.Children.Add(Ui.Row(new TextBlock { Text = "Filter ", VerticalAlignment = VerticalAlignment.Center }, _owFilter,
            Ui.Button("+ New character…", NewCharacter, "A new OBJ_EVENT_GFX with its own picture (32×512 like the other people).")));
        o.Children.Add(_owDetail);
        o.Children.Add(_overworld);
        tabs.Items.Add(new TabItem { Header = "Overworld", Content = new ScrollViewer { Content = o, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Padding = new Thickness(0, 8, 0, 0) } });
        return tabs;
    }

    private readonly StackPanel _player = new();

    private void Refresh()
    {
        _owCache.Clear(); _owTiles.Clear();
        FillPlayer();
        FillTrainers();
        var root = _ctx.Root;
        _sprites = root != null && _ctx.Platinum != null ? PlatinumOverworld.List(root) : [];
        _owDetail.Children.Clear();
        if (root != null && _ctx.Platinum != null && !PlatinumOverworld.IsSupported(root))
            _owDetail.Children.Add(Ui.Hint("This pokeplatinum keeps the overworld sprites as finished binary files (res/prebuilt/data/mmodel). Newer pokeplatinum builds them from PNG pictures – update the project to manage them here."));
        FillOverworld();
    }

    private static Border Tile(UIElement content, string tip, Action click)
    {
        var b = new Border
        {
            Child = content, Width = 132, Margin = new Thickness(0, 0, 8, 8), Padding = new Thickness(6), CornerRadius = new CornerRadius(4),
            BorderBrush = (Brush)Application.Current.FindResource("BorderBrush"), BorderThickness = new Thickness(1),
            Background = (Brush)Application.Current.FindResource("SurfaceBrush"), ToolTip = tip, Cursor = System.Windows.Input.Cursors.Hand,
        };
        b.MouseLeftButtonUp += (_, _) => click();
        return b;
    }

    // ------------------------------------------------------------------ player character

    private void FillPlayer()
    {
        _player.Children.Clear();
        var root = _ctx.Root;
        if (root == null || _ctx.Platinum == null) return;
        _player.Children.Add(Ui.Hint("Every picture of the player characters in one place. Click a picture to replace it with a PNG of the same size – " +
                                     "you choose whether it keeps the old colours (recommended when the palette is shared) or gets its own."));
        var all = PlatinumProtagonist.List(root);
        foreach (var female in new[] { false, true })
        {
            _player.Children.Add(Ui.Title(female ? "Dawn (female)" : "Lucas (male)"));
            var wrap = new WrapPanel();
            foreach (var pic in all.Where(p => p.Female == female))
            {
                var img = Ui.Picture(pic.FrameSize == 32 ? 2 : 1);
                Imaging.LoadAsync(img, () =>
                {
                    var png = Png.Load(ProjectPaths.Combine(root, pic.PngRel));
                    int h = pic.FrameSize > 0 ? Math.Min(png.Height, pic.FrameSize) : png.Height;
                    var frame = new RgbaImage(png.Width, h);
                    for (int y = 0; y < h; y++)
                        for (int x = 0; x < png.Width; x++)
                        {
                            int i = y * png.Width + x;
                            frame[x, y] = png.Indices != null && png.Indices[i] == 0 ? Rgba32.Transparent : png.Rgba.Pixels[i];
                        }
                    return frame;
                });
                var stack = new StackPanel();
                stack.Children.Add(new Border { Child = img, Height = 100, Background = Imaging.Checkerboard(8), Padding = new Thickness(2) });
                stack.Children.Add(new TextBlock { Text = pic.Label, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
                stack.Children.Add(new TextBlock { Text = Path.GetFileName(pic.PngRel), FontSize = 10, Foreground = Brushes.Gray });
                var tile = Tile(stack, $"{pic.PngRel}\n{pic.Hint}\nClick to replace.", () => ReplacePlayerPicture(pic));
                tile.Width = 170;
                wrap.Children.Add(tile);
            }
            if (PlatinumTownMapIcon.Exists(root))
            {
                bool girl = female;
                var icon = Ui.Picture(4);
                Imaging.LoadAsync(icon, () => PlatinumTownMapIcon.Icon(root, girl));
                var stack = new StackPanel();
                stack.Children.Add(new Border { Child = icon, Height = 100, Background = Imaging.Checkerboard(8), Padding = new Thickness(2) });
                stack.Children.Add(new TextBlock { Text = "Town Map icon", FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
                stack.Children.Add(new TextBlock { Text = Path.GetFileName(PlatinumTownMapIcon.IconsFile), FontSize = 10, Foreground = Brushes.Gray });
                var tile = Tile(stack, $"{PlatinumTownMapIcon.IconsFile}\nThe head shown on the Town Map where the player is (16×16).\nClick to replace.", () => ReplaceTownMapIcon(girl));
                tile.Width = 170;
                wrap.Children.Add(tile);
            }
            _player.Children.Add(wrap);
        }
    }

    private void ReplaceTownMapIcon(bool female)
    {
        if (_ctx.Root is not { } root) return;
        var dlg = new Microsoft.Win32.OpenFileDialog { Filter = "PNG pictures|*.png", Title = $"Town Map icon of {(female ? "Dawn" : "Lucas")} (16×16; other sizes are scaled)" };
        if (dlg.ShowDialog(Application.Current.MainWindow) != true) return;
        try
        {
            var files = PlatinumTownMapIcon.PlanReplace(root, female, Png.Load(dlg.FileName));
            var note = female ? "Dawn's head shares its palette with the map cursor: up to 13 colours, the cursor keeps its two." : "Up to 15 colours.";
            if (PlatinumModules.Write(_ctx, "Town Map icon", files, note, "Build the ROM afterwards.")) FillPlayer();
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or PngFormatException or FormatException) { ProjectWriter.Warn("Town Map icon", ex.Message); }
    }

    private void ReplacePlayerPicture(ProtagonistPicture pic)
    {
        var root = _ctx.Root;
        if (root == null) return;
        var dlg = new Microsoft.Win32.OpenFileDialog { Filter = "PNG pictures|*.png", Title = $"New picture: {pic.Label} ({Path.GetFileName(pic.PngRel)})" };
        if (dlg.ShowDialog(Application.Current.MainWindow) != true) return;
        var keep = AppDialog.Show(Application.Current.MainWindow,
            "Use the colours of the old picture?\n\nYes – the new picture is drawn with the old palette (safe when other pictures share it).\nNo – the picture keeps its own colours (an indexed PNG keeps its palette exactly; colour 0 is transparent).",
            pic.Label, MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
        if (keep == MessageBoxResult.Cancel) return;
        try
        {
            var bytes = PlatinumGraphics.Replace(root, pic.PngRel, Png.Load(dlg.FileName), keep == MessageBoxResult.Yes);
            if (PlatinumModules.Write(_ctx, pic.Label, [new PlatinumFileChange(pic.PngRel, bytes, "new picture")], pic.Hint, "Build the ROM afterwards.")) { _owCache.Clear(); _owTiles.Clear(); FillPlayer(); }
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or PngFormatException) { ProjectWriter.Warn(pic.Label, ex.Message); }
    }

    // ------------------------------------------------------------------ trainer pictures

    private void FillTrainers()
    {
        _trainers.Children.Clear();
        var p = _ctx.Platinum;
        if (p == null) return;
        var names = p.TextBank("trainer_class_names").Select(x => x.Text).ToList();
        _trainerHint.Text = $"{p.TrainerClasses.Count} trainer classes. Click a picture to look at it and its battle animation (Barry, gym leaders, Elite Four …), replace frames or the whole sheet and change the timing. Every trainer of the class uses it.";
        for (int i = 0; i < p.TrainerClasses.Count; i++)
        {
            var cls = p.TrainerClasses[i];
            var img = Ui.Picture();
            var root = p.Root;
            var frames = new TextBlock { FontSize = 10, Foreground = (Brush)Application.Current.FindResource("AccentDarkBrush") };
            Imaging.LoadAsync(img, () =>
            {
                if (PlatinumTrainers.ClassPicture(root, cls) is not { } path) return null;
                var png = Png.Load(path);
                int n = PlatinumTrainerAnimations.FrameCount(png);
                if (n > 1) frames.Dispatcher.BeginInvoke(() => frames.Text = $"▶ animated · {n} frames");
                // The still picture: the first frame of the sheet.
                var rgba = new RgbaImage(png.Width, Math.Min(png.Height, PlatinumTrainerAnimations.Size));
                for (int k = 0; k < rgba.Pixels.Length; k++)
                    rgba.Pixels[k] = png.Indices != null && png.Indices[k] == 0 ? Rgba32.Transparent : png.Rgba.Pixels[k];
                return rgba;
            });
            var stack = new StackPanel();
            stack.Children.Add(new Border { Child = img, Width = 84, Height = 84, Background = Imaging.Checkerboard(8), HorizontalAlignment = HorizontalAlignment.Center });
            stack.Children.Add(new TextBlock { Text = i < names.Count ? names[i] : "", FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });
            stack.Children.Add(new TextBlock { Text = PlatinumNames.Pretty(cls, "TRAINER_CLASS_"), FontSize = 10, Foreground = Brushes.Gray, TextTrimming = TextTrimming.CharacterEllipsis });
            stack.Children.Add(frames);
            var title = i < names.Count ? names[i] : cls;
            _trainers.Children.Add(Tile(stack, $"{cls} – click to look at the picture and its animation, or replace it", () => OpenTrainerPicture(cls, title)));
        }
    }

    private void OpenTrainerPicture(string cls, string title)
    {
        if (_ctx.Root == null) return;
        var w = new TrainerAnimationWindow(_ctx, cls, title);
        w.ShowDialog();
        if (w.Changed) FillTrainers();
    }

    private void NewClass()
    {
        var p = _ctx.Platinum;
        if (p == null) return;
        var dlg = new NewTrainerClassWindow(p.Root, p.TrainerClasses, null);
        if (dlg.ShowDialog() != true || dlg.Result is not { } c) return;
        try
        {
            var (files, notes) = PlatinumTrainerClasses.PlanAdd(p.Root, c);
            PlatinumModules.Write(_ctx, "New trainer class", files, [.. notes, $"Choose {c.Constant} for trainers in the Trainers module, then build the ROM."]);
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or PngFormatException or System.Text.Json.JsonException) { ProjectWriter.Warn("New trainer class", ex.Message); }
    }

    // ------------------------------------------------------------------ overworld

    private BitmapSource? OverworldFrame(string pngRel, int frame)
    {
        var key = pngRel + "#" + frame;
        if (_owCache.TryGetValue(key, out var b)) return b;
        b = null;
        try { if (_ctx.Root != null && PlatinumOverworld.Frame(_ctx.Root, pngRel, frame) is { } img) b = Imaging.ToBitmap(img); }
        catch (Exception ex) when (ex is IOException or PngFormatException) { }
        return _owCache[key] = b;
    }

    private void FillOverworld()
    {
        _overworld.Children.Clear();
        var f = _owFilter.Text.Trim();
        foreach (var s in _sprites.Where(s => s.PngRel != null && (f.Length == 0 || s.Gfx.Contains(f, StringComparison.OrdinalIgnoreCase) || s.PngRel.Contains(f, StringComparison.OrdinalIgnoreCase))))
        {
            var img = Ui.Picture(2);
            var root = _ctx.Root;
            var png = s.PngRel!;
            if (root != null) Imaging.LoadAsync(img, () => PlatinumOverworld.Frame(root, png, 0), _owTiles, png);
            var stack = new StackPanel();
            stack.Children.Add(new Border { Child = img, Height = 68, Background = Imaging.Checkerboard(8), HorizontalAlignment = HorizontalAlignment.Center, Padding = new Thickness(2) });
            stack.Children.Add(new TextBlock { Text = PlatinumNames.Pretty(s.Gfx, "OBJ_EVENT_GFX_"), FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });
            stack.Children.Add(new TextBlock { Text = Path.GetFileName(s.PngRel), FontSize = 10, Foreground = Brushes.Gray, TextTrimming = TextTrimming.CharacterEllipsis });
            _overworld.Children.Add(Tile(stack, $"{s.Gfx} · {s.PngRel}", () => ShowSprite(s)));
        }
    }

    private void ShowSprite(OverworldSprite s)
    {
        _owDetail.Children.Clear();
        if (s.PngRel == null || _ctx.Root == null) return;
        _owDetail.Children.Add(new TextBlock { Text = $"{s.Gfx}  ·  {s.PngRel}", FontWeight = FontWeights.SemiBold, FontSize = 14 });
        var frames = new WrapPanel { Margin = new Thickness(0, 4, 0, 4) };
        int count = 1;
        try { var png = Png.Load(ProjectPaths.Combine(_ctx.Root, s.PngRel)); count = Math.Max(1, png.Height / Math.Max(1, png.Width)); }
        catch (Exception ex) when (ex is IOException or PngFormatException) { }
        for (int i = 0; i < count; i++)
        {
            var img = Ui.Picture(2);
            img.Source = OverworldFrame(s.PngRel, i);
            frames.Children.Add(new Border { Child = img, Background = Imaging.Checkerboard(8), Margin = new Thickness(0, 0, 4, 4), ToolTip = $"Frame {i + 1}" });
        }
        _owDetail.Children.Add(frames);
        _owDetail.Children.Add(Ui.Row(
            Ui.Button("Replace picture…", () => ReplaceSprite(s), "PNG of the same size (frames of 32×32 below each other), or only the different frames as the game stores them (e.g. 24 for the player's 32). An indexed PNG keeps its own palette (colour 0 = transparent); other PNGs: up to 15 colours, transparent or the top-left colour = background."),
            Ui.Button("Show file", () => Ui.OpenFolder(ProjectPaths.Combine(_ctx.Root!, s.PngRel)))));
    }

    private void ReplaceSprite(OverworldSprite s)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog { Filter = "PNG pictures|*.png", Title = $"New picture for {s.Gfx}" };
        if (dlg.ShowDialog(Application.Current.MainWindow) != true || _ctx.Root == null) return;
        try
        {
            var change = PlatinumOverworld.PlanReplace(_ctx.Root, s, Png.Load(dlg.FileName));
            if (PlatinumModules.Write(_ctx, "Replace overworld sprite", [change], "Every OBJ_EVENT_GFX that uses this picture changes.", "Build the ROM afterwards.")) { _owCache.Clear(); _owTiles.Clear(); FillOverworld(); ShowSprite(s); }
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or PngFormatException) { ProjectWriter.Warn("Replace overworld sprite", ex.Message); }
    }

    private void NewCharacter()
    {
        var root = _ctx.Root;
        if (root == null) return;
        if (!PlatinumOverworld.IsSupported(root)) { ProjectWriter.Warn("New character", "This pokeplatinum does not build its overworld sprites from PNG pictures yet – update the project first."); return; }
        var file = new Microsoft.Win32.OpenFileDialog { Filter = "PNG pictures|*.png", Title = "Picture of the new character (32×512: 16 frames of 32×32, like npc/youngster.png)" };
        if (file.ShowDialog(Application.Current.MainWindow) != true) return;
        var name = Path.GetFileNameWithoutExtension(file.FileName);
        var dlg = new InputDialog("New character",
            "The new character gets its own OBJ_EVENT_GFX constant; how it moves, its animation and shadow are copied from a similar character (a normal person: OBJ_EVENT_GFX_YOUNGSTER).",
            ("Constant (OBJ_EVENT_GFX_…)", "OBJ_EVENT_GFX_" + name.ToUpperInvariant()), ("File name (npc/<name>.png)", name.ToLowerInvariant()), ("Copy behaviour from", "OBJ_EVENT_GFX_YOUNGSTER"));
        if (dlg.ShowDialog() != true) return;
        try
        {
            var constant = dlg.Values[0].Trim().ToUpperInvariant();
            if (!constant.StartsWith("OBJ_EVENT_GFX_", StringComparison.Ordinal)) constant = "OBJ_EVENT_GFX_" + constant;
            var (files, notes) = PlatinumOverworld.PlanAdd(root, constant, dlg.Values[1], dlg.Values[2].Trim().ToUpperInvariant(), Png.Load(file.FileName));
            if (PlatinumModules.Write(_ctx, "New overworld character", files, [.. notes])) Refresh();
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or PngFormatException) { ProjectWriter.Warn("New character", ex.Message); }
    }
}
