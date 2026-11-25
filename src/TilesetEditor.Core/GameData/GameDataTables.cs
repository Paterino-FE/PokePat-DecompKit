using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using TilesetEditor.Core.Project;
using TilesetEditor.Core.Trainers;

namespace TilesetEditor.Core.GameData;

public enum GameDataKind { Text, Number, Constant }

/// <summary>Fields that hold several values shown as separate columns.</summary>
public enum GameDataComposite
{
    None,
    /// <summary>MON_TYPES(a) / MON_TYPES(a, b) or { a, b }.</summary>
    MonTypes,
    /// <summary>{ ABILITY_A, ABILITY_B, ABILITY_HIDDEN }.</summary>
    AbilityList,
}

public enum GameDataSource { CTable, MapSectionsJson }

/// <summary>A column of a game data table: which field, how it is shown and which values are allowed.</summary>
public sealed record GameDataColumn(string Header, string Field, GameDataKind Kind)
{
    /// <summary>Part of a composite field (0-based), -1 = whole field.</summary>
    public int Part { get; init; } = -1;
    public GameDataComposite Composite { get; init; }
    /// <summary>#define giving the maximum text length (e.g. ITEM_NAME_LENGTH), plus <see cref="LengthOffset"/>.</summary>
    public string? LengthDefine { get; init; }
    public int LengthOffset { get; init; }
    /// <summary>Alternative: a file and a regex whose group 1 is the array size (e.g. "u8 name[13]"), plus <see cref="LengthOffset"/>.</summary>
    public (string File, string Pattern)? LengthPattern { get; init; }
    public int FallbackLength { get; init; }
    /// <summary>Exceeding the length is only a warning (pointer strings without fixed buffer).</summary>
    public bool SoftLength { get; init; }
    public bool LineBreaks { get; init; }
    /// <summary>Text fields may name a "static const u8 sX[] = _(…)" string of the same file.</summary>
    public bool SharedStrings { get; init; }
    public long Min { get; init; }
    public long Max { get; init; } = long.MaxValue;
    public string? ConstantPrefix { get; init; }
    public string? OptionsFile { get; init; }
    public string? OptionsFilter { get; init; }
    /// <summary>Own regex (group 1 = constant) for <see cref="OptionsFile"/>, instead of the default #define / enum pattern.</summary>
    public string? OptionsPattern { get; init; }
    /// <summary>Positional fields: a missing value may be added after the existing ones.</summary>
    public bool AppendIfMissing { get; init; }
    public string FillerIfMissing { get; init; } = "0";
    public string? MissingNote { get; init; }
    public double Width { get; init; } = 110;
    public string? Hint { get; init; }
}

/// <summary>A game data table: source file(s), table variable, key prefix and columns.</summary>
public sealed record GameDataTableDef(string Id, string Title, string Files, string? Marker, string KeyPrefix, IReadOnlyList<GameDataColumn> Columns)
{
    public GameDataSource Source { get; init; } = GameDataSource.CTable;
    public string Note { get; init; } = "";
    public bool CanAdd { get; init; }
}

public sealed class GameDataCell
{
    public required string Original { get; init; }
    public string Value { get; set; } = "";
    public bool Editable { get; init; }
    /// <summary>Explains why the value is read-only, or where it comes from.</summary>
    public string? Note { get; init; }
    public bool IsChanged => Value != Original;
}

public sealed class GameDataRow
{
    public required string Key { get; init; }
    public required string File { get; init; }
    public int Line { get; init; }
    public required GameDataCell[] Cells { get; init; }
    public string? ReadOnlyReason { get; set; }
    /// <summary>Field id → raw value as read (null = field missing), the precondition for writing.</summary>
    public Dictionary<string, string?> FieldRaw { get; } = [];
    public bool IsChanged => Cells.Any(c => c.IsChanged);
}

public sealed class GameDataTable
{
    public required GameDataTableDef Def { get; init; }
    public required List<GameDataRow> Rows { get; init; }
    public required IReadOnlyList<string> Files { get; init; }
    /// <summary>Allowed values for constant columns (null for other columns).</summary>
    public required IReadOnlyList<IReadOnlyList<string>?> Options { get; init; }
    /// <summary>Maximum text length per column (0 = none).</summary>
    public required int[] MaxLength { get; init; }
    public required GameText Text { get; init; }

    public IEnumerable<GameDataRow> ChangedRows => Rows.Where(r => r.IsChanged);

    public TextCheck Validate(GameDataRow row, int column)
    {
        var col = Def.Columns[column];
        var v = row.Cells[column].Value;
        switch (col.Kind)
        {
            case GameDataKind.Text:
                var check = Text.Check(v, MaxLength[column], col.SoftLength, col.LineBreaks, col.Header);
                if (check.Error == null && Def.Source == GameDataSource.MapSectionsJson)
                {
                    var clean = GameDataCatalog.CleanString(v);
                    var clash = Rows.FirstOrDefault(r => r != row && r.Cells[column].Value != v && GameDataCatalog.CleanString(r.Cells[column].Value) == clean);
                    if (clash != null)
                        return new TextCheck($"\"{v}\" and \"{clash.Cells[column].Value}\" ({clash.Key}) would get the same C name sMapName_{clean} in the build. Choose a name that differs in letters or digits.", null);
                }
                return check;
            case GameDataKind.Number:
                if (!long.TryParse(v, NumberStyles.None, CultureInfo.InvariantCulture, out var n))
                    return new TextCheck($"{col.Header} must be a whole number.", null);
                if (n < col.Min || n > col.Max) return new TextCheck($"{col.Header} must be between {col.Min} and {col.Max}.", null);
                return TextCheck.Ok;
            default:
                var options = Options[column];
                if (v.Length == 0) return new TextCheck($"{col.Header} must not be empty.", null);
                if (options is { Count: > 0 } && !options.Contains(v)) return new TextCheck($"{v} is not a known {col.ConstantPrefix} constant.", null);
                if (!Regex.IsMatch(v, @"^[A-Za-z_]\w*$")) return new TextCheck($"{v} is not a constant name.", null);
                return TextCheck.Ok;
        }
    }
}

public sealed class GameDataConflictException(string message) : Exception(message);

/// <summary>The supported tables, loading them into rows and writing changed rows back in place.</summary>
public static partial class GameDataCatalog
{
    private const string TypesFile = "include/constants/pokemon.h";

    public static IReadOnlyList<GameDataTableDef> Tables { get; } =
    [
        new("trainer_classes", "Trainer classes", "src/battle_main.c", "gTrainerClasses", "TRAINER_CLASS_",
        [
            new("Name", "#0", GameDataKind.Text) { LengthPattern = ("include/data.h", @"struct\s+TrainerClass\s*\{\s*u8\s+name\[(\d+)\]"), LengthOffset = -1, FallbackLength = 12, Width = 150 },
            new("Prize money", "#1", GameDataKind.Number) { Min = 0, Max = 255, AppendIfMissing = true, MissingNote = "Not set – the game uses 5.", Width = 90,
                Hint = "Base money; the game multiplies it with the level of the trainer's last Pokémon (and more for double battles)." },
            new("Poké Ball", "#2", GameDataKind.Constant) { ConstantPrefix = "BALL_", OptionsFile = "include/pokeball.h", OptionsPattern = @"^[ \t]*(BALL_\w+)[ \t]*=[ \t]*\d+[ \t]*,", AppendIfMissing = true, Width = 150,
                MissingNote = "Not set – Poké Ball. Only used if B_TRAINER_CLASS_POKE_BALLS >= GEN_7 (include/config/battle.h).",
                Hint = "Ball of the class's Pokémon; only used if B_TRAINER_CLASS_POKE_BALLS >= GEN_7." },
        ])
        {
            CanAdd = true,
            Note = "Name and prize money of each trainer class (src/battle_main.c, gTrainerClasses). \"Add trainer class…\" adds the constant in include/constants/trainers.h (TRAINER_CLASS_COUNT + 1) and a table entry; then pick it for trainers in the Trainers module.",
        },
        new("items", "Items", "src/data/items.h", "gItemsInfo", "ITEM_",
        [
            new("Name", "name", GameDataKind.Text) { LengthDefine = "ITEM_NAME_LENGTH", LengthOffset = -1, FallbackLength = 19, Width = 160 },
            new("Price", "price", GameDataKind.Number) { Min = 0, Max = 999999, Width = 80 },
            new("Description", "description", GameDataKind.Text) { LineBreaks = true, SharedStrings = true, Width = 420, Hint = "\\n = new line. The bag shows about 3 short lines." },
        ])
        {
            Note = "Adding items is not supported here: a new item needs a constant (ITEMS_COUNT), icon graphics, pocket and use functions and more. Prices written as expressions (e.g. depending on I_PRICE) are read-only. A description shared by several items is copied for the edited item only.",
        },
        new("moves", "Moves", "src/data/moves_info.h", "gMovesInfo", "MOVE_",
        [
            new("Name", "name", GameDataKind.Text) { LengthDefine = "MOVE_NAME_LENGTH", FallbackLength = 16, SoftLength = true, Width = 140,
                Hint = "Longer names than MOVE_NAME_LENGTH are allowed (Z-/Max moves use them) but may be cut off in menus." },
            new("Type", "type", GameDataKind.Constant) { ConstantPrefix = "TYPE_", OptionsFile = TypesFile, OptionsFilter = "^TYPE_(?!NONE$)", Width = 120 },
            new("Category", "category", GameDataKind.Constant) { ConstantPrefix = "DAMAGE_CATEGORY_", OptionsFile = TypesFile, Width = 190 },
            new("Power", "power", GameDataKind.Number) { Min = 0, Max = 511, Width = 60, MissingNote = "Not set (0)." },
            new("Accuracy", "accuracy", GameDataKind.Number) { Min = 0, Max = 100, Width = 70, MissingNote = "Not set (0 = never misses).", Hint = "0 = never misses." },
            new("PP", "pp", GameDataKind.Number) { Min = 0, Max = 64, Width = 50, MissingNote = "Not set (0)." },
            new("Description", "description", GameDataKind.Text) { LineBreaks = true, SharedStrings = true, Width = 380, Hint = "\\n = new line." },
        ])
        {
            Note = "Adding moves is not supported here: a move needs a constant, a battle effect, an animation script, contest data and learnsets. Values written as expressions (e.g. B_UPDATED_MOVE_DATA >= GEN_6 ? 90 : 95) or inside #if are read-only.",
        },
        new("abilities", "Abilities", "src/data/abilities.h", "gAbilitiesInfo", "ABILITY_",
        [
            new("Name", "name", GameDataKind.Text) { LengthDefine = "ABILITY_NAME_LENGTH", FallbackLength = 16, Width = 150 },
            new("Description", "description", GameDataKind.Text) { SharedStrings = true, Width = 380 },
        ])
        {
            Note = "Adding abilities is not supported here: the effect of an ability is battle code. Only names and descriptions can be changed.",
        },
        new("species", "Pokémon", "src/data/pokemon/species_info/gen_*_families.h", null, "SPECIES_",
        [
            new("Name", "speciesName", GameDataKind.Text) { LengthDefine = "POKEMON_NAME_LENGTH", FallbackLength = 12, Width = 120 },
            new("HP", "baseHP", GameDataKind.Number) { Min = 1, Max = 255, Width = 45 },
            new("Atk", "baseAttack", GameDataKind.Number) { Min = 1, Max = 255, Width = 45 },
            new("Def", "baseDefense", GameDataKind.Number) { Min = 1, Max = 255, Width = 45 },
            new("SpA", "baseSpAttack", GameDataKind.Number) { Min = 1, Max = 255, Width = 45 },
            new("SpD", "baseSpDefense", GameDataKind.Number) { Min = 1, Max = 255, Width = 45 },
            new("Spe", "baseSpeed", GameDataKind.Number) { Min = 1, Max = 255, Width = 45 },
            new("Type 1", "types", GameDataKind.Constant) { Part = 0, Composite = GameDataComposite.MonTypes, ConstantPrefix = "TYPE_", OptionsFile = TypesFile, OptionsFilter = "^TYPE_(?!NONE$|MYSTERY$|STELLAR$)", Width = 115 },
            new("Type 2", "types", GameDataKind.Constant) { Part = 1, Composite = GameDataComposite.MonTypes, ConstantPrefix = "TYPE_", OptionsFile = TypesFile, OptionsFilter = "^TYPE_(?!NONE$|MYSTERY$|STELLAR$)", Width = 115,
                Hint = "Same as Type 1 = single type." },
            new("Ability 1", "abilities", GameDataKind.Constant) { Part = 0, Composite = GameDataComposite.AbilityList, ConstantPrefix = "ABILITY_", OptionsFile = "include/constants/abilities.h", Width = 165 },
            new("Ability 2", "abilities", GameDataKind.Constant) { Part = 1, Composite = GameDataComposite.AbilityList, ConstantPrefix = "ABILITY_", OptionsFile = "include/constants/abilities.h", Width = 165 },
            new("Hidden ability", "abilities", GameDataKind.Constant) { Part = 2, Composite = GameDataComposite.AbilityList, ConstantPrefix = "ABILITY_", OptionsFile = "include/constants/abilities.h", Width = 165 },
        ])
        {
            Note = "Adding Pokémon is not supported here: a species needs graphics, cry, learnsets, evolutions, Pokédex data and more. Forms defined by macros, values inside #if (e.g. P_UPDATED_STATS) and family-wide macros (e.g. CLEFAIRY_FAMILY_TYPES) are read-only.",
        },
        new("types", "Types", "src/data/types_info.h", "gTypesInfo", "TYPE_",
        [
            new("Name", "name", GameDataKind.Text) { LengthDefine = "TYPE_NAME_LENGTH", FallbackLength = 8, Width = 120 },
        ])
        {
            Note = "Only the type names. Adding a type changes the type chart, icons and many tables – not supported here.",
        },
        new("natures", "Natures", "src/pokemon.c", "gNaturesInfo", "NATURE_",
        [
            new("Name", "name", GameDataKind.Text) { FallbackLength = 12, SoftLength = true, Width = 120 },
            new("Raises", "statUp", GameDataKind.Constant) { ConstantPrefix = "STAT_", OptionsFile = TypesFile, OptionsFilter = "^STAT_(ATK|DEF|SPEED|SPATK|SPDEF)$", Width = 110 },
            new("Lowers", "statDown", GameDataKind.Constant) { ConstantPrefix = "STAT_", OptionsFile = TypesFile, OptionsFilter = "^STAT_(ATK|DEF|SPEED|SPATK|SPDEF)$", Width = 110 },
        ])
        {
            Note = "Nature names and the stat each nature raises / lowers (the same stat in both = neutral nature). Adding natures is not supported (NUM_NATURES is used by save data and many tables).",
        },
        new("locations", "Locations", "src/data/region_map/region_map_sections.json", null, "MAPSEC_",
        [
            new("Name", "name", GameDataKind.Text) { LengthDefine = "MAP_NAME_LENGTH", FallbackLength = 16, Width = 180 },
        ])
        {
            Source = GameDataSource.MapSectionsJson,
            Note = "Names of the region map sections (shown on the region map and when entering a map). The build generates the C table from region_map_sections.json. New map sections are added in Porymap (region map editor).",
        },
    ];

    public static GameDataTableDef Get(string id) => Tables.First(t => t.Id == id);

    // ---------------------------------------------------------------- loading

    /// <summary>Project-relative source files of a table ("*" in a file name expands, sorted by name).</summary>
    public static IReadOnlyList<string> ResolveFiles(string root, GameDataTableDef def)
    {
        var result = new List<string>();
        foreach (var part in def.Files.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!part.Contains('*')) { if (File.Exists(ProjectPaths.Combine(root, part))) result.Add(part); continue; }
            var dir = part[..part.LastIndexOf('/')];
            var full = ProjectPaths.Combine(root, dir);
            if (!Directory.Exists(full)) continue;
            result.AddRange(Directory.GetFiles(full, part[(part.LastIndexOf('/') + 1)..])
                .Select(f => dir + "/" + Path.GetFileName(f)).OrderBy(f => f, StringComparer.Ordinal));
        }
        return result;
    }

    public static GameDataTable Load(string root, GameDataTableDef def, GameText? text = null)
    {
        text ??= GameText.Load(root);
        var files = ResolveFiles(root, def);
        if (files.Count == 0) throw new FileNotFoundException($"{def.Files} was not found in the project.");
        var rows = new List<GameDataRow>();
        foreach (var file in files)
        {
            var content = ReadText(root, file);
            if (def.Source == GameDataSource.MapSectionsJson) rows.AddRange(LoadMapSections(def, file, content));
            else rows.AddRange(LoadCTable(def, file, CTable.Parse(content, def.Marker, def.KeyPrefix)));
        }
        foreach (var g in rows.GroupBy(r => r.Key).Where(g => g.Count() > 1))
            foreach (var r in g) r.ReadOnlyReason ??= "Defined more than once – edit it in the source files.";
        var options = def.Columns.Select((c, i) => c.Kind == GameDataKind.Constant ? (IReadOnlyList<string>?)Options(root, c, rows, i) : null).ToList();
        var maxLength = def.Columns.Select(c => c.Kind == GameDataKind.Text ? MaxLength(root, c) : 0).ToArray();
        return new GameDataTable { Def = def, Rows = rows, Files = files, Options = options, MaxLength = maxLength, Text = text };
    }

    public static string ReadText(string root, string rel) => new UTF8Encoding(false).GetString(File.ReadAllBytes(ProjectPaths.Combine(root, rel)));

    private static IEnumerable<GameDataRow> LoadCTable(GameDataTableDef def, string file, CTableFile parsed)
    {
        foreach (var e in parsed.Entries)
        {
            var row = new GameDataRow { Key = e.Key, File = file, Line = e.Line, Cells = new GameDataCell[def.Columns.Count], ReadOnlyReason = e.ReadOnlyReason };
            for (int i = 0; i < def.Columns.Count; i++)
            {
                var col = def.Columns[i];
                var f = e.Field(col.Field);
                row.FieldRaw[col.Field] = f?.Raw;
                row.Cells[i] = ReadCell(col, f, e, parsed);
            }
            yield return row;
        }
    }

    private static string Collapse(string raw) => Regex.Replace(raw, @"\s+", " ");

    private static GameDataCell ReadCell(GameDataColumn col, CTableField? f, CTableEntry e, CTableFile parsed)
    {
        if (f == null)
        {
            bool canAppend = col.AppendIfMissing && e.Positional && e.Fields.Count > 0;
            return new GameDataCell { Original = "", Value = "", Editable = canAppend, Note = col.MissingNote ?? "Not set in this entry (or set by a macro)." };
        }
        string shown = col.Composite != GameDataComposite.None && SplitComposite(col.Composite, f.Raw) is { } ps ? ps[col.Part] : Collapse(f.Raw);
        if (f.Conditional)
            return new GameDataCell { Original = shown, Value = shown, Note = "Inside #if – the value depends on the configuration. Edit it in the source file." };
        switch (col.Kind)
        {
            case GameDataKind.Text:
                if (CString.TryRead(f.Raw) is { } s) return new GameDataCell { Original = s, Value = s, Editable = true };
                if (col.SharedStrings && Regex.IsMatch(f.Raw, @"^\w+$") && parsed.Strings.TryGetValue(f.Raw, out var st) && !st.Conditional && CString.TryRead(st.Raw) is { } shared)
                {
                    int refs = parsed.References(f.Raw);
                    var note = refs > 1
                        ? $"Shared text {f.Raw} (used {refs} times). Changing it here gives this entry its own copy."
                        : $"Text {f.Raw} defined above the table.";
                    return new GameDataCell { Original = shared, Value = shared, Editable = true, Note = note };
                }
                return new GameDataCell { Original = shown, Value = shown, Note = "Not a plain text (macro or variable) – edit it in the source file." };
            case GameDataKind.Number:
                if (Regex.IsMatch(f.Raw, @"^\d+$")) return new GameDataCell { Original = f.Raw, Value = f.Raw, Editable = true };
                if (Regex.IsMatch(f.Raw, @"^0[xX][0-9A-Fa-f]+$"))
                {
                    var d = Convert.ToInt64(f.Raw[2..], 16).ToString(CultureInfo.InvariantCulture);
                    return new GameDataCell { Original = d, Value = d, Editable = true, Note = "Written as hex number " + f.Raw + "." };
                }
                return new GameDataCell { Original = shown, Value = shown, Note = "Expression (e.g. depends on a config option) – edit it in the source file." };
            default:
                if (col.Composite != GameDataComposite.None)
                {
                    if (SplitComposite(col.Composite, f.Raw) is { } parts && parts.All(p => p.StartsWith(col.ConstantPrefix ?? "", StringComparison.Ordinal)))
                        return new GameDataCell { Original = parts[col.Part], Value = parts[col.Part], Editable = true };
                    return new GameDataCell { Original = shown, Value = shown, Note = "Macro or expression – edit it in the source file." };
                }
                if (Regex.IsMatch(f.Raw, @"^" + Regex.Escape(col.ConstantPrefix ?? "") + @"\w+$"))
                    return new GameDataCell { Original = f.Raw, Value = f.Raw, Editable = true };
                return new GameDataCell { Original = shown, Value = shown, Note = "Expression (e.g. depends on a config option) – edit it in the source file." };
        }
    }

    [GeneratedRegex(@"^MON_TYPES\(\s*(\w+)\s*(?:,\s*(\w+)\s*)?\)$")]
    private static partial Regex MonTypes();

    [GeneratedRegex(@"^\{\s*(\w+)\s*(?:,\s*(\w+)\s*)?(?:,\s*(\w+)\s*)?,?\s*\}$")]
    private static partial Regex BraceList();

    /// <summary>The parts of a composite field, or null if it is not in a simple form.</summary>
    public static string[]? SplitComposite(GameDataComposite kind, string raw)
    {
        raw = raw.Trim();
        if (kind == GameDataComposite.MonTypes)
        {
            var m = MonTypes().Match(raw);
            if (!m.Success) m = BraceList().Match(raw);
            if (!m.Success || m.Groups[3].Success) return null;
            var a = m.Groups[1].Value;
            return [a, m.Groups[2].Success ? m.Groups[2].Value : a];
        }
        if (kind == GameDataComposite.AbilityList)
        {
            var m = BraceList().Match(raw);
            if (!m.Success) return null;
            return [m.Groups[1].Value, m.Groups[2].Success ? m.Groups[2].Value : "ABILITY_NONE", m.Groups[3].Success ? m.Groups[3].Value : "ABILITY_NONE"];
        }
        return null;
    }

    /// <summary>A composite field with new parts, in the notation of <paramref name="originalRaw"/>.</summary>
    public static string JoinComposite(GameDataComposite kind, string originalRaw, IReadOnlyList<string> parts)
    {
        bool spaced = originalRaw.StartsWith("{ ", StringComparison.Ordinal);
        string Brace(IEnumerable<string> p) => spaced ? "{ " + string.Join(", ", p) + " }" : "{" + string.Join(", ", p) + "}";
        if (kind == GameDataComposite.MonTypes)
        {
            if (originalRaw.TrimStart().StartsWith('{')) return Brace([parts[0], parts[1]]);
            return parts[1] == parts[0] || parts[1].Length == 0 ? $"MON_TYPES({parts[0]})" : $"MON_TYPES({parts[0]}, {parts[1]})";
        }
        return Brace(parts);
    }

    private static List<string> Options(string root, GameDataColumn col, List<GameDataRow> rows, int index)
    {
        var result = new List<string>();
        var prefix = col.ConstantPrefix ?? "";
        if (col.OptionsFile != null && File.Exists(ProjectPaths.Combine(root, col.OptionsFile)))
        {
            var t = ReadText(root, col.OptionsFile);
            var rx = col.OptionsPattern != null ? new Regex(col.OptionsPattern, RegexOptions.Multiline) : new Regex(@"^[ \t]*(?:#define[ \t]+(" + Regex.Escape(prefix) + @"\w+)[ \t]+\S|(" + Regex.Escape(prefix) + @"\w+)[ \t]*(?:=[^,\r\n]*)?,)", RegexOptions.Multiline);
            foreach (Match m in rx.Matches(t))
            {
                var name = m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value;
                if (name.Length == 0) continue;
                if (name.Contains("COUNT") || result.Contains(name)) continue;
                result.Add(name);
            }
        }
        foreach (var r in rows)
        {
            var c = r.Cells[index];
            if (c.Editable && c.Original.Length > 0 && !result.Contains(c.Original)) result.Add(c.Original);
        }
        if (col.OptionsFilter != null)
        {
            var keep = new Regex(col.OptionsFilter);
            var used = rows.Where(r => r.Cells[index].Editable).Select(r => r.Cells[index].Original).ToHashSet();
            result = result.Where(o => keep.IsMatch(o) || used.Contains(o)).ToList();
        }
        return result;
    }

    private static int MaxLength(string root, GameDataColumn col)
    {
        try
        {
            if (col.LengthDefine != null)
            {
                var defines = new CDefines();
                foreach (var f in new[] { "include/constants/global.h", "include/region_map.h", "include/global.h" })
                {
                    var p = ProjectPaths.Combine(root, f);
                    if (File.Exists(p)) defines.AddFromText(File.ReadAllText(p));
                }
                if (defines.TryEvaluate(col.LengthDefine) is { } v) return (int)v + col.LengthOffset;
            }
            if (col.LengthPattern is { } lp && File.Exists(ProjectPaths.Combine(root, lp.File)))
            {
                var m = Regex.Match(File.ReadAllText(ProjectPaths.Combine(root, lp.File)), lp.Pattern);
                if (m.Success && int.TryParse(m.Groups[1].Value, out var n)) return n + col.LengthOffset;
            }
        }
        catch (IOException) { }
        return col.FallbackLength;
    }

    // ---------------------------------------------------------------- writing

    /// <summary>The project-relative files that have changed rows.</summary>
    public static IEnumerable<string> ChangedFiles(GameDataTable table) => table.ChangedRows.Select(r => r.File).Distinct();

    /// <summary>Returns <paramref name="text"/> (current content of <paramref name="file"/>) with the changed rows of the table written in.</summary>
    public static string Write(GameDataTable table, string file, string text)
    {
        var rows = table.ChangedRows.Where(r => r.File == file).ToList();
        if (rows.Count == 0) return text;
        return table.Def.Source == GameDataSource.MapSectionsJson ? WriteMapSections(table, rows, text) : WriteCTable(table, rows, text);
    }

    private static string WriteCTable(GameDataTable table, List<GameDataRow> rows, string text)
    {
        var def = table.Def;
        var parsed = CTable.Parse(text, def.Marker, def.KeyPrefix);
        var edits = new List<(int, int, string)>();
        foreach (var row in rows)
        {
            if (row.ReadOnlyReason != null) throw new GameDataConflictException($"{row.Key}: {row.ReadOnlyReason}");
            var entry = parsed.Entry(row.Key) ?? throw new GameDataConflictException($"{row.Key} is no longer in {row.File}. Reload and try again.");
            var appends = new SortedDictionary<int, string>();
            foreach (var group in def.Columns.Select((c, i) => (c, i)).Where(x => row.Cells[x.i].IsChanged).GroupBy(x => x.c.Field))
            {
                var id = group.Key;
                var field = entry.Field(id);
                var expected = row.FieldRaw.GetValueOrDefault(id);
                if (field?.Raw != expected)
                    throw new GameDataConflictException($"{row.Key}: {id} was changed outside the toolkit since it was loaded. Reload and try again.");
                var (col, idx) = group.First();
                if (field == null)
                {
                    if (!(col.AppendIfMissing && entry.Positional && col.Field.StartsWith('#')))
                        throw new GameDataConflictException($"{row.Key}: {col.Header} is not set in the file and cannot be added here.");
                    appends[int.Parse(col.Field[1..], CultureInfo.InvariantCulture)] = NewRaw(col, row.Cells[idx].Value, null);
                    continue;
                }
                if (field.Conditional) throw new GameDataConflictException($"{row.Key}: {col.Header} is inside #if and cannot be changed here.");
                if (col.Composite != GameDataComposite.None)
                {
                    var parts = SplitComposite(col.Composite, field.Raw) ?? throw new GameDataConflictException($"{row.Key}: {id} is not in a simple form.");
                    for (int i = 0; i < def.Columns.Count; i++)
                        if (def.Columns[i].Field == id && def.Columns[i].Part >= 0) parts[def.Columns[i].Part] = row.Cells[i].Value;
                    edits.Add((field.ValueStart, field.ValueEnd, JoinComposite(col.Composite, field.Raw, parts)));
                    continue;
                }
                var value = row.Cells[idx].Value;
                if (col.Kind == GameDataKind.Text && CString.TryRead(field.Raw) == null)
                {
                    // A named string ("sFullHealDesc"): edit it in place if only this entry uses it, otherwise give the entry its own text.
                    if (!parsed.Strings.TryGetValue(field.Raw, out var st) || CString.TryRead(st.Raw) != row.Cells[idx].Original)
                        throw new GameDataConflictException($"{row.Key}: the text {field.Raw} was changed outside the toolkit. Reload and try again.");
                    if (st.IsStatic && parsed.References(st.Name) == 1)
                        edits.Add((st.ValueStart, st.ValueEnd, CString.Format(value, st.Raw, text, st.ValueStart)));
                    else
                        edits.Add((field.ValueStart, field.ValueEnd, CString.Format(value, "COMPOUND_STRING(\"\")", text, field.ValueStart)));
                    continue;
                }
                edits.Add((field.ValueStart, field.ValueEnd, NewRaw(col, value, field.Raw, text, field.ValueStart)));
            }
            if (appends.Count > 0)
            {
                var last = entry.Fields[^1];
                var sb = new StringBuilder();
                for (int i = last.Index + 1; i <= appends.Keys.Max(); i++)
                {
                    string v = appends.TryGetValue(i, out var a) ? a : def.Columns.FirstOrDefault(c => c.Field == "#" + i)?.FillerIfMissing ?? "0";
                    sb.Append(", ").Append(v);
                }
                edits.Add((last.ValueEnd, last.ValueEnd, sb.ToString()));
            }
        }
        return CTable.ApplyEdits(text, edits);
    }

    private static string NewRaw(GameDataColumn col, string value, string? originalRaw, string? fileText = null, int rawStart = 0)
    {
        switch (col.Kind)
        {
            case GameDataKind.Text:
                return originalRaw != null && fileText != null ? CString.Format(value, originalRaw, fileText, rawStart) : "_(\"" + CString.Encode(value) + "\")";
            case GameDataKind.Number:
                if (originalRaw != null && originalRaw.StartsWith("0x", StringComparison.OrdinalIgnoreCase) && long.TryParse(value, out var n))
                {
                    bool upper = originalRaw.Skip(2).Any(char.IsUpper);
                    return originalRaw[..2] + n.ToString(upper ? "X" : "x", CultureInfo.InvariantCulture);
                }
                return value;
            default:
                return value;
        }
    }

    // ---------------------------------------------------------------- region map sections (JSON)

    private sealed record MapSection(string Id, int ObjectStart, int NameStart, int NameEnd, string? Name, int Line);

    [GeneratedRegex(@"""id""\s*:\s*""(\w+)""")]
    private static partial Regex JsonId();

    [GeneratedRegex(@"""name""\s*:\s*""((?:[^""\\]|\\.)*)""")]
    private static partial Regex JsonName();

    private static List<MapSection> ParseMapSections(string text, string prefix)
    {
        var lines = CTable.LineStarts(text);
        var result = new List<MapSection>();
        foreach (Match m in JsonId().Matches(text))
        {
            if (!m.Groups[1].Value.StartsWith(prefix, StringComparison.Ordinal)) continue;
            int open = text.LastIndexOf('{', m.Index);
            int close = open < 0 ? -1 : TrainerFile.Match(text, open);
            if (close < m.Index) continue;
            var n = JsonName().Match(text, open);
            bool has = n.Success && n.Index < close;
            result.Add(new MapSection(m.Groups[1].Value, open, has ? n.Groups[1].Index : -1, has ? n.Groups[1].Index + n.Groups[1].Length : -1,
                has ? JsonSerializer.Deserialize<string>("\"" + n.Groups[1].Value + "\"") : null, CTable.LineOf(lines, m.Index)));
        }
        return result;
    }

    private static IEnumerable<GameDataRow> LoadMapSections(GameDataTableDef def, string file, string text)
    {
        foreach (var s in ParseMapSections(text, def.KeyPrefix))
        {
            var cell = s.Name != null
                ? new GameDataCell { Original = s.Name, Value = s.Name, Editable = true }
                : new GameDataCell { Original = "", Value = "", Note = "This section has no name in the JSON file." };
            var row = new GameDataRow { Key = s.Id, File = file, Line = s.Line, Cells = [cell] };
            row.FieldRaw["name"] = s.Name;
            yield return row;
        }
    }

    private static string WriteMapSections(GameDataTable table, List<GameDataRow> rows, string text)
    {
        var sections = ParseMapSections(text, table.Def.KeyPrefix).GroupBy(s => s.Id).ToDictionary(g => g.Key, g => g.ToList());
        var edits = new List<(int, int, string)>();
        foreach (var row in rows)
        {
            if (!sections.TryGetValue(row.Key, out var list) || list.Count != 1) throw new GameDataConflictException($"{row.Key} is no longer unique in {row.File}. Reload and try again.");
            var s = list[0];
            if (s.Name == null || s.Name != row.FieldRaw["name"]) throw new GameDataConflictException($"{row.Key}: the name was changed outside the toolkit. Reload and try again.");
            var escaped = row.Cells[0].Value.Replace("\\", "\\\\").Replace("\"", "\\\"");
            edits.Add((s.NameStart, s.NameEnd, escaped));
        }
        return CTable.ApplyEdits(text, edits);
    }

    /// <summary>What jsonproc's cleanString makes of a name (the C variable sMapName_…): every byte that is not an ASCII letter or digit becomes '_'.</summary>
    public static string CleanString(string name)
    {
        var bytes = Encoding.UTF8.GetBytes(name);
        var sb = new StringBuilder();
        for (int i = 0; i < bytes.Length; i++)
        {
            byte b = bytes[i];
            bool alnum = b < 128 && char.IsAsciiLetterOrDigit((char)b);
            sb.Append(alnum && !(i == 0 && char.IsAsciiDigit((char)b)) ? (char)b : '_');
        }
        return sb.ToString();
    }
}
