using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using TilesetEditor.App.Infrastructure;
using TilesetEditor.App.Shell;
using TilesetEditor.Core.Maps;
using TilesetEditor.Core.Project;

namespace TilesetEditor.App.Modules.Maps;

public sealed record MapLink(string Text, string? TargetMap);

/// <summary>Overview of maps and layouts with a rendered preview. Maps are created and edited in Porymap (double-click opens one there).</summary>
public sealed class MapsViewModel : ObservableObject, IToolkitModule
{
    private readonly ProjectContext _ctx;
    private readonly Action<string> _newTilesetForPrimary;
    private readonly Action<string?> _openInPorymap;
    private MapCatalogResult? _catalog;
    private int _loadGeneration, _previewGeneration;

    public MapsViewModel(ProjectContext ctx, Action<string> newTilesetForPrimary, Action<string?> openInPorymap)
    {
        _ctx = ctx;
        _newTilesetForPrimary = newTilesetForPrimary;
        _openInPorymap = openInPorymap;
        OpenInPorymapCommand = new RelayCommand(() => _openInPorymap(Selected!.Name), () => Selected != null);
        ChangeTilesetsCommand = new RelayCommand(ChangeTilesets, () => Selected?.Layout != null && _ctx.Project != null);
        _ctx.ProjectChanged += (_, _) => _ = LoadAsync();
        NewTilesetCommand = new RelayCommand(() => _newTilesetForPrimary(Selected!.PrimaryTileset!), () => Selected?.PrimaryTileset != null);
        OpenFolderCommand = new RelayCommand(() => OpenInExplorer(Selected!.Folder, select: false), () => Selected != null);
        OpenFileCommand = new RelayCommand(p => { if (p is string rel) OpenInExplorer(rel, select: true); });
        FollowLinkCommand = new RelayCommand(p => { if (p is MapLink { TargetMap: { } t }) SelectMap(t); });
        View = new MapsView { DataContext = this };
        _ = LoadAsync();
    }

    public string Title => "Maps";
    public string Description => "Maps, layouts, tilesets, scripts";
    public bool IsAvailable => true;
    public override string ToString() => Title;
    public FrameworkElement View { get; }
    public bool HasUnsavedChanges => false;
    public string? UnsavedChangesDescription => null;

    public ICommand NewTilesetCommand { get; }
    public ICommand OpenFolderCommand { get; }
    public ICommand OpenFileCommand { get; }
    public ICommand FollowLinkCommand { get; }
    public ICommand OpenInPorymapCommand { get; }
    public ICommand ChangeTilesetsCommand { get; }

    /// <summary>Switches the primary/secondary tileset of the selected map's layout (data/layouts/layouts.json).</summary>
    private void ChangeTilesets()
    {
        var project = _ctx.Project;
        var layout = Selected?.Layout;
        if (project == null || layout == null) return;
        var primary = new System.Windows.Controls.ComboBox { ItemsSource = project.PrimaryTilesets.Select(t => t.Name).ToList(), SelectedItem = layout.PrimaryTileset, IsTextSearchEnabled = true };
        var secondary = new System.Windows.Controls.ComboBox { ItemsSource = project.SecondaryTilesets.Select(t => t.Name).ToList(), SelectedItem = layout.SecondaryTileset, IsTextSearchEnabled = true };
        var users = _all.Count(m => m.LayoutId == layout.Id);
        var panel = new System.Windows.Controls.StackPanel { Margin = new Thickness(12) };
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = $"Layout {layout.Id}" + (users > 1 ? $" (used by {users} maps)" : ""), FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 8),
        });
        panel.Children.Add(new System.Windows.Controls.TextBlock { Text = "Primary tileset" });
        panel.Children.Add(primary);
        panel.Children.Add(new System.Windows.Controls.TextBlock { Text = "Secondary tileset", Margin = new Thickness(0, 8, 0, 0) });
        panel.Children.Add(secondary);
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "The block layout of the map stays; metatile numbers now point into the chosen tilesets. Import new tilesets in the Tilesets module.",
            TextWrapping = TextWrapping.Wrap, FontSize = 11, Margin = new Thickness(0, 8, 0, 0),
        });
        var ok = new System.Windows.Controls.Button { Content = "Save…", IsDefault = true, FontWeight = FontWeights.SemiBold };
        var cancel = new System.Windows.Controls.Button { Content = "Cancel", IsCancel = true };
        var buttons = new System.Windows.Controls.StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);
        panel.Children.Add(buttons);
        var w = new Window { Title = $"Tilesets of {Selected!.Name}", Content = panel, Width = 460, SizeToContent = SizeToContent.Height, Owner = Application.Current.MainWindow, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        ok.Click += (_, _) => w.DialogResult = true;
        if (w.ShowDialog() != true) return;
        var files = new List<TilesetEditor.Export.PlannedFile>();
        try
        {
            var changes = new List<(string Key, string Value)>();
            if (primary.SelectedItem is string p && p != layout.PrimaryTileset) changes.Add(("primary_tileset", p));
            if (secondary.SelectedItem is string s && s != layout.SecondaryTileset) changes.Add(("secondary_tileset", s));
            if (changes.Count == 0) return;
            // One edit of layouts.json with both keys: apply them one after the other on the text.
            var file = TilesetEditor.Export.TilesetImportPlanner.AssignLayouts(project, [layout.Id], changes[0].Key, changes[0].Value);
            if (changes.Count > 1)
            {
                var text = System.Text.Encoding.UTF8.GetString(file.Content);
                var second = TilesetEditor.Export.TilesetImportPlanner.AssignLayoutsInText(text, [layout.Id], changes[1].Key, changes[1].Value);
                file = file with { Content = new System.Text.UTF8Encoding(false).GetBytes(second), Description = $"{layout.Id}: {string.Join(", ", changes.Select(c => $"{c.Key} = {c.Value}"))}" };
            }
            files.Add(file);
        }
        catch (TilesetEditor.Export.ExportValidationException ex) { ProjectWriter.Warn("Tilesets", string.Join("\n", ex.Errors)); return; }
        if (ProjectWriter.Apply(project.Root, $"Tilesets of {layout.Id}", "data/layouts", files)) _ctx.Reload();
    }

    private BitmapSource? _preview;
    /// <summary>The selected map rendered with its tilesets (16 px per block).</summary>
    public BitmapSource? Preview { get => _preview; private set { if (Set(ref _preview, value)) OnPropertyChanged(nameof(HasPreview)); } }
    public bool HasPreview => Preview != null;

    private string _previewNote = "";
    public string PreviewNote { get => _previewNote; private set => Set(ref _previewNote, value); }

    public ObservableCollection<MapInfo> Maps { get; } = [];
    private List<MapInfo> _all = [];

    private string _filter = "";
    public string Filter { get => _filter; set { if (Set(ref _filter, value)) ApplyFilter(); } }

    private string _summary = "";
    public string Summary { get => _summary; private set => Set(ref _summary, value); }

    private bool _isLoading;
    public bool IsLoading { get => _isLoading; private set => Set(ref _isLoading, value); }

    private MapInfo? _selected;
    public MapInfo? Selected
    {
        get => _selected;
        set
        {
            if (!Set(ref _selected, value)) return;
            BuildDetails();
            _ = RenderPreviewAsync();
        }
    }

    // Details
    public ObservableCollection<MapLink> Links { get; } = [];
    public ObservableCollection<string> Scripts { get; } = [];
    public ObservableCollection<string> ScriptLabels { get; } = [];
    public ObservableCollection<string> TilesetUsage { get; } = [];
    private string _details = "";
    public string Details { get => _details; private set => Set(ref _details, value); }

    /// <summary>Highlight from the Porymap bridge: the map currently open in Porymap.</summary>
    private string? _porymapMap;
    public string? PorymapMap { get => _porymapMap; set { if (Set(ref _porymapMap, value)) OnPropertyChanged(nameof(PorymapHint)); } }
    public string PorymapHint => PorymapMap == null ? "" : $"Open in Porymap: {PorymapMap}";

    private async Task LoadAsync()
    {
        var project = _ctx.Project;
        int gen = ++_loadGeneration;
        if (project == null) { _all = []; ApplyFilter(); Summary = ""; return; }
        IsLoading = true;
        Summary = "Reading maps …";
        var keep = Selected?.Name;
        // ~500 map.json files over a WSL share: read in the background.
        var catalog = await Task.Run(() => MapCatalog.Load(project));
        if (gen != _loadGeneration) return;
        _catalog = catalog;
        _all = catalog.Maps.ToList();
        IsLoading = false;
        Summary = $"{_all.Count} maps in {catalog.GroupOrder.Count} groups, {project.Layouts.Count} layouts" +
                  (catalog.Problems.Count > 0 ? $" · {catalog.Problems.Count} note(s): {catalog.Problems[0]}" : "");
        ApplyFilter();
        if (keep != null) SelectMap(keep);
    }

    private void ApplyFilter()
    {
        var f = Filter.Trim();
        Maps.Clear();
        foreach (var m in _all.Where(m => f.Length == 0
                     || m.Name.Contains(f, StringComparison.OrdinalIgnoreCase)
                     || (m.LayoutId?.Contains(f, StringComparison.OrdinalIgnoreCase) ?? false)
                     || (m.PrimaryTileset?.Contains(f, StringComparison.OrdinalIgnoreCase) ?? false)
                     || (m.SecondaryTileset?.Contains(f, StringComparison.OrdinalIgnoreCase) ?? false)
                     || m.Group.Contains(f, StringComparison.OrdinalIgnoreCase)))
            Maps.Add(m);
    }

    /// <summary>Selects a map by name or id (also used by the Porymap bridge).</summary>
    public bool SelectMap(string nameOrId)
    {
        var m = _catalog?.Find(nameOrId);
        if (m == null) return false;
        if (!Maps.Contains(m)) Filter = "";
        Selected = m;
        return true;
    }

    private void BuildDetails()
    {
        Links.Clear();
        Scripts.Clear();
        ScriptLabels.Clear();
        TilesetUsage.Clear();
        var m = Selected;
        if (m == null) { Details = ""; return; }
        var l = m.Layout;
        Details = string.Join("\n", new[]
        {
            $"Map:            {m.Name}  ({m.Id})",
            $"Group:          {m.Group}",
            $"Layout:         {m.LayoutId ?? "–"}" + (l != null ? $"  ({l.Width}×{l.Height} blocks, {l.BlockdataPath})" : ""),
            $"Primary:        {m.PrimaryTileset ?? "–"}",
            $"Secondary:      {m.SecondaryTileset ?? "–"}",
            $"Type / weather: {m.MapType} / {m.Weather}",
            $"Music:          {m.Music}",
            $"Region map:     {m.RegionMapSection}",
            $"Events:         {m.ObjectEvents} persons/objects, {m.Warps.Count} warps, {m.CoordEvents} triggers, {m.BgEvents} signs/hidden",
        });
        foreach (var c in m.Connections) Links.Add(new MapLink($"Connection {c.Direction}: {c.Map} (offset {c.Offset})", c.Map));
        foreach (var w in m.Warps) Links.Add(new MapLink($"Warp ({w.X},{w.Y}) → {w.DestinationMap} #{w.DestinationWarpId}", w.DestinationMap));
        foreach (var f in m.ScriptFiles) Scripts.Add(f);
        if (m.SharedScriptsMap != null) ScriptLabels.Add($"(shared scripts of {m.SharedScriptsMap})");
        foreach (var s in m.EventScripts) ScriptLabels.Add(s);
        if (_catalog != null && m.SecondaryTileset != null)
        {
            var users = _catalog.UsingTileset(m.SecondaryTileset).Select(x => x.Name).ToList();
            TilesetUsage.Add($"{m.SecondaryTileset} is used by {users.Count} map(s):");
            foreach (var u in users.Take(30)) TilesetUsage.Add("  " + u);
            if (users.Count > 30) TilesetUsage.Add($"  … and {users.Count - 30} more");
        }
    }

    private async Task RenderPreviewAsync()
    {
        var project = _ctx.Project;
        var m = Selected;
        int gen = ++_previewGeneration;
        Preview = null;
        if (project == null || m == null) { PreviewNote = ""; return; }
        PreviewNote = "Drawing map …";
        BitmapSource? bmp = null;
        string note;
        try
        {
            (bmp, note) = await Task.Run(() =>
            {
                var scene = MapScene.Load(project, m.Name);
                var img = MapRenderer.Render(project, scene);
                if (img == null) return ((BitmapSource?)null, scene.Problems.FirstOrDefault() ?? "No layout / block data for this map.");
                return ((BitmapSource?)Imaging.ToBitmap(img), $"{img.Width / MapRenderer.BlockSize}×{img.Height / MapRenderer.BlockSize} blocks · double-click a map to open it in Porymap");
            });
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            note = "The map could not be drawn: " + ex.Message;
        }
        if (gen != _previewGeneration) return; // another map was selected meanwhile
        Preview = bmp;
        PreviewNote = note;
    }

    private void OpenInExplorer(string rel, bool select)
    {
        if (_ctx.Root == null) return;
        var full = ProjectPaths.Combine(_ctx.Root, rel.Trim());
        if (select && File.Exists(full)) Process.Start("explorer.exe", $"/select,\"{full}\"");
        else if (Directory.Exists(full)) Process.Start("explorer.exe", $"\"{full}\"");
    }
}
