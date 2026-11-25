using System.Text.Json;

namespace TilesetEditor.Core.Project;

public sealed record MapConnection(string Direction, string Map, int Offset);
public sealed record MapWarp(int X, int Y, string DestinationMap, string DestinationWarpId);

/// <summary>One map from data/maps/&lt;Name&gt;/map.json (read-only view; maps are edited in Porymap).</summary>
public sealed class MapInfo
{
    public required string Name { get; init; }
    public required string Id { get; init; }
    public required string Group { get; init; }
    public required string Folder { get; init; }
    public string? LayoutId { get; init; }
    public LayoutInfo? Layout { get; set; }
    public string? Music { get; init; }
    public string? MapType { get; init; }
    public string? Weather { get; init; }
    public string? RegionMapSection { get; init; }
    public string? SharedScriptsMap { get; init; }
    public IReadOnlyList<MapConnection> Connections { get; init; } = [];
    public IReadOnlyList<MapWarp> Warps { get; init; } = [];
    public int ObjectEvents { get; init; }
    public int CoordEvents { get; init; }
    public int BgEvents { get; init; }
    /// <summary>Script labels referenced by the map's events (object, coord, bg).</summary>
    public IReadOnlyList<string> EventScripts { get; init; } = [];
    /// <summary>Existing script files of the map (scripts.pory, scripts.inc), project-relative.</summary>
    public IReadOnlyList<string> ScriptFiles { get; init; } = [];

    public string? PrimaryTileset => Layout?.PrimaryTileset;
    public string? SecondaryTileset => Layout?.SecondaryTileset;
    public int EventCount => ObjectEvents + CoordEvents + BgEvents + Warps.Count;
}

public sealed class MapCatalogResult
{
    public required IReadOnlyList<MapInfo> Maps { get; init; }
    public required IReadOnlyList<string> GroupOrder { get; init; }
    public List<string> Problems { get; } = [];

    public MapInfo? Find(string nameOrId) =>
        Maps.FirstOrDefault(m => m.Name == nameOrId || m.Id == nameOrId);

    /// <summary>Maps whose layout uses the tileset (as primary or secondary).</summary>
    public IEnumerable<MapInfo> UsingTileset(string tileset) =>
        Maps.Where(m => m.PrimaryTileset == tileset || m.SecondaryTileset == tileset);
}

/// <summary>Reads data/maps/map_groups.json and every listed map.json (Porymap's format).</summary>
public static class MapCatalog
{
    public const string MapGroupsFile = "data/maps/map_groups.json";

    public static MapCatalogResult Load(ProjectInfo project) => Load(new DiskFileSource(project.Root), project.Layouts);

    public static MapCatalogResult Load(IFileSource fs, IReadOnlyList<LayoutInfo> layouts)
    {
        var problems = new List<string>();
        var maps = new List<MapInfo>();
        var groupOrder = new List<string>();
        if (!fs.FileExists(MapGroupsFile))
        {
            var r = new MapCatalogResult { Maps = maps, GroupOrder = groupOrder };
            r.Problems.Add($"{MapGroupsFile} is missing.");
            return r;
        }

        var layoutById = layouts.GroupBy(l => l.Id).ToDictionary(g => g.Key, g => g.First());
        using var groups = JsonDocument.Parse(fs.ReadAllText(MapGroupsFile));
        if (groups.RootElement.TryGetProperty("group_order", out var order))
            groupOrder.AddRange(order.EnumerateArray().Select(e => e.GetString() ?? ""));

        foreach (var group in groupOrder)
        {
            if (!groups.RootElement.TryGetProperty(group, out var names)) { problems.Add($"Group {group} is missing in {MapGroupsFile}."); continue; }
            foreach (var n in names.EnumerateArray())
            {
                var name = n.GetString();
                if (string.IsNullOrEmpty(name)) continue;
                var folder = $"data/maps/{name}";
                var file = folder + "/map.json";
                if (!fs.FileExists(file)) { problems.Add($"{file} is missing."); continue; }
                try
                {
                    maps.Add(ParseMap(fs, name, group, folder, file, layoutById));
                }
                catch (Exception ex) when (ex is JsonException or InvalidOperationException or KeyNotFoundException or FormatException)
                {
                    problems.Add($"{file}: {ex.Message}");
                }
            }
        }

        var result = new MapCatalogResult { Maps = maps, GroupOrder = groupOrder };
        result.Problems.AddRange(problems);
        return result;
    }

    private static MapInfo ParseMap(IFileSource fs, string name, string group, string folder, string file, Dictionary<string, LayoutInfo> layouts)
    {
        using var doc = JsonDocument.Parse(fs.ReadAllText(file));
        var r = doc.RootElement;
        string? S(string key) => r.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        int I(JsonElement e, string key) => e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32()
            : e.TryGetProperty(key, out v) && v.ValueKind == JsonValueKind.String && int.TryParse(v.GetString(), out var p) ? p : 0;
        IEnumerable<JsonElement> A(string key) =>
            r.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Array ? v.EnumerateArray() : [];

        var scripts = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var key in new[] { "object_events", "coord_events", "bg_events" })
            foreach (var e in A(key))
                if (e.TryGetProperty("script", out var s) && s.ValueKind == JsonValueKind.String && s.GetString() is { Length: > 0 } label && label != "0" && label != "NULL")
                    scripts.Add(label);

        var layoutId = S("layout");
        return new MapInfo
        {
            Name = name,
            Id = S("id") ?? name,
            Group = group,
            Folder = folder,
            LayoutId = layoutId,
            Layout = layoutId != null && layouts.TryGetValue(layoutId, out var l) ? l : null,
            Music = S("music"),
            MapType = S("map_type"),
            Weather = S("weather"),
            RegionMapSection = S("region_map_section"),
            SharedScriptsMap = S("shared_scripts_map"),
            Connections = A("connections").Select(c => new MapConnection(
                c.TryGetProperty("direction", out var d) ? d.GetString() ?? "" : "",
                c.TryGetProperty("map", out var m) ? m.GetString() ?? "" : "",
                I(c, "offset"))).ToList(),
            Warps = A("warp_events").Select(w => new MapWarp(I(w, "x"), I(w, "y"),
                w.TryGetProperty("dest_map", out var dm) ? dm.GetString() ?? "" : "",
                w.TryGetProperty("dest_warp_id", out var dw) ? dw.ToString() : "")).ToList(),
            ObjectEvents = A("object_events").Count(),
            CoordEvents = A("coord_events").Count(),
            BgEvents = A("bg_events").Count(),
            EventScripts = scripts.ToList(),
            ScriptFiles = new[] { folder + "/scripts.pory", folder + "/scripts.inc" }.Where(fs.FileExists).ToList(),
        };
    }
}
