using System.Buffers.Binary;
using System.Text.Json;
using System.Text.RegularExpressions;
using TilesetEditor.Core.Project;

namespace TilesetEditor.Core.Maps;

public enum Facing
{
    Down,
    Up,
    Left,
    Right,
}

public static class FacingExtensions
{
    public static (int Dx, int Dy) Delta(this Facing f) => f switch
    {
        Facing.Down => (0, 1),
        Facing.Up => (0, -1),
        Facing.Left => (-1, 0),
        _ => (1, 0),
    };

    public static Facing Opposite(this Facing f) => f switch
    {
        Facing.Down => Facing.Up,
        Facing.Up => Facing.Down,
        Facing.Left => Facing.Right,
        _ => Facing.Left,
    };

    /// <summary>"down"/"south"/"DIR_SOUTH" … → facing.</summary>
    public static Facing? Parse(string? text)
    {
        var t = (text ?? "").Trim().ToLowerInvariant();
        if (t.StartsWith("dir_", StringComparison.Ordinal)) t = t[4..];
        return t switch
        {
            "down" or "south" => Facing.Down,
            "up" or "north" => Facing.Up,
            "left" or "west" => Facing.Left,
            "right" or "east" => Facing.Right,
            _ => null,
        };
    }

    /// <summary>Direction from (fx,fy) towards (tx,ty); the larger axis wins (like the game's GetDirectionToFace).</summary>
    public static Facing Towards(int fx, int fy, int tx, int ty, Facing fallback)
    {
        int dx = tx - fx, dy = ty - fy;
        if (dx == 0 && dy == 0) return fallback;
        if (Math.Abs(dx) > Math.Abs(dy)) return dx < 0 ? Facing.Left : Facing.Right;
        return dy < 0 ? Facing.Up : Facing.Down;
    }
}

/// <summary>
/// The blockdata of a layout (map.bin): one u16 per block – metatile id (0x3FF), collision (0xC00), elevation (0xF000).
/// </summary>
public sealed class MapGrid
{
    public const int MetatileIdMask = 0x3FF;
    public const int CollisionMask = 0xC00;
    public const int ElevationMask = 0xF000;

    public int Width { get; }
    public int Height { get; }
    public ushort[] Blocks { get; }

    public MapGrid(int width, int height, ushort[] blocks)
    {
        if (blocks.Length < width * height) throw new ArgumentException("Too few blocks for the layout size.", nameof(blocks));
        Width = width;
        Height = height;
        Blocks = blocks;
    }

    public static MapGrid Load(string path, int width, int height)
    {
        var bytes = File.ReadAllBytes(path);
        var blocks = new ushort[width * height];
        for (int i = 0; i < blocks.Length && i * 2 + 1 < bytes.Length; i++)
            blocks[i] = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(i * 2));
        return new MapGrid(width, height, blocks);
    }

    public bool InBounds(int x, int y) => x >= 0 && y >= 0 && x < Width && y < Height;
    public int MetatileId(int x, int y) => Blocks[y * Width + x] & MetatileIdMask;
    public int Collision(int x, int y) => (Blocks[y * Width + x] & CollisionMask) >> 10;
    public int Elevation(int x, int y) => (Blocks[y * Width + x] & ElevationMask) >> 12;
    public bool IsBlocked(int x, int y) => !InBounds(x, y) || Collision(x, y) != 0;
}

/// <summary>A person / object of a map (object_events entry).</summary>
public sealed record MapObject(int Index, string? LocalId, string GraphicsId, int X, int Y, int Elevation, string MovementType, string Script)
{
    /// <summary>1-based number used by scripts when no local_id constant is set.</summary>
    public int Number => Index + 1;

    public string Name => (LocalId is { Length: > 0 } id && !int.TryParse(id, out _) ? id.Replace("LOCALID_", "") : $"#{Number}") +
                          " " + GraphicsId.Replace("OBJ_EVENT_GFX_", "");

    /// <summary>Facing at map load, guessed from the movement type (MOVEMENT_TYPE_FACE_LEFT → left; default down).</summary>
    public Facing InitialFacing
    {
        get
        {
            var t = MovementType.Replace("MOVEMENT_TYPE_", "");
            int best = int.MaxValue;
            Facing f = Facing.Down;
            foreach (var (word, dir) in new[] { ("DOWN", Facing.Down), ("UP", Facing.Up), ("LEFT", Facing.Left), ("RIGHT", Facing.Right),
                         ("SOUTH", Facing.Down), ("NORTH", Facing.Up), ("WEST", Facing.Left), ("EAST", Facing.Right) })
            {
                var m = Regex.Match(t, $@"(^|_){word}(_|$)");
                if (m.Success && m.Index < best) { best = m.Index; f = dir; }
            }
            return f;
        }
    }
}

public sealed record MapTrigger(int Index, int X, int Y, string Script);

/// <summary>Everything the movement preview needs about one map: layout, blockdata, people, triggers and LOCALID constants.</summary>
public sealed partial class MapScene
{
    public required string MapFolder { get; init; }
    public LayoutInfo? Layout { get; init; }
    public MapGrid? Grid { get; init; }
    public required IReadOnlyList<MapObject> Objects { get; init; }
    public required IReadOnlyList<MapTrigger> Triggers { get; init; }
    /// <summary>LOCALID_… → number, from ".set LOCALID_X, n" (scripts.inc), "const LOCALID_X = n" (scripts.pory) or map_event_ids.h.</summary>
    public required IReadOnlyDictionary<string, int> LocalIdConstants { get; init; }
    public List<string> Problems { get; } = [];

    public int Width => Grid?.Width ?? Layout?.Width ?? 0;
    public int Height => Grid?.Height ?? Layout?.Height ?? 0;

    [GeneratedRegex(@"^\s*\.set\s+(LOCALID_\w+)\s*,\s*(\d+)", RegexOptions.Multiline)]
    private static partial Regex SetLocalId();

    [GeneratedRegex(@"^\s*const\s+(LOCALID_\w+)\s*=\s*(\d+)", RegexOptions.Multiline)]
    private static partial Regex ConstLocalId();

    [GeneratedRegex(@"^\s*#define\s+(LOCALID_\w+)\s+(\d+)", RegexOptions.Multiline)]
    private static partial Regex DefineLocalId();

    /// <summary>Resolves a script target to a person of this map. OBJ_EVENT_ID_PLAYER is not a person (returns null).</summary>
    /// <param name="talker">Person whose script runs (for VAR_LAST_TALKED), if known.</param>
    public MapObject? FindObject(string target, MapObject? talker = null)
    {
        var t = target.Trim();
        if (t.Length == 0 || IsPlayer(t)) return null;
        if (t is "VAR_LAST_TALKED" or "LAST_TALKED") return talker;
        // local_id field in map.json (expansion 1.10+ style), exact match first.
        var byField = Objects.FirstOrDefault(o => string.Equals(o.LocalId, t, StringComparison.Ordinal));
        if (byField != null) return byField;
        int n;
        if (t.StartsWith("LOCALID_", StringComparison.Ordinal))
        {
            if (!LocalIdConstants.TryGetValue(t, out n)) return null;
        }
        else if (!TryNumber(t, out n)) return null;
        return Objects.FirstOrDefault(o => o.LocalId == n.ToString()) ?? (n >= 1 && n <= Objects.Count ? Objects[n - 1] : null);
    }

    public static bool IsPlayer(string target) => target.Trim() is "OBJ_EVENT_ID_PLAYER" or "PLAYER" or "255" or "0xFF" or "0xff";

    private static bool TryNumber(string s, out int n)
    {
        if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            return int.TryParse(s[2..], System.Globalization.NumberStyles.HexNumber, null, out n);
        return int.TryParse(s, out n);
    }

    /// <summary>Reads data/maps/&lt;folder&gt;/map.json, its layout and blockdata.</summary>
    public static MapScene Load(ProjectInfo project, string mapFolder) => Load(project.Root, project.Layouts, mapFolder);

    public static MapScene Load(string root, IReadOnlyList<LayoutInfo> layouts, string mapFolder)
    {
        var problems = new List<string>();
        var dir = Path.Combine(root, "data", "maps", mapFolder);
        var objects = new List<MapObject>();
        var triggers = new List<MapTrigger>();
        LayoutInfo? layout = null;
        var mapJson = Path.Combine(dir, "map.json");
        if (!File.Exists(mapJson)) problems.Add($"data/maps/{mapFolder}/map.json is missing.");
        else
        {
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(mapJson));
                var r = doc.RootElement;
                string S(JsonElement e, string k) => e.TryGetProperty(k, out var v) ? v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : v.ToString() : "";
                int I(JsonElement e, string k) => e.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32()
                    : int.TryParse(S(e, k), out var p) ? p : 0;
                var layoutId = S(r, "layout");
                layout = layouts.FirstOrDefault(l => l.Id == layoutId);
                if (layout == null) problems.Add($"Layout {layoutId} was not found in data/layouts/layouts.json.");
                if (r.TryGetProperty("object_events", out var objs) && objs.ValueKind == JsonValueKind.Array)
                {
                    int i = 0;
                    foreach (var e in objs.EnumerateArray())
                    {
                        var localId = S(e, "local_id");
                        objects.Add(new MapObject(i++, localId.Length > 0 ? localId : null, S(e, "graphics_id"), I(e, "x"), I(e, "y"),
                            I(e, "elevation"), S(e, "movement_type"), S(e, "script")));
                    }
                }
                if (r.TryGetProperty("coord_events", out var coords) && coords.ValueKind == JsonValueKind.Array)
                {
                    int i = 0;
                    foreach (var e in coords.EnumerateArray())
                        triggers.Add(new MapTrigger(i++, I(e, "x"), I(e, "y"), S(e, "script")));
                }
            }
            catch (JsonException ex)
            {
                problems.Add($"map.json could not be read: {ex.Message}");
            }
        }

        MapGrid? grid = null;
        if (layout is { BlockdataPath: { } bd, Width: > 0, Height: > 0 })
        {
            var full = ProjectPaths.Combine(root, bd);
            if (File.Exists(full)) grid = MapGrid.Load(full, layout.Width, layout.Height);
            else problems.Add($"{bd} is missing.");
        }

        var ids = new Dictionary<string, int>(StringComparer.Ordinal);
        void Collect(string file, Regex regex)
        {
            if (!File.Exists(file)) return;
            foreach (Match m in regex.Matches(File.ReadAllText(file)))
                ids.TryAdd(m.Groups[1].Value, int.Parse(m.Groups[2].Value));
        }
        Collect(Path.Combine(dir, "scripts.inc"), SetLocalId());
        Collect(Path.Combine(dir, "scripts.pory"), ConstLocalId());
        Collect(Path.Combine(root, "include", "constants", "map_event_ids.h"), DefineLocalId());

        var scene = new MapScene
        {
            MapFolder = mapFolder, Layout = layout, Grid = grid, Objects = objects, Triggers = triggers, LocalIdConstants = ids,
        };
        scene.Problems.AddRange(problems);
        return scene;
    }
}
