using System.Text.Json;
using System.Text.RegularExpressions;
using TilesetEditor.Core.Project;

namespace TilesetEditor.Core.Platinum;

/// <summary>A map header of include/data/map_headers.h: which matrix, events, texts and area data a map uses.</summary>
public sealed record PlatinumMapHeader(
    string Id,
    int Index,
    string? Matrix,
    string? Events,
    string? AreaData,
    string? LabelText,
    string? Weather,
    string? Camera,
    string? DayMusic,
    string? Encounters,
    IReadOnlyDictionary<string, string> Fields)
{
    /// <summary>Project-relative files of the map (both layouts of pokeplatinum: per-map folders or the older central tables).</summary>
    public string? EventsFile { get; init; }
    public string? ScriptFile { get; init; }
    public string? TextFile { get; init; }
    /// <summary>The map's folder (res/field/maps/x) in the per-map layout, null in the older layout.</summary>
    public string? Folder { get; init; }

    /// <summary>MAP_HEADER_JUBILIFE_CITY_MART → "Jubilife City Mart".</summary>
    public string FriendlyName => PlatinumNames.Pretty(Id, "MAP_HEADER_");
    public override string ToString() => FriendlyName;
}

public static class PlatinumNames
{
    /// <summary>SPECIES_MR_MIME → "Mr Mime", TYPE_FIRE → "Fire".</summary>
    public static string Pretty(string constant, string prefix)
    {
        var s = constant.StartsWith(prefix, StringComparison.Ordinal) ? constant[prefix.Length..] : constant;
        return string.Join(' ', s.Split('_', StringSplitOptions.RemoveEmptyEntries)
            .Select(w => w.All(char.IsDigit) || (w.Length <= 3 && w.Any(char.IsDigit)) ? w : char.ToUpperInvariant(w[0]) + w[1..].ToLowerInvariant()));
    }

    /// <summary>SPECIES_NIDORAN_F → "nidoran_f" (the folder below res/pokemon).</summary>
    public static string SpeciesFolder(string constant) =>
        (constant.StartsWith("SPECIES_", StringComparison.Ordinal) ? constant[8..] : constant).ToLowerInvariant();
}

/// <summary>
/// What the toolkit knows about a pret/pokeplatinum project: the constant lists metang generates (generated/*.txt),
/// species folders, map headers. Everything else (game data, trainers, maps) is read by the modules on demand.
/// </summary>
public sealed partial class PlatinumProject
{
    public required string Root { get; init; }
    public required IReadOnlyList<string> Species { get; init; }
    public required IReadOnlyList<string> Moves { get; init; }
    public required IReadOnlyList<string> Items { get; init; }
    public required IReadOnlyList<string> Abilities { get; init; }
    public required IReadOnlyList<string> Types { get; init; }
    public required IReadOnlyList<string> TrainerClasses { get; init; }
    public required IReadOnlyList<PlatinumMapHeader> MapHeaders { get; init; }
    public int TrainerCount { get; init; }
    /// <summary>Name of the ROM as the build writes it (build/…nds).</summary>
    public string RomPath { get; init; } = GameKind.Platinum.DefaultRomPath();
    public List<string> Warnings { get; } = [];

    public const string MapHeadersFile = "include/data/map_headers.h";
    public const string SpeciesDir = "res/pokemon";
    public const string TrainersDir = "res/trainers/data";
    public const string TrainerClassesDir = "res/trainers/classes";
    public const string MovesDir = "res/moves";
    public const string ItemsDir = "res/items/data";

    public string FullPath(string rel) => ProjectPaths.Combine(Root, rel);

    [GeneratedRegex(@"\[(MAP_HEADER_\w+)\]\s*=\s*\{(.*?)\}\s*,", RegexOptions.Singleline)]
    private static partial Regex HeaderEntry();

    [GeneratedRegex(@"\.(\w+)\s*=\s*([^,\n]+)")]
    private static partial Regex HeaderField();

    public static PlatinumProject Load(string root)
    {
        if (GameDetector.Detect(root) != GameKind.Platinum)
            throw new ProjectLoadException($"'{root}' does not look like a pokeplatinum project: platinum.us/, res/pokemon/ or {MapHeadersFile} is missing.");
        var warnings = new List<string>();

        List<string> List(string name)
        {
            var path = ProjectPaths.Combine(root, "generated/" + name + ".txt");
            if (!File.Exists(path)) { warnings.Add($"generated/{name}.txt is missing."); return []; }
            return File.ReadLines(path).Select(l => l.Split('=')[0].Trim()).Where(l => l.Length > 0 && !l.StartsWith('#')).ToList();
        }

        var headers = new List<PlatinumMapHeader>();
        var headersPath = ProjectPaths.Combine(root, MapHeadersFile);
        if (Directory.Exists(ProjectPaths.Combine(root, MapsDir)) && File.Exists(ProjectPaths.Combine(root, MapGroupsFile)))
            headers = LoadMapFolders(root, warnings);
        else if (File.Exists(headersPath))
        {
            var text = ProjectLoader.StripComments(File.ReadAllText(headersPath));
            int i = 0;
            foreach (Match m in HeaderEntry().Matches(text))
            {
                var fields = HeaderField().Matches(m.Groups[2].Value)
                    .GroupBy(f => f.Groups[1].Value).ToDictionary(g => g.Key, g => g.First().Groups[2].Value.Trim(), StringComparer.Ordinal);
                var events = fields.GetValueOrDefault("eventsArchiveID");
                var scripts = fields.GetValueOrDefault("scriptsArchiveID");
                var msgBank = fields.GetValueOrDefault("msgArchiveID");
                headers.Add(new PlatinumMapHeader(m.Groups[1].Value, i++,
                    fields.GetValueOrDefault("mapMatrixID"), events, fields.GetValueOrDefault("areaDataArchiveID"),
                    fields.GetValueOrDefault("mapLabelTextID"), fields.GetValueOrDefault("weather"), fields.GetValueOrDefault("cameraType"),
                    fields.GetValueOrDefault("dayMusicID"), fields.GetValueOrDefault("wildEncountersArchiveID"), fields)
                {
                    EventsFile = events != null ? $"res/field/events/{events}.json" : null,
                    ScriptFile = scripts != null ? $"res/field/scripts/{scripts}.s" : null,
                    TextFile = msgBank is { } t && t.StartsWith("TEXT_BANK_", StringComparison.Ordinal) ? $"res/text/{t[10..].ToLowerInvariant()}.json" : null,
                });
            }
            if (headers.Count == 0) warnings.Add($"No map headers found in {MapHeadersFile}.");
        }
        else warnings.Add($"{MapHeadersFile} is missing.");

        var trainersDir = ProjectPaths.Combine(root, TrainersDir);
        var p = new PlatinumProject
        {
            Root = root,
            Species = List("species"),
            Moves = List("moves"),
            Items = List("items"),
            Abilities = List("abilities"),
            Types = List("pokemon_types"),
            TrainerClasses = List("trainer_classes"),
            MapHeaders = headers,
            TrainerCount = Directory.Exists(trainersDir) ? Directory.EnumerateFiles(trainersDir, "*.json").Count() : 0,
        };
        p.Warnings.AddRange(warnings);
        return p;
    }

    public const string MapsDir = "res/field/maps";
    public const string MapGroupsFile = "res/field/maps/groups.json";

    /// <summary>
    /// The per-map layout of newer pokeplatinum: res/field/maps/&lt;map&gt;/header.json (+ events.json, scripts.s); the
    /// matrices are the groups of res/field/maps/groups.json (header.json "mapGroup").
    /// </summary>
    private static List<PlatinumMapHeader> LoadMapFolders(string root, List<string> warnings)
    {
        // Each file access over a network share (\\wsl.localhost) takes milliseconds: one listing per folder instead of a
        // check per file, and the ~600 folders in parallel.
        var dirs = Directory.GetDirectories(ProjectPaths.Combine(root, MapsDir)).OrderBy(d => d, StringComparer.Ordinal).ToArray();
        var found = new (string Name, Dictionary<string, string> Fields, HashSet<string> Files)?[dirs.Length];
        var problems = new System.Collections.Concurrent.ConcurrentBag<string>();
        ParallelIo.For(dirs.Length, k =>
        {
            var dir = dirs[k];
            var name = Path.GetFileName(dir);
            var files = Directory.GetFiles(dir).Select(Path.GetFileName).OfType<string>().ToHashSet(StringComparer.Ordinal);
            if (!files.Contains("header.json")) return;
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(dir, "header.json")));
                var fields = doc.RootElement.EnumerateObject().ToDictionary(p => p.Name,
                    p => p.Value.ValueKind == JsonValueKind.String ? p.Value.GetString() ?? "" : p.Value.GetRawText(), StringComparer.Ordinal);
                found[k] = (name, fields, files);
            }
            catch (JsonException ex) { problems.Add($"{MapsDir}/{name}/header.json: {ex.Message}"); }
        });
        warnings.AddRange(problems.OrderBy(x => x, StringComparer.Ordinal));
        var list = new List<PlatinumMapHeader>();
        int i = 0;
        foreach (var entry in found)
        {
            if (entry is not { } e) continue;
            var (name, fields, files) = e;
            var folder = $"{MapsDir}/{name}";
            string? Existing(string file) => files.Contains(file) ? $"{folder}/{file}" : null;
            var text = fields.GetValueOrDefault("textBank");
            list.Add(new PlatinumMapHeader("MAP_HEADER_" + name.ToUpperInvariant(), i++,
                fields.GetValueOrDefault("mapGroup"), Existing("events.json") != null ? name : null, fields.GetValueOrDefault("areaData"),
                fields.GetValueOrDefault("locationName"), fields.GetValueOrDefault("weather"), fields.GetValueOrDefault("cameraType"),
                fields.GetValueOrDefault("musicDay"), Existing("encounters.json") != null ? "encounters.json" : null, fields)
            {
                EventsFile = Existing("events.json"),
                ScriptFile = Existing("scripts.s"),
                TextFile = text is { } t && t.StartsWith("TEXT_BANK_", StringComparison.Ordinal) ? $"res/text/{t[10..].ToLowerInvariant()}.json" : null,
                Folder = folder,
            });
        }
        if (list.Count == 0) warnings.Add($"No map folders with header.json found in {MapsDir}.");
        return list;
    }

    /// <summary>Lines of a generated/*.txt constant list (empty if missing).</summary>
    public IReadOnlyList<string> ConstantList(string name)
    {
        var path = FullPath("generated/" + name + ".txt");
        return File.Exists(path)
            ? File.ReadLines(path).Select(l => l.Split('=')[0].Trim()).Where(l => l.Length > 0).ToList()
            : [];
    }

    /// <summary>English texts of a res/text bank (index = message number), e.g. location_names, trainer_class_names.</summary>
    public IReadOnlyList<(string Id, string Text)> TextBank(string name)
    {
        var path = FullPath("res/text/" + name + ".json");
        if (!File.Exists(path)) return [];
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        if (!doc.RootElement.TryGetProperty("messages", out var msgs)) return [];
        return msgs.EnumerateArray().Select(m => (
            m.TryGetProperty("id", out var id) ? id.GetString() ?? "" : "",
            m.TryGetProperty("en_US", out var t) ? (t.ValueKind == JsonValueKind.Array ? string.Concat(t.EnumerateArray().Select(x => x.GetString())) : t.GetString() ?? "") : ""))
            .ToList();
    }
}
