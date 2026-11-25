using System.Text.Json;
using System.Text.RegularExpressions;
using TilesetEditor.Core.Project;

namespace TilesetEditor.Core.Events;

public enum FlagKind
{
    Flag,
    Var,
}

/// <summary>What a place in the project does with a flag or variable.</summary>
public enum FlagUsageKind
{
    Sets,
    Clears,
    Checks,
    /// <summary>map.json: a person is hidden while the flag is set.</summary>
    Visibility,
    /// <summary>map.json: a trigger tile fires while the variable has a value.</summary>
    Trigger,
    /// <summary>map.json: a hidden item is collected once the flag is set.</summary>
    HiddenItem,
    Other,
}

/// <summary>One #define FLAG_… / VAR_… from include/constants/flags.h or vars.h.</summary>
public sealed record FlagInfo(string Name, FlagKind Kind, string ValueText, long? Value, string Comment, string File, int Line, string Category)
{
    /// <summary>FLAG_UNUSED_… / VAR_UNUSED_… in the saved range: can be claimed under a new name.</summary>
    public bool IsClaimable => Name.Contains("_UNUSED_", StringComparison.Ordinal)
                               && Value is { } v && (Kind == FlagKind.Flag ? v < 0x4000 : v is >= 0x4000 and < 0x8000);
    public string Hex => Value is { } v ? $"0x{v:X}" : ValueText;
    public override string ToString() => Name;
}

public sealed record FlagUsage(string Name, FlagUsageKind Kind, string File, int Line, string Map, string Text)
{
    public string KindText => Kind switch
    {
        FlagUsageKind.Sets => "sets",
        FlagUsageKind.Clears => "clears",
        FlagUsageKind.Checks => "checks",
        FlagUsageKind.Visibility => "hides a person while set",
        FlagUsageKind.Trigger => "trigger tile",
        FlagUsageKind.HiddenItem => "hidden item",
        _ => "uses",
    };
}

/// <summary>A person on a map with its visibility flag (object_events[i].flag).</summary>
public sealed record MapObjectFlag(string Map, string MapJson, int Index, int X, int Y, string Graphics, string Flag, string Script);

/// <summary>Flags and variables of the branch with category and every place that uses them.</summary>
public sealed partial class FlagCatalog
{
    public const string FlagsFile = "include/constants/flags.h";
    public const string VarsFile = "include/constants/vars.h";
    public const string OpponentsFile = "include/constants/opponents.h";

    public required IReadOnlyList<FlagInfo> Entries { get; init; }
    public required IReadOnlyDictionary<string, List<FlagUsage>> Usages { get; init; }
    public required IReadOnlyList<MapObjectFlag> Objects { get; init; }
    /// <summary>Map folder name → map id (MossdeepCity → MAP_MOSSDEEP_CITY).</summary>
    public required IReadOnlyDictionary<string, string> MapIds { get; init; }

    public IReadOnlyList<FlagUsage> UsagesOf(string name) => Usages.TryGetValue(name, out var l) ? l : [];
    public bool IsUnusedAnywhere(string name) => UsagesOf(name).Count == 0;

    [GeneratedRegex(@"^[ \t]*#[ \t]*define[ \t]+((?:FLAG|VAR)_\w+)(?!\()[ \t]+(.+?)[ \t]*(?://[ \t]*(.*?))?[ \t]*$")]
    private static partial Regex DefineLine();

    [GeneratedRegex(@"\b(?:FLAG|VAR)_[A-Za-z0-9_]+\b")]
    private static partial Regex SymbolRef();

    private static readonly string[] RangeMarkers = ["_START", "_END", "_COUNT", "NUM_", "FLAGS_START"];

    public static string CategoryOf(string name, FlagKind kind, long? value)
    {
        if (kind == FlagKind.Flag)
        {
            if (name.StartsWith("FLAG_TEMP_", StringComparison.Ordinal)) return "Temporary (per map)";
            if (name.Contains("_UNUSED_", StringComparison.Ordinal)) return "Free";
            if (value >= 0x4000) return "Special (not saved)";
            if (name.StartsWith("FLAG_HIDE_", StringComparison.Ordinal)) return "Show/hide persons";
            if (name.StartsWith("FLAG_HIDDEN_ITEM_", StringComparison.Ordinal)) return "Hidden items";
            if (name.StartsWith("FLAG_ITEM_", StringComparison.Ordinal)) return "Items on the ground";
            if (name.StartsWith("FLAG_BADGE", StringComparison.Ordinal)) return "Badges";
            if (name.StartsWith("FLAG_DAILY_", StringComparison.Ordinal)) return "Daily";
            if (name.StartsWith("FLAG_DEFEATED_", StringComparison.Ordinal)) return "Defeated";
            if (name.StartsWith("FLAG_SYS_", StringComparison.Ordinal)) return "System";
            if (name.StartsWith("FLAG_RECEIVED_", StringComparison.Ordinal)) return "Received";
            return "Story / general";
        }
        if (name.StartsWith("VAR_TEMP_", StringComparison.Ordinal)) return "Temporary (per map)";
        if (name.Contains("_UNUSED_", StringComparison.Ordinal)) return "Free";
        if (name.StartsWith("VAR_OBJ_GFX_ID_", StringComparison.Ordinal)) return "Person graphics";
        if (value >= 0x8000) return "Special (script, not saved)";
        return "Story / general";
    }

    public static List<FlagInfo> ParseDefinitions(string flagsText, string varsText, string extraDefines = "")
    {
        var defines = new CDefines();
        defines.AddFromText(extraDefines); // e.g. opponents.h: trainer flags depend on MAX_TRAINERS_COUNT
        defines.AddFromText(flagsText);
        defines.AddFromText(varsText);
        var result = new List<FlagInfo>();
        void Parse(string text, string file)
        {
            var lines = text.Replace("\r\n", "\n").Split('\n');
            for (int i = 0; i < lines.Length; i++)
            {
                var m = DefineLine().Match(lines[i]);
                if (!m.Success) continue;
                var name = m.Groups[1].Value;
                if (RangeMarkers.Any(r => name.Contains(r, StringComparison.Ordinal))) continue;
                var kind = name.StartsWith("FLAG_", StringComparison.Ordinal) ? FlagKind.Flag : FlagKind.Var;
                var value = defines.TryEvaluate(name);
                result.Add(new FlagInfo(name, kind, m.Groups[2].Value.Trim(), value, m.Groups[3].Value.Trim(), file, i + 1, CategoryOf(name, kind, value)));
            }
        }
        Parse(flagsText, FlagsFile);
        Parse(varsText, VarsFile);
        return result.GroupBy(r => r.Name).Select(g => g.First()).ToList();
    }

    /// <summary>Classifies a script or C line by its command.</summary>
    public static FlagUsageKind Classify(string line, string name)
    {
        var t = line.Trim();
        var lower = t.ToLowerInvariant();
        if (lower.StartsWith("setflag", StringComparison.Ordinal) || t.Contains("FlagSet(" + name, StringComparison.Ordinal)) return FlagUsageKind.Sets;
        if (lower.StartsWith("clearflag", StringComparison.Ordinal) || t.Contains("FlagClear(" + name, StringComparison.Ordinal)) return FlagUsageKind.Clears;
        if (Regex.IsMatch(lower, @"^(setvar|addvar|subvar|copyvar|setorcopyvar)\b") && Regex.IsMatch(t, @"^\w+\s*\(?\s*" + name + @"\b")) return FlagUsageKind.Sets;
        if (t.Contains("VarSet(" + name, StringComparison.Ordinal)) return FlagUsageKind.Sets;
        if (Regex.IsMatch(lower, @"^(goto_if|call_if|checkflag|compare|switch|if\b|elif\b|while\b|case\b)") || t.Contains("flag(" + name, StringComparison.Ordinal)
            || t.Contains("var(" + name, StringComparison.Ordinal) || t.Contains("FlagGet(" + name, StringComparison.Ordinal) || t.Contains("VarGet(" + name, StringComparison.Ordinal))
            return FlagUsageKind.Checks;
        return FlagUsageKind.Other;
    }

    public static FlagCatalog Load(string root)
    {
        string Read(string rel)
        {
            var p = Path.Combine(root, rel.Replace('/', Path.DirectorySeparatorChar));
            return File.Exists(p) ? File.ReadAllText(p) : "";
        }
        var entries = ParseDefinitions(Read(FlagsFile), Read(VarsFile), Read(OpponentsFile));
        var known = entries.Select(e => e.Name).ToHashSet(StringComparer.Ordinal);
        var usages = new Dictionary<string, List<FlagUsage>>(StringComparer.Ordinal);
        void Add(FlagUsage u)
        {
            if (!usages.TryGetValue(u.Name, out var l)) usages[u.Name] = l = [];
            l.Add(u);
        }

        var files = new List<(string Full, string Map)>();
        var mapsDir = Path.Combine(root, "data", "maps");
        var mapIds = new Dictionary<string, string>(StringComparer.Ordinal);
        var objects = new List<MapObjectFlag>();
        if (Directory.Exists(mapsDir))
            foreach (var dir in Directory.EnumerateDirectories(mapsDir))
            {
                var map = Path.GetFileName(dir);
                foreach (var f in Directory.EnumerateFiles(dir))
                    if (f.EndsWith("scripts.inc", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".pory", StringComparison.OrdinalIgnoreCase))
                        files.Add((f, map));
                var json = Path.Combine(dir, "map.json");
                if (File.Exists(json)) ScanMapJson(root, json, map, Add, mapIds, objects);
            }
        var scriptsDir = Path.Combine(root, "data", "scripts");
        if (Directory.Exists(scriptsDir))
            foreach (var f in Directory.EnumerateFiles(scriptsDir, "*", SearchOption.AllDirectories))
                if (f.EndsWith(".inc", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".pory", StringComparison.OrdinalIgnoreCase))
                    files.Add((f, ""));
        var srcDir = Path.Combine(root, "src");
        if (Directory.Exists(srcDir))
            foreach (var f in Directory.EnumerateFiles(srcDir, "*.c")) files.Add((f, ""));

        foreach (var (full, map) in files)
        {
            var rel = Path.GetRelativePath(root, full).Replace('\\', '/');
            // Generated .inc next to a .pory source would count every use twice.
            if (full.EndsWith(".inc", StringComparison.OrdinalIgnoreCase) && File.Exists(Path.ChangeExtension(full, ".pory"))) continue;
            string text;
            try { text = File.ReadAllText(full); }
            catch (IOException) { continue; }
            if (!text.Contains("FLAG_", StringComparison.Ordinal) && !text.Contains("VAR_", StringComparison.Ordinal)) continue;
            var lines = text.Split('\n');
            for (int i = 0; i < lines.Length; i++)
            {
                var line = lines[i];
                if (!line.Contains("FLAG_", StringComparison.Ordinal) && !line.Contains("VAR_", StringComparison.Ordinal)) continue;
                if (line.TrimStart().StartsWith("#define", StringComparison.Ordinal)) continue;
                foreach (var name in SymbolRef().Matches(line).Select(m => m.Value).Distinct())
                    if (known.Contains(name))
                        Add(new FlagUsage(name, Classify(line, name), rel, i + 1, map, line.Trim()));
            }
        }
        return new FlagCatalog { Entries = entries, Usages = usages, Objects = objects, MapIds = mapIds };
    }

    private static void ScanMapJson(string root, string path, string map, Action<FlagUsage> add, Dictionary<string, string> mapIds, List<MapObjectFlag> objects)
    {
        var rel = Path.GetRelativePath(root, path).Replace('\\', '/');
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var r = doc.RootElement;
            if (r.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String) mapIds[map] = id.GetString()!;
            string S(JsonElement e, string k) => e.TryGetProperty(k, out var v) ? v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : v.ToString() : "";
            int I(JsonElement e, string k) => e.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : 0;
            if (r.TryGetProperty("object_events", out var objs) && objs.ValueKind == JsonValueKind.Array)
            {
                int i = 0;
                foreach (var o in objs.EnumerateArray())
                {
                    var flag = S(o, "flag");
                    if (o.TryGetProperty("flag", out _))
                        objects.Add(new MapObjectFlag(map, rel, i, I(o, "x"), I(o, "y"), S(o, "graphics_id").Replace("OBJ_EVENT_GFX_", ""), flag, S(o, "script")));
                    if (flag.StartsWith("FLAG_", StringComparison.Ordinal))
                        add(new FlagUsage(flag, FlagUsageKind.Visibility, rel, 0, map, $"Person #{i + 1} ({I(o, "x")},{I(o, "y")}) {S(o, "graphics_id").Replace("OBJ_EVENT_GFX_", "")}"));
                    i++;
                }
            }
            if (r.TryGetProperty("coord_events", out var coords) && coords.ValueKind == JsonValueKind.Array)
            {
                int i = 0;
                foreach (var c in coords.EnumerateArray())
                {
                    var v = S(c, "var");
                    if (v.StartsWith("VAR_", StringComparison.Ordinal))
                        add(new FlagUsage(v, FlagUsageKind.Trigger, rel, 0, map, $"Trigger #{i + 1} ({I(c, "x")},{I(c, "y")}) at value {S(c, "var_value")} → {S(c, "script")}"));
                    i++;
                }
            }
            if (r.TryGetProperty("bg_events", out var bgs) && bgs.ValueKind == JsonValueKind.Array)
                foreach (var b in bgs.EnumerateArray())
                {
                    var f = S(b, "flag");
                    if (f.StartsWith("FLAG_", StringComparison.Ordinal))
                        add(new FlagUsage(f, FlagUsageKind.HiddenItem, rel, 0, map, $"Hidden item {S(b, "item")} ({I(b, "x")},{I(b, "y")})"));
                }
        }
        catch (JsonException)
        {
            // A broken map.json is reported by the Maps module; here it simply contributes nothing.
        }
    }
}

/// <summary>Text edits on flags.h / vars.h: claim a free slot under a new name, or change a description.</summary>
public static partial class FlagEditor
{
    [GeneratedRegex(@"^(FLAG|VAR)_[A-Z0-9_]+$")]
    private static partial Regex ValidName();

    public static bool IsValidName(string name, FlagKind kind) =>
        ValidName().IsMatch(name) && name.StartsWith(kind == FlagKind.Flag ? "FLAG_" : "VAR_", StringComparison.Ordinal) && name.Length <= 60;

    /// <summary>Makes a constant name from free text: "Rival besiegt" → FLAG_RIVAL_BESIEGT.</summary>
    public static string ToName(string text, FlagKind kind)
    {
        var prefix = kind == FlagKind.Flag ? "FLAG_" : "VAR_";
        var t = text.Trim();
        if (t.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) t = t[prefix.Length..];
        t = t.Replace("ä", "AE").Replace("ö", "OE").Replace("ü", "UE").Replace("Ä", "AE").Replace("Ö", "OE").Replace("Ü", "UE").Replace("ß", "SS");
        t = Regex.Replace(Regex.Replace(t, @"([a-z0-9])([A-Z])", "$1_$2"), @"[^A-Za-z0-9]+", "_").Trim('_').ToUpperInvariant();
        return t.Length == 0 ? "" : prefix + t;
    }

    public const int FlagBlock = 64; // 8 bytes of save data
    public const int VarBlock = 16;  // 32 bytes of save data
    public const string ExtraFlagsStart = "TOOLKIT_FLAGS_START";
    public const string ExtraFlagsEnd = "TOOLKIT_FLAGS_END";

    /// <summary>
    /// Adds <paramref name="count"/> new saved flags (named FLAG_UNUSED_0x…, so they can be claimed like any free flag)
    /// after the last saved flag: a block TOOLKIT_FLAGS_START…TOOLKIT_FLAGS_END before FLAGS_COUNT, which then counts them.
    /// Grows SaveBlock1 by count/8 bytes – existing save files no longer match.
    /// </summary>
    public static (string Text, List<string> Names) AddFlags(string flagsText, int count = FlagBlock, string extraDefines = "")
    {
        if (count <= 0 || count % 8 != 0) throw new ArgumentException("Flags are added in multiples of 8.", nameof(count));
        var nl = flagsText.Contains("\r\n") ? "\r\n" : "\n";
        var countLine = Regex.Match(flagsText, @"^[ \t]*#define[ \t]+FLAGS_COUNT[ \t]+\(([^)\r\n]+?)[ \t]*\+[ \t]*1[ \t]*\)[^\r\n]*\r?\n", RegexOptions.Multiline);
        if (!countLine.Success) throw new InvalidOperationException("\"#define FLAGS_COUNT (… + 1)\" was not found in " + FlagCatalog.FlagsFile + ".");
        var defines = new CDefines();
        defines.AddFromText(extraDefines);
        defines.AddFromText(flagsText);
        var names = new List<string>();
        var sb = new System.Text.StringBuilder();
        string Line(string name, string value, string comment = "") => $"#define {name.PadRight(44)}{value}{(comment.Length > 0 ? " // " + comment : "")}{nl}";
        string result;
        var endMatch = Regex.Match(flagsText, @"^[ \t]*#define[ \t]+" + ExtraFlagsEnd + @"[ \t]+\(" + ExtraFlagsStart + @"[ \t]*\+[ \t]*0x([0-9A-Fa-f]+)\)[^\r\n]*\r?\n", RegexOptions.Multiline);
        defines.AddFromText("#define TK_LAST_SAVED_FLAG (" + countLine.Groups[1].Value.Trim() + ")");
        long start = defines.TryEvaluate("TK_LAST_SAVED_FLAG") is { } last ? last + 1 : throw new InvalidOperationException("The last flag number could not be computed.");
        if (endMatch.Success)
        {
            // Extend the existing block: new defines before its END line, END moves up.
            int oldEnd = Convert.ToInt32(endMatch.Groups[1].Value, 16);
            long blockStart = defines.TryEvaluate(ExtraFlagsStart) ?? throw new InvalidOperationException(ExtraFlagsStart + " could not be computed.");
            for (int i = 1; i <= count; i++)
            {
                long id = blockStart + oldEnd + i;
                var name = $"FLAG_UNUSED_0x{id:X3}";
                names.Add(name);
                sb.Append(Line(name, $"({ExtraFlagsStart} + 0x{oldEnd + i:X})", "Unused Flag"));
            }
            sb.Append(Line(ExtraFlagsEnd, $"({ExtraFlagsStart} + 0x{oldEnd + count:X})"));
            result = flagsText[..endMatch.Index] + sb + flagsText[(endMatch.Index + endMatch.Length)..];
        }
        else
        {
            sb.Append($"// Extra saved flags added by the PokePat DecompKit (each block of {count} flags = {count / 8} bytes of save data).{nl}");
            sb.Append(Line(ExtraFlagsStart, $"({countLine.Groups[1].Value.Trim()} + 1)"));
            for (int i = 0; i < count; i++)
            {
                var name = $"FLAG_UNUSED_0x{start + i:X3}";
                names.Add(name);
                sb.Append(Line(name, $"({ExtraFlagsStart} + 0x{i:X})", "Unused Flag"));
            }
            sb.Append(Line(ExtraFlagsEnd, $"({ExtraFlagsStart} + 0x{count - 1:X})"));
            sb.Append(nl).Append($"#define FLAGS_COUNT ({ExtraFlagsEnd} + 1){nl}");
            result = flagsText[..countLine.Index] + sb + flagsText[(countLine.Index + countLine.Length)..];
        }
        foreach (var n in names)
            if (Regex.IsMatch(flagsText, @"\b" + n + @"\b")) throw new InvalidOperationException($"{n} already exists – the flag numbering is not as expected.");
        return (result, names);
    }

    /// <summary>
    /// Adds <paramref name="count"/> saved variables VAR_UNUSED_0x… after VARS_END and raises VARS_END (must stay below
    /// the special variables at 0x8000). Grows SaveBlock1 by 2 bytes per variable – existing save files no longer match.
    /// </summary>
    public static (string Text, List<string> Names) AddVars(string varsText, int count = VarBlock)
    {
        var nl = varsText.Contains("\r\n") ? "\r\n" : "\n";
        var end = Regex.Match(varsText, @"^([ \t]*#define[ \t]+VARS_END[ \t]+)0x([0-9A-Fa-f]+)([^\r\n]*)\r?\n", RegexOptions.Multiline);
        if (!end.Success) throw new InvalidOperationException("\"#define VARS_END 0x…\" was not found in " + FlagCatalog.VarsFile + ".");
        int last = Convert.ToInt32(end.Groups[2].Value, 16);
        if (last + count >= 0x8000) throw new InvalidOperationException("Variables would reach the special variables at 0x8000.");
        int column = end.Groups[1].Value.TrimStart().Length;
        var names = new List<string>();
        var sb = new System.Text.StringBuilder();
        for (int i = 1; i <= count; i++)
        {
            var name = $"VAR_UNUSED_0x{last + i:X4}";
            if (Regex.IsMatch(varsText, @"\b" + name + @"\b")) throw new InvalidOperationException($"{name} already exists.");
            names.Add(name);
            sb.Append(($"#define {name}").PadRight(column)).Append($"0x{last + i:X4} // Unused Var").Append(nl);
        }
        sb.Append(nl);
        var newEnd = end.Groups[1].Value + $"0x{last + count:X4}" + end.Groups[3].Value + nl;
        return (varsText[..end.Index] + sb + newEnd + varsText[(end.Index + end.Length)..], names);
    }

    /// <summary>
    /// Rewrites the #define line of <paramref name="oldName"/>: new name (keeps the value and the column of the value
    /// where possible) and/or a new trailing comment. Every other line stays byte-identical.
    /// </summary>
    public static string Rewrite(string text, string oldName, string newName, string? comment)
    {
        var rx = new Regex(@"^([ \t]*#[ \t]*define[ \t]+)" + Regex.Escape(oldName) + @"([ \t]+)([^\r\n]*?)([ \t]*//[^\r\n]*)?(\r?)$", RegexOptions.Multiline);
        var matches = rx.Matches(text);
        if (matches.Count != 1) throw new InvalidOperationException($"{oldName} was found {matches.Count} times (expected exactly once).");
        if (oldName != newName && Regex.IsMatch(text, @"\b" + Regex.Escape(newName) + @"\b"))
            throw new InvalidOperationException($"{newName} already exists.");
        var m = matches[0];
        int valueColumn = oldName.Length + m.Groups[2].Value.Length;
        string gap = newName.Length < valueColumn ? new string(' ', valueColumn - newName.Length) : " ";
        string value = m.Groups[3].Value.TrimEnd();
        string tail;
        if (comment == null) tail = m.Groups[4].Value;
        else if (comment.Trim().Length == 0) tail = "";
        else
        {
            var old = m.Groups[4].Value;
            int pad = old.Length > 0 ? old.Length - old.TrimStart().Length : 1;
            tail = new string(' ', Math.Max(1, pad)) + "// " + comment.Trim().Replace("\r", " ").Replace("\n", " ");
        }
        var line = m.Groups[1].Value + newName + gap + value + tail + m.Groups[5].Value;
        return text[..m.Index] + line + text[(m.Index + m.Length)..];
    }
}
