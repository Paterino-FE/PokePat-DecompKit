using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using TilesetEditor.App.Infrastructure;
using TilesetEditor.App.Shell;
using TilesetEditor.Core.Events;
using TilesetEditor.Core.Gba;
using TilesetEditor.Core.Imaging;
using TilesetEditor.Core.Maps;
using TilesetEditor.Core.Project;
using TilesetEditor.Core.SecretBases;
using TilesetEditor.Core.Sprites;
using TilesetEditor.Export;

namespace TilesetEditor.App.Modules.SecretBases;

/// <summary>A decoration row with its small preview.</summary>
public sealed class DecorationRow(Decoration d, BitmapSource? icon)
{
    public Decoration Decoration { get; } = d;
    public BitmapSource? Icon { get; } = icon;
    public string Constant => Decoration.Constant;
    public int Id => Decoration.Id;
    public string Name => Decoration.Name;
    public int Price => Decoration.Price;
    public string Category => Decoration.CategoryText;
    public string Shape => Decoration.ShapeText;
    public string Permission => Decoration.PermissionText;
    public string Graphics => Decoration.GraphicsText;
    public string IconSource => Decoration.IconPng != null ? "own icon" : Decoration.HasIconEntry ? "from graphics" : "missing entry";
    public string Description => Decoration.DescriptionOneLine;
}

/// <summary>
/// Secret bases: where the entrances are (bg_event "secret_base" on outdoor maps), which interior each id leads to, the
/// interiors with entrance and PC positions, and the decorations that can be placed in them (edit, add sprite decorations).
/// </summary>
public sealed class SecretBasesViewModel : ObservableObject, IToolkitModule
{
    private readonly ProjectContext _ctx;
    private SecretBaseData? _data;
    private DecorationData? _decor;
    private List<OverworldSprite> _sprites = [];
    private Dictionary<string, Rgba32[]> _palettes = [];
    private readonly Dictionary<string, (RgbaImage Image, MapGrid Grid)?> _mapCache = new(StringComparer.Ordinal);
    private int _loadVersion;

    public SecretBasesViewModel(ProjectContext ctx)
    {
        _ctx = ctx;
        _ctx.ProjectChanged += (_, _) => _ = LoadAsync();
        NewLocationCommand = new RelayCommand(NewLocation, () => _data != null && _ctx.Project != null);
        SaveDecorationCommand = new RelayCommand(SaveDecoration, () => SelectedDecoration != null && DecorationEdited);
        ResetDecorationCommand = new RelayCommand(() => ShowDecoration(), () => SelectedDecoration != null && DecorationEdited);
        NewDecorationCommand = new RelayCommand(NewDecoration, () => _decor != null && _ctx.Project != null);
        FixIconTableCommand = new RelayCommand(FixIconTable, () => _decor?.Items.Any(d => !d.HasIconEntry) == true);
        View = new SecretBasesView { DataContext = this };
        _ = LoadAsync();
    }

    public string Title => "Secret Bases";
    public string Description => "Secret base locations, interiors and decorations";
    public bool IsAvailable => true;
    public override string ToString() => Title;
    public FrameworkElement View { get; }
    public bool HasUnsavedChanges => DecorationEdited;
    public string? UnsavedChangesDescription => DecorationEdited ? $"Secret Bases: unsaved changes of {SelectedDecoration?.Constant}" : null;

    public ICommand NewLocationCommand { get; }
    public ICommand SaveDecorationCommand { get; }
    public ICommand ResetDecorationCommand { get; }
    public ICommand NewDecorationCommand { get; }
    public ICommand FixIconTableCommand { get; }

    private string _summary = "", _problems = "";
    public string Summary { get => _summary; private set => Set(ref _summary, value); }
    public string Problems { get => _problems; private set { if (Set(ref _problems, value)) OnPropertyChanged(nameof(HasProblems)); } }
    public bool HasProblems => Problems.Length > 0;

    public IReadOnlyList<string> Zooms { get; } = ["1×", "2×", "3×", "4×"];
    private int _zoomIndex = 1;
    public int ZoomIndex { get => _zoomIndex; set { if (Set(ref _zoomIndex, Math.Clamp(value, 0, 3))) OnPropertyChanged(nameof(Zoom)); } }
    public double Zoom => ZoomIndex + 1;

    private async Task LoadAsync()
    {
        var project = _ctx.Project;
        if (project == null) return;
        int version = ++_loadVersion;
        Summary = "Reading secret bases and decorations …";
        var (data, decor, sprites, palettes) = await Task.Run(() =>
        {
            var d = SecretBaseCatalog.Load(project.Root, project.Layouts);
            var dec = Decorations.Load(project.Root);
            var s = OverworldSprites.Load(project.Root);
            var p = new Dictionary<string, Rgba32[]>();
            foreach (var (tag, path) in OverworldSprites.Palettes(s))
            {
                try { p[tag] = JascPalette.Load(Path.Combine(project.Root, path)); }
                catch (Exception ex) when (ex is IOException or FormatException) { }
            }
            return (d, dec, s, p);
        });
        if (version != _loadVersion) return;
        _data = data;
        _decor = decor;
        _sprites = sprites;
        _palettes = palettes;
        lock (_mapCache) _mapCache.Clear();
        int warnings = data.Locations.Count(l => l.Warnings.Count > 0);
        Summary = $"{data.Locations.Count} secret base locations on {data.Locations.Select(l => l.MapFolder).Distinct().Count()} maps · " +
                  $"{data.Groups.Count} interiors · {data.Ids.Count} ids defined · {decor.Items.Count} decorations" +
                  (warnings > 0 ? $" · {warnings} location(s) with warnings" : "");
        Problems = string.Join("\n", data.Problems.Concat(decor.Problems));

        var keepLoc = SelectedLocation is { } sl ? (sl.MapFolder, sl.EventIndex) : default;
        ApplyLocations();
        SelectedLocation = Locations.FirstOrDefault(l => (l.MapFolder, l.EventIndex) == keepLoc) ?? Locations.FirstOrDefault();
        var keepGroup = SelectedGroup?.Constant;
        Groups.Clear();
        foreach (var g in data.Groups.OrderBy(g => g.Index)) Groups.Add(g);
        SelectedGroup = Groups.FirstOrDefault(g => g.Constant == keepGroup) ?? Groups.FirstOrDefault();

        var rows = await Task.Run(() => decor.Items.Select(d =>
        {
            var img = SecretBasePreview.Decoration(project, d, decor, sprites, palettes);
            return new DecorationRow(d, img == null ? null : Imaging.ToBitmap(Fit(img)));
        }).ToList());
        if (version != _loadVersion) return;
        _allRows = rows;
        var keepDecor = SelectedDecoration?.Constant;
        ApplyDecorations();
        SelectedDecoration = DecorationRows.FirstOrDefault(r => r.Constant == keepDecor) ?? DecorationRows.FirstOrDefault();
        CommandManager.InvalidateRequerySuggested();
    }

    private static RgbaImage Fit(RgbaImage img)
    {
        // Small list preview: at most 32 px high.
        if (img.Height <= 32 && img.Width <= 64) return img;
        int f = Math.Max((img.Height + 31) / 32, (img.Width + 63) / 64);
        var small = new RgbaImage(img.Width / f, img.Height / f);
        for (int y = 0; y < small.Height; y++) for (int x = 0; x < small.Width; x++) small[x, y] = img[x * f, y * f];
        return small;
    }

    private (RgbaImage Image, MapGrid Grid)? RenderMap(string folder)
    {
        var project = _ctx.Project;
        if (project == null) return null;
        lock (_mapCache)
            if (_mapCache.TryGetValue(folder, out var cached)) return cached;
        var scene = MapScene.Load(project, folder);
        (RgbaImage, MapGrid)? result = MapRenderer.Render(project, scene) is { } img && scene.Grid != null ? (img, scene.Grid) : null;
        lock (_mapCache) _mapCache[folder] = result;
        return result;
    }

    // ------------------------------------------------------------------ Locations

    public ObservableCollection<SecretBaseLocation> Locations { get; } = [];
    private string _locationFilter = "";
    private bool _onlyWarnings;
    public string LocationFilter { get => _locationFilter; set { if (Set(ref _locationFilter, value)) ApplyLocations(); } }
    public bool OnlyWarnings { get => _onlyWarnings; set { if (Set(ref _onlyWarnings, value)) ApplyLocations(); } }

    private void ApplyLocations()
    {
        Locations.Clear();
        if (_data == null) return;
        var f = LocationFilter.Trim();
        foreach (var l in _data.Locations
                     .Where(l => !OnlyWarnings || l.Warnings.Count > 0)
                     .Where(l => f.Length == 0 || l.MapFolder.Contains(f, StringComparison.OrdinalIgnoreCase) || l.IdConstant.Contains(f, StringComparison.OrdinalIgnoreCase)
                                 || l.Type.Contains(f, StringComparison.OrdinalIgnoreCase))
                     .OrderBy(l => l.MapFolder, StringComparer.Ordinal).ThenBy(l => l.Id ?? 0))
            Locations.Add(l);
    }

    private SecretBaseLocation? _selectedLocation;
    private BitmapSource? _locationPreview;
    private string _locationDetails = "";
    private Point _locationMarker;
    public SecretBaseLocation? SelectedLocation { get => _selectedLocation; set { if (Set(ref _selectedLocation, value)) _ = ShowLocationAsync(); } }
    public BitmapSource? LocationPreview { get => _locationPreview; private set => Set(ref _locationPreview, value); }
    public string LocationDetails { get => _locationDetails; private set => Set(ref _locationDetails, value); }
    /// <summary>Pixel position (unzoomed) of the selected entrance, for scrolling it into view.</summary>
    public Point LocationMarker { get => _locationMarker; private set => Set(ref _locationMarker, value); }

    private async Task ShowLocationAsync()
    {
        var loc = SelectedLocation;
        var data = _data;
        if (loc == null || data == null) { LocationPreview = null; LocationDetails = ""; return; }
        var g = loc.Group;
        LocationDetails = $"{loc.MapFolder}  ({loc.X},{loc.Y}), elevation {loc.Elevation}, bg_events #{loc.EventIndex + 1}\n" +
                          $"Id: {loc.IdConstant} = {loc.IdText}  →  interior {g?.ShortName ?? "?"} ({g?.MapFolder ?? "?"}), type {loc.Type}\n" +
                          $"Outside metatile: {loc.MetatileText}" + (loc.IsEntranceMetatile ? $" – entrance metatile ({loc.MetatileType ?? "?"})" : " – NOT an entrance metatile") +
                          (loc.Warnings.Count > 0 ? "\n\nWarnings:\n• " + string.Join("\n• ", loc.Warnings) : "");
        var others = data.Locations.Where(o => o.MapFolder == loc.MapFolder && o != loc).ToList();
        var img = await Task.Run(() =>
        {
            var map = RenderMap(loc.MapFolder);
            if (map == null) return null;
            var copy = map.Value.Image.Clone();
            foreach (var o in others) SecretBasePreview.MarkTile(copy, o.X, o.Y, SecretBasePreview.Other);
            SecretBasePreview.MarkTile(copy, loc.X, loc.Y, SecretBasePreview.Selected);
            return Imaging.ToBitmap(copy);
        });
        if (SelectedLocation != loc) return;
        LocationPreview = img;
        LocationMarker = new Point(loc.X * MapRenderer.BlockSize + 8, loc.Y * MapRenderer.BlockSize + 8);
        if (img == null) LocationDetails += "\n\nThe map could not be drawn (layout or tilesets missing).";
    }

    private void NewLocation()
    {
        var project = _ctx.Project;
        if (project == null || _data == null) return;
        var win = new NewSecretBaseWindow(project, _data, SelectedLocation?.MapFolder, RenderMap) { Owner = Application.Current.MainWindow };
        if (win.ShowDialog() != true || win.Result is not { } r) return;
        var files = new List<PlannedFile>();
        try
        {
            files.Add(ProjectWriter.ModifyText(project.Root, SecretBaseCatalog.ConstantsFile, t => SecretBaseCatalog.InsertIdConstant(t, r.Id),
                $"#define {r.Id.Name} {r.Id.Value} (interior {r.Id.Group.ShortName}, ones digit {r.Id.Slot})"));
            files.Add(ProjectWriter.ModifyText(project.Root, $"data/maps/{r.MapFolder}/map.json",
                j => MapEventScripts.AddSecretBase(j, r.X, r.Y, r.Elevation, r.Id.Name), $"bg_event secret_base at ({r.X},{r.Y}) → {r.Id.Name}"));
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or System.Text.Json.JsonException)
        {
            ProjectWriter.Warn("New secret base location", ex.Message);
            return;
        }
        var notes = new List<string>
        {
            $"Secret Power on ({r.X},{r.Y}) of {r.MapFolder} opens a base in {r.Id.Group.MapFolder ?? r.Id.Group.ShortName}.",
            "Every location of the same interior shares that interior map, but each id is a separate base in the save file.",
        };
        if (r.Warning != null) notes.Add("Warning: " + r.Warning);
        if (!ProjectWriter.Apply(project.Root, "New secret base location", $"data/maps/{r.MapFolder}", files, [.. notes])) return;
        LocationFilter = "";
        _ctx.Reload();
    }

    // ------------------------------------------------------------------ Interiors

    public ObservableCollection<SecretBaseGroup> Groups { get; } = [];
    private SecretBaseGroup? _selectedGroup;
    private BitmapSource? _interiorPreview;
    private string _interiorDetails = "";
    public SecretBaseGroup? SelectedGroup { get => _selectedGroup; set { if (Set(ref _selectedGroup, value)) _ = ShowGroupAsync(); } }
    public BitmapSource? InteriorPreview { get => _interiorPreview; private set => Set(ref _interiorPreview, value); }
    public string InteriorDetails { get => _interiorDetails; private set => Set(ref _interiorDetails, value); }

    private async Task ShowGroupAsync()
    {
        var g = SelectedGroup;
        var root = _ctx.Root;
        if (g == null || root == null) { InteriorPreview = null; InteriorDetails = ""; return; }
        (int X, int Y)? warp = null;
        if (g.MapFolder != null && g.WarpId is { } w)
        {
            var json = Path.Combine(root, "data", "maps", g.MapFolder, "map.json");
            if (File.Exists(json)) warp = SecretBaseCatalog.ReadWarp(File.ReadAllText(json), w);
        }
        var lines = new List<string>
        {
            $"{g.Constant} = SECRET_BASE_GROUP({g.Index}) · type {g.Type}",
            $"Interior map: {g.MapFolder ?? "?"} ({g.MapConstant ?? "no entry in sSecretBaseEntrancePositions"})",
            $"Entrance warp #{g.WarpId}" + (warp is { } wp ? $" at ({wp.X},{wp.Y})" : " (not found)") + $" · first visit: player in front of the PC at ({g.PcX},{g.PcY})",
            "",
            "Ids of this interior (ones digit 1–9):",
        };
        for (int k = 1; k <= 9; k++)
        {
            int value = g.Index * 10 + k;
            var c = g.Ids.FirstOrDefault(i => i.Value == value);
            var uses = g.Locations.Where(l => l.Id == value).Select(l => $"{l.MapFolder} ({l.X},{l.Y})").ToList();
            lines.Add($"  {value,3}  {c?.Name ?? "(free)",-28} {(uses.Count > 0 ? string.Join(", ", uses) : c != null ? "defined, not placed on a map" : "")}");
        }
        lines.Add("");
        lines.Add("Legend: green = entrance warp (the player arrives here), blue = position in front of the PC on the first visit.");
        InteriorDetails = string.Join("\n", lines);
        if (g.MapFolder == null) { InteriorPreview = null; return; }
        var folder = g.MapFolder;
        var img = await Task.Run(() =>
        {
            var map = RenderMap(folder);
            if (map == null) return null;
            var copy = map.Value.Image.Clone();
            if (warp is { } wp2) SecretBasePreview.MarkTile(copy, wp2.X, wp2.Y, SecretBasePreview.Entrance);
            if (g.PcX is { } px && g.PcY is { } py) SecretBasePreview.MarkTile(copy, px, py, SecretBasePreview.Computer);
            return Imaging.ToBitmap(copy);
        });
        if (SelectedGroup == g) InteriorPreview = img;
    }

    // ------------------------------------------------------------------ Decorations

    private List<DecorationRow> _allRows = [];
    public ObservableCollection<DecorationRow> DecorationRows { get; } = [];
    private string _decorationFilter = "";
    public string DecorationFilter { get => _decorationFilter; set { if (Set(ref _decorationFilter, value)) ApplyDecorations(); } }

    private void ApplyDecorations()
    {
        var f = DecorationFilter.Trim();
        DecorationRows.Clear();
        foreach (var r in _allRows.Where(r => f.Length == 0 || r.Constant.Contains(f, StringComparison.OrdinalIgnoreCase) || r.Name.Contains(f, StringComparison.OrdinalIgnoreCase)
                                              || r.Category.Contains(f, StringComparison.OrdinalIgnoreCase) || r.Graphics.Contains(f, StringComparison.OrdinalIgnoreCase)))
            DecorationRows.Add(r);
    }

    private DecorationRow? _selectedDecoration;
    private BitmapSource? _decorationPreview;
    private string _decorationDetails = "", _editName = "", _editPrice = "", _editDescription = "";
    public DecorationRow? SelectedDecoration
    {
        get => _selectedDecoration;
        set
        {
            if (_selectedDecoration == value) return;
            if (DecorationEdited && MessageBox.Show(Application.Current.MainWindow, $"Discard the unsaved changes of {_selectedDecoration?.Constant}?", "Decorations",
                    MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            {
                // Keep the selection; the grid is told to re-read it.
                Application.Current.Dispatcher.BeginInvoke(() => OnPropertyChanged(nameof(SelectedDecoration)));
                return;
            }
            _selectedDecoration = value;
            OnPropertyChanged();
            ShowDecoration();
        }
    }
    public BitmapSource? DecorationPreview { get => _decorationPreview; private set => Set(ref _decorationPreview, value); }
    public string DecorationDetails { get => _decorationDetails; private set => Set(ref _decorationDetails, value); }
    public string EditName { get => _editName; set { if (Set(ref _editName, value)) OnPropertyChanged(nameof(DecorationEdited)); } }
    public string EditPrice { get => _editPrice; set { if (Set(ref _editPrice, value)) OnPropertyChanged(nameof(DecorationEdited)); } }
    public string EditDescription { get => _editDescription; set { if (Set(ref _editDescription, value)) OnPropertyChanged(nameof(DecorationEdited)); } }
    public bool DecorationEdited => SelectedDecoration is { } r &&
                                    (EditName != r.Decoration.Name || EditPrice.Trim() != r.Decoration.Price.ToString() || Norm(EditDescription) != r.Decoration.Description);

    private static string Norm(string s) => s.Replace("\r\n", "\n").TrimEnd('\n');

    private void ShowDecoration()
    {
        var r = SelectedDecoration;
        if (r == null) { _editName = _editPrice = _editDescription = ""; DecorationPreview = null; DecorationDetails = ""; RaiseEdits(); return; }
        var d = r.Decoration;
        _editName = d.Name;
        _editPrice = d.Price.ToString();
        _editDescription = d.Description.Replace("\n", "\r\n");
        RaiseEdits();
        var shared = _decor?.Items.Where(o => o != d && o.DescriptionSymbol == d.DescriptionSymbol).Select(o => o.Constant).ToList() ?? [];
        DecorationDetails = $"{d.Constant} = {d.Id}\n" +
                            $"Category {d.CategoryText} · shape {d.ShapeText} · permission {d.PermissionText}\n" +
                            (d.IsSprite
                                ? $"Graphics: object sprite {d.ObjectGfx} ({d.TilesSymbol}). Placed in a base it becomes one of the OBJ_EVENT_GFX_VAR_ persons of the interior map."
                                : $"Graphics: {d.Tiles.Count} metatile(s) of the secret base tileset ({d.TilesSymbol}), drawn into the map when placed.") +
                            $"\nMenu icon: {(d.IconPng != null ? d.IconPng : d.HasIconEntry ? "none – the menu builds it from the sprite/metatiles" : "missing entry in gDecorIconTable!")}" +
                            (shared.Count > 0 ? $"\nThe description {d.DescriptionSymbol} is shared with {string.Join(", ", shared)}." : "");
        var project = _ctx.Project;
        if (project == null || _decor == null) { DecorationPreview = null; return; }
        var big = SecretBasePreview.Decoration(project, d, _decor, _sprites, _palettes, preferIcon: false);
        var icon = d.IconPng != null ? SecretBasePreview.Decoration(project, d, _decor, _sprites, _palettes) : null;
        if (big == null) { DecorationPreview = icon != null ? ImageReplaceFlow.Scaled(icon, 3) : null; return; }
        if (icon != null)
        {
            // Graphics and icon side by side.
            var both = new RgbaImage(big.Width + 8 + icon.Width, Math.Max(big.Height, icon.Height));
            both.Blit(big, 0, 0, 0, both.Height - big.Height, big.Width, big.Height);
            both.Blit(icon, 0, 0, big.Width + 8, 0, icon.Width, icon.Height);
            big = both;
        }
        DecorationPreview = ImageReplaceFlow.Scaled(big, big.Width > 48 ? 2 : 4);
    }

    private void RaiseEdits()
    {
        OnPropertyChanged(nameof(EditName));
        OnPropertyChanged(nameof(EditPrice));
        OnPropertyChanged(nameof(EditDescription));
        OnPropertyChanged(nameof(DecorationEdited));
    }

    private void SaveDecoration()
    {
        var project = _ctx.Project;
        if (project == null || SelectedDecoration is not { } r) return;
        var d = r.Decoration;
        if (!int.TryParse(EditPrice.Trim(), out var price)) { ProjectWriter.Warn("Decoration", "The price must be a number (0 … 65535)."); return; }
        var name = EditName.Trim();
        var desc = Norm(EditDescription);
        var files = new List<PlannedFile>();
        try
        {
            if (name != d.Name || price != d.Price)
                files.Add(ProjectWriter.ModifyText(project.Root, Decorations.HeaderFile,
                    t => Decorations.SetPrice(Decorations.SetName(t, d.Constant, name), d.Constant, price),
                    $"{d.Constant}: " + string.Join(", ", new[] { name != d.Name ? $"name \"{d.Name}\" → \"{name}\"" : null, price != d.Price ? $"price {d.Price} → {price}" : null }.OfType<string>())));
            if (desc != d.Description)
                files.Add(ProjectWriter.ModifyText(project.Root, Decorations.DescriptionFile, t => Decorations.SetDescription(t, d.DescriptionSymbol, desc),
                    $"{d.DescriptionSymbol}: new text"));
        }
        catch (InvalidOperationException ex)
        {
            ProjectWriter.Warn("Decoration", ex.Message);
            return;
        }
        if (files.Count == 0) return;
        var shared = _decor?.Items.Where(o => o != d && o.DescriptionSymbol == d.DescriptionSymbol).Select(o => o.Constant).ToList() ?? [];
        var notes = shared.Count > 0 && desc != d.Description ? new[] { $"The description is shared with {string.Join(", ", shared)} – they change too." } : [];
        if (!ProjectWriter.Apply(project.Root, $"Edit decoration {d.Constant}", "src/data/decoration", files, notes)) return;
        _editName = d.Name; _editPrice = d.Price.ToString(); _editDescription = d.Description; // not dirty while reloading
        _selectedDecoration = null;
        OnPropertyChanged(nameof(DecorationEdited));
        DecorationFilter = d.Constant;
        _ctx.Reload();
    }

    private void FixIconTable()
    {
        var project = _ctx.Project;
        if (project == null || _decor == null) return;
        var missing = _decor.Items.Where(d => !d.HasIconEntry).ToList();
        if (missing.Count == 0) return;
        PlannedFile file;
        try
        {
            file = ProjectWriter.ModifyText(project.Root, Decorations.IconTableFile, t => Decorations.AddMissingIconEntries(t, missing),
                "Adds {NULL, NULL} for " + string.Join(", ", missing.Select(m => m.Constant)));
        }
        catch (InvalidOperationException ex) { ProjectWriter.Warn("Icon table", ex.Message); return; }
        if (ProjectWriter.Apply(project.Root, "Complete gDecorIconTable", "src/data/decoration", [file],
                "{NULL, NULL} makes the decoration menu build the icon from the object sprite or the metatiles, like for the small dolls."))
            _ctx.Reload();
    }

    private void NewDecoration()
    {
        var project = _ctx.Project;
        if (project == null || _decor == null) return;
        var win = new NewDecorationWindow(project, _decor, _sprites, _palettes) { Owner = Application.Current.MainWindow };
        if (win.ShowDialog() != true || win.Request is not { } req) return;
        DecorationPlanResult result;
        try
        {
            result = DecorationPlanner.Plan(project.Root, req, _decor, _sprites, _palettes);
        }
        catch (ExportValidationException ex)
        {
            ProjectWriter.Warn("New decoration", string.Join("\n", ex.Errors));
            return;
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or PngFormatException)
        {
            ProjectWriter.Warn("New decoration", ex.Message);
            return;
        }
        if (result.ColorChanges.Count > 0 && MessageBox.Show(Application.Current.MainWindow,
                "These colors are not in the palette and will be replaced:\n\n" +
                string.Join("\n", result.ColorChanges.Take(12).Select(c => $"  {c.From.ToHex()} → {c.To.ToHex()}")) + "\n\nContinue?",
                "Confirm color loss", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        if (!ProjectWriter.Apply(project.Root, result.Plan.TilesetName, result.Plan.TilesetFolder, result.Plan.Files, [.. result.Plan.Notes])) return;
        DecorationFilter = result.Constant;
        _ctx.Reload();
    }
}
