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

public enum GameDataSource
{
    CTable,
    MapSectionsJson,
    /// <summary>pokeplatinum: one JSON file per entry (res/pokemon/*/data.json, res/items/data/*.json …).</summary>
    JsonFiles,
    /// <summary>pokeplatinum: a text bank (res/text/*.json), one message per entry.</summary>
    TextBank,
}

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
    /// <summary>Header(s) listing the allowed constants; several separated by ';'.</summary>
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
    /// <summary>pokeplatinum: the value is a JSON boolean (true/false) instead of a string.</summary>
    public bool JsonBool { get; init; }
    /// <summary>pokeplatinum: a text bank (project-relative) this column reads from instead of the row's file.</summary>
    public string? SourceFile { get; init; }
}

/// <summary>A game data table: source file(s), table variable, key prefix and columns.</summary>
public sealed record GameDataTableDef(string Id, string Title, string Files, string? Marker, string KeyPrefix, IReadOnlyList<GameDataColumn> Columns)
{
    public GameDataSource Source { get; init; } = GameDataSource.CTable;
    public string Note { get; init; } = "";
    public bool CanAdd { get; init; }
    /// <summary>Headers of the columns whose values group the entries (pocket, type …) for the category filter.</summary>
    public IReadOnlyList<string> CategoryColumns { get; init; } = [];
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
    /// <summary>pokeplatinum text banks: the JSON path of the row's message ("messages[12]."), put before each column's field.</summary>
    public string PathPrefix { get; init; } = "";
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
    /// <summary>Decides config-dependent values (ternaries, #if blocks); null = such values stay read-only.</summary>
    public ConfigEvaluator? Config { get; init; }

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
            new("Poké Ball", "#2", GameDataKind.Constant) { ConstantPrefix = "BALL_", OptionsFile = "include/pokeball.h;include/constants/pokeball.h", OptionsPattern = @"^[ \t]*(BALL_\w+)[ \t]*=[ \t]*\d+[ \t]*,", AppendIfMissing = true, Width = 150,
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
            new("Pocket", "pocket", GameDataKind.Constant) { ConstantPrefix = "POCKET_", OptionsFile = "include/constants/item.h", OptionsFilter = "^POCKET_(?!DUMMY$)", Width = 140,
                Hint = "Bag pocket the item goes into." },
            new("Kind", "sortType", GameDataKind.Constant) { ConstantPrefix = "ITEM_TYPE_", OptionsFile = "include/item.h", Width = 190,
                MissingNote = "Not set (only expansion 1.13+ sorts items by kind).", Hint = "Sort order inside the pocket (expansion 1.13+)." },
        ])
        {
            CategoryColumns = ["Pocket", "Kind"],
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
            CategoryColumns = ["Type", "Category"],
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
            CategoryColumns = ["Type 1", "Type 2"],
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

    public static GameDataTable Load(string root, GameDataTableDef def, GameText? text = null, ConfigEvaluator? config = null)
    {
        text ??= GameText.Load(root);
        config ??= ConfigEvaluator.Load(root);
        var files = ResolveFiles(root, def);
        if (files.Count == 0) throw new FileNotFoundException($"{def.Files} was not found in the project.");
        var rows = new List<GameDataRow>();
        foreach (var file in files)
        {
            var content = ReadText(root, file);
            if (def.Source == GameDataSource.MapSectionsJson) rows.AddRange(LoadMapSections(def, file, content));
            else rows.AddRange(LoadCTable(def, file, CTable.Parse(content, def.Marker, def.KeyPrefix), config));
        }
        foreach (var g in rows.GroupBy(r => r.Key).Where(g => g.Count() > 1))
            foreach (var r in g) r.ReadOnlyReason ??= "Defined more than once – edit it in the source files.";
        var options = def.Columns.Select((c, i) => c.Kind == GameDataKind.Constant ? (IReadOnlyList<string>?)Options(root, c, rows, i) : null).ToList();
        var maxLength = def.Columns.Select(c => c.Kind == GameDataKind.Text ? MaxLength(root, c) : 0).ToArray();
        return new GameDataTable { Def = def, Rows = rows, Files = files, Options = options, MaxLength = maxLength, Text = text, Config = config };
    }

    public static string ReadText(string root, string rel) => new UTF8Encoding(false).GetString(File.ReadAllBytes(ProjectPaths.Combine(root, rel)));

    private static IEnumerable<GameDataRow> LoadCTable(GameDataTableDef def, string file, CTableFile parsed, ConfigEvaluator? config)
    {
        config = config?.ForFile(parsed.Text);
        foreach (var e in parsed.Entries)
        {
            var row = new GameDataRow { Key = e.Key, File = file, Line = e.Line, Cells = new GameDataCell[def.Columns.Count], ReadOnlyReason = e.ReadOnlyReason };
            for (int i = 0; i < def.Columns.Count; i++)
            {
                var col = def.Columns[i];
                var f = e.Field(col.Field);
                row.FieldRaw[col.Field] = f?.Raw;
                row.Cells[i] = ReadCell(col, f, e, parsed, config);
            }
            yield return row;
        }
    }

    private static string Collapse(string raw) => Regex.Replace(raw, @"\s+", " ");

    private static GameDataCell ReadCell(GameDataColumn col, CTableField? f, CTableEntry e, CTableFile parsed, ConfigEvaluator? config)
    {
        if (f == null)
        {
            bool canAppend = col.AppendIfMissing && e.Positional && e.Fields.Count > 0;
            return new GameDataCell { Original = "", Value = "", Editable = canAppend, Note = col.MissingNote ?? "Not set in this entry (or set by a macro)." };
        }
        if (f.Conditional)
        {
            // #if alternatives: show the value of the current configuration; saving replaces the block with a fixed value.
            var region = config == null ? null : FindConditionalRegion(parsed.Text, e, f, config);
            if (region == null)
            {
                string raw0 = col.Composite != GameDataComposite.None && SplitComposite(col.Composite, f.Raw) is { } p0 ? p0[col.Part] : Collapse(f.Raw);
                return new GameDataCell { Original = raw0, Value = raw0, Note = "Inside #if in a form the toolkit cannot resolve – edit it in the source file." };
            }
            var inner = ReadPlainCell(col, region.ActiveRaw, parsed, config);
            if (!inner.Editable) return new GameDataCell { Original = inner.Original, Value = inner.Value, Note = inner.Note };
            return new GameDataCell
            {
                Original = inner.Original, Value = inner.Value, Editable = true,
                Note = $"Depends on the configuration ({region.Depends}); shown: the value for the current settings. " +
                       "Saving a new value replaces the whole #if block with this fixed value.",
            };
        }
        return ReadPlainCell(col, f.Raw, parsed, config);
    }

    /// <summary>A cell for a value outside #if (or the active branch of one).</summary>
    private static GameDataCell ReadPlainCell(GameDataColumn col, string raw, CTableFile parsed, ConfigEvaluator? config)
    {
        var f = new CTableField("", 0, 0, 0, raw, false);
        string shown = col.Composite != GameDataComposite.None && SplitComposite(col.Composite, f.Raw) is { } ps ? ps[col.Part] : Collapse(f.Raw);
        // "cond ? a : b" with a decidable condition: show and edit the value that applies now.
        if (col.Kind != GameDataKind.Text && col.Composite == GameDataComposite.None && config?.ResolveTernary(raw) is { } chosen
            && ReadPlainCell(col, chosen, parsed, null) is { Editable: true } resolved)
        {
            var names = string.Join(", ", ConfigEvaluator.Names(raw[..Math.Max(0, raw.IndexOf('?'))]));
            return new GameDataCell
            {
                Original = resolved.Original, Value = resolved.Value, Editable = true,
                Note = $"Depends on {names} ({Collapse(raw)}); shown: the value for the current settings. Saving a new value replaces the expression with a fixed value.",
            };
        }
        // A number written as an expression or a #define (10000 * TREASURE_FACTOR, PIKACHU_DEFENSE): show the result.
        if (col.Kind == GameDataKind.Number && !Regex.IsMatch(raw, @"^(\d+|0[xX][0-9A-Fa-f]+)$") && config?.Number(raw) is { } number
            && number >= col.Min && number <= col.Max)
        {
            var value = number.ToString(CultureInfo.InvariantCulture);
            return new GameDataCell
            {
                Original = value, Value = value, Editable = true,
                Note = $"Written as {Collapse(raw)} = {value}. Saving a new value writes a fixed number for this entry only.",
            };
        }
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
        // Several files separated by ';' (constants moved between headers over the versions, e.g. BALL_ → constants/pokeball.h).
        foreach (var optionsFile in (col.OptionsFile ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!File.Exists(ProjectPaths.Combine(root, optionsFile))) continue;
            var t = ReadText(root, optionsFile);
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
                ConditionalRegion? region = null;
                if (field is { Conditional: true })
                {
                    region = (table.Config == null ? null : FindConditionalRegion(text, entry, field, table.Config))
                             ?? throw new GameDataConflictException($"{row.Key}: {col.Header} is inside #if and cannot be changed here.");
                    edits.Add((region.Start, region.End, region.Indent + "." + id + " = " + NewConditionalRaw(def, row, group.Select(g => g.i), region, text) + "," + region.NewLine));
                    continue;
                }
                if (field == null)
                {
                    if (!(col.AppendIfMissing && entry.Positional && col.Field.StartsWith('#')))
                        throw new GameDataConflictException($"{row.Key}: {col.Header} is not set in the file and cannot be added here.");
                    appends[int.Parse(col.Field[1..], CultureInfo.InvariantCulture)] = NewRaw(col, row.Cells[idx].Value, null);
                    continue;
                }
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

    /// <summary>The fixed value that replaces an #if block: composite parts, a text in the block's layout, or a number/constant.</summary>
    private static string NewConditionalRaw(GameDataTableDef def, GameDataRow row, IEnumerable<int> changedColumns, ConditionalRegion region, string fileText)
    {
        var cols = changedColumns.ToList();
        var col = def.Columns[cols[0]];
        var current = region.ActiveRaw;
        if (col.Composite != GameDataComposite.None)
        {
            var parts = SplitComposite(col.Composite, current) ?? throw new GameDataConflictException($"{row.Key}: {col.Field} is not in a simple form.");
            for (int i = 0; i < def.Columns.Count; i++)
                if (def.Columns[i].Field == col.Field && def.Columns[i].Part >= 0) parts[def.Columns[i].Part] = row.Cells[i].Value;
            return JoinComposite(col.Composite, current, parts);
        }
        var value = row.Cells[cols[0]].Value;
        if (col.Kind != GameDataKind.Text) return value;
        // A text: one literal per line under the field, like the multi-line texts of the file.
        var macro = CString.Macro(current) ?? "COMPOUND_STRING";
        var pieces = CString.Pieces(value).Select(p => "\"" + CString.Encode(p) + "\"").ToList();
        if (pieces.Count == 1 && !current.Contains('\n')) return $"{macro}({pieces[0]})";
        string inner = region.Indent + "    ";
        return macro + "(" + string.Concat(pieces.Select(p => region.NewLine + inner + p)) + ")";
    }

    /// <summary>
    /// The lines of an entry that hold a field's #if alternatives: either "#if … .f = a, #else .f = b, #endif" or a value that
    /// branches inside (".f = COMPOUND_STRING(\"x\" #if … \"a\"), #else \"b\"), #endif"). Start..End covers whole lines
    /// (End after the #endif line's line break); ActiveRaw is the value the current configuration compiles.
    /// </summary>
    public sealed record ConditionalRegion(int Start, int End, string Indent, string NewLine, string ActiveRaw, string Depends);

    [GeneratedRegex(@"^[ \t]*#[ \t]*(if|ifdef|ifndef|elif|else|endif)\b(.*)$")]
    private static partial Regex DirectiveLine();

    public static ConditionalRegion? FindConditionalRegion(string text, CTableEntry entry, CTableField field, ConfigEvaluator config)
    {
        if (field.Name.Length == 0) return null; // positional values: not supported
        string nl = text.Contains("\r\n") ? "\r\n" : "\n";
        int fieldLine = text.LastIndexOf('\n', Math.Max(0, field.ValueStart - 1)) + 1;
        var head = Regex.Match(text[fieldLine..field.ValueStart], @"^([ \t]*)\." + Regex.Escape(field.Name) + @"[ \t]*=[ \t]*$");
        if (!head.Success) return null;
        string indent = head.Groups[1].Value;

        // Lines from the field to the entry's end: (start, end incl. line break, text without line break).
        var lines = new List<(int Start, int End, string Text)>();
        for (int pos = fieldLine; pos < entry.Close;)
        {
            int eol = text.IndexOf('\n', pos);
            int end = eol < 0 ? text.Length : eol + 1;
            lines.Add((pos, end, text[pos..end].TrimEnd('\n', '\r')));
            pos = end;
        }
        // Shape A: the field line directly follows an #if line.
        int start = fieldLine;
        int prevEnd = fieldLine - 1;
        int prevStart = prevEnd <= 0 ? 0 : text.LastIndexOf('\n', prevEnd - 1) + 1;
        var prev = prevEnd > 0 ? text[prevStart..prevEnd].TrimEnd('\r') : "";
        bool shapeA = DirectiveLine().Match(prev) is { Success: true } pm && pm.Groups[1].Value is "if" or "ifdef" or "ifndef";
        int depth = 0;
        bool sawDirective = false;
        int regionEnd = -1;
        var conditions = new List<string>();
        if (shapeA) { start = prevStart; depth = 1; sawDirective = true; conditions.Add(DirectiveLine().Match(prev).Groups[2].Value); }
        for (int i = 0; i < lines.Count; i++)
        {
            var (_, end, line) = lines[i];
            var d = DirectiveLine().Match(line);
            if (d.Success)
            {
                var kw = d.Groups[1].Value;
                sawDirective = true;
                if (kw is "if" or "ifdef" or "ifndef") { depth++; conditions.Add(d.Groups[2].Value); }
                else if (kw == "elif") conditions.Add(d.Groups[2].Value);
                else if (kw == "endif" && --depth == 0) { regionEnd = end; break; }
                else if (kw == "endif" && depth < 0) return null;
                continue;
            }
            var trimmed = line.TrimStart();
            // Another designated field inside the region: not a block of this field alone.
            var other = Regex.Match(trimmed, @"^\.(\w+)[ \t]*=(?!=)");
            if (i > 0 && other.Success && other.Groups[1].Value != field.Name) return null;
            // Shape B: before the first directive the value must still be open (a line ending in ',' closes it).
            if (!shapeA && !sawDirective && i > 0 && trimmed.EndsWith(',')) return null;
            if (!shapeA && !sawDirective && i == 0 && Regex.IsMatch(trimmed, @",\s*(//.*)?$")) return null;
        }
        if (regionEnd < 0) return null;
        var active = config.Preprocess(text[start..regionEnd]);
        if (active == null) return null;
        var m = Regex.Matches(active, @"(?:^|\n)[ \t]*\." + Regex.Escape(field.Name) + @"[ \t]*=[ \t]*");
        if (m.Count != 1) return null; // the field is missing (or twice) in the current configuration
        var raw = active[(m[0].Index + m[0].Length)..].Trim();
        if (raw.EndsWith(',')) raw = raw[..^1].TrimEnd();
        var depends = string.Join(", ", conditions.SelectMany(ConfigEvaluator.Names).Distinct());
        return new ConditionalRegion(start, regionEnd, indent, nl, raw, depends.Length > 0 ? depends : "#if");
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

    // "id" since expansion 1.12, "map_section" before.
    [GeneratedRegex(@"""(?:id|map_section)""\s*:\s*""(\w+)""")]
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
