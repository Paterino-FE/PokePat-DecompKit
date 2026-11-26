using System.Text.Json;
using System.Text.RegularExpressions;
using TilesetEditor.Core.Maps;
using TilesetEditor.Core.Project;

namespace TilesetEditor.Core.SecretBases;

/// <summary>One secret base id constant (SECRET_BASE_RED_CAVE1_2 = 2). The interior is id / 10, the ones digit tells bases apart.</summary>
public sealed record SecretBaseIdConstant(string Name, int Value)
{
    public int GroupIndex => Value / 10;
    public int Slot => Value % 10;
}

/// <summary>A closed/open metatile pair of sSecretBaseEntranceMetatiles (src/secret_base.c).</summary>
public sealed record EntranceMetatile(string ClosedName, int? Closed, string OpenName, int? Open, string? Type);

/// <summary>An interior map shared by up to nine secret base locations (SECRET_BASE_RED_CAVE1 = SECRET_BASE_GROUP(0)).</summary>
public sealed class SecretBaseGroup
{
    public required string Constant { get; init; }
    public required int Index { get; init; }
    /// <summary>Type part of the name, e.g. RED_CAVE, TREE.</summary>
    public required string Type { get; init; }
    public required int Number { get; init; }
    /// <summary>MAP_SECRET_BASE_RED_CAVE1 from sSecretBaseEntrancePositions (null if the group has no entry there).</summary>
    public string? MapConstant { get; set; }
    public string? MapFolder { get; set; }
    public int? WarpId { get; set; }
    /// <summary>Where the player is placed on the first visit (in front of the PC).</summary>
    public int? PcX { get; set; }
    public int? PcY { get; set; }
    public List<SecretBaseIdConstant> Ids { get; } = [];
    public List<SecretBaseLocation> Locations { get; } = [];

    public string ShortName => Constant.Replace("SECRET_BASE_", "");
    public int FreeSlots => Enumerable.Range(1, 9).Count(k => Ids.All(i => i.Value != Index * 10 + k));
    public string Display => $"{ShortName}  ·  {Locations.Count} location(s), {FreeSlots} free id(s)";
    public override string ToString() => Display;
}

/// <summary>A "secret_base" bg_event on an outdoor map.</summary>
public sealed class SecretBaseLocation
{
    public required string MapFolder { get; init; }
    public required int EventIndex { get; init; }
    public required int X { get; init; }
    public required int Y { get; init; }
    public required int Elevation { get; init; }
    public required string IdConstant { get; init; }
    public int? Id { get; set; }
    public SecretBaseGroup? Group { get; set; }
    public int? OutsideMetatile { get; set; }
    public bool IsEntranceMetatile { get; set; }
    /// <summary>Type implied by the entrance metatile (what GetSecretBaseTypeInFrontOfPlayer reports in-game).</summary>
    public string? MetatileType { get; set; }
    public List<string> Warnings { get; } = [];

    public string Type => Group?.Type ?? "?";
    public string Interior => Group?.MapFolder ?? Group?.ShortName ?? "?";
    public string IdText => Id?.ToString() ?? "?";
    public string MetatileText => OutsideMetatile is { } m ? $"0x{m:X3}" : "?";
    public string Check => Warnings.Count == 0 ? "OK" : "⚠ " + string.Join(" ", Warnings);
}

/// <summary>Everything about secret bases that the toolkit reads.</summary>
public sealed class SecretBaseData
{
    public List<SecretBaseGroup> Groups { get; } = [];
    public List<SecretBaseIdConstant> Ids { get; } = [];
    public List<SecretBaseLocation> Locations { get; } = [];
    public List<EntranceMetatile> EntranceMetatiles { get; } = [];
    /// <summary>SECRET_BASE_RED_CAVE = 1 … (type number → name).</summary>
    public Dictionary<string, int> Types { get; } = [];
    public int? NumGroups { get; set; }
    public List<string> Problems { get; } = [];
    /// <summary>All map folders of data/maps (for choosing where a new entrance goes).</summary>
    public List<string> MapFolders { get; } = [];

    public SecretBaseGroup? GroupOf(int id) => Groups.FirstOrDefault(g => g.Index == id / 10);
}

/// <summary>
/// Reads include/constants/secret_bases.h, sSecretBaseEntrancePositions / sSecretBaseEntranceMetatiles (src/secret_base.c)
/// and every "secret_base" bg_event of data/maps/*/map.json; plans new locations (id constant + bg_event).
/// </summary>
public static partial class SecretBaseCatalog
{
    public const string ConstantsFile = "include/constants/secret_bases.h";
    public const string SourceFile = "src/secret_base.c";
    public const string MetatileLabelsFile = "include/constants/metatile_labels.h";

    [GeneratedRegex(@"^#define[ \t]+SECRET_BASE_([A-Z_]*[A-Z])[ \t]+(\d+)[ \t]*(?://[^\r\n]*)?\r?$", RegexOptions.Multiline)]
    private static partial Regex TypeDefine();

    [GeneratedRegex(@"^#define[ \t]+(SECRET_BASE_\w+?\d+_\d+)[ \t]+(\d+)[ \t]*\r?$", RegexOptions.Multiline)]
    private static partial Regex IdDefine();

    [GeneratedRegex(@"^#define[ \t]+(SECRET_BASE_([A-Z_]+?)(\d+))[ \t]+SECRET_BASE_GROUP\((\d+)\)", RegexOptions.Multiline)]
    private static partial Regex GroupDefine();

    [GeneratedRegex(@"^#define[ \t]+NUM_SECRET_BASE_GROUPS[ \t]+(\d+)", RegexOptions.Multiline)]
    private static partial Regex NumGroupsDefine();

    [GeneratedRegex(@"\[(SECRET_BASE_\w+)\]\s*=\s*MAP_NUM\((\w+)\)\s*,\s*(\d+)\s*,\s*(\d+)\s*,\s*(\d+)")]
    private static partial Regex PositionEntry();

    [GeneratedRegex(@"\{\s*\.closedMetatileId\s*=\s*(\w+)\s*,\s*\.openMetatileId\s*=\s*(\w+)\s*\}")]
    private static partial Regex EntranceEntry();

    [GeneratedRegex(@"^#define[ \t]+(METATILE_\w+)[ \t]+(0x[0-9A-Fa-f]+|\d+)", RegexOptions.Multiline)]
    private static partial Regex MetatileDefine();

    [GeneratedRegex(@"""id""\s*:\s*""(MAP_\w+)""")]
    private static partial Regex MapIdField();

    public static Dictionary<string, int> ParseMetatileLabels(string text)
    {
        var d = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (Match m in MetatileDefine().Matches(text))
        {
            var v = m.Groups[2].Value;
            d.TryAdd(m.Groups[1].Value, v.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? Convert.ToInt32(v[2..], 16) : int.Parse(v));
        }
        return d;
    }

    /// <summary>Parses the constants header and secret_base.c (no map files).</summary>
    public static SecretBaseData ParseSources(string constantsText, string sourceText, string metatileLabelsText)
    {
        var data = new SecretBaseData();
        foreach (Match m in TypeDefine().Matches(constantsText))
            data.Types.TryAdd(m.Groups[1].Value, int.Parse(m.Groups[2].Value));
        foreach (Match m in IdDefine().Matches(constantsText))
            data.Ids.Add(new SecretBaseIdConstant(m.Groups[1].Value, int.Parse(m.Groups[2].Value)));
        foreach (Match m in GroupDefine().Matches(constantsText))
            data.Groups.Add(new SecretBaseGroup
            {
                Constant = m.Groups[1].Value, Type = m.Groups[2].Value, Number = int.Parse(m.Groups[3].Value), Index = int.Parse(m.Groups[4].Value),
            });
        if (NumGroupsDefine().Match(constantsText) is { Success: true } n) data.NumGroups = int.Parse(n.Groups[1].Value);
        foreach (var id in data.Ids) data.GroupOf(id.Value)?.Ids.Add(id);
        foreach (var g in data.Groups) g.Ids.Sort((a, b) => a.Value.CompareTo(b.Value));

        foreach (Match m in PositionEntry().Matches(sourceText))
        {
            var g = data.Groups.FirstOrDefault(x => x.Constant == m.Groups[1].Value);
            if (g == null) { data.Problems.Add($"sSecretBaseEntrancePositions names {m.Groups[1].Value}, which is not a secret base group."); continue; }
            // MAP_NUM(SECRET_BASE_X) up to expansion 1.12, MAP_NUM(MAP_SECRET_BASE_X) later.
            g.MapConstant = m.Groups[2].Value.StartsWith("MAP_", StringComparison.Ordinal) ? m.Groups[2].Value : "MAP_" + m.Groups[2].Value;
            g.WarpId = int.Parse(m.Groups[3].Value);
            g.PcX = int.Parse(m.Groups[4].Value);
            g.PcY = int.Parse(m.Groups[5].Value);
        }
        foreach (var g in data.Groups.Where(g => g.MapConstant == null))
            data.Problems.Add($"{g.Constant} has no entry in sSecretBaseEntrancePositions ({SourceFile}).");
        if (data.NumGroups is { } count && count != data.Groups.Count)
            data.Problems.Add($"NUM_SECRET_BASE_GROUPS is {count}, but {data.Groups.Count} groups are defined.");

        var labels = ParseMetatileLabels(metatileLabelsText);
        foreach (Match m in EntranceEntry().Matches(sourceText))
        {
            string closed = m.Groups[1].Value, open = m.Groups[2].Value;
            int? Value(string name) => labels.TryGetValue(name, out var v) ? v : int.TryParse(name, out var n2) ? n2 : null;
            var upper = closed.ToUpperInvariant().Replace("_", "");
            var type = data.Types.Keys.OrderByDescending(t => t.Length).FirstOrDefault(t => upper.Contains(t.Replace("_", ""), StringComparison.Ordinal));
            data.EntranceMetatiles.Add(new EntranceMetatile(closed, Value(closed), open, Value(open), type));
        }
        return data;
    }

    /// <summary>Reads sources, the interior map folders and every secret_base bg_event; checks the outside metatiles.</summary>
    public static SecretBaseData Load(string root, IReadOnlyList<LayoutInfo> layouts)
    {
        string Read(string rel)
        {
            var p = ProjectPaths.Combine(root, rel);
            return File.Exists(p) ? File.ReadAllText(p) : "";
        }
        var data = ParseSources(Read(ConstantsFile), Read(SourceFile), Read(MetatileLabelsFile));
        if (data.Groups.Count == 0) data.Problems.Add($"No secret base groups found in {ConstantsFile}.");

        var mapsDir = Path.Combine(root, "data", "maps");
        var folderById = new Dictionary<string, string>(StringComparer.Ordinal);
        var withBases = new List<(string Folder, string Json)>();
        if (Directory.Exists(mapsDir))
        {
            foreach (var dir in Directory.EnumerateDirectories(mapsDir).OrderBy(d => d, StringComparer.Ordinal))
            {
                var file = Path.Combine(dir, "map.json");
                if (!File.Exists(file)) continue;
                var text = File.ReadAllText(file);
                var folder = Path.GetFileName(dir);
                data.MapFolders.Add(folder);
                if (MapIdField().Match(text) is { Success: true } id) folderById.TryAdd(id.Groups[1].Value, folder);
                if (text.Contains("\"secret_base\"", StringComparison.Ordinal)) withBases.Add((folder, text));
            }
        }
        foreach (var g in data.Groups)
            if (g.MapConstant != null)
            {
                g.MapFolder = folderById.GetValueOrDefault(g.MapConstant);
                if (g.MapFolder == null) data.Problems.Add($"The interior map {g.MapConstant} of {g.Constant} was not found in data/maps.");
            }

        var valueByName = data.Ids.GroupBy(i => i.Name).ToDictionary(x => x.Key, x => x.First().Value);
        foreach (var (folder, json) in withBases)
        {
            List<SecretBaseLocation> found;
            try { found = ReadLocations(folder, json); }
            catch (JsonException ex) { data.Problems.Add($"data/maps/{folder}/map.json could not be read: {ex.Message}"); continue; }
            MapGrid? grid = null;
            if (found.Count > 0)
            {
                var scene = MapScene.Load(root, layouts, folder);
                grid = scene.Grid;
            }
            foreach (var loc in found)
            {
                Evaluate(data, loc, grid, valueByName);
                data.Locations.Add(loc);
            }
        }

        foreach (var dup in data.Locations.Where(l => l.Id != null).GroupBy(l => l.Id).Where(g => g.Count() > 1))
            foreach (var l in dup)
                l.Warnings.Add($"Id {dup.Key} is used by {dup.Count()} locations – they would share one base.");
        return data;
    }

    /// <summary>Position of warp_events[index] of a map.json (where the player arrives in an interior), or null.</summary>
    public static (int X, int Y)? ReadWarp(string json, int index)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("warp_events", out var arr) || arr.ValueKind != JsonValueKind.Array || index < 0 || index >= arr.GetArrayLength())
                return null;
            var e = arr[index];
            int I(string k) => e.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : 0;
            return (I("x"), I("y"));
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>All "secret_base" bg_events of one map.json.</summary>
    public static List<SecretBaseLocation> ReadLocations(string folder, string json)
    {
        var result = new List<SecretBaseLocation>();
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("bg_events", out var arr) || arr.ValueKind != JsonValueKind.Array) return result;
        int i = 0;
        foreach (var e in arr.EnumerateArray())
        {
            string S(string k) => e.TryGetProperty(k, out var v) ? v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : v.ToString() : "";
            int I(string k) => e.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : int.TryParse(S(k), out var p) ? p : 0;
            if (S("type") == "secret_base")
                result.Add(new SecretBaseLocation { MapFolder = folder, EventIndex = i, X = I("x"), Y = I("y"), Elevation = I("elevation"), IdConstant = S("secret_base_id") });
            i++;
        }
        return result;
    }

    private static void Evaluate(SecretBaseData data, SecretBaseLocation loc, MapGrid? grid, Dictionary<string, int> valueByName)
    {
        if (valueByName.TryGetValue(loc.IdConstant, out var v)) loc.Id = v;
        else if (int.TryParse(loc.IdConstant, out var n)) loc.Id = n;
        else loc.Warnings.Add($"{loc.IdConstant} is not defined in {ConstantsFile}.");
        if (loc.Id is { } id)
        {
            loc.Group = data.GroupOf(id);
            if (loc.Group == null) loc.Warnings.Add($"Id {id} points to interior group {id / 10}, which does not exist.");
            else
            {
                loc.Group.Locations.Add(loc);
                if (id % 10 == 0) loc.Warnings.Add("The ones digit is 0 (id 0 means \"no secret base\" in the save file).");
            }
        }
        if (grid == null) { loc.Warnings.Add("The map's blockdata could not be read."); return; }
        if (!grid.InBounds(loc.X, loc.Y)) { loc.Warnings.Add($"({loc.X},{loc.Y}) is outside the map."); return; }
        int metatile = grid.MetatileId(loc.X, loc.Y);
        loc.OutsideMetatile = metatile;
        var entrance = data.EntranceMetatiles.FirstOrDefault(e => e.Closed == metatile || e.Open == metatile);
        loc.IsEntranceMetatile = entrance != null;
        loc.MetatileType = entrance?.Type;
        if (entrance == null)
            loc.Warnings.Add($"The tile there (metatile 0x{metatile:X3}) is not a secret base entrance metatile – Secret Power will not work on it.");
        else if (loc.Group != null && entrance.Type != null && entrance.Type != loc.Group.Type)
            loc.Warnings.Add($"Entrance looks like {entrance.Type}, but the id leads to a {loc.Group.Type} interior.");
    }

    /// <summary>Type implied by a metatile (null if it is not an entrance metatile).</summary>
    public static string? TypeOfMetatile(SecretBaseData data, int metatile) =>
        data.EntranceMetatiles.FirstOrDefault(e => e.Closed == metatile || e.Open == metatile)?.Type;

    public sealed record FreeId(SecretBaseGroup Group, int Slot, int Value, string Name);

    /// <summary>The next unused id of the group (ones digit 1–9), or null if the group is full.</summary>
    public static FreeId? NextFreeId(SecretBaseData data, SecretBaseGroup group)
    {
        for (int k = 1; k <= 9; k++)
        {
            int value = group.Index * 10 + k;
            string name = $"SECRET_BASE_{group.Type}{group.Number}_{k}";
            if (data.Ids.Any(i => i.Value == value || i.Name == name)) continue;
            return new FreeId(group, k, value, name);
        }
        return null;
    }

    /// <summary>A free id in the preferred group, else in another group of the same type (fewest locations first).</summary>
    public static FreeId? NextFreeId(SecretBaseData data, string type, SecretBaseGroup? preferred)
    {
        if (preferred != null && NextFreeId(data, preferred) is { } p) return p;
        foreach (var g in data.Groups.Where(g => g.Type == type && g.MapFolder != null).OrderBy(g => g.Locations.Count).ThenBy(g => g.Index))
            if (NextFreeId(data, g) is { } f) return f;
        return null;
    }

    /// <summary>
    /// Inserts "#define SECRET_BASE_X_k  value" into secret_bases.h: after the group's highest smaller id (id order),
    /// value aligned like the neighbouring lines. All other bytes stay unchanged.
    /// </summary>
    public static string InsertIdConstant(string text, FreeId id)
    {
        if (Regex.IsMatch(text, @"^#define[ \t]+" + id.Name + @"\b", RegexOptions.Multiline))
            throw new InvalidOperationException($"{id.Name} already exists.");
        string nl = text.Contains("\r\n") ? "\r\n" : "\n";
        var ids = IdDefine().Matches(text).Select(m => (Match: m, Value: int.Parse(m.Groups[2].Value))).ToList();
        if (ids.Any(x => x.Value == id.Value)) throw new InvalidOperationException($"Id {id.Value} is already defined.");
        var sameGroup = ids.Where(x => x.Value / 10 == id.Group.Index).ToList();
        var sample = sameGroup.LastOrDefault().Match ?? ids.LastOrDefault().Match;
        int column = sample != null ? sample.Groups[2].Index - sample.Index : 0;
        string head = "#define " + id.Name;
        string line = (head.Length < column ? head.PadRight(column) : head + " ") + id.Value;

        var before = sameGroup.Where(x => x.Value < id.Value).LastOrDefault();
        if (before.Match != null)
        {
            int end = LineEnd(text, before.Match.Index);
            return text[..end] + nl + line + text[end..];
        }
        var after = sameGroup.FirstOrDefault(x => x.Value > id.Value);
        if (after.Match != null)
            return text[..after.Match.Index] + line + nl + text[after.Match.Index..];
        // Group without ids: put the id block before the group's #define, separated by a blank line.
        var groupLine = GroupDefine().Matches(text).FirstOrDefault(m => m.Groups[1].Value == id.Group.Constant)
                        ?? throw new InvalidOperationException($"#define {id.Group.Constant} was not found.");
        return text[..groupLine.Index] + line + nl + nl + text[groupLine.Index..];
    }

    /// <summary>Index of the line break that ends the line containing <paramref name="index"/> (before "\r\n").</summary>
    private static int LineEnd(string text, int index)
    {
        int n = text.IndexOf('\n', index);
        if (n < 0) return text.Length;
        return n > 0 && text[n - 1] == '\r' ? n - 1 : n;
    }
}
