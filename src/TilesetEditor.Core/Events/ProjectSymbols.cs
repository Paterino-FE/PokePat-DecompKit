using System.Text.Json;
using System.Text.RegularExpressions;

namespace TilesetEditor.Core.Events;

public sealed record FlagSymbol(string Name, string Comment)
{
    public bool IsUnused => Name.StartsWith("FLAG_UNUSED", StringComparison.Ordinal) || Comment.Contains("Unused", StringComparison.OrdinalIgnoreCase);
    public string Display => IsUnused ? $"{Name}  (frei)" : Name;
    public override string ToString() => Name;
}

public sealed record ItemSymbol(string Name, string DisplayName)
{
    public string Display => DisplayName.Length > 0 ? $"{DisplayName}  ({Name})" : Name;
    public override string ToString() => Name;
}

/// <summary>A script command macro from asm/macros/event.inc.</summary>
public sealed record MacroParam(string Name, bool Required, string? Default);
public sealed record ScriptMacro(string Name, IReadOnlyList<MacroParam> Params, string Description)
{
    public string Signature => Params.Count == 0 ? Name : $"{Name} {string.Join(", ", Params.Select(p => p.Required ? p.Name : $"[{p.Name}]"))}";
    public override string ToString() => Name;
}

/// <summary>Constants and commands of the branch, for the pickers in the event builder.</summary>
public sealed partial class ProjectSymbols
{
    public required IReadOnlyList<FlagSymbol> Flags { get; init; }
    public required IReadOnlyList<ItemSymbol> Items { get; init; }
    public IReadOnlyList<string> Vars { get; init; } = [];
    public IReadOnlyList<string> Species { get; init; } = [];
    public IReadOnlyList<string> Trainers { get; init; } = [];
    public IReadOnlyList<string> Maps { get; init; } = [];
    public IReadOnlyList<string> Music { get; init; } = [];
    public IReadOnlyList<string> Sounds { get; init; } = [];
    public IReadOnlyList<string> MovementActions { get; init; } = [];
    public IReadOnlyList<string> Specials { get; init; } = [];
    public IReadOnlyList<string> Weathers { get; init; } = [];
    public IReadOnlyList<string> ObjectGraphics { get; init; } = [];
    public IReadOnlyList<string> MovementTypes { get; init; } = [];
    public IReadOnlyList<ScriptMacro> Macros { get; init; } = [];

    [GeneratedRegex(@"^\s*#define\s+(FLAG_\w+)\s+[^/\r\n]*(?://\s*(.*))?$", RegexOptions.Multiline)]
    private static partial Regex FlagDefine();

    [GeneratedRegex(@"\[(ITEM_\w+)\]\s*=\s*\{\s*\.name\s*=\s*(?:_|ITEM_NAME|COMPOUND_STRING)\(""([^""]*)""\)", RegexOptions.Singleline)]
    private static partial Regex ItemName();

    [GeneratedRegex(@"create_movement_action\s+(\w+)\s*,")]
    private static partial Regex MovementAction();

    [GeneratedRegex(@"^\s*def_special\s+(\w+)", RegexOptions.Multiline)]
    private static partial Regex SpecialDef();

    [GeneratedRegex(@"""id""\s*:\s*""(MAP_\w+)""")]
    private static partial Regex MapId();

    // Range markers and helpers, not real values.
    private static readonly string[] Excludes = ["_START", "_END", "_COUNT", "FLAGS_", "SPECIAL_FLAGS", "TEMP_FLAGS", "_FIRST", "_LAST", "_LIST_END"];

    private static List<string> Defines(string text, string prefix, params string[] extraExcludes) =>
        Regex.Matches(text, @"^\s*#define\s+(" + prefix + @"\w+)\b(?!\()", RegexOptions.Multiline)
            .Select(m => m.Groups[1].Value)
            .Where(n => !Excludes.Concat(extraExcludes).Any(x => n.Contains(x, StringComparison.Ordinal)))
            .Distinct().ToList();

    public static ProjectSymbols Load(string projectRoot)
    {
        string Read(params string[] parts)
        {
            var p = Path.Combine([projectRoot, .. parts]);
            return File.Exists(p) ? File.ReadAllText(p) : "";
        }
        var flags = FlagDefine().Matches(Read("include", "constants", "flags.h"))
            .Select(m => new FlagSymbol(m.Groups[1].Value, m.Groups[2].Value.Trim()))
            .Where(f => !Excludes.Any(x => f.Name.Contains(x, StringComparison.Ordinal)))
            .GroupBy(f => f.Name).Select(g => g.First()).ToList();
        var names = ItemName().Matches(Read("src", "data", "items.h"))
            .GroupBy(m => m.Groups[1].Value).ToDictionary(g => g.Key, g => g.First().Groups[2].Value);
        var items = Defines(Read("include", "constants", "items.h"), "ITEM_", "ITEMS_COUNT", "ITEM_NONE", "ITEM_FIELD_ARROW")
            .Select(n => new ItemSymbol(n, names.GetValueOrDefault(n, ""))).ToList();
        var songs = Read("include", "constants", "songs.h");

        var maps = new List<string>();
        var mapsDir = Path.Combine(projectRoot, "data", "maps");
        if (Directory.Exists(mapsDir))
            foreach (var f in Directory.EnumerateFiles(mapsDir, "map.json", SearchOption.AllDirectories))
            {
                var m = MapId().Match(File.ReadAllText(f));
                if (m.Success) maps.Add(m.Groups[1].Value);
            }

        return new ProjectSymbols
        {
            Flags = flags,
            Items = items,
            Vars = Defines(Read("include", "constants", "vars.h"), "VAR_"),
            Species = Defines(Read("include", "constants", "species.h"), "SPECIES_", "SPECIES_NONE", "SPECIES_EGG"),
            Trainers = Defines(Read("include", "constants", "opponents.h"), "TRAINER_", "TRAINER_NONE", "TRAINERS_COUNT"),
            Maps = maps.OrderBy(m => m, StringComparer.Ordinal).ToList(),
            Music = Defines(songs, "MUS_"),
            Sounds = Defines(songs, "SE_"),
            MovementActions = MovementAction().Matches(Read("asm", "macros", "movement.inc")).Select(m => m.Groups[1].Value)
                .Where(n => n != "name").Distinct().ToList(),
            Specials = SpecialDef().Matches(Read("data", "specials.inc")).Select(m => m.Groups[1].Value).Distinct().ToList(),
            Weathers = Defines(Read("include", "constants", "weather.h"), "WEATHER_", "WEATHER_PAL", "WEATHER_SUN_", "WEATHER_ID_"),
            ObjectGraphics = Defines(Read("include", "constants", "event_objects.h"), "OBJ_EVENT_GFX_", "OBJ_EVENT_GFX_VARS", "NUM_OBJ_EVENT_GFX"),
            MovementTypes = Defines(Read("include", "constants", "event_object_movement.h"), "MOVEMENT_TYPE_", "NUM_MOVEMENT_TYPES"),
            Macros = ParseMacros(Read("asm", "macros", "event.inc")),
        };
    }

    /// <summary>".macro name a:req, b=default, c" with the "@ …" comment lines above it as description.</summary>
    public static List<ScriptMacro> ParseMacros(string text)
    {
        var result = new List<ScriptMacro>();
        var comment = new List<string>();
        foreach (var raw in text.Replace("\r\n", "\n").Split('\n'))
        {
            var line = raw.Trim();
            if (line.StartsWith('@')) { comment.Add(line.TrimStart('@').Trim()); continue; }
            var m = Regex.Match(line, @"^\.macro\s+(\w+)\s*(.*)$");
            if (m.Success)
            {
                var ps = Regex.Split(m.Groups[2].Value.Trim(), @"\s*,\s*|\s+(?=\w+(?::|=|\s*,|\s*$))").Where(s => s.Length > 0)
                    .Select(p =>
                    {
                        var req = p.EndsWith(":req", StringComparison.Ordinal);
                        var name = req ? p[..^4] : p;
                        string? def = null;
                        int eq = name.IndexOf('=');
                        if (eq > 0) { def = name[(eq + 1)..].Trim(); name = name[..eq].Trim(); }
                        if (name.EndsWith(":vararg", StringComparison.Ordinal)) name = name[..^7];
                        return new MacroParam(name, req, def);
                    }).ToList();
                result.Add(new ScriptMacro(m.Groups[1].Value, ps, string.Join(" ", comment)));
            }
            if (line.Length > 0 && !line.StartsWith('@')) comment.Clear();
        }
        return result.GroupBy(r => r.Name).Select(g => g.First()).OrderBy(r => r.Name, StringComparer.Ordinal).ToList();
    }

    public bool IsFlag(string name) => Flags.Any(f => f.Name == name);
    public bool IsItem(string name) => Items.Any(i => i.Name == name);
}
