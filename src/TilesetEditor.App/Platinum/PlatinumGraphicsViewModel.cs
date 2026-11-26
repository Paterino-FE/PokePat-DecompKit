using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using TilesetEditor.App.Infrastructure;
using TilesetEditor.App.Shell;
using TilesetEditor.Core.Imaging;
using TilesetEditor.Core.Platinum;
using TilesetEditor.Core.Project;

namespace TilesetEditor.App.Platinum;

/// <summary>
/// Graphics of a Platinum project that need care: the battle healthboxes. Shows the four pictures, checks that they are
/// true 4-bit PNGs with the palette of player_singles.png in the same order, repairs both and replaces pictures.
/// </summary>
public sealed class PlatinumGraphicsViewModel : ObservableObject, IToolkitModule
{
    private readonly DeferredLoad _reload;
    private readonly ProjectContext _ctx;
    private readonly StackPanel _panel = new() { Margin = new Thickness(20), MaxWidth = 1100, HorizontalAlignment = HorizontalAlignment.Left };

    public PlatinumGraphicsViewModel(ProjectContext ctx)
    {
        _ctx = ctx;
        _reload = new DeferredLoad(() => { Refresh(); LoadAll(); ShowCategory(); });
        _ctx.PlatinumChanged += (_, _) => _reload.Request();
        var tabs = new TabControl { Margin = new Thickness(8) };
        tabs.Items.Add(new TabItem { Header = "By category", Content = BuildCategories() });
        tabs.Items.Add(new TabItem { Header = "All pictures", Content = BuildBrowser() });
        tabs.Items.Add(new TabItem { Header = "Battle healthboxes", Content = new ScrollViewer { Content = _panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto } });
        View = tabs;
        _reload.Attach(View);
        Refresh();
        LoadAll();
    }

    public string Title => "Graphics";
    public string Description => "Every picture of res/graphics, healthboxes";
    public bool IsAvailable => true;
    public override string ToString() => Title;
    public FrameworkElement View { get; }
    public bool HasUnsavedChanges => false;
    public string? UnsavedChangesDescription => null;

    // ------------------------------------------------------------------ all pictures

    private readonly ListBox _files = new() { BorderThickness = new Thickness(0), FontFamily = new FontFamily("Consolas"), FontSize = 11 };
    private readonly TextBox _filter = new();
    private readonly ComboBox _group = new() { Margin = new Thickness(0, 0, 0, 6) };
    private readonly StackPanel _info = new() { Margin = new Thickness(16, 0, 0, 0) };
    private List<string> _all = [];

    private FrameworkElement BuildBrowser()
    {
        var grid = new Grid { Margin = new Thickness(8) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(340) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var left = Ui.FilteredList(_files, _filter, "Filter pictures");
        DockPanel.SetDock(_group, Dock.Top);
        left.Children.Insert(0, _group);
        _group.SelectionChanged += (_, _) => Filter();
        _filter.TextChanged += (_, _) => Filter();
        VirtualizingPanel.SetIsVirtualizing(_files, true);
        _files.SelectionChanged += (_, _) => ShowPicture();
        grid.Children.Add(left);
        var scroll = new ScrollViewer { Content = _info, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        Grid.SetColumn(scroll, 1);
        grid.Children.Add(scroll);
        _info.Children.Add(Ui.Hint("Choose a picture on the left. All pictures of res/graphics are listed by screen (bag, Pokédex, title screen, battle …)."));
        return grid;
    }

    private void LoadAll()
    {
        var root = _ctx.Root;
        _all = root != null && _ctx.Platinum != null ? PlatinumGraphics.List(root) : [];
        var groups = _all.Select(PlatinumGraphics.GroupOf).Distinct().OrderBy(g => g).ToList();
        _group.ItemsSource = new[] { $"All folders ({_all.Count} pictures)" }.Concat(groups).ToList();
        _group.SelectedIndex = 0;
        Filter();
    }

    private void Filter()
    {
        var f = _filter.Text.Trim();
        var g = _group.SelectedIndex > 0 ? _group.SelectedItem as string : null;
        _files.ItemsSource = _all.Where(x => (g == null || PlatinumGraphics.GroupOf(x) == g) && (f.Length == 0 || x.Contains(f, StringComparison.OrdinalIgnoreCase)))
            .Select(x => x[(PlatinumGraphics.Folder.Length + 1)..]).ToList();
    }

    private void ShowPicture()
    {
        _info.Children.Clear();
        if (_files.SelectedItem is not string shortPath) return;
        BuildPictureDetail(_info, PlatinumGraphics.Folder + "/" + shortPath, ShowPicture);
    }

    /// <summary>Preview, format, palette, checks and the actions of one picture, into <paramref name="_info"/>.</summary>
    private void BuildPictureDetail(StackPanel _info, string rel, Action refresh, Rgba32[]? matchPalette = null)
    {
        if (_ctx.Root is not { } root) return;
        var shortPath = rel.StartsWith(PlatinumGraphics.Folder + "/", StringComparison.Ordinal) ? rel[(PlatinumGraphics.Folder.Length + 1)..] : rel;
        try
        {
            var info = PlatinumGraphics.Inspect(root, rel);
            var png = Png.Load(ProjectPaths.Combine(root, rel));
            _info.Children.Add(new TextBlock { Text = shortPath, FontWeight = FontWeights.SemiBold, FontSize = 14 });
            _info.Children.Add(new TextBlock
            {
                Text = $"{info.Width}×{info.Height} · " + (info.Indexed ? $"{info.BitDepth}-bit indexed, {info.PaletteSize} palette entries, highest index used {info.MaxIndex}" : "not indexed (RGB/RGBA)"),
                FontFamily = new FontFamily("Consolas"), FontSize = 11, Margin = new Thickness(0, 2, 0, 6),
            });
            foreach (var pr in info.Problems) _info.Children.Add(new TextBlock { Text = "✗ " + pr, TextWrapping = TextWrapping.Wrap, MaxWidth = 700, HorizontalAlignment = HorizontalAlignment.Left, Foreground = (Brush)Application.Current.FindResource("AccentDarkBrush") });
            if (info.Problems.Count == 0) _info.Children.Add(new TextBlock { Text = "✓ No problems found.", Foreground = (Brush)Application.Current.FindResource("SuccessBrush") });
            foreach (var n in info.Notes) _info.Children.Add(Ui.Hint(n, 700));
            double scale = Math.Max(1, Math.Min(4, Math.Floor(512.0 / Math.Max(info.Width, info.Height))));
            var img = Ui.Picture(scale);
            img.Source = Imaging.ToBitmap(png.Rgba);
            _info.Children.Add(Fit(new Border { Child = img, Background = Imaging.Checkerboard(8) }, HorizontalAlignment.Left, new Thickness(0, 6, 0, 6)));
            if (png.Palette is { Length: > 0 } pal)
            {
                var swatches = new WrapPanel { MaxWidth = 560, HorizontalAlignment = HorizontalAlignment.Left };
                foreach (var c in pal.Take(info.BitDepth == 4 ? 16 : Math.Min(pal.Length, 256)))
                    swatches.Children.Add(new Border { Width = 14, Height = 14, Background = Imaging.Brush(c with { A = 255 }), BorderBrush = Brushes.Gray, BorderThickness = new Thickness(0.5), ToolTip = c.ToHex() });
                _info.Children.Add(swatches);
            }
            var buttons = Ui.Row(
                Ui.Button("Replace…", () => ReplacePicture(rel, refresh, matchPalette), "Same size. You choose whether the new picture uses the old colours (recommended when other pictures share the palette) or gets its own."),
                Ui.Button("Show file", () => Ui.OpenFolder(ProjectPaths.Combine(root, rel))));
            if (info.Indexed && info.BitDepth == 8 && info.MaxIndex < 16)
                buttons.Children.Insert(1, Ui.Button("Save as 4-bit", () =>
                {
                    if (PlatinumModules.Write(_ctx, "Save as 4-bit", [new PlatinumFileChange(rel, PlatinumGraphics.To4Bit(root, rel), "same picture, true 4-bit PNG")], "Pixels and colours stay the same.")) refresh();
                }));
            _info.Children.Add(buttons);
        }
        catch (Exception ex) when (ex is IOException or PngFormatException or InvalidDataException)
        {
            _info.Children.Add(Ui.Hint($"{rel} could not be read: {ex.Message}"));
        }
    }

    private void ReplacePicture(string rel, Action refresh, Rgba32[]? matchPalette)
    {
        var root = _ctx.Root;
        if (root == null) return;
        var dlg = new Microsoft.Win32.OpenFileDialog { Filter = "PNG pictures|*.png", Title = $"New {System.IO.Path.GetFileName(rel)}" };
        if (dlg.ShowDialog(Application.Current.MainWindow) != true) return;
        var keep = AppDialog.Show(Application.Current.MainWindow,
            "Use the colours of the old picture?\n\nYes – recommended: the new picture is drawn with the old palette, so other pictures that share the palette stay right.\nNo – the picture gets its own palette (only if this picture has its own colours).",
            "Replace picture", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
        if (keep == MessageBoxResult.Cancel) return;
        try
        {
            var bytes = PlatinumGraphics.Replace(root, rel, Png.Load(dlg.FileName), keep == MessageBoxResult.Yes, matchPalette);
            if (PlatinumModules.Write(_ctx, "Replace picture", [new PlatinumFileChange(rel, bytes, keep == MessageBoxResult.Yes ? "new picture in the old colours" : "new picture with its own palette")],
                    "Size and bit depth stay as before.", "Build the ROM afterwards and look at the screen in-game.")) refresh();
        }
        catch (Exception ex) when (ex is IOException or PngFormatException or InvalidDataException) { ProjectWriter.Warn("Replace picture", ex.Message); }
    }

    // ------------------------------------------------------------------ by category

    private readonly ListBox _categories = new() { BorderThickness = new Thickness(0), DisplayMemberPath = nameof(GraphicsCategory.Title) };
    private readonly StackPanel _categoryPanel = new() { Margin = new Thickness(16, 0, 0, 0) };
    private readonly StackPanel _categoryDetail = new() { Margin = new Thickness(0, 8, 0, 8) };

    private FrameworkElement BuildCategories()
    {
        var grid = new Grid { Margin = new Thickness(8) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(240) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        _categories.ItemsSource = PlatinumGraphicsCatalog.Categories;
        _categories.SelectionChanged += (_, _) => ShowCategory();
        grid.Children.Add(_categories);
        var scroll = new ScrollViewer { Content = _categoryPanel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        Grid.SetColumn(scroll, 1);
        grid.Children.Add(scroll);
        _categories.SelectedIndex = 0;
        return grid;
    }

    /// <summary>Shrinks a (large) picture to the space there is, never enlarges it: no sideways scrolling.</summary>
    private static Viewbox Fit(UIElement child, HorizontalAlignment align = HorizontalAlignment.Center, Thickness? margin = null) => new()
    {
        Child = child, Stretch = Stretch.Uniform, StretchDirection = StretchDirection.DownOnly, HorizontalAlignment = align, VerticalAlignment = VerticalAlignment.Center,
        Margin = margin ?? new Thickness(0),
    };

    private const double TileWidth = 176, TilePicture = 120;

    /// <summary>A tile of the same size as all others, so the pictures line up in rows that follow the window width.</summary>
    private Border PictureTile(Func<RgbaImage?> produce, string title, string subtitle, string tip, Action click, double scale = 1)
    {
        var picture = Ui.Picture(scale);
        Imaging.LoadAsync(picture, produce);
        var stack = new StackPanel();
        stack.Children.Add(new Border
        {
            Height = TilePicture, Background = (Brush)Application.Current.FindResource("PanelBrush"), CornerRadius = new CornerRadius(3),
            Child = Fit(new Border { Child = picture, Background = Imaging.Checkerboard(8), Padding = new Thickness(1) }),
        });
        stack.Children.Add(new TextBlock { Text = title, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 4, 0, 0) });
        stack.Children.Add(new TextBlock { Text = subtitle, FontSize = 10, Foreground = Brushes.Gray, TextTrimming = TextTrimming.CharacterEllipsis });
        var b = new Border
        {
            Child = stack, Width = TileWidth, Margin = new Thickness(0, 0, 8, 8), Padding = new Thickness(6), CornerRadius = new CornerRadius(4), ToolTip = tip, Cursor = System.Windows.Input.Cursors.Hand,
            BorderBrush = (Brush)Application.Current.FindResource("BorderBrush"), BorderThickness = new Thickness(1), Background = (Brush)Application.Current.FindResource("SurfaceBrush"),
        };
        b.MouseLeftButtonUp += (_, _) => click();
        return b;
    }

    private static TextBlock GroupHeader(string text, int count) => new()
    {
        Text = count > 0 ? $"{text}  ·  {count}" : text, FontWeight = FontWeights.SemiBold, FontSize = 14, Margin = new Thickness(0, 10, 0, 4),
    };

    private void ShowCategory()
    {
        _categoryPanel.Children.Clear();
        _categoryDetail.Children.Clear();
        var root = _ctx.Root;
        if (_categories.SelectedItem is not GraphicsCategory c || root == null || _ctx.Platinum == null) return;
        _categoryPanel.Children.Add(Ui.Title(c.Title));
        _categoryPanel.Children.Add(Ui.Hint(c.Description, 900));
        _categoryPanel.Children.Add(_categoryDetail);
        try
        {
            if (c.Id == "terrain")
            {
                foreach (var t in PlatinumGraphicsCatalog.Terrains(root))
                {
                    var box = new StackPanel();
                    box.Children.Add(GroupHeader(PlatinumNames.Pretty(t.Name.ToUpperInvariant(), ""), 0));
                    var row = new WrapPanel();
                    var pals = t.Palettes.Count > 0 ? t.Palettes.Cast<string?>().ToList() : [null];
                    foreach (var pal in pals)
                        foreach (var png in new[] { t.EnemyPng, t.PlayerPng }.OfType<string>())
                        {
                            var time = pal == null ? "" : System.IO.Path.GetFileNameWithoutExtension(pal);
                            var match = pal != null ? Core.Gba.JascPalette.Load(ProjectPaths.Combine(root, pal)) : null;
                            var side = png.EndsWith("player.png", StringComparison.Ordinal) ? "Player side" : "Opponent side";
                            row.Children.Add(PictureTile(() => PlatinumGraphicsCatalog.Render(root, png, pal), side, time.Length > 0 ? time : " ", $"{png}\nwith {pal ?? "its own palette"}\nClick for details and to replace.",
                                () => { _categoryDetail.Children.Clear(); BuildPictureDetail(_categoryDetail, png, ShowCategory, match); }));
                        }
                    box.Children.Add(row);
                    _categoryPanel.Children.Add(box);
                }
                return;
            }
            var pictures = PlatinumGraphicsCatalog.Pictures(root, c, _all.Count > 0 ? _all : null);
            _categoryPanel.Children.Add(new TextBlock { Text = $"{pictures.Count} pictures", Foreground = Brushes.Gray, Margin = new Thickness(0, 0, 0, 6) });
            // Staggered by folder: one heading per folder, the pictures below in rows of equal tiles.
            int shown = 0;
            foreach (var group in pictures.GroupBy(p => System.IO.Path.GetDirectoryName(p)!.Replace('\\', '/')))
            {
                if (shown >= 400) break;
                var folder = group.Key.Length > PlatinumGraphics.Folder.Length ? group.Key[(PlatinumGraphics.Folder.Length + 1)..] : group.Key;
                var groupWrap = new WrapPanel();
                foreach (var p in group.Take(400 - shown))
                {
                    shown++;
                    groupWrap.Children.Add(PictureTile(() => PlatinumGraphicsCatalog.Render(root, p, null), System.IO.Path.GetFileNameWithoutExtension(p), "", p,
                        () => { _categoryDetail.Children.Clear(); BuildPictureDetail(_categoryDetail, p, ShowCategory); _categoryDetail.BringIntoView(); }));
                }
                _categoryPanel.Children.Add(GroupHeader(folder, group.Count()));
                _categoryPanel.Children.Add(groupWrap);
            }
            if (pictures.Count > 400) _categoryPanel.Children.Add(Ui.Hint($"The first 400 of {pictures.Count} pictures are shown – the tab \"All pictures\" lists every one."));
        }
        catch (Exception ex) when (ex is IOException or PngFormatException or InvalidDataException) { _categoryPanel.Children.Add(Ui.Hint(ex.Message)); }
    }

    // ------------------------------------------------------------------ healthboxes

    private void Refresh()
    {
        _panel.Children.Clear();
        _panel.Children.Add(Ui.Title("Battle healthboxes"));
        _panel.Children.Add(Ui.Hint(
            "res/graphics/battle/healthbox: the boxes with name, level and HP in battle. All four pictures must be true 4-bit indexed PNGs " +
            "(many paint programs save 16 colours as an 8-bit PNG – that scrambles the boxes in-game) and use the palette of player_singles.png " +
            "in the same order. Index 14 (text) and 15 (background/mask) are used by the game while battling."));
        var root = _ctx.Root;
        if (root == null || _ctx.Platinum == null) { _panel.Children.Add(Ui.Hint("No Platinum project is open.")); return; }

        List<HealthboxCheck> checks;
        Rgba32[]? master;
        try
        {
            master = PlatinumHealthbox.MasterPalette(root);
            checks = PlatinumHealthbox.CheckAll(root);
        }
        catch (Exception ex) when (ex is IOException or PngFormatException)
        {
            _panel.Children.Add(Ui.Hint("The pictures could not be read: " + ex.Message));
            return;
        }

        if (master != null)
        {
            var swatches = new WrapPanel { Margin = new Thickness(0, 6, 0, 10) };
            for (int i = 0; i < master.Length; i++)
            {
                var cell = new StackPanel { Margin = new Thickness(0, 0, 6, 0) };
                cell.Children.Add(new Border { Width = 26, Height = 20, Background = Imaging.Brush(master[i]), BorderBrush = Brushes.Gray, BorderThickness = new Thickness(1), ToolTip = master[i].ToHex() });
                cell.Children.Add(new TextBlock { Text = i.ToString(), FontSize = 10, HorizontalAlignment = HorizontalAlignment.Center });
                swatches.Children.Add(cell);
            }
            _panel.Children.Add(new TextBlock { Text = "Master palette (player_singles.png)", FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 8, 0, 0) });
            _panel.Children.Add(swatches);
        }

        int broken = checks.Count(c => !c.Ok);
        if (checks.Any(c => c.CanFix4bpp || c.CanSyncPalette))
            _panel.Children.Add(Ui.Row(Ui.Button("🛠 Repair all", () => Repair(checks.Where(c => c.CanFix4bpp || c.CanSyncPalette).ToList(), master), "Saves every picture as true 4-bit PNG and puts its colours in the order of the master palette.")));
        _panel.Children.Add(new TextBlock
        {
            Text = broken == 0 ? "✓ All four pictures are fine." : $"✗ {broken} picture(s) need attention.",
            Foreground = broken == 0 ? (Brush)Application.Current.FindResource("SuccessBrush") : (Brush)Application.Current.FindResource("AccentDarkBrush"),
            FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 4, 0, 8),
        });

        foreach (var c in checks) _panel.Children.Add(Card(root, c, master));
    }

    private FrameworkElement Card(string root, HealthboxCheck c, Rgba32[]? master)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        double scale = c.File.Width > 300 ? 1.5 : 3;
        var img = Ui.Picture(scale);
        if (PlatinumHealthbox.Preview(c) is { } preview) img.Source = Imaging.ToBitmap(preview);
        var picture = Fit(new Border { Child = img, Background = Imaging.Checkerboard(8) }, HorizontalAlignment.Left, new Thickness(0, 0, 12, 0));
        picture.VerticalAlignment = VerticalAlignment.Top;
        if (c.File.Width > 300) { Grid.SetColumnSpan(picture, 2); picture.HorizontalAlignment = HorizontalAlignment.Left; }
        grid.Children.Add(picture);

        var info = new StackPanel();
        info.Children.Add(new TextBlock { Text = $"{c.File.Title}  ·  {c.File.Name}", FontWeight = FontWeights.SemiBold, FontSize = 14 });
        var png = c.Png;
        info.Children.Add(new TextBlock
        {
            Text = png == null ? "–" : $"{png.Width}×{png.Height}, {(png.IsIndexed ? $"{png.BitDepth}-bit indexed, {png.Palette?.Length ?? 0} palette entries" : "not indexed")}",
            FontFamily = new FontFamily("Consolas"), FontSize = 11,
        });
        info.Children.Add(Ui.Hint(c.File.Role, 600));
        foreach (var p in c.Problems)
            info.Children.Add(new TextBlock { Text = "✗ " + p, TextWrapping = TextWrapping.Wrap, MaxWidth = 600, Foreground = (Brush)Application.Current.FindResource("AccentDarkBrush"), HorizontalAlignment = HorizontalAlignment.Left });
        if (c.Ok) info.Children.Add(new TextBlock { Text = "✓ True 4-bit, palette in order.", Foreground = (Brush)Application.Current.FindResource("SuccessBrush") });
        foreach (var n in c.Notes) info.Children.Add(Ui.Hint(n, 600));
        var buttons = Ui.Row(Ui.Button("Replace…", () => Replace(root, c, master),
            c.File.Name == PlatinumHealthbox.MasterName ? "Indexed PNG with at most 16 colours; its palette becomes the master palette." : "PNG using only the colours of the master palette (RGB or indexed)."));
        if (c.CanFix4bpp) buttons.Children.Add(Ui.Button("Save as 4-bit", () => Repair([c], master), "Same picture and colours, stored as a true 4-bit PNG."));
        if (c.CanSyncPalette) buttons.Children.Add(Ui.Button("Use master palette", () => Repair([c], master), "Re-numbers the colours so they match the order of player_singles.png."));
        buttons.Children.Add(Ui.Button("Show file", () => Ui.OpenFolder(ProjectPaths.Combine(root, c.File.Path))));
        info.Children.Add(buttons);
        if (c.File.Width > 300) { Grid.SetRow(info, 1); grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); info.Margin = new Thickness(0, 8, 0, 0); Grid.SetColumnSpan(info, 2); }
        else Grid.SetColumn(info, 1);
        grid.Children.Add(info);
        return Ui.Card(grid);
    }

    private void Repair(IReadOnlyList<HealthboxCheck> checks, Rgba32[]? master)
    {
        var changes = new List<PlatinumFileChange>();
        try
        {
            foreach (var c in checks)
            {
                if (c.Png == null) continue;
                if (c.CanSyncPalette && master != null)
                    changes.Add(new PlatinumFileChange(c.File.Path, PlatinumHealthbox.MapToMaster(c.Png.Rgba, master), "colours in master palette order, 4-bit"));
                else if (c.CanFix4bpp)
                    changes.Add(new PlatinumFileChange(c.File.Path, PlatinumHealthbox.To4bpp(c.Png), "same picture, saved as true 4-bit PNG"));
            }
        }
        catch (InvalidDataException ex) { ProjectWriter.Warn("Healthboxes", ex.Message); return; }
        if (PlatinumModules.Write(_ctx, "Repair healthboxes", changes, "Pixels and colours stay the same; only the storage (4-bit) and the order of the palette change.", "Build the ROM afterwards and look at a single and a double battle."))
            Refresh();
    }

    private void Replace(string root, HealthboxCheck c, Rgba32[]? master)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog { Filter = "PNG pictures|*.png", Title = $"New {c.File.Name} ({c.File.Width}×{c.File.Height})" };
        if (dlg.ShowDialog(Application.Current.MainWindow) != true) return;
        try
        {
            var png = Png.Load(dlg.FileName);
            if (png.Width != c.File.Width || png.Height != c.File.Height)
                throw new InvalidDataException($"The picture is {png.Width}×{png.Height}; {c.File.Name} must be {c.File.Width}×{c.File.Height}.");
            byte[] bytes;
            var notes = new List<string> { "Saved as a true 4-bit indexed PNG." };
            if (c.File.Name == PlatinumHealthbox.MasterName)
            {
                bytes = PlatinumHealthbox.NewMaster(png, out var w);
                notes.AddRange(w);
            }
            else
            {
                if (master == null) throw new InvalidDataException("player_singles.png could not be read – its palette is needed.");
                bytes = PlatinumHealthbox.MapToMaster(png.Rgba, master);
                notes.Add("Each colour was given the number it has in player_singles.png.");
            }
            if (PlatinumModules.Write(_ctx, "Replace healthbox", [new PlatinumFileChange(c.File.Path, bytes, "new picture")], [.. notes, "Build the ROM afterwards."]))
                Refresh();
        }
        catch (Exception ex) when (ex is IOException or PngFormatException or InvalidDataException)
        {
            ProjectWriter.Warn("Replace healthbox", ex.Message);
        }
    }
}
