using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using TilesetEditor.Core.GameData;
using TilesetEditor.Core.Project;

namespace TilesetEditor.Core.Platinum;

/// <summary>
/// Game data of pokeplatinum: the JSON files of species, moves and items and the English text banks (abilities, trainer
/// classes, locations, types, natures). Values are replaced in place in the JSON text, so the files keep their format.
/// </summary>
public sealed class PlatinumGameData : IGameDataProvider
{
    public static readonly PlatinumGameData Instance = new();

    private static readonly UTF8Encoding Utf8 = new(false);

    public GameKind Game => GameKind.Platinum;
    public string SourceFolder => "res";

    private static GameDataColumn Num(string header, string field, long min, long max, double width = 55, string? hint = null) =>
        new(header, field, GameDataKind.Number) { Min = min, Max = max, Width = width, Hint = hint };

    private static GameDataColumn Const(string header, string field, string list, string prefix, double width = 140, string? filter = null, string? hint = null) =>
        new(header, field, GameDataKind.Constant) { OptionsFile = list, ConstantPrefix = prefix, OptionsFilter = filter, Width = width, Hint = hint };

    public IReadOnlyList<GameDataTableDef> Tables { get; } =
    [
        new("pl_species", "Pokémon", "res/pokemon/*/data.json", "species", "SPECIES_",
        [
            new("Name", "pokedex_data.en.name", GameDataKind.Text) { FallbackLength = 10, Width = 110, Hint = "Shown in capitals in the original game (e.g. BULBASAUR)." },
            Num("HP", "base_stats.hp", 1, 255, 45),
            Num("Atk", "base_stats.attack", 1, 255, 45),
            Num("Def", "base_stats.defense", 1, 255, 45),
            Num("SpA", "base_stats.special_attack", 1, 255, 45),
            Num("SpD", "base_stats.special_defense", 1, 255, 45),
            Num("Spe", "base_stats.speed", 1, 255, 45),
            Const("Type 1", "types[0]", "pokemon_types", "TYPE_", 115),
            Const("Type 2", "types[1]", "pokemon_types", "TYPE_", 115, hint: "Same as Type 1 = single type."),
            Const("Ability 1", "abilities[0]", "abilities", "ABILITY_", 160),
            Const("Ability 2", "abilities[1]", "abilities", "ABILITY_", 160, hint: "ABILITY_NONE = only one ability."),
            Num("Catch rate", "catch_rate", 0, 255, 70, "3 = legendary, 45 = starter, 255 = very easy."),
            Num("Base EXP", "base_exp_reward", 0, 255, 70),
            Const("Gender", "gender_ratio", "gender_ratios", "GENDER_RATIO_", 160),
            Const("Growth", "exp_rate", "exp_rates", "EXP_RATE_", 130),
            Const("Egg group 1", "egg_groups[0]", "egg_groups", "EGG_GROUP_", 140),
            Const("Egg group 2", "egg_groups[1]", "egg_groups", "EGG_GROUP_", 140),
            Const("Held item (50%)", "held_items.common", "items", "ITEM_", 150),
            Const("Held item (5%)", "held_items.rare", "items", "ITEM_", 150),
            Num("Friendship", "base_friendship", 0, 255, 70),
            Num("Egg cycles", "hatch_cycles", 0, 255, 70),
            new("Category", "pokedex_data.en.category", GameDataKind.Text) { FallbackLength = 12, SoftLength = true, Width = 130, Hint = "Pokédex category, e.g. \"Seed Pokémon\"." },
            new("Pokédex entry", "pokedex_data.en.entry_text", GameDataKind.Text) { LineBreaks = true, Width = 380, Hint = "\\n = new line (3 lines fit)." },
        ])
        {
            Source = GameDataSource.JsonFiles,
            CategoryColumns = ["Type 1", "Type 2"],
            Note = "Base stats, types, abilities and more of every Pokémon (res/pokemon/<name>/data.json). Learnsets, evolutions and sprites: Pokémon module. Adding species is not supported here.",
        },
        new("pl_moves", "Moves", "res/moves/*/data.json", "moves", "MOVE_",
        [
            new("Name", "name", GameDataKind.Text) { FallbackLength = 12, SoftLength = true, Width = 130 },
            Const("Type", "type", "pokemon_types", "TYPE_", 115),
            Const("Class", "class", "move_classes", "CLASS_", 130, hint: "Physical / special is decided per move in Gen 4."),
            Num("Power", "power", 0, 255, 55),
            Num("Accuracy", "accuracy", 0, 100, 70, "0 = never misses."),
            Num("PP", "pp", 0, 64, 45),
            Num("Effect %", "effect.chance", 0, 100, 65, "Chance of the secondary effect."),
            Const("Effect", "effect.type", "move_battle_effects", "BATTLE_EFFECT_", 240),
            Const("Target", "range", "move_ranges", "RANGE_", 180),
            new("Description", "description", GameDataKind.Text) { LineBreaks = true, Width = 380, Hint = "\\n = new line." },
        ])
        {
            Source = GameDataSource.JsonFiles,
            CategoryColumns = ["Type", "Class"],
            Note = "Moves (res/moves/<name>/data.json). Animations and battle scripts are not changed here; adding moves is not supported.",
        },
        new("pl_items", "Items", "res/items/data/*.json", "items", "ITEM_",
        [
            new("Name", "name", GameDataKind.Text) { FallbackLength = 14, SoftLength = true, Width = 150 },
            new("Plural", "plural", GameDataKind.Text) { FallbackLength = 16, SoftLength = true, Width = 150 },
            Num("Price", "price", 0, 65535, 70),
            new("Description", "description", GameDataKind.Text) { LineBreaks = true, Width = 380, Hint = "\\n = new line." },
            Const("Pocket", "fieldPocket", "@POCKET_", "POCKET_", 140, "^POCKET_(?!MAX$|LIST_END$)"),
            Const("Hold effect", "holdEffect", "item_hold_effects", "HOLD_EFFECT_", 200),
            Num("Fling power", "flingPower", 0, 255, 75),
            new("Can't toss", "preventToss", GameDataKind.Constant) { JsonBool = true, Width = 75, Hint = "true = key item that cannot be thrown away." },
        ])
        {
            Source = GameDataSource.JsonFiles,
            CategoryColumns = ["Pocket"],
            Note = "Items (res/items/data/<name>.json). Icons and use functions are not changed here; adding items is not supported.",
        },
        new("pl_abilities", "Abilities", "res/text/ability_names.json", "abilities", "ABILITY_",
        [
            new("Name", "en_US", GameDataKind.Text) { FallbackLength = 12, SoftLength = true, Width = 150 },
            new("Description", "en_US", GameDataKind.Text) { SourceFile = "res/text/ability_descriptions.json", LineBreaks = true, Width = 380, Hint = "\\n = new line." },
        ])
        {
            Source = GameDataSource.TextBank,
            Note = "Names and descriptions of the abilities (res/text/ability_names.json, ability_descriptions.json). What an ability does is battle code.",
        },
        new("pl_trainer_classes", "Trainer classes", "res/text/trainer_class_names.json", "trainer_classes", "TRAINER_CLASS_",
        [
            new("Name", "en_US", GameDataKind.Text) { FallbackLength = 12, SoftLength = true, Width = 180 },
        ])
        {
            Source = GameDataSource.TextBank,
            Note = "Names of the trainer classes (res/text/trainer_class_names.json). Picture and team per trainer: Trainers module.",
        },
        new("pl_types", "Types", "res/text/pokemon_type_names.json", "pokemon_types", "TYPE_",
        [
            new("Name", "en_US", GameDataKind.Text) { FallbackLength = 8, SoftLength = true, Width = 140 },
        ])
        {
            Source = GameDataSource.TextBank,
            Note = "Type names (res/text/pokemon_type_names.json). The type chart is battle code.",
        },
        new("pl_natures", "Natures", "res/text/nature_names.json", "natures", "NATURE_",
        [
            new("Name", "en_US", GameDataKind.Text) { FallbackLength = 10, SoftLength = true, Width = 140 },
        ])
        {
            Source = GameDataSource.TextBank,
            Note = "Nature names (res/text/nature_names.json).",
        },
        new("pl_locations", "Locations", "res/text/location_names.json", null, "",
        [
            new("Name", "en_US", GameDataKind.Text) { FallbackLength = 16, SoftLength = true, Width = 200 },
        ])
        {
            Source = GameDataSource.TextBank,
            Note = "Names of the places (res/text/location_names.json): shown when entering a map and on the Town Map. Which map uses which name: Maps module.",
        },
    ];

    public GameText LoadText(string root) => GameText.LoadPlatinum(root);

    // ---------------------------------------------------------------- loading

    public GameDataTable Load(string root, GameDataTableDef def, GameText text)
    {
        var files = ResolveFiles(root, def.Files);
        if (files.Count == 0) throw new FileNotFoundException($"{def.Files} was not found in the project.");
        var constants = def.Marker != null ? ConstantList(root, def.Marker) : [];
        var rows = def.Source == GameDataSource.TextBank ? LoadTextBank(root, def, files[0], constants) : LoadFiles(root, def, files, constants);
        var options = def.Columns.Select(c => c.Kind == GameDataKind.Constant ? (IReadOnlyList<string>?)Options(root, c) : null).ToList();
        var maxLength = def.Columns.Select(c => c.Kind == GameDataKind.Text ? c.FallbackLength : 0).ToArray();
        var allFiles = files.Concat(def.Columns.Where(c => c.SourceFile != null).Select(c => c.SourceFile!)).Distinct().ToList();
        return new GameDataTable { Def = def, Rows = rows, Files = allFiles, Options = options, MaxLength = maxLength, Text = text };
    }

    /// <summary>Project-relative files of a pattern; "*" may stand for a folder ("res/pokemon/*/data.json") or a file name.</summary>
    public static IReadOnlyList<string> ResolveFiles(string root, string pattern)
    {
        var parts = pattern.Split('/');
        var current = new List<string> { "" };
        for (int i = 0; i < parts.Length; i++)
        {
            bool last = i == parts.Length - 1;
            var next = new List<string>();
            // Hundreds of folders (res/pokemon/*/data.json): checked in parallel – every check is a round trip over a WSL share.
            var results = new List<string>[current.Count];
            var part = parts[i];
            var dirs = current;
            ParallelIo.For(dirs.Count, k =>
            {
                var dir = dirs[k];
                var found = new List<string>();
                results[k] = found;
                string Join(string name) => dir.Length == 0 ? name : dir + "/" + name;
                if (!part.Contains('*'))
                {
                    var candidate = Join(part);
                    if (last ? File.Exists(ProjectPaths.Combine(root, candidate)) : Directory.Exists(ProjectPaths.Combine(root, candidate))) found.Add(candidate);
                    return;
                }
                var full = ProjectPaths.Combine(root, dir.Length == 0 ? "." : dir);
                if (!Directory.Exists(full)) return;
                var names = last ? Directory.GetFiles(full, part) : Directory.GetDirectories(full, part);
                found.AddRange(names.Select(f => Join(Path.GetFileName(f))).OrderBy(f => f, StringComparer.Ordinal));
            });
            foreach (var r in results) next.AddRange(r);
            current = next;
        }
        return current;
    }

    private static List<string> ConstantList(string root, string name)
    {
        var path = ProjectPaths.Combine(root, "generated/" + name + ".txt");
        return File.Exists(path) ? File.ReadLines(path).Select(l => l.Split('=')[0].Trim()).Where(l => l.Length > 0).ToList() : [];
    }

    private static List<string> Options(string root, GameDataColumn c)
    {
        if (c.JsonBool) return ["true", "false"];
        if (c.OptionsFile == null) return [];
        List<string> list;
        if (c.OptionsFile.StartsWith('@'))
        {
            // #defines of include/constants/items.h with the prefix (POCKET_…).
            var prefix = c.OptionsFile[1..];
            var header = ProjectPaths.Combine(root, "include/constants/items.h");
            list = File.Exists(header)
                ? Regex.Matches(File.ReadAllText(header), @"#define\s+(" + Regex.Escape(prefix) + @"\w+)").Select(m => m.Groups[1].Value).Distinct().ToList()
                : [];
        }
        else list = ConstantList(root, c.OptionsFile);
        if (c.OptionsFilter != null) list = list.Where(v => Regex.IsMatch(v, c.OptionsFilter)).ToList();
        return list;
    }

    /// <summary>res/pokemon/bulbasaur/data.json → SPECIES_BULBASAUR; res/items/data/antidote.json → ITEM_ANTIDOTE.</summary>
    public static string KeyOf(GameDataTableDef def, string file)
    {
        var name = Path.GetFileName(file) == "data.json" ? Path.GetFileName(Path.GetDirectoryName(file)!) : Path.GetFileNameWithoutExtension(file);
        return def.KeyPrefix + name.ToUpperInvariant();
    }

    private static List<GameDataRow> LoadFiles(string root, GameDataTableDef def, IReadOnlyList<string> files, List<string> order)
    {
        // One file per Pokémon / move / item: read in parallel (over a WSL share each read is a slow round trip).
        var read = new GameDataRow[files.Count];
        ParallelIo.For(files.Count, k =>
        {
            var file = files[k];
            var text = GameDataCatalog.ReadText(root, file);
            Dictionary<string, JsonSpan> spans;
            try { spans = JsonText.Locate(text); }
            catch (JsonException ex) { throw new FormatException($"{file}: {ex.Message}"); }
            var row = new GameDataRow { Key = KeyOf(def, file), File = file, Line = 1, Cells = new GameDataCell[def.Columns.Count] };
            for (int i = 0; i < def.Columns.Count; i++) row.Cells[i] = ReadCell(def.Columns[i], spans, "", row);
            read[k] = row;
        });
        var rows = read.ToList();
        // The game's order (generated/species.txt …) instead of the alphabetical folder order.
        var index = order.Select((k, i) => (k, i)).GroupBy(x => x.k).ToDictionary(g => g.Key, g => g.First().i, StringComparer.Ordinal);
        return rows.OrderBy(r => index.TryGetValue(r.Key, out var i) ? i : int.MaxValue).ThenBy(r => r.Key, StringComparer.Ordinal).ToList();
    }

    private static List<GameDataRow> LoadTextBank(string root, GameDataTableDef def, string file, List<string> keys)
    {
        var texts = new Dictionary<string, Dictionary<string, JsonSpan>>(StringComparer.Ordinal);
        Dictionary<string, JsonSpan> Spans(string f)
        {
            if (!texts.TryGetValue(f, out var s))
            {
                var path = ProjectPaths.Combine(root, f);
                s = File.Exists(path) ? JsonText.Locate(GameDataCatalog.ReadText(root, f)) : [];
                texts[f] = s;
            }
            return s;
        }
        var main = Spans(file);
        var rows = new List<GameDataRow>();
        for (int i = 0; main.ContainsKey($"messages[{i}]"); i++)
        {
            var id = JsonText.Display(main, $"messages[{i}].id") ?? "";
            var key = i < keys.Count ? keys[i] : id;
            var row = new GameDataRow { Key = key, File = file, Line = i, Cells = new GameDataCell[def.Columns.Count], PathPrefix = $"messages[{i}]." };
            for (int c = 0; c < def.Columns.Count; c++)
            {
                var col = def.Columns[c];
                row.Cells[c] = ReadCell(col, col.SourceFile != null ? Spans(col.SourceFile) : main, row.PathPrefix, row);
            }
            rows.Add(row);
        }
        return rows;
    }

    private static GameDataCell ReadCell(GameDataColumn col, Dictionary<string, JsonSpan> spans, string prefix, GameDataRow row)
    {
        var path = prefix + col.Field;
        row.FieldRaw[FieldKey(col, path)] = spans.TryGetValue(path, out var span) ? span.Raw : null;
        if (span == null)
            return new GameDataCell { Original = "", Value = "", Editable = false, Note = $"Not set in this file ({col.Field})." };
        string value;
        bool editable = true;
        string? note = null;
        switch (span.Kind)
        {
            case JsonValueKind.Array when col.Kind == GameDataKind.Text:
                using (var doc = JsonDocument.Parse(span.Raw)) value = JsonText.JoinLines(doc.RootElement);
                break;
            case JsonValueKind.String:
                var s = JsonText.Display(spans, path) ?? "";
                value = col.Kind == GameDataKind.Text ? JsonText.ToEscapes(s) : s;
                break;
            case JsonValueKind.Number:
                value = span.Raw;
                if (col.Kind == GameDataKind.Number && !Regex.IsMatch(value, @"^\d+$")) { editable = false; note = "Not a whole number – edit it in the JSON file."; }
                break;
            case JsonValueKind.True or JsonValueKind.False:
                value = span.Raw;
                editable = col.JsonBool;
                break;
            case JsonValueKind.Null:
                value = "";
                editable = false;
                note = "null in the JSON file.";
                break;
            default:
                value = Collapse(span.Raw);
                editable = false;
                note = "A list or object – edit it in the JSON file.";
                break;
        }
        return new GameDataCell { Original = value, Value = value, Editable = editable, Note = note };
    }

    private static string Collapse(string raw) => Regex.Replace(raw, @"\s+", " ");

    private static string FieldKey(GameDataColumn col, string path) => (col.SourceFile ?? "") + "|" + path;

    // ---------------------------------------------------------------- writing

    public IEnumerable<string> ChangedFiles(GameDataTable table) =>
        table.ChangedRows.SelectMany(r => Enumerable.Range(0, r.Cells.Length).Where(i => r.Cells[i].IsChanged)
            .Select(i => table.Def.Columns[i].SourceFile ?? r.File)).Distinct();

    public string Write(GameDataTable table, string file, string text)
    {
        var spans = JsonText.Locate(text);
        var changes = new List<(JsonSpan, string)>();
        foreach (var row in table.ChangedRows)
            for (int i = 0; i < row.Cells.Length; i++)
            {
                var col = table.Def.Columns[i];
                var cell = row.Cells[i];
                if (!cell.IsChanged || (col.SourceFile ?? row.File) != file) continue;
                var path = row.PathPrefix + col.Field;
                if (!spans.TryGetValue(path, out var span))
                    throw new GameDataConflictException($"{row.Key}: '{path}' is no longer in {file}. Reload and try again.");
                if (row.FieldRaw.GetValueOrDefault(FieldKey(col, path)) != span.Raw)
                    throw new GameDataConflictException($"{row.Key}: {col.Header} was changed in {file} outside PokéPat. Reload and try again.");
                changes.Add((span, NewRaw(col, cell.Value, span, text)));
            }
        return JsonText.Replace(text, changes);
    }

    private static string NewRaw(GameDataColumn col, string value, JsonSpan span, string text)
    {
        if (col.Kind == GameDataKind.Number || col.JsonBool) return value;
        if (col.Kind == GameDataKind.Text)
        {
            var real = JsonText.FromEscapes(value);
            if (span.Kind == JsonValueKind.Array)
            {
                var indent = JsonText.IndentAt(text, span.Start);
                int step = DetectStep(span.Raw, indent);
                return JsonText.LinesArray(JsonText.SplitLines(real), indent, step);
            }
            return JsonText.Quote(real);
        }
        return JsonText.Quote(value);
    }

    /// <summary>Indent step of an existing multi-line array (2 or 4 spaces), default 4.</summary>
    private static int DetectStep(string arrayRaw, string indent)
    {
        var m = Regex.Match(arrayRaw, @"\[\s*\n([ \t]*)\S");
        return m.Success && m.Groups[1].Value.Length > indent.Length ? m.Groups[1].Value.Length - indent.Length : 4;
    }

    public List<QuickFilter> QuickFilters(GameDataTable table, string root) => [];

    public IReadOnlyList<string> SaveNotes(IReadOnlyList<GameDataTable> changed) =>
    [
        "Only the changed values are replaced; the JSON files keep their format.",
        "Build the ROM afterwards to see the changes in-game (the build turns the JSON files into the game's NARC archives).",
        "Texts were checked against tools/msgenc/charmap.txt. Whether long texts fit in the menus is not checked – look at them in-game.",
    ];
}
