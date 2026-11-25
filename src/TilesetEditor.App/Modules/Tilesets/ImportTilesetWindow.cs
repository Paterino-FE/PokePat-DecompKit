using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;
using TilesetEditor.App.Infrastructure;
using TilesetEditor.App.Shell;
using TilesetEditor.Core.Project;
using TilesetEditor.Core.Tilesets;
using TilesetEditor.Export;

namespace TilesetEditor.App.Modules.Tilesets;

/// <summary>
/// "Import a finished tileset": pick or drop a folder (asset repos, other hacks), PokePat works out what it is, suggests name,
/// kind and base, shows the metatiles as the game draws them and writes it as a new tileset or over an existing one –
/// optionally switching chosen maps to it right away.
/// </summary>
public sealed class ImportTilesetWindow : Window
{
    private readonly ProjectInfo _project;
    private TilesetPackage? _package;
    private ExportPlan? _plan;

    private readonly TextBox _folder = new() { IsReadOnly = true, Background = Brushes.Transparent };
    private readonly TextBlock _analysis = new() { TextWrapping = TextWrapping.Wrap, FontFamily = new FontFamily("Consolas"), FontSize = 11, Margin = new Thickness(0, 4, 0, 0) };
    private readonly RadioButton _modeNew = new() { Content = "Add as a new tileset (only the maps you choose use it)", IsChecked = true, GroupName = "mode" };
    private readonly RadioButton _modeReplace = new() { Content = "Replace the base tileset (every map that uses it changes)", GroupName = "mode", Margin = new Thickness(0, 4, 0, 0) };
    private readonly TextBox _name = new() { FontFamily = new FontFamily("Consolas") };
    private readonly ComboBox _kind = new() { Width = 120, HorizontalAlignment = HorizontalAlignment.Left };
    private readonly ComboBox _base = new();
    private readonly CheckBox _packageMetatiles = new() { Content = "Use the folder's metatiles (otherwise the base's layout and behaviors stay)", IsChecked = true };
    private readonly CheckBox _animations = new() { Content = "Keep the base's animations (water, flowers …)", IsChecked = true };
    private readonly TextBox _mapFilter = new();
    private readonly StackPanel _maps = new();
    private readonly ComboBox _contextPrimary = new();
    private readonly Image _preview = new() { Stretch = Stretch.None, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) };
    private readonly Button _import = new() { Content = "Import…", FontWeight = FontWeights.SemiBold, IsEnabled = false };
    private readonly HashSet<string> _chosenLayouts = [];
    private bool _nameTouched, _updating;

    /// <summary>True when the import was written (the caller reloads the project).</summary>
    public bool Imported { get; private set; }

    public ImportTilesetWindow(ProjectInfo project, string? folder = null)
    {
        _project = project;
        Title = "Import a finished tileset";
        Width = 1040;
        Height = 760;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        AllowDrop = true;
        DragOver += (_, e) => { e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None; e.Handled = true; };
        Drop += (_, e) =>
        {
            if (e.Data.GetData(DataFormats.FileDrop) is string[] { Length: > 0 } dropped)
                LoadFolder(Directory.Exists(dropped[0]) ? dropped[0] : Path.GetDirectoryName(dropped[0])!);
        };

        _kind.ItemsSource = new[] { TilesetKind.Primary, TilesetKind.Secondary };
        _kind.SelectedIndex = 0;
        _contextPrimary.ItemsSource = project.PrimaryTilesets.Select(t => t.Name).ToList();
        _contextPrimary.SelectedItem = project.PrimaryTilesets.FirstOrDefault(t => t.Name == "gTileset_General")?.Name ?? project.PrimaryTilesets.FirstOrDefault()?.Name;

        var browse = new Button { Content = "Choose folder…" };
        browse.Click += (_, _) =>
        {
            var dlg = new OpenFolderDialog { Title = "Folder with the tileset (tiles PNG, palettes, metatiles.bin …)" };
            if (dlg.ShowDialog(this) == true) LoadFolder(dlg.FolderName);
        };

        // Left column: what and how
        var left = new StackPanel();
        left.Children.Add(Section("1 · Folder"));
        left.Children.Add(Row(browse, _folder));
        left.Children.Add(Hint("Or drag the folder onto this window. Typical content: a tiles PNG, 00.pal … 15.pal, metatiles.bin, metatile_attributes.bin – partial sets (\"use these palettes with General\") are fine."));
        left.Children.Add(_analysis);
        left.Children.Add(Section("2 · How"));
        left.Children.Add(_modeNew);
        left.Children.Add(Labelled("Name", _name));
        left.Children.Add(_modeReplace);
        left.Children.Add(Labelled("Kind", _kind));
        left.Children.Add(Labelled("Based on", _base));
        left.Children.Add(Hint("Files the folder does not have (palettes, behaviors, metatiles) are taken from this tileset."));
        left.Children.Add(_packageMetatiles);
        left.Children.Add(_animations);
        left.Children.Add(Section("3 · Use on maps (optional)"));
        left.Children.Add(Labelled("Filter", _mapFilter));
        left.Children.Add(new ScrollViewer { Content = _maps, Height = 170, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });

        // Right column: preview
        var right = new DockPanel { Margin = new Thickness(12, 0, 0, 0) };
        var previewHead = new StackPanel();
        previewHead.Children.Add(Section("Preview (metatiles as the game draws them)"));
        previewHead.Children.Add(Labelled("Primary for a secondary", _contextPrimary));
        DockPanel.SetDock(previewHead, Dock.Top);
        right.Children.Add(previewHead);
        right.Children.Add(new Border
        {
            BorderBrush = (Brush)Application.Current.FindResource("BorderBrush"), BorderThickness = new Thickness(1), Background = new SolidColorBrush(Color.FromRgb(0x20, 0x24, 0x2A)),
            Child = new ScrollViewer { Content = _preview, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Auto },
        });

        var cancel = new Button { Content = "Close", IsCancel = true };
        _import.Click += (_, _) => Import();
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 8, 0, 0) };
        buttons.Children.Add(_import);
        buttons.Children.Add(cancel);

        var grid = new Grid { Margin = new Thickness(12) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(470) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var leftScroll = new ScrollViewer { Content = left, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        grid.Children.Add(leftScroll);
        Grid.SetColumn(right, 1);
        grid.Children.Add(right);
        Grid.SetRow(_status, 1);
        Grid.SetColumnSpan(_status, 2);
        grid.Children.Add(_status);
        Grid.SetRow(buttons, 2);
        Grid.SetColumnSpan(buttons, 2);
        grid.Children.Add(buttons);
        Content = grid;

        _name.TextChanged += (_, _) => { if (!_updating) { _nameTouched = true; Update(); } };
        _modeNew.Checked += (_, _) => Update();
        _modeReplace.Checked += (_, _) => Update();
        _kind.SelectionChanged += (_, _) => { if (!_updating) { FillBases(); Update(); } };
        _base.SelectionChanged += (_, _) => Update();
        _packageMetatiles.Click += (_, _) => Update();
        _animations.Click += (_, _) => Update();
        _contextPrimary.SelectionChanged += (_, _) => Update();
        _mapFilter.TextChanged += (_, _) => FillMaps();
        FillBases();
        FillMaps();
        Update();
        if (folder != null) Loaded += (_, _) => LoadFolder(folder);
    }

    private static TextBlock Section(string text) => new() { Text = text, FontWeight = FontWeights.SemiBold, FontSize = 14, Margin = new Thickness(0, 10, 0, 4) };
    private static TextBlock Hint(string text)
    {
        var t = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, FontSize = 11, Margin = new Thickness(0, 2, 0, 2) };
        t.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
        return t;
    }

    private static FrameworkElement Labelled(string label, FrameworkElement control)
    {
        var g = new Grid { Margin = new Thickness(0, 3, 0, 3) };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(130) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        g.Children.Add(new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center });
        Grid.SetColumn(control, 1);
        g.Children.Add(control);
        return g;
    }

    private static FrameworkElement Row(FrameworkElement a, FrameworkElement b)
    {
        var d = new DockPanel { Margin = new Thickness(0, 2, 0, 2) };
        DockPanel.SetDock(a, Dock.Left);
        d.Children.Add(a);
        d.Children.Add(b);
        return d;
    }

    private void LoadFolder(string folder)
    {
        try
        {
            _package = TilesetPackage.Read(folder, _project);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _package = null;
            _status.Text = "The folder could not be read: " + ex.Message;
            return;
        }
        _folder.Text = folder;
        var p = _package;
        var lines = new List<string>
        {
            $"Tile image:  {(p.TilesImage != null ? $"{Path.GetFileName(p.TilesImage)} – {p.TileCount} tiles" : "none")}",
            $"Palettes:    {(p.Palettes.Count == 0 ? "none" : string.Join(" ", p.Palettes.Keys.Select(k => k.ToString("00"))))}",
            $"Metatiles:   {(p.MetatilesFile != null ? $"{p.MetatileCount} ({(p.TilesPerMetatile == 12 ? "triple" : p.TilesPerMetatile == 8 ? "dual" : "?")} layer)" : "none")}",
            $"Behaviors:   {(p.AttributesFile != null ? "metatile_attributes.bin" : "none")}",
            $"Looks like:  {p.SuggestedKind.ToString().ToLowerInvariant()} tileset{(p.SuggestedBase != null ? $", a variant of {p.SuggestedBase}" : "")}",
        };
        lines.AddRange(p.Notes.Select(n => "• " + n));
        lines.AddRange(p.Problems.Select(n => "! " + n));
        _analysis.Text = string.Join("\n", lines);
        _updating = true;
        _kind.SelectedItem = p.SuggestedKind;
        FillBases();
        _base.SelectedItem = p.SuggestedBase ?? NoBase;
        if (!_nameTouched) _name.Text = p.SuggestedName;
        _packageMetatiles.IsChecked = p.HasMetatiles;
        _updating = false;
        Update();
    }

    private const string NoBase = "(none – complete tileset)";

    private void FillBases()
    {
        var kind = _kind.SelectedItem as TilesetKind? ?? TilesetKind.Primary;
        var keep = _base.SelectedItem as string;
        var names = new List<string> { NoBase };
        names.AddRange(_project.Tilesets.Where(t => t.IsSecondary == (kind == TilesetKind.Secondary)).Select(t => t.Name));
        _base.ItemsSource = names;
        _base.SelectedItem = keep != null && names.Contains(keep) ? keep : NoBase;
    }

    private void FillMaps()
    {
        var f = _mapFilter.Text.Trim();
        _maps.Children.Clear();
        foreach (var l in _project.Layouts.Where(l => f.Length == 0 || l.Id.Contains(f, StringComparison.OrdinalIgnoreCase) || l.Name.Contains(f, StringComparison.OrdinalIgnoreCase)).Take(400))
        {
            var cb = new CheckBox
            {
                Content = $"{l.Name}   ({l.PrimaryTileset} / {l.SecondaryTileset})",
                IsChecked = _chosenLayouts.Contains(l.Id),
                Tag = l.Id,
                FontSize = 11,
            };
            cb.Click += (_, _) =>
            {
                if (cb.IsChecked == true) _chosenLayouts.Add(l.Id); else _chosenLayouts.Remove(l.Id);
                Update();
            };
            _maps.Children.Add(cb);
        }
    }

    private TilesetImportOptions Options() => new()
    {
        Mode = _modeReplace.IsChecked == true ? TilesetImportMode.Replace : TilesetImportMode.New,
        Kind = _kind.SelectedItem as TilesetKind? ?? TilesetKind.Primary,
        Name = _name.Text.Trim(),
        Base = _base.SelectedItem is string b && b != NoBase ? b : null,
        Metatiles = _packageMetatiles.IsChecked == true ? MetatileSource.Package : MetatileSource.Base,
        KeepAnimations = _animations.IsChecked == true,
        AssignToLayouts = _chosenLayouts.ToList(),
    };

    private void Update()
    {
        if (_updating) return;
        _name.IsEnabled = _modeNew.IsChecked == true;
        _packageMetatiles.IsEnabled = _package?.HasMetatiles == true;
        _plan = null;
        _import.IsEnabled = false;
        if (_package == null) { _status.Text = "Choose the folder of a tileset."; _preview.Source = null; return; }
        if (_package.IsArtwork)
        {
            _status.Text = "This folder holds images, not a finished tileset. Use \"Open PNG…\" / \"Layer PNGs…\" in the Tilesets module: Porytiles builds a tileset from them.";
            _preview.Source = null;
            return;
        }
        var options = Options();
        try
        {
            _plan = TilesetImportPlanner.Plan(_project, _package, options);
            var ctx = _project.FindTileset(_contextPrimary.SelectedItem as string ?? "");
            var img = TilesetImportPlanner.Preview(_project, _plan, options.Kind, ctx);
            _preview.Source = img != null ? Imaging.ToBitmap(Scale(img, 2)) : null;
            int created = _plan.Created.Count(), modified = _plan.Modified.Count();
            _status.SetResourceReference(TextBlock.ForegroundProperty, "InkBrush");
            _status.Text = $"Ready: {created} new and {modified} changed file(s)" +
                           (options.AssignToLayouts.Count > 0 ? $", {options.AssignToLayouts.Count} map layout(s) switched to it" : "") + ". " +
                           string.Join(" ", _plan.Notes);
            _import.IsEnabled = true;
        }
        catch (ExportValidationException ex)
        {
            _status.SetResourceReference(TextBlock.ForegroundProperty, "ErrorBrush");
            _status.Text = string.Join("\n", ex.Errors);
            _preview.Source = null;
        }
        catch (Exception ex) when (ex is IOException or FormatException or InvalidDataException)
        {
            _status.SetResourceReference(TextBlock.ForegroundProperty, "ErrorBrush");
            _status.Text = "The files could not be read: " + ex.Message;
            _preview.Source = null;
        }
    }

    private static Core.Imaging.RgbaImage Scale(Core.Imaging.RgbaImage img, int f)
    {
        var big = new Core.Imaging.RgbaImage(img.Width * f, img.Height * f);
        for (int y = 0; y < big.Height; y++) for (int x = 0; x < big.Width; x++) big[x, y] = img[x / f, y / f];
        return big;
    }

    private void Import()
    {
        if (_plan == null) return;
        if (!ProjectWriter.Apply(_project.Root, _plan.TilesetName, _plan.TilesetFolder, _plan.Files.ToList(), [.. _plan.Notes])) return;
        Imported = true;
        DialogResult = true;
    }
}
