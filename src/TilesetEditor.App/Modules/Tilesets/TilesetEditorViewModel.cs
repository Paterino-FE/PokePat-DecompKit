using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using TilesetEditor.App.Controls;
using TilesetEditor.App.Infrastructure;
using TilesetEditor.App.Shell;
using TilesetEditor.Compiler;
using TilesetEditor.Core.Artwork;
using TilesetEditor.Core.Gba;
using TilesetEditor.Core.Imaging;
using TilesetEditor.Core.Project;
using TilesetEditor.Export;

namespace TilesetEditor.App.Modules.Tilesets;

public sealed record Swatch(Brush Brush, string Tooltip, string Label = "");
public sealed record PaletteRow(string Title, IReadOnlyList<Swatch> Colors);
public sealed record ColorUsageRow(ColorUsage Usage, Brush Brush)
{
    public string Text => $"{Usage.Hex}  ({Usage.Pixels} px, {Usage.Tiles} tiles)";
}

/// <summary>Tileset module: PNG import → checks → Porytiles compile in a sandbox → GBA preview → export.</summary>
public sealed class TilesetEditorViewModel : ObservableObject, IToolkitModule
{
    private readonly ProjectContext _ctx;
    private TilesetData? _primaryData;
    private Artwork? _artwork;
    private int _artVersion;
    private readonly Stack<Artwork> _undo = new();
    private CompileResult? _compile;
    private int _compiledVersion = -1;
    private string? _compiledPrimary;
    private bool _exported;
    private CancellationTokenSource? _cts;
    private List<Issue> _preIssues = [];

    public TilesetEditorViewModel(ProjectContext ctx)
    {
        _ctx = ctx;
        _ctx.ProjectChanged += (_, _) => OnProjectChanged();
        OpenImageCommand = new RelayCommand(OpenImage, () => _ctx.IsOpen && !IsCompiling);
        OpenLayersCommand = new RelayCommand(OpenLayers, () => _ctx.IsOpen && !IsCompiling);
        CompileCommand = new AsyncRelayCommand(CompileAsync, () => _ctx.IsOpen && Artwork != null && SelectedPrimary != null && !IsCompiling);
        CancelCompileCommand = new RelayCommand(() => _cts?.Cancel(), () => IsCompiling);
        ExportCommand = new RelayCommand(Export, () => IsCompiledCurrent && !IsCompiling);
        ReplaceColorCommand = new RelayCommand(ReplaceColor, () => Artwork != null && SelectedColor != null && !IsCompiling);
        SnapGbaCommand = new RelayCommand(SnapGba, () => Artwork != null && !IsCompiling);
        UndoCommand = new RelayCommand(Undo, () => _undo.Count > 0 && !IsCompiling);
        TileClickCommand = new RelayCommand(p => { if (p is Point pt) SelectTile((int)pt.X / 8, (int)pt.Y / 8); });
        UseColorAsReplacementCommand = new RelayCommand(p => { if (p is Swatch s) ReplacementText = s.Label; });
        View = new TilesetEditorView { DataContext = this };
        OnProjectChanged();
    }

    // ---- IToolkitModule
    public string Title => "Tilesets";
    public string Description => "Create a secondary tileset from PNG";
    public bool IsAvailable => true;
    public override string ToString() => Title;
    public FrameworkElement View { get; }
    public bool HasUnsavedChanges => Artwork != null && !_exported;
    public string? UnsavedChangesDescription => HasUnsavedChanges
        ? $"Tileset editor: import \"{TilesetName}\" has not been exported to the project yet."
        : null;

    // ---- Commands
    public ICommand OpenImageCommand { get; }
    public ICommand OpenLayersCommand { get; }
    public ICommand CompileCommand { get; }
    public ICommand CancelCompileCommand { get; }
    public ICommand ExportCommand { get; }
    public ICommand ReplaceColorCommand { get; }
    public ICommand SnapGbaCommand { get; }
    public ICommand UndoCommand { get; }
    public ICommand TileClickCommand { get; }
    public ICommand UseColorAsReplacementCommand { get; }

    // ---- Project / primary
    public ObservableCollection<TilesetInfo> PrimaryTilesets { get; } = [];
    public ObservableCollection<PaletteRow> PrimaryPalettes { get; } = [];

    private TilesetInfo? _selectedPrimary;
    public TilesetInfo? SelectedPrimary
    {
        get => _selectedPrimary;
        set
        {
            if (!Set(ref _selectedPrimary, value)) return;
            LoadPrimary();
            RunPrecheck();
            OnPropertyChanged(nameof(CompileStatus));
        }
    }

    private string _projectSummary = "No project open.";
    public string ProjectSummary { get => _projectSummary; private set => Set(ref _projectSummary, value); }

    private string _primaryInfo = "";
    public string PrimaryInfo { get => _primaryInfo; private set => Set(ref _primaryInfo, value); }

    // ---- Name
    private string _tilesetName = "gTileset_NewTileset";
    public string TilesetName
    {
        get => _tilesetName;
        set
        {
            if (!Set(ref _tilesetName, value)) return;
            ValidateName();
        }
    }

    private string? _nameError;
    public string? NameError { get => _nameError; private set => Set(ref _nameError, value); }

    private string _folderPreview = "";
    public string FolderPreview { get => _folderPreview; private set => Set(ref _folderPreview, value); }

    // ---- Import
    public IReadOnlyList<Layer> AllLayers { get; } = Enum.GetValues<Layer>();
    private Layer _singleImageLayer = Layer.Middle;
    public Layer SingleImageLayer { get => _singleImageLayer; set => Set(ref _singleImageLayer, value); }

    public Artwork? Artwork
    {
        get => _artwork;
        private set
        {
            _artwork = value;
            _artVersion++;
            _exported = false;
            OnPropertyChanged();
            OnPropertyChanged(nameof(ImportSummary));
            OnPropertyChanged(nameof(HasArtwork));
            OnPropertyChanged(nameof(IsCompiledCurrent));
            OnPropertyChanged(nameof(CompileStatus));
            RefreshPreview();
            RefreshColors();
            RunPrecheck();
            if (SelectedTile is { } t) SelectTile((int)t.X, (int)t.Y); else ClearSelection();
        }
    }

    public bool HasArtwork => Artwork != null;

    public string LayerModeText => _ctx.Project == null ? "" :
        _ctx.Project.Constants.LayerMode == LayerMode.Dual
            ? "Dual layer (8 tiles per metatile): each metatile uses two of the three layers bottom/middle/top."
            : "Triple layer (12 tiles per metatile): all three layers per metatile.";

    public string ImportSummary => Artwork == null
        ? "No image imported yet. Drag a PNG here or open one with the buttons."
        : $"{Artwork.Width}×{Artwork.Height} px · {Artwork.Columns}×{Artwork.Rows} = {Artwork.MetatileCount} metatiles · layers: " +
          string.Join(", ", Artwork.PresentLayers().Select(l => $"{LayerNames.German(l.Layer)} ({Path.GetFileName(Artwork.SourceFiles.GetValueOrDefault(l.Layer) ?? "edited")})"));

    private bool _showBottom = true, _showMiddle = true, _showTop = true, _showTileGrid;
    public bool ShowBottom { get => _showBottom; set { if (Set(ref _showBottom, value)) RefreshPreview(); } }
    public bool ShowMiddle { get => _showMiddle; set { if (Set(ref _showMiddle, value)) RefreshPreview(); } }
    public bool ShowTop { get => _showTop; set { if (Set(ref _showTop, value)) RefreshPreview(); } }
    public bool ShowTileGrid { get => _showTileGrid; set => Set(ref _showTileGrid, value); }

    private double _zoom = 4;
    public double Zoom { get => _zoom; set => Set(ref _zoom, Math.Clamp(Math.Round(value), 1, 12)); }

    private BitmapSource? _previewImage, _compiledImage;
    public BitmapSource? PreviewImage { get => _previewImage; private set => Set(ref _previewImage, value); }
    public BitmapSource? CompiledImage { get => _compiledImage; private set => Set(ref _compiledImage, value); }

    private int _selectedTab;
    public int SelectedTab { get => _selectedTab; set => Set(ref _selectedTab, value); }

    private IReadOnlyList<CanvasMarker> _markers = [];
    public IReadOnlyList<CanvasMarker> Markers { get => _markers; private set => Set(ref _markers, value); }

    // ---- Issues
    public ObservableCollection<Issue> Issues { get; } = [];
    private Issue? _selectedIssue;
    public Issue? SelectedIssue
    {
        get => _selectedIssue;
        set
        {
            if (!Set(ref _selectedIssue, value)) return;
            if (value?.Location is { TileX: { } tx, TileY: { } ty }) SelectTile(tx, ty);
        }
    }

    private string _issueSummary = "";
    public string IssueSummary { get => _issueSummary; private set => Set(ref _issueSummary, value); }

    // ---- Selection
    private Point? _selectedTile;
    public Point? SelectedTile { get => _selectedTile; private set => Set(ref _selectedTile, value); }
    private string _selectionTitle = "No tile selected – click in the preview.";
    public string SelectionTitle { get => _selectionTitle; private set => Set(ref _selectionTitle, value); }
    public ObservableCollection<PaletteRow> SelectedTileColors { get; } = [];
    public ObservableCollection<PaletteRow> CompiledTilePalettes { get; } = [];
    private string _compiledTileInfo = "";
    public string CompiledTileInfo { get => _compiledTileInfo; private set => Set(ref _compiledTileInfo, value); }

    // ---- Colors
    public ObservableCollection<ColorUsageRow> ColorUsages { get; } = [];
    private ColorUsageRow? _selectedColor;
    public ColorUsageRow? SelectedColor { get => _selectedColor; set => Set(ref _selectedColor, value); }
    private string _replacementText = "";
    public string ReplacementText { get => _replacementText; set => Set(ref _replacementText, value); }
    private string _colorSummary = "";
    public string ColorSummary { get => _colorSummary; private set => Set(ref _colorSummary, value); }

    // ---- Compile state
    private bool _isCompiling;
    public bool IsCompiling { get => _isCompiling; private set { if (Set(ref _isCompiling, value)) CommandManager.InvalidateRequerySuggested(); } }
    private string _log = "";
    public string Log { get => _log; private set => Set(ref _log, value); }

    public bool IsCompiledCurrent => _compile is { Success: true } && _compiledVersion == _artVersion && _compiledPrimary == SelectedPrimary?.Name;

    public string CompileStatus
    {
        get
        {
            if (IsCompiling) return "Porytiles running …";
            if (Artwork == null) return "";
            if (_compile == null) return "Not compiled yet.";
            if (!IsCompiledCurrent) return _compile.Success ? "Image or primary changed – please recompile." : "Last compile failed.";
            var c = _compile.Compiled!;
            var pals = c.MetatileEntries.Select(e => e.Palette).Where(p => p >= _ctx.Project!.Constants.NumPalsInPrimary).Distinct().Count();
            return $"Compiled with Porytiles {_compile.CompilerVersion?.Version}: {c.Tiles.Count}/{_ctx.Project!.Constants.MaxSecondaryTiles} tiles, " +
                   $"{c.MetatileCount} metatiles, {pals}/{_ctx.Project.Constants.NumPalsInSecondary} own palettes." + (_exported ? " Exported." : "");
        }
    }

    // =====================================================================

    private void OnProjectChanged()
    {
        PrimaryTilesets.Clear();
        var p = _ctx.Project;
        if (p == null)
        {
            ProjectSummary = "No project open.";
            return;
        }
        foreach (var t in p.PrimaryTilesets) PrimaryTilesets.Add(t);
        var c = p.Constants;
        ProjectSummary = $"{c.LayerMode} layer · Secondary: up to {c.MaxSecondaryTiles} tiles, {c.MaxSecondaryMetatiles} metatiles, " +
                         $"palettes {c.NumPalsInPrimary}–{c.NumPalsTotal - 1} · attributes {p.Attributes.SizeBytes} bytes";
        OnPropertyChanged(nameof(LayerModeText));
        // Keep the current selection across reloads (e.g. after an export).
        var keep = SelectedPrimary?.Name;
        _selectedPrimary = null;
        SelectedPrimary = PrimaryTilesets.FirstOrDefault(t => t.Name == keep) ?? PrimaryTilesets.FirstOrDefault(t => t.Name == "gTileset_General") ?? PrimaryTilesets.FirstOrDefault();
        if (_tilesetName == "gTileset_NewTileset" || p.FindTileset(_tilesetName) != null) _tilesetName = SuggestFreeName(p);
        OnPropertyChanged(nameof(TilesetName));
        ValidateName();
    }

    /// <summary>Called from the Maps module and the Porymap bridge: start a new secondary for a map with this primary.</summary>
    public void PreparePrimary(string primaryName)
    {
        var t = PrimaryTilesets.FirstOrDefault(x => x.Name == primaryName);
        if (t != null) SelectedPrimary = t;
        AppendLog($"Primary tileset taken from the map: {primaryName}" + (t == null ? " (not found in the project)" : ""));
    }

    private static string SuggestFreeName(ProjectInfo p)
    {
        var fs = new DiskFileSource(p.Root);
        for (int i = 1; ; i++)
        {
            var n = i == 1 ? "gTileset_NewTileset" : $"gTileset_NewTileset{i}";
            if (ExportPlanner.CheckCollisions(p, fs, n).Count == 0) return n;
        }
    }

    private void ValidateName()
    {
        var p = _ctx.Project;
        var err = TilesetNaming.Validate(TilesetName);
        if (err == null && p != null)
        {
            var coll = ExportPlanner.CheckCollisions(p, new DiskFileSource(p.Root), TilesetName);
            if (coll.Count > 0) err = coll[0];
        }
        NameError = err;
        FolderPreview = p != null && TilesetNaming.Validate(TilesetName) == null
            ? $"Target folder: {p.SecondaryTilesetsDir}{TilesetNaming.ToFolderName(TilesetName)}/"
            : "";
    }

    private void LoadPrimary()
    {
        PrimaryPalettes.Clear();
        _primaryData = null;
        var p = _ctx.Project;
        var t = SelectedPrimary;
        if (p == null || t == null) { PrimaryInfo = ""; return; }
        try
        {
            _primaryData = p.LoadTilesetData(t);
            for (int i = 0; i < p.Constants.NumPalsInPrimary && i < _primaryData.Palettes.Count; i++)
                PrimaryPalettes.Add(new PaletteRow($"{i:00}", _primaryData.Palettes[i].Select((c, k) => Sw(c, $"Palette {i}, color {k}: {c.ToHex()}")).ToList()));
            PrimaryInfo = $"{t.Name}: {_primaryData.Tiles.Count} tiles, {_primaryData.MetatileCount} metatiles, palettes 0–{p.Constants.NumPalsInPrimary - 1} " +
                          $"(folder {Path.GetDirectoryName(t.TilesImagePath)?.Replace('\\', '/')})";
        }
        catch (Exception ex) when (ex is IOException or FormatException or PngFormatException or InvalidOperationException)
        {
            PrimaryInfo = $"Could not load {t.Name}: {ex.Message}";
        }
    }

    private static Swatch Sw(Rgba32 c, string tip) => new(Imaging.Brush(GbaColor.ToDisplayed(c with { A = 255 })), tip, c.ToHex());

    // ---- Import

    public void ImportFiles(IReadOnlyList<string> paths)
    {
        if (!_ctx.IsOpen) { Message("Please open a project folder first."); return; }
        var pngs = paths.Where(p => p.EndsWith(".png", StringComparison.OrdinalIgnoreCase)).ToList();
        if (pngs.Count == 0) { Message("Please use PNG files."); return; }
        if (!ConfirmDiscard()) return;
        try
        {
            Artwork art;
            if (pngs.Count == 1)
            {
                art = ArtworkLoader.LoadSingle(pngs[0], SingleImageLayer);
            }
            else
            {
                var assign = ArtworkLoader.AssignLayers(pngs);
                var text = string.Join("\n", assign.Select(kv => $"{LayerNames.German(kv.Key)}: {Path.GetFileName(kv.Value)}"));
                if (MessageBox.Show(Owner, $"The files will be assigned to these layers:\n\n{text}\n\nContinue?",
                        "Assign layers", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
                art = ArtworkLoader.LoadLayers(assign);
            }
            _undo.Clear();
            _compile = null;
            CompiledImage = null;
            SelectedTile = null;
            if (TilesetName.StartsWith("gTileset_NewTileset", StringComparison.Ordinal))
            {
                var suggestion = TilesetNaming.Suggest(Path.GetFileNameWithoutExtension(pngs[0]).Replace("_middle", "").Replace("_top", "").Replace("_bottom", ""));
                if (TilesetNaming.Validate(suggestion) == null && _ctx.Project != null &&
                    ExportPlanner.CheckCollisions(_ctx.Project, new DiskFileSource(_ctx.Project.Root), suggestion).Count == 0)
                    TilesetName = suggestion;
            }
            Artwork = art;
            SelectedTab = 0;
            AppendLog($"Imported: {string.Join(", ", pngs.Select(Path.GetFileName))}");
        }
        catch (ArtworkImportException ex)
        {
            MessageBox.Show(Owner, ex.Message, "Import not possible", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void OpenImage()
    {
        var dlg = new OpenFileDialog { Filter = "PNG images|*.png", Title = $"Open PNG for layer {LayerNames.German(SingleImageLayer)}" };
        if (dlg.ShowDialog(Owner) == true) ImportFiles([dlg.FileName]);
    }

    private void OpenLayers()
    {
        var dlg = new OpenFileDialog { Filter = "PNG images|*.png", Multiselect = true, Title = "Open layer PNGs (bottom/middle/top, 2–3 files)" };
        if (dlg.ShowDialog(Owner) == true) ImportFiles(dlg.FileNames);
    }

    private bool ConfirmDiscard()
    {
        if (Artwork == null || _exported) return true;
        return MessageBox.Show(Owner, "The current import has not been exported yet and will be lost. Continue?",
            "Replace import", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;
    }

    // ---- Preview and checks

    private void RefreshPreview()
    {
        PreviewImage = Artwork == null ? null : Imaging.ToBitmap(Artwork.Composite([ShowBottom, ShowMiddle, ShowTop]));
    }

    private void RunPrecheck()
    {
        _preIssues = Artwork == null || _ctx.Project == null ? [] : ArtworkAnalyzer.Analyze(Artwork, _ctx.Project.Constants, _primaryData?.Palettes);
        RebuildIssues();
    }

    private void RebuildIssues()
    {
        Issues.Clear();
        var compileIssues = _compile != null && _compiledVersion == _artVersion ? _compile.Issues.Where(i => i.Severity != IssueSeverity.Info) : [];
        foreach (var i in _preIssues.Concat(compileIssues).OrderByDescending(i => i.Severity)) Issues.Add(i);
        Markers = Issues.Where(i => i.Location is { TileX: not null, TileY: not null })
            .Select(i => new CanvasMarker(i.Location!.TileX!.Value, i.Location.TileY!.Value, i.Severity == IssueSeverity.Error))
            .Distinct().ToList();
        int e = Issues.Count(i => i.Severity == IssueSeverity.Error), w = Issues.Count(i => i.Severity == IssueSeverity.Warning);
        IssueSummary = Artwork == null ? "" : e + w == 0 ? "No problems found." : $"{e} errors, {w} warnings";
    }

    private void RefreshColors()
    {
        ColorUsages.Clear();
        if (Artwork == null) { ColorSummary = ""; return; }
        var usage = ColorOperations.Usage(Artwork);
        foreach (var u in usage) ColorUsages.Add(new ColorUsageRow(u, Imaging.Brush(u.Color)));
        int gba = usage.Select(u => GbaColor.ToBgr555(u.Color)).Distinct().Count();
        ColorSummary = $"{usage.Count} colors in the image ({gba} distinct GBA colors). Transparent: alpha 0 or {Artwork.TransparentColor.ToHex()}.";
    }

    private void ClearSelection()
    {
        SelectedTileColors.Clear();
        CompiledTilePalettes.Clear();
        CompiledTileInfo = "";
        SelectionTitle = "No tile selected – click in the preview.";
    }

    private void SelectTile(int tx, int ty)
    {
        var art = Artwork;
        if (art == null || tx < 0 || ty < 0 || tx >= art.TileColumns || ty >= art.TileRows) { ClearSelection(); return; }
        SelectedTile = new Point(tx, ty);
        int m = art.MetatileIndexAt(tx * 8, ty * 8);
        SelectionTitle = $"Tile {tx},{ty} · Metatile {m} (column {m % art.Columns}, row {m / art.Columns})";
        SelectedTileColors.Clear();
        foreach (var (layer, _) in art.PresentLayers())
        {
            var colors = ArtworkAnalyzer.TileColors(art, layer, tx, ty);
            if (colors.Count == 0) continue;
            SelectedTileColors.Add(new PaletteRow($"{LayerNames.German(layer)}: {colors.Count} color(s){(colors.Count > ArtworkAnalyzer.MaxColorsPerTile ? " – too many!" : "")}",
                colors.Select(c => Sw(c.Color, $"{c.Color.ToHex()} – {c.Pixels} pixels (GBA 0x{GbaColor.ToBgr555(c.Color):X4})")).ToList()));
        }
        UpdateCompiledSelection(m, tx, ty);
    }

    private void UpdateCompiledSelection(int m, int tx, int ty)
    {
        CompiledTilePalettes.Clear();
        CompiledTileInfo = "";
        if (!IsCompiledCurrent || _ctx.Project == null) return;
        var c = _compile!.Compiled!;
        if (m >= c.MetatileCount) return;
        var p = _ctx.Project;
        int q = (tx % 2) + 2 * (ty % 2);
        int lt = p.Attributes.LayerType(c.Attributes[m]);
        string[] names = lt switch
        {
            1 => ["Bottom", "Middle"],
            2 => ["Bottom", "Top"],
            _ => ["Middle", "Top"],
        };
        var lines = new List<string> { $"Layer type: {LayerTypes.Describe(lt)}, behavior 0x{p.Attributes.Behavior(c.Attributes[m]):X2}" };
        var renderer = new TilesetRenderer(p.Constants, _compile.Primary!, c);
        int layers = c.TilesPerMetatile / 4;
        for (int l = 0; l < layers; l++)
        {
            var e = c.GetEntry(m, l * 4 + q);
            string layerName = layers == 2 ? names[l] : new[] { "Bottom", "Middle", "Top" }[l];
            bool empty = e.TileId == 0 && e.Palette == 0;
            string src = e.TileId < p.Constants.NumTilesInPrimary ? "Primary" : "Secondary";
            lines.Add(empty ? $"{layerName}: empty" :
                $"{layerName}: Tile {e.TileId} ({src}), palette bank {e.Palette}{(e.HFlip ? ", H-flipped" : "")}{(e.VFlip ? ", V-flipped" : "")}");
            if (!empty)
                CompiledTilePalettes.Add(new PaletteRow($"Bank {e.Palette} ({layerName}, {(e.Palette < p.Constants.NumPalsInPrimary ? "Primary" : "Secondary")})",
                    Enumerable.Range(0, 16).Select(k =>
                    {
                        var col = renderer.ResolveColor(e.Palette, k);
                        return new Swatch(Imaging.Brush(col), k == 0 ? "Index 0: transparent" : $"Index {k}: {col.ToHex()} (as on the GBA)", col.ToHex());
                    }).ToList()));
        }
        CompiledTileInfo = string.Join("\n", lines);
    }

    // ---- Color editing

    private void ReplaceColor()
    {
        if (Artwork == null || SelectedColor == null) return;
        if (!Rgba32.TryParse(ReplacementText, out var to))
        {
            Message("Please enter a target color as #RRGGBB or R,G,B (or click a color on the right).");
            return;
        }
        var change = ColorOperations.Replace(Artwork, SelectedColor.Usage.Color, to);
        ApplyColorChange(change, $"{SelectedColor.Usage.Hex} → {to.ToHex()}");
    }

    private void SnapGba()
    {
        if (Artwork == null) return;
        ApplyColorChange(ColorOperations.SnapToGba(Artwork), "Round to GBA colors");
    }

    private void ApplyColorChange(ColorChange change, string title)
    {
        if (change.ChangedPixels == 0) { Message("No pixels affected."); return; }
        if (change.LosesColors)
        {
            var examples = string.Join("\n", change.Mapping.Take(12).Select(m => $"  {m.From.ToHex()} → {m.To.ToHex()}"));
            var answer = MessageBox.Show(Owner,
                $"{title}\n\nThis merges colors: {change.UniqueBefore} → {change.UniqueAfter} colors " +
                $"({change.LostColors} lost, {change.ChangedPixels} pixels change).\n\n{examples}" +
                (change.Mapping.Count > 12 ? $"\n  … and {change.Mapping.Count - 12} more" : "") +
                "\n\nThe change only affects the import in the toolkit (can be undone), not the source file. Continue?",
                "Confirm color loss", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (answer != MessageBoxResult.Yes) return;
        }
        _undo.Push(Artwork!);
        Artwork = change.Result;
        AppendLog($"{title}: {change.ChangedPixels} pixels changed, {change.UniqueBefore} → {change.UniqueAfter} colors.");
    }

    private void Undo()
    {
        if (_undo.Count == 0) return;
        Artwork = _undo.Pop();
        AppendLog("Last color change undone.");
    }

    // ---- Compile

    private async Task CompileAsync()
    {
        var p = _ctx.Project;
        if (p == null || Artwork == null || SelectedPrimary == null) return;
        if (_preIssues.Any(i => i.Severity == IssueSeverity.Error) &&
            MessageBox.Show(Owner, "The pre-check reports errors. Porytiles will likely fail, but it will provide its own diagnostics. Compile anyway?",
                "Compile", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;

        IsCompiling = true;
        OnPropertyChanged(nameof(CompileStatus));
        _cts = new CancellationTokenSource();
        int version = _artVersion;
        var art = Artwork;
        var primary = SelectedPrimary;
        string name = TilesetNaming.Validate(TilesetName) == null ? TilesetName : "gTileset_TilesetEditorPreview";
        var compiler = new SandboxCompiler(new PorytilesClient(_ctx.Tools.Porytiles));
        var progress = new Progress<string>(AppendLog);
        try
        {
            // Runs on the thread pool; the UI stays responsive.
            var result = await Task.Run(() => compiler.CompileAsync(new CompileRequest
            {
                Project = p, Primary = primary, Artwork = art, TilesetName = name,
            }, progress, _cts.Token));
            _compile = result;
            _compiledVersion = version;
            _compiledPrimary = primary.Name;
            if (result.Success && version == _artVersion)
            {
                var img = new RgbaImage(art.Width, art.Height);
                var renderer = new TilesetRenderer(p.Constants, result.Primary!, result.Compiled!);
                for (int m = 0; m < Math.Min(art.MetatileCount, result.Compiled!.MetatileCount); m++)
                {
                    var (x, y) = art.MetatileOrigin(m);
                    renderer.DrawMetatile(img, m, x, y);
                }
                CompiledImage = Imaging.ToBitmap(img);
                SelectedTab = 1;
            }
            else if (!result.Success)
            {
                CompiledImage = null;
            }
        }
        catch (OperationCanceledException)
        {
            AppendLog("Cancelled.");
        }
        finally
        {
            IsCompiling = false;
            _cts.Dispose();
            _cts = null;
        }
        RebuildIssues();
        OnPropertyChanged(nameof(IsCompiledCurrent));
        OnPropertyChanged(nameof(CompileStatus));
        if (SelectedTile is { } t) SelectTile((int)t.X, (int)t.Y);
    }

    // ---- Export

    private void Export()
    {
        var p = _ctx.Project;
        if (p == null || !IsCompiledCurrent || Artwork == null || SelectedPrimary == null) return;
        ValidateName();
        if (NameError != null) { Message(NameError); return; }

        ExportPlan plan;
        try
        {
            plan = ExportPlanner.Plan(p, new ExportInput
            {
                TilesetName = TilesetName,
                PrimaryName = SelectedPrimary.Name,
                CompiledDir = _compile!.OutputDir!,
                Compiled = _compile.Compiled!,
                Primary = _compile.Primary!,
                CompilerVersion = _compile.CompilerVersion?.Version ?? "?",
                CompileTransparency = _compile.SandboxTransparency,
                SourceArtwork = Artwork,
            }, new ExportOptions { IncludeSources = IncludeSources });
        }
        catch (ExportValidationException ex)
        {
            MessageBox.Show(Owner, ex.Message, "Export not possible", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        catch (IOException ex)
        {
            MessageBox.Show(Owner, ex.Message, "Export not possible", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var dlg = new ExportPlanWindow(plan) { Owner = Owner };
        if (dlg.ShowDialog() != true) return;

        var name = TilesetName;
        var result = new ExportApplier().Apply(plan, () =>
        {
            var reloaded = ProjectLoader.Load(p.Root);
            return reloaded.FindTileset(name) is { IsFullyResolved: true } ? null : "The new tileset cannot be resolved after writing.";
        });
        if (!result.Success)
        {
            AppendLog("Export failed: " + result.Error);
            MessageBox.Show(Owner,
                $"Export failed: {result.Error}\n\n" +
                (result.RolledBack ? "All changes were rolled back." + (result.RollbackProblems.Count > 0 ? "\nProblems during rollback:\n" + string.Join("\n", result.RollbackProblems) : "") : ""),
                "Export", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }
        _exported = true;
        AppendLog($"Exported to {plan.TilesetFolder} ({result.Written.Count} files). Backup: {result.BackupDir}");
        MessageBox.Show(Owner,
            $"{name} was exported.\n\nFolder: {plan.TilesetFolder}\nModified: {string.Join(", ", plan.Modified.Select(f => Path.GetFileName(f.RelativePath)))}\n" +
            $"Backup of the modified files: {result.BackupDir}\n\nNext step: open the layout in Porymap and select \"{name}\" as the secondary tileset.",
            "Export complete", MessageBoxButton.OK, MessageBoxImage.Information);
        _ctx.Reload();
        OnPropertyChanged(nameof(CompileStatus));
    }

    private bool _includeSources = true;
    public bool IncludeSources { get => _includeSources; set => Set(ref _includeSources, value); }

    // ---- helpers

    private static Window? Owner => Application.Current?.MainWindow;

    private static void Message(string text) => MessageBox.Show(Owner, text, "Tileset editor", MessageBoxButton.OK, MessageBoxImage.Information);

    private void AppendLog(string line)
    {
        var text = Log + line.TrimEnd() + "\n";
        if (text.Length > 200_000) text = text[^150_000..];
        Log = text;
    }
}
