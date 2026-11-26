using System.Text.Json;
using TilesetEditor.Core.Imaging;

namespace TilesetEditor.Core.Platinum;

/// <summary>res/field/matrices/map_matrix_NNN.json: a grid of 32×32-tile chunks (land data), with header and height per cell.</summary>
public sealed class PlatinumMatrix
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required int Width { get; init; }
    public required int Height { get; init; }
    /// <summary>MAP_NNN per cell (row-major), "MAP_NONE" for empty cells.</summary>
    public required string[] Maps { get; init; }
    /// <summary>MAP_HEADER_… per cell, null if the matrix has no header table (one header for all).</summary>
    public string[]? Headers { get; init; }
    public int[]? Altitudes { get; init; }

    public string MapAt(int x, int y) => Maps[y * Width + x];
    public string? HeaderAt(int x, int y) => Headers?[y * Width + x];

    public static PlatinumMatrix Load(string path, string id)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        return Parse(doc.RootElement, id);
    }

    public static PlatinumMatrix Parse(JsonElement r, string id)
    {
        string[][] Grid(string name) => r.TryGetProperty(name, out var g) && g.ValueKind == JsonValueKind.Array
            ? g.EnumerateArray().Select(row => row.EnumerateArray().Select(c => c.ValueKind == JsonValueKind.Number ? c.GetRawText() : c.GetString() ?? "").ToArray()).ToArray()
            : [];
        var maps = Grid("maps");
        int h = maps.Length, w = h == 0 ? 0 : maps.Max(row => row.Length);
        string[] Flat(string[][] g, string fill) => Enumerable.Range(0, w * h).Select(i => i / w < g.Length && i % w < g[i / w].Length ? g[i / w][i % w] : fill).ToArray();
        var headers = Grid("headers");
        var alt = Grid("altitudes");
        return new PlatinumMatrix
        {
            Id = id,
            Name = r.TryGetProperty("name", out var n) ? n.GetString() ?? id : id,
            Width = w,
            Height = h,
            Maps = Flat(maps, "MAP_NONE"),
            Headers = headers.Length > 0 ? Flat(headers, "") : null,
            Altitudes = alt.Length > 0 ? Flat(alt, "0").Select(a => int.TryParse(a, out var v) ? v : 0).ToArray() : null,
        };
    }
}

/// <summary>A prop (building, sign, tree …) placed in a land data chunk; position in tiles relative to the chunk's corner.</summary>
public sealed record PlatinumProp(int ModelId, double X, double Y, double Z, string? ModelName)
{
    /// <summary>Position in model units (16 per tile) from the chunk centre, as the game places the model.</summary>
    public (float X, float Y, float Z) Units => ((float)((X - PlatinumLandData.Size / 2.0) * 16), (float)(Y * 16), (float)((Z - PlatinumLandData.Size / 2.0) * 16));
}

/// <summary>
/// One 32×32 chunk (res/field/maps/data/map_data_NNN.bin): a header of four sizes, the terrain attributes
/// (u16 per tile: bit 15 = collision, low byte = tile behavior), the props (0x30 bytes each), the NSBMD model and the BDHC heights.
/// </summary>
public sealed class PlatinumLandData
{
    public const int Size = 32;
    public required ushort[] Attributes { get; init; }
    public required IReadOnlyList<PlatinumProp> Props { get; init; }
    public int ModelSize { get; init; }
    /// <summary>The chunk's 3D model (NSBMD), empty if missing.</summary>
    public byte[] Model { get; init; } = [];

    public bool Collision(int x, int z) => (Attributes[z * Size + x] & 0x8000) != 0;
    public byte Behavior(int x, int z) => (byte)Attributes[z * Size + x];

    public static PlatinumLandData Parse(byte[] data, IReadOnlyList<string>? propNames = null)
    {
        if (data.Length < 16) throw new InvalidDataException("Land data is too short.");
        int permSize = BitConverter.ToInt32(data, 0), propSize = BitConverter.ToInt32(data, 4), modelSize = BitConverter.ToInt32(data, 8);
        if (permSize < Size * Size * 2 || 16 + permSize + propSize > data.Length) throw new InvalidDataException("Land data header is not valid.");
        var attrs = new ushort[Size * Size];
        for (int i = 0; i < attrs.Length; i++) attrs[i] = BitConverter.ToUInt16(data, 16 + i * 2);
        var props = new List<PlatinumProp>();
        int o = 16 + permSize;
        for (int i = 0; i + 0x30 <= propSize; i += 0x30)
        {
            int id = BitConverter.ToInt32(data, o + i);
            // fx32 positions (4096 = 1 unit, 16 units = 1 tile) measured from the chunk centre.
            double X(int off) => BitConverter.ToInt32(data, o + i + off) / 4096.0 / 16.0;
            props.Add(new PlatinumProp(id, X(4) + Size / 2.0, X(8), X(12) + Size / 2.0,
                propNames != null && id >= 0 && id < propNames.Count ? propNames[id] : null));
        }
        int modelAt = 16 + permSize + propSize;
        var model = modelSize > 0 && modelAt + modelSize <= data.Length ? data.AsSpan(modelAt, modelSize).ToArray() : [];
        return new PlatinumLandData { Attributes = attrs, Props = props, ModelSize = modelSize, Model = model };
    }
}

public enum PlatinumEventKind { Person, Warp, Sign, Trigger }

/// <summary>An event of res/field/events/events_*.json, at matrix-wide tile coordinates.</summary>
public sealed record PlatinumMapEvent(PlatinumEventKind Kind, int Index, int X, int Z, int Width, int Length, string Label, string Details,
    string? Graphics = null, int Direction = 1);

/// <summary>Map data of a pokeplatinum project and the top-down preview (collision, tile behaviors, props, events).</summary>
public sealed class PlatinumMaps
{
    private readonly string _root;
    private readonly Dictionary<string, PlatinumMatrix> _matrices = new(StringComparer.Ordinal);
    private readonly Dictionary<string, PlatinumLandData?> _land = new(StringComparer.Ordinal);
    private IReadOnlyList<string>? _landFiles, _propNames, _matrixOrder;

    public PlatinumMaps(string root) => _root = root;

    /// <summary>Newer pokeplatinum keeps the land data in res/field/mapmodels, older versions in res/field/maps.</summary>
    private string ModelDir => Directory.Exists(P("res/field/mapmodels/data")) ? "res/field/mapmodels" : "res/field/maps";

    private string P(string rel) => Path.Combine(_root, rel.Replace('/', Path.DirectorySeparatorChar));

    private static IReadOnlyList<string> Order(string path) =>
        File.Exists(path) ? File.ReadLines(path).Select(l => l.Trim()).Where(l => l.Length > 0).ToList() : [];

    public IReadOnlyList<string> PropNames => _propNames ??= Order(P("res/field/props/models/map_prop_models.order"))
        .Select(f => Path.GetFileNameWithoutExtension(f)).ToList();

    /// <summary>MAP_NNN → land data file (index into map_data.order).</summary>
    public string? LandFile(string map)
    {
        _landFiles ??= Order(P($"{ModelDir}/data/map_data.order"));
        if (!map.StartsWith("MAP_", StringComparison.Ordinal) || !int.TryParse(map[4..], out var n)) return null;
        if (n >= 0 && n < _landFiles.Count) return $"{ModelDir}/data/" + _landFiles[n];
        var direct = $"{ModelDir}/data/map_data_{n:000}.bin";
        return File.Exists(P(direct)) ? direct : null;
    }

    public PlatinumLandData? Land(string map)
    {
        if (_land.TryGetValue(map, out var cached)) return cached;
        PlatinumLandData? land = null;
        var file = LandFile(map);
        if (file != null && File.Exists(P(file)))
        {
            try { land = PlatinumLandData.Parse(File.ReadAllBytes(P(file)), PropNames); }
            catch (InvalidDataException) { land = null; }
        }
        return _land[map] = land;
    }

    /// <summary>map_matrix_NNN (the value in a map header) → the matrix; also accepts a plain number.</summary>
    public PlatinumMatrix? Matrix(string id)
    {
        if (_matrices.TryGetValue(id, out var m)) return m;
        // Newer pokeplatinum: the matrices are the groups of res/field/maps/groups.json, named by "id".
        var groups = P(PlatinumProject.MapGroupsFile);
        if (File.Exists(groups))
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(groups));
            foreach (var g in doc.RootElement.EnumerateArray())
                if (g.TryGetProperty("id", out var gid) && gid.GetString() == id)
                    return _matrices[id] = PlatinumMatrix.Parse(g, id);
            return null;
        }
        _matrixOrder ??= Order(P("res/field/matrices/map_matrices.order"));
        var name = id;
        if (int.TryParse(id, out var n) && n >= 0 && n < _matrixOrder.Count) name = _matrixOrder[n];
        var path = P($"res/field/matrices/{name}.json");
        if (!File.Exists(path)) return null;
        return _matrices[id] = PlatinumMatrix.Load(path, name);
    }

    /// <summary>The cells (in the header's matrix) that belong to the header, as a bounding box; whole matrix if it has no header table.</summary>
    public (int X, int Y, int W, int H)? Area(PlatinumMapHeader header, PlatinumMatrix matrix)
    {
        if (matrix.Headers == null || matrix.Width * matrix.Height <= 4) return (0, 0, matrix.Width, matrix.Height);
        int minX = int.MaxValue, minY = int.MaxValue, maxX = -1, maxY = -1;
        for (int y = 0; y < matrix.Height; y++)
            for (int x = 0; x < matrix.Width; x++)
                if (matrix.HeaderAt(x, y) == header.Id)
                {
                    minX = Math.Min(minX, x); minY = Math.Min(minY, y); maxX = Math.Max(maxX, x); maxY = Math.Max(maxY, y);
                }
        if (maxX < 0)
        {
            // Matrices used by one header only often keep MAP_HEADER_EVERYWHERE etc. in their table: show the used cells.
            for (int y = 0; y < matrix.Height; y++)
                for (int x = 0; x < matrix.Width; x++)
                    if (matrix.MapAt(x, y) != "MAP_NONE")
                    {
                        minX = Math.Min(minX, x); minY = Math.Min(minY, y); maxX = Math.Max(maxX, x); maxY = Math.Max(maxY, y);
                    }
        }
        return maxX < 0 ? null : (minX, minY, maxX - minX + 1, maxY - minY + 1);
    }

    /// <summary>Events of a header (events_xxx archive name from the header).</summary>
    /// <summary>DIR_NORTH/0, DIR_SOUTH/1, DIR_WEST/2, DIR_EAST/3 (south when unknown).</summary>
    public static int Direction(string dir) => dir switch
    {
        "DIR_NORTH" or "0" => 0,
        "DIR_WEST" or "2" => 2,
        "DIR_EAST" or "3" => 3,
        _ => 1,
    };

    public IReadOnlyList<PlatinumMapEvent> Events(PlatinumMapHeader header)
    {
        if (header.EventsFile == null) return [];
        var path = P(header.EventsFile);
        if (!File.Exists(path)) return [];
        var list = new List<PlatinumMapEvent>();
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var r = doc.RootElement;
        int Int(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i) ? i : 0;
        string Str(JsonElement e, string name) => e.TryGetProperty(name, out var v) ? (v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : v.GetRawText()) : "";
        IEnumerable<JsonElement> Arr(string name) => r.TryGetProperty(name, out var a) && a.ValueKind == JsonValueKind.Array ? a.EnumerateArray() : [];
        int i = 0;
        // "script" is a number (older pokeplatinum) or the script's label (newer).
        foreach (var o in Arr("object_events"))
            list.Add(new PlatinumMapEvent(PlatinumEventKind.Person, i++, Int(o, "x"), Int(o, "z"), 1, 1,
                PlatinumNames.Pretty(Str(o, "graphics_id"), "OBJ_EVENT_GFX_"),
                $"{Str(o, "id")} · {Str(o, "graphics_id")} · {Str(o, "movement_type")} · script {Str(o, "script")}" +
                (Str(o, "trainer_type") is { Length: > 0 } tt && tt != "TRAINER_TYPE_NONE" ? $" · {tt}" : ""),
                Str(o, "graphics_id"), Direction(Str(o, "initial_dir"))));
        i = 0;
        foreach (var o in Arr("warp_events"))
            list.Add(new PlatinumMapEvent(PlatinumEventKind.Warp, i++, Int(o, "x"), Int(o, "z"), 1, 1,
                "→ " + PlatinumNames.Pretty(Str(o, "dest_header_id"), "MAP_HEADER_"), $"Warp {i - 1} → {Str(o, "dest_header_id")} (warp {Str(o, "dest_warp_id")})"));
        i = 0;
        foreach (var o in Arr("bg_events"))
            list.Add(new PlatinumMapEvent(PlatinumEventKind.Sign, i++, Int(o, "x"), Int(o, "z"), 1, 1, "Sign / hidden", $"Background event {i - 1} · script {Str(o, "script")} · type {Str(o, "type")}"));
        i = 0;
        foreach (var o in Arr("coord_events"))
            list.Add(new PlatinumMapEvent(PlatinumEventKind.Trigger, i++, Int(o, "x"), Int(o, "z"), Math.Max(1, Int(o, "width")), Math.Max(1, Int(o, "length")),
                "Trigger", $"Trigger {i - 1} · script {Str(o, "script")} · {Str(o, "var")} = {Str(o, "value")}"));
        return list;
    }

    // ------------------------------------------------------------------ preview

    /// <summary>Colour of a tile in the preview: by behavior, darker when it blocks.</summary>
    public static Rgba32 TileColor(byte behavior, bool collision)
    {
        var c = BehaviorColor(behavior);
        if (collision) c = new Rgba32((byte)(c.R * 0.45), (byte)(c.G * 0.45), (byte)(c.B * 0.45), 255);
        return c;
    }

    public static Rgba32 BehaviorColor(byte b) => BehaviorGroup(b) switch
    {
        "Tall grass" => new Rgba32(88, 176, 72, 255),
        "Water" => new Rgba32(72, 132, 220, 255),
        "Shallow water / puddle" => new Rgba32(130, 180, 230, 255),
        "Sand" => new Rgba32(226, 204, 140, 255),
        "Snow / ice" => new Rgba32(225, 238, 246, 255),
        "Cave / mountain floor" => new Rgba32(170, 140, 110, 255),
        "Ledge / jump" => new Rgba32(205, 120, 60, 255),
        "Warp / door / stairs" => new Rgba32(220, 70, 170, 255),
        "Bridge" => new Rgba32(170, 120, 80, 255),
        "Slide / spin" => new Rgba32(120, 200, 200, 255),
        "Object (PC, TV, shelf …)" => new Rgba32(240, 200, 80, 255),
        _ => new Rgba32(208, 214, 196, 255),
    };

    /// <summary>Groups of tile behaviors (include/constants/field/map_tile_behaviors.h) for the legend.</summary>
    public static string BehaviorGroup(byte b) => b switch
    {
        0x02 or 0x03 => "Tall grass",
        0x10 or 0x13 or 0x15 or 0x19 or 0x1A => "Water",
        0x16 or 0x17 or 0x1D => "Shallow water / puddle",
        0x21 => "Sand",
        0x20 or 0xA1 or 0xA2 or 0xA3 or 0xA4 or 0xA5 or 0xA6 => "Snow / ice",
        0x08 or 0x0B or 0x0C => "Cave / mountain floor",
        >= 0x38 and <= 0x3B or >= 0x5A and <= 0x5D => "Ledge / jump",
        0x5E or 0x5F or >= 0x62 and <= 0x65 or 0x67 or 0x69 or 0x6A or 0x6B or >= 0x6C and <= 0x6F => "Warp / door / stairs",
        >= 0x70 and <= 0x7D => "Bridge",
        >= 0x40 and <= 0x43 => "Slide / spin",
        >= 0x80 and <= 0x9F => "Object (PC, TV, shelf …)",
        _ => "Floor",
    };

    public static readonly string[] LegendGroups =
    [
        "Floor", "Tall grass", "Water", "Shallow water / puddle", "Sand", "Snow / ice", "Cave / mountain floor",
        "Ledge / jump", "Warp / door / stairs", "Bridge", "Slide / spin", "Object (PC, TV, shelf …)",
    ];

    public static Rgba32 GroupColor(string group) => group switch
    {
        "Tall grass" => BehaviorColor(0x02),
        "Water" => BehaviorColor(0x10),
        "Shallow water / puddle" => BehaviorColor(0x16),
        "Sand" => BehaviorColor(0x21),
        "Snow / ice" => BehaviorColor(0x20),
        "Cave / mountain floor" => BehaviorColor(0x08),
        "Ledge / jump" => BehaviorColor(0x38),
        "Warp / door / stairs" => BehaviorColor(0x69),
        "Bridge" => BehaviorColor(0x70),
        "Slide / spin" => BehaviorColor(0x40),
        "Object (PC, TV, shelf …)" => BehaviorColor(0x80),
        _ => BehaviorColor(0),
    };

    /// <summary>
    /// Renders the cells of an area (matrix cells) top-down: <paramref name="tile"/> pixels per tile, blocked tiles darker,
    /// a grid line every chunk, cells without land data hatched. Returns the image and the tile origin of the area.
    /// </summary>
    public RgbaImage Render(PlatinumMatrix matrix, (int X, int Y, int W, int H) area, int tile, bool grid = true)
    {
        int size = PlatinumLandData.Size;
        var img = new RgbaImage(area.W * size * tile, area.H * size * tile);
        img.Fill(new Rgba32(40, 36, 36, 255));
        for (int cy = 0; cy < area.H; cy++)
            for (int cx = 0; cx < area.W; cx++)
            {
                int mx = area.X + cx, my = area.Y + cy;
                if (mx < 0 || my < 0 || mx >= matrix.Width || my >= matrix.Height) continue;
                var map = matrix.MapAt(mx, my);
                var land = map == "MAP_NONE" ? null : Land(map);
                int ox = cx * size * tile, oy = cy * size * tile;
                for (int z = 0; z < size; z++)
                    for (int x = 0; x < size; x++)
                    {
                        Rgba32 c;
                        if (land == null) c = ((x + z) & 3) == 0 ? new Rgba32(64, 58, 58, 255) : new Rgba32(40, 36, 36, 255);
                        else c = TileColor(land.Behavior(x, z), land.Collision(x, z));
                        for (int py = 0; py < tile; py++)
                            for (int px = 0; px < tile; px++)
                            {
                                var pc = c;
                                if (tile >= 4 && (px == 0 || py == 0)) pc = Shade(c, 0.9);
                                img[ox + x * tile + px, oy + z * tile + py] = pc;
                            }
                    }
                if (grid)
                {
                    var line = new Rgba32(255, 255, 255, 255);
                    for (int i = 0; i < size * tile; i++)
                    {
                        img[ox + i, oy] = Blend(img[ox + i, oy], line, 0.5);
                        img[ox, oy + i] = Blend(img[ox, oy + i], line, 0.5);
                    }
                }
            }
        return img;
    }

    // ------------------------------------------------------------------ textured preview (3D models seen from above)

    private readonly Dictionary<string, NitroTextureSet?> _textureSets = new(StringComparer.Ordinal);
    private readonly Dictionary<string, NitroModel?> _models = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string?> _areaTextureSet = new(StringComparer.Ordinal);

    private NitroTextureSet? TextureSet(string rel)
    {
        if (_textureSets.TryGetValue(rel, out var t)) return t;
        var path = P(rel);
        try { t = File.Exists(path) ? NitroTextureSet.Load(File.ReadAllBytes(path)) : null; }
        catch (Exception ex) when (ex is IOException or IndexOutOfRangeException or ArgumentOutOfRangeException) { t = null; }
        return _textureSets[rel] = t;
    }

    /// <summary>area_data_006 → res/field/maps/texture_sets/map_texture_set_006.nsbtx (from the area data JSON).</summary>
    public string? MapTextureSetFile(string? areaData)
    {
        if (areaData == null) return null;
        if (_areaTextureSet.TryGetValue(areaData, out var cached)) return cached;
        string? result = null;
        var path = P($"res/field/area_data/{areaData}.json");
        if (File.Exists(path))
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            if (doc.RootElement.TryGetProperty("mapTextureSet", out var ts) && ts.GetString() is { } name)
                result = $"{ModelDir}/texture_sets/{name}.nsbtx";
        }
        return _areaTextureSet[areaData] = result;
    }

    private NitroModel? PropModel(PlatinumProp prop)
    {
        var key = prop.ModelName ?? prop.ModelId.ToString();
        if (_models.TryGetValue(key, out var m)) return m;
        m = null;
        if (prop.ModelName != null)
        {
            var path = P($"res/field/props/models/{prop.ModelName}.nsbmd");
            try { if (File.Exists(path)) m = NitroModel.Load(File.ReadAllBytes(path)); }
            catch (Exception ex) when (ex is IOException or IndexOutOfRangeException or ArgumentOutOfRangeException) { m = null; }
        }
        return _models[key] = m;
    }

    /// <summary>
    /// Renders the chunks of an area from their 3D models, seen straight from above, with the textures of the area's texture
    /// set and the props (buildings, trees, signs) with their own textures. <paramref name="areaDataOf"/> gives the area data
    /// of a matrix cell (its map header's), so neighbouring areas use their own textures.
    /// </summary>
    public RgbaImage RenderTextured(PlatinumMatrix matrix, (int X, int Y, int W, int H) area, int tile, Func<int, int, string?> areaDataOf, bool props = true)
    {
        int size = PlatinumLandData.Size;
        var img = new RgbaImage(area.W * size * tile, area.H * size * tile);
        img.Fill(new Rgba32(24, 22, 22, 255));
        float unitsPerChunk = size * 16f;
        var r = new TopDownRasterizer(img, tile / 16f, 0, 0);
        var propSets = new[] { TextureSet("res/field/props/texture_sets/prop_texture_set_000.nsbtx"), TextureSet("res/field/props/texture_sets/prop_texture_set_001.nsbtx") };
        foreach (var translucent in new[] { false, true })
            for (int cy = 0; cy < area.H; cy++)
                for (int cx = 0; cx < area.W; cx++)
                {
                    int mx = area.X + cx, my = area.Y + cy;
                    if (mx < 0 || my < 0 || mx >= matrix.Width || my >= matrix.Height) continue;
                    var map = matrix.MapAt(mx, my);
                    var land = map == "MAP_NONE" ? null : Land(map);
                    if (land == null) continue;
                    var setFile = MapTextureSetFile(areaDataOf(mx, my));
                    var mapTextures = setFile != null ? TextureSet(setFile) : null;
                    float centerX = cx * unitsPerChunk + unitsPerChunk / 2, centerZ = cy * unitsPerChunk + unitsPerChunk / 2;
                    NitroModel? model = null;
                    var key = "land:" + map;
                    if (!_models.TryGetValue(key, out model))
                    {
                        try { model = land.Model.Length > 0 ? NitroModel.Load(land.Model) : null; }
                        catch (Exception ex) when (ex is IndexOutOfRangeException or ArgumentOutOfRangeException) { model = null; }
                        _models[key] = model;
                    }
                    if (model != null)
                        r.Draw(model, m => m.Texture != null ? (model.Textures?.Has(m.Texture) == true ? model.Textures : mapTextures)?.Decode(m.Texture, m.Palette) : null,
                            centerX, 0, centerZ, translucentPass: translucent);
                    if (!props) continue;
                    foreach (var prop in land.Props)
                    {
                        var pm = PropModel(prop);
                        if (pm == null) continue;
                        var (ux, uy, uz) = prop.Units;
                        r.Draw(pm, m =>
                        {
                            if (m.Texture == null) return null;
                            if (pm.Textures?.Has(m.Texture) == true) return pm.Textures.Decode(m.Texture, m.Palette);
                            foreach (var set in propSets) if (set?.Has(m.Texture) == true) return set.Decode(m.Texture, m.Palette);
                            return mapTextures?.Has(m.Texture) == true ? mapTextures.Decode(m.Texture, m.Palette) : null;
                        }, centerX + ux, uy, centerZ + uz, translucentPass: translucent);
                    }
                }
        return img;
    }

    /// <summary>Tints blocked tiles red and marks special tiles (grass, water, warps) with a thin frame, over a textured image.</summary>
    public void OverlayCollision(RgbaImage img, PlatinumMatrix matrix, (int X, int Y, int W, int H) area, int tile)
    {
        int size = PlatinumLandData.Size;
        var red = new Rgba32(230, 40, 40, 255);
        for (int cy = 0; cy < area.H; cy++)
            for (int cx = 0; cx < area.W; cx++)
            {
                int mx = area.X + cx, my = area.Y + cy;
                if (mx < 0 || my < 0 || mx >= matrix.Width || my >= matrix.Height) continue;
                var map = matrix.MapAt(mx, my);
                var land = map == "MAP_NONE" ? null : Land(map);
                if (land == null) continue;
                for (int z = 0; z < size; z++)
                    for (int x = 0; x < size; x++)
                    {
                        bool blocked = land.Collision(x, z);
                        var group = BehaviorGroup(land.Behavior(x, z));
                        int ox = (cx * size + x) * tile, oy = (cy * size + z) * tile;
                        for (int py = 0; py < tile; py++)
                            for (int px = 0; px < tile; px++)
                            {
                                int ix = ox + px, iy = oy + py;
                                if (blocked) img[ix, iy] = Blend(img[ix, iy], red, 0.38);
                                else if (group != "Floor" && tile >= 4 && (px == 0 || py == 0 || px == tile - 1 || py == tile - 1))
                                    img[ix, iy] = BehaviorColor(land.Behavior(x, z));
                            }
                    }
            }
    }

    private static Rgba32 Shade(Rgba32 c, double f) => new((byte)(c.R * f), (byte)(c.G * f), (byte)(c.B * f), c.A);

    private static Rgba32 Blend(Rgba32 a, Rgba32 b, double t) =>
        new((byte)(a.R + (b.R - a.R) * t), (byte)(a.G + (b.G - a.G) * t), (byte)(a.B + (b.B - a.B) * t), 255);
}
