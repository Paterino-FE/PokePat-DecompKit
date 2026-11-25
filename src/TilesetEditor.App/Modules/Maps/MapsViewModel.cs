using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Input;
using TilesetEditor.App.Infrastructure;
using TilesetEditor.App.Shell;
using TilesetEditor.Core.Project;

namespace TilesetEditor.App.Modules.Maps;

public sealed record MapLink(string Text, string? TargetMap);

/// <summary>Read-only overview of maps and layouts. Maps are created and edited in Porymap.</summary>
public sealed class MapsViewModel : ObservableObject, IToolkitModule
{
    private readonly ProjectContext _ctx;
    private readonly Action<string> _newTilesetForPrimary;
    private MapCatalogResult? _catalog;
    private int _loadGeneration;

    public MapsViewModel(ProjectContext ctx, Action<string> newTilesetForPrimary)
    {
        _ctx = ctx;
        _newTilesetForPrimary = newTilesetForPrimary;
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

    private void OpenInExplorer(string rel, bool select)
    {
        if (_ctx.Root == null) return;
        var full = ProjectPaths.Combine(_ctx.Root, rel.Trim());
        if (select && File.Exists(full)) Process.Start("explorer.exe", $"/select,\"{full}\"");
        else if (Directory.Exists(full)) Process.Start("explorer.exe", $"\"{full}\"");
    }
}
