using TilesetEditor.Core.GameData;

namespace TilesetEditor.Tests;

public class GameDataTests
{
    private static string Ref => TestPaths.ReferenceProject;

    private static GameDataTable Load(string id) => GameDataCatalog.Load(Ref, GameDataCatalog.Get(id));

    private static GameDataRow Row(GameDataTable t, string key) => t.Rows.Single(r => r.Key == key);

    private static int Col(GameDataTable t, string header) => t.Def.Columns.ToList().FindIndex(c => c.Header == header);

    private static string Cell(GameDataTable t, string key, string header) => Row(t, key).Cells[Col(t, header)].Value;

    /// <summary>The differing middle parts of two texts after removing the common prefix and suffix.</summary>
    private static (string Old, string New) Diff(string a, string b)
    {
        int p = 0;
        while (p < a.Length && p < b.Length && a[p] == b[p]) p++;
        int s = 0;
        while (s < a.Length - p && s < b.Length - p && a[^(s + 1)] == b[^(s + 1)]) s++;
        return (a[p..(a.Length - s)], b[p..(b.Length - s)]);
    }

    private static string Write(GameDataTable t, string file) => GameDataCatalog.Write(t, file, GameDataCatalog.ReadText(Ref, file));

    [Theory]
    [InlineData("trainer_classes", 60)]
    [InlineData("items", 800)]
    [InlineData("moves", 900)]
    [InlineData("abilities", 300)]
    [InlineData("species", 1200)]
    [InlineData("types", 19)]
    [InlineData("natures", 25)]
    [InlineData("locations", 200)]
    public void Every_table_parses_with_plausible_counts(string id, int minRows)
    {
        var t = Load(id);
        Assert.True(t.Rows.Count >= minRows, $"{id}: {t.Rows.Count} rows");
        // Most names are plain strings and editable.
        int name = Col(t, "Name");
        double editable = t.Rows.Count(r => r.ReadOnlyReason == null && r.Cells[name].Editable) / (double)t.Rows.Count;
        Assert.True(editable > 0.9, $"{id}: only {editable:P0} of the names are editable");
        for (int i = 0; i < t.Def.Columns.Count; i++)
            if (t.Def.Columns[i].Kind == GameDataKind.Constant && t.Rows.Any(r => r.FieldRaw[t.Def.Columns[i].Field] != null)) Assert.NotEmpty(t.Options[i]!);
    }

    [Fact]
    public void Items_and_moves_have_category_columns()
    {
        var items = Load("items");
        Assert.Equal("POCKET_POKE_BALLS", Cell(items, "ITEM_POKE_BALL", "Pocket"));
        Assert.Contains("POCKET_KEY_ITEMS", items.Options[Col(items, "Pocket")]!);
        Assert.All(items.Def.CategoryColumns, h => Assert.True(Col(items, h) >= 0, h));
        var moves = Load("moves");
        Assert.Equal(["Type", "Category"], moves.Def.CategoryColumns);
    }

    [Fact]
    public void Quick_filters_group_items_and_moves_with_game_icons()
    {
        var items = Load("items");
        var f = QuickFilters.For(items, Ref);
        var balls = Assert.Single(f, x => x.Label == "Poké Balls");
        Assert.True(balls.Count >= 12);
        Assert.Equal((24, 24), (balls.Icon!.Width, balls.Icon.Height));
        Assert.Contains(f, x => x.Label == "Berries" && x.Icon != null);
        Assert.Contains(f, x => x.Label == "TMs & HMs" && x.Icon != null);
        var potion = Row(items, "ITEM_POTION");
        Assert.False(balls.Matches(c => QuickFilters.Cell(items, potion, c)));
        // Every item is in at most a handful of groups and the groups cover most items.
        Assert.True(items.Rows.Count(r => f.Any(x => x.Matches(c => QuickFilters.Cell(items, r, c)))) > items.Rows.Count * 3 / 4);

        var moves = Load("moves");
        var mf = QuickFilters.For(moves, Ref);
        var fire = Assert.Single(mf, x => x.Label == "Fire");
        Assert.Equal((32, 16), (fire.Icon!.Width, fire.Icon.Height));
        Assert.Equal(moves.Rows.Count(r => Cell(moves, r.Key, "Type") == "TYPE_FIRE"), fire.Count);
        Assert.Contains(mf, x => x.Label == "Physical" && x.Icon is { Width: 16, Height: 16 });
        Assert.Contains(QuickFilters.For(Load("species"), Ref), x => x.Label == "Dragon");
        Assert.Empty(QuickFilters.For(Load("natures"), Ref));
    }

    [Fact]
    public void Known_values_are_read()
    {
        var tc = Load("trainer_classes");
        Assert.Equal("HIKER", Cell(tc, "TRAINER_CLASS_HIKER", "Name"));
        Assert.Equal("10", Cell(tc, "TRAINER_CLASS_HIKER", "Prize money"));
        Assert.Equal("{PKMN} TRAINER", Cell(tc, "TRAINER_CLASS_PKMN_TRAINER_1", "Name"));
        Assert.Equal("", Cell(tc, "TRAINER_CLASS_PKMN_TRAINER_1", "Prize money"));
        Assert.True(Row(tc, "TRAINER_CLASS_PKMN_TRAINER_1").Cells[Col(tc, "Prize money")].Editable);
        Assert.Equal(12, tc.MaxLength[Col(tc, "Name")]);
        var balls = tc.Options[Col(tc, "Poké Ball")]!;
        Assert.Contains("BALL_ULTRA", balls);
        Assert.DoesNotContain("BALL_ROTATE_LEFT", balls);
        // A config-dependent expression shows the value that applies now and can be replaced by a fixed one.
        var breeder = Row(tc, "TRAINER_CLASS_PKMN_BREEDER").Cells[Col(tc, "Poké Ball")];
        Assert.True(breeder.Editable, breeder.Note);
        Assert.StartsWith("BALL_", breeder.Value);
        Assert.Contains("B_TRAINER_CLASS_POKE_BALLS", breeder.Note);

        var items = Load("items");
        Assert.Equal("Poké Ball", Cell(items, "ITEM_POKE_BALL", "Name"));
        Assert.True(Row(items, "ITEM_POKE_BALL").Cells[Col(items, "Name")].Editable); // _("…") or ITEM_NAME("…") (1.13+)
        Assert.Equal("200", Cell(items, "ITEM_POKE_BALL", "Price"));
        Assert.Equal(@"A tool used for\ncatching wild\nPokémon.", Cell(items, "ITEM_POKE_BALL", "Description"));
        Assert.True(Row(items, "ITEM_ULTRA_BALL").Cells[Col(items, "Price")].Editable); // (I_PRICE >= GEN_7) ? 800 : 1200 → the current value
        Assert.Equal(19, items.MaxLength[Col(items, "Name")]);

        var moves = Load("moves");
        Assert.Equal("Pound", Cell(moves, "MOVE_POUND", "Name"));
        Assert.Equal("40", Cell(moves, "MOVE_POUND", "Power"));
        Assert.Equal("TYPE_NORMAL", Cell(moves, "MOVE_POUND", "Type"));
        Assert.Equal("DAMAGE_CATEGORY_PHYSICAL", Cell(moves, "MOVE_POUND", "Category"));
        Assert.Contains("TYPE_FIRE", moves.Options[Col(moves, "Type")]!);

        var species = Load("species");
        Assert.Equal("Bulbasaur", Cell(species, "SPECIES_BULBASAUR", "Name"));
        Assert.Equal("45", Cell(species, "SPECIES_BULBASAUR", "HP"));
        Assert.Equal("TYPE_GRASS", Cell(species, "SPECIES_BULBASAUR", "Type 1"));
        Assert.Equal("TYPE_POISON", Cell(species, "SPECIES_BULBASAUR", "Type 2"));
        Assert.Equal("ABILITY_CHLOROPHYLL", Cell(species, "SPECIES_BULBASAUR", "Hidden ability"));
        Assert.Equal("TYPE_FIRE", Cell(species, "SPECIES_CHARMANDER", "Type 2"));
        Assert.Equal(12, species.MaxLength[Col(species, "Name")]);

        Assert.Equal("LITTLEROOT TOWN", Cell(Load("locations"), "MAPSEC_LITTLEROOT_TOWN", "Name"));
        Assert.Equal("Stench", Cell(Load("abilities"), "ABILITY_STENCH", "Name"));
        Assert.Equal("Fire", Cell(Load("types"), "TYPE_FIRE", "Name"));
        var natures = Load("natures");
        Assert.Equal("Hardy", Cell(natures, "NATURE_HARDY", "Name"));
        Assert.Equal("STAT_DEF", Cell(natures, "NATURE_LONELY", "Lowers"));
    }

    [Theory]
    [InlineData("trainer_classes")]
    [InlineData("items")]
    [InlineData("moves")]
    [InlineData("abilities")]
    [InlineData("species")]
    [InlineData("types")]
    [InlineData("natures")]
    public void Rewriting_every_value_unchanged_is_byte_identical(string id)
    {
        var def = GameDataCatalog.Get(id);
        foreach (var file in GameDataCatalog.ResolveFiles(Ref, def))
        {
            var text = GameDataCatalog.ReadText(Ref, file);
            var parsed = CTable.Parse(text, def.Marker, def.KeyPrefix);
            var edits = new List<(int, int, string)>();
            foreach (var e in parsed.Entries)
                foreach (var col in def.Columns.Where(c => c.Part <= 0))
                {
                    if (e.Field(col.Field) is not { Conditional: false } f) continue;
                    string? again = null;
                    if (col.Kind == GameDataKind.Text && CString.TryRead(f.Raw) is { } s)
                    {
                        // Texts split at other places than line breaks are re-split when edited; only check the regular ones.
                        if (CString.Pieces(s).Count != CString.LiteralCount(f.Raw)) continue;
                        again = CString.Layout(s, f.Raw, text, f.ValueStart);
                    }
                    else if (col.Composite != GameDataComposite.None && GameDataCatalog.SplitComposite(col.Composite, f.Raw) is { } parts)
                    {
                        // Single-type MON_TYPES(a) and "{ a, b, c }" come back as written.
                        var joined = GameDataCatalog.JoinComposite(col.Composite, f.Raw, parts);
                        if (col.Composite == GameDataComposite.AbilityList && parts.Length == 3 && f.Raw.Count(c => c == ',') < 2) continue;
                        again = joined;
                    }
                    if (again == null) continue;
                    Assert.Equal(f.Raw, again);
                    edits.Add((f.ValueStart, f.ValueEnd, again));
                }
            Assert.Equal(text, CTable.ApplyEdits(text, edits));
            // Without changed rows the writer returns the text unchanged.
            var table = GameDataCatalog.Load(Ref, def);
            Assert.Equal(text, GameDataCatalog.Write(table, file, text));
        }
    }

    [Fact]
    public void Editing_one_value_changes_only_that_value()
    {
        var items = Load("items");
        Row(items, "ITEM_POKE_BALL").Cells[Col(items, "Price")].Value = "999";
        var old = GameDataCatalog.ReadText(Ref, "src/data/items.h");
        var updated = Write(items, "src/data/items.h");
        Assert.Equal(("200", "999"), Diff(old, updated));
        var e = CTable.Parse(updated, "gItemsInfo", "ITEM_").Entry("ITEM_POKE_BALL")!;
        Assert.Equal("999", e.Field("price")!.Raw);
    }

    [Fact]
    public void Description_keeps_the_multi_line_layout()
    {
        var items = Load("items");
        Row(items, "ITEM_POKE_BALL").Cells[Col(items, "Description")].Value = @"A ball for\ncatching.";
        var updated = Write(items, "src/data/items.h");
        Assert.Contains(".description = COMPOUND_STRING(\n            \"A ball for\\n\"\n            \"catching.\"),\n        .pocket = POCKET_POKE_BALLS,", updated);
        var e = CTable.Parse(updated, "gItemsInfo", "ITEM_").Entry("ITEM_POKE_BALL")!;
        Assert.Equal(@"A ball for\ncatching.", CString.TryRead(e.Field("description")!.Raw));
        Assert.Equal(CTable.Parse(GameDataCatalog.ReadText(Ref, "src/data/items.h"), "gItemsInfo", "ITEM_").Entries.Count,
            CTable.Parse(updated, "gItemsInfo", "ITEM_").Entries.Count);
    }

    [Fact]
    public void Shared_description_is_copied_for_the_edited_item_only()
    {
        var items = Load("items");
        int d = Col(items, "Description");
        // Expansion up to 1.12 shares some descriptions (static const u8 sX[]); later versions write every text inline.
        var row = items.Rows.FirstOrDefault(r => r.Cells[d].Note?.StartsWith("Shared text") == true && r.ReadOnlyReason == null);
        if (row == null) return;
        var name = row.FieldRaw["description"]!;
        row.Cells[d].Value = "Own text.";
        var old = GameDataCatalog.ReadText(Ref, "src/data/items.h");
        var updated = Write(items, "src/data/items.h");
        var parsed = CTable.Parse(updated, "gItemsInfo", "ITEM_");
        Assert.Equal("COMPOUND_STRING(\"Own text.\")", parsed.Entry(row.Key)!.Field("description")!.Raw);
        Assert.Equal(CTable.Parse(old, "gItemsInfo", "ITEM_").Strings[name].Raw, parsed.Strings[name].Raw);
        Assert.Equal((name, "COMPOUND_STRING(\"Own text.\")"), Diff(old, updated));
    }

    [Fact]
    public void Species_types_and_abilities_are_written_in_their_notation()
    {
        var sp = Load("species");
        var row = Row(sp, "SPECIES_BULBASAUR");
        row.Cells[Col(sp, "Type 2")].Value = "TYPE_GRASS";
        row.Cells[Col(sp, "Hidden ability")].Value = "ABILITY_THICK_FAT";
        row.Cells[Col(sp, "Atk")].Value = "50";
        var updated = Write(sp, row.File);
        var old = GameDataCatalog.ReadText(Ref, row.File);
        Assert.Contains(".types = MON_TYPES(TYPE_GRASS),", updated);
        Assert.Contains(".abilities = { ABILITY_OVERGROW, ABILITY_NONE, ABILITY_THICK_FAT },", updated);
        Assert.Contains(".baseAttack    = 50,", updated);
        Assert.Equal(old.Length - "TYPE_GRASS, TYPE_POISON".Length + "TYPE_GRASS".Length - "ABILITY_CHLOROPHYLL".Length + "ABILITY_THICK_FAT".Length, updated.Length);
    }

    [Fact]
    public void Trainer_class_money_and_ball_can_be_added_to_short_entries()
    {
        var tc = Load("trainer_classes");
        Row(tc, "TRAINER_CLASS_PKMN_TRAINER_1").Cells[Col(tc, "Prize money")].Value = "7";
        Row(tc, "TRAINER_CLASS_HIKER").Cells[Col(tc, "Poké Ball")].Value = "BALL_NET";
        Row(tc, "TRAINER_CLASS_TEAM_AQUA").Cells[Col(tc, "Poké Ball")].Value = "BALL_NET";
        Row(tc, "TRAINER_CLASS_LADY").Cells[Col(tc, "Name")].Value = "NOBLE";
        var updated = Write(tc, "src/battle_main.c");
        Assert.Contains("[TRAINER_CLASS_PKMN_TRAINER_1] = { _(\"{PKMN} TRAINER\"), 7 },", updated);
        // 1.10: the ball is added; 1.17: the config-dependent ball expression becomes BALL_NET.
        Assert.Contains("[TRAINER_CLASS_HIKER] = { _(\"HIKER\"), 10, BALL_NET },", updated);
        Assert.Contains("[TRAINER_CLASS_TEAM_AQUA] = { _(\"TEAM AQUA\"), 0, BALL_NET },", updated);
        Assert.Contains("[TRAINER_CLASS_LADY] = { _(\"NOBLE\"), 50 },", updated);
        var parsed = CTable.Parse(updated, "gTrainerClasses", "TRAINER_CLASS_");
        Assert.Equal(tc.Rows.Count, parsed.Entries.Count);
    }

    [Fact]
    public void Location_name_changes_only_the_json_value()
    {
        var loc = Load("locations");
        Row(loc, "MAPSEC_LITTLEROOT_TOWN").Cells[0].Value = "NEWROOT TOWN";
        var file = "src/data/region_map/region_map_sections.json";
        var old = GameDataCatalog.ReadText(Ref, file);
        var updated = Write(loc, file);
        Assert.Equal(("LITTLE", "NEW"), Diff(old, updated));
        System.Text.Json.JsonDocument.Parse(updated).Dispose();

        // Two names that become the same C identifier in the build are rejected.
        var row = Row(loc, "MAPSEC_OLDALE_TOWN");
        row.Cells[0].Value = "NEWROOT-TOWN";
        Assert.NotNull(loc.Validate(row, 0).Error);
    }

    [Fact]
    public void Adding_a_trainer_class_produces_parseable_constants_and_table()
    {
        var constants = GameDataCatalog.ReadText(Ref, TrainerClasses.ConstantsFile);
        var table = GameDataCatalog.ReadText(Ref, TrainerClasses.TableFile);
        var before = new TilesetEditor.Core.Project.CDefines();
        before.AddFromText(constants);
        long n = before.TryEvaluate("TRAINER_CLASS_COUNT")!.Value;
        bool enumStyle = !System.Text.RegularExpressions.Regex.IsMatch(constants, @"#define\s+TRAINER_CLASS_COUNT\b");

        var result = TrainerClasses.Add(constants, table, "TRAINER_CLASS_TESTER", "TESTER", 12, null);
        Assert.Equal(n, result.Value);
        var defines = new TilesetEditor.Core.Project.CDefines();
        defines.AddFromText(result.Constants);
        Assert.Equal(n, defines.TryEvaluate("TRAINER_CLASS_TESTER"));
        Assert.Equal(n + 1, defines.TryEvaluate("TRAINER_CLASS_COUNT"));
        var lines = result.Constants.Split('\n');
        if (enumStyle)
        {
            // expansion 1.13+: a new enumerator directly before the count, nothing else changes.
            Assert.Contains("    TRAINER_CLASS_TESTER,\n    TRAINER_CLASS_COUNT,", result.Constants);
            Assert.Equal(constants.Length + "    TRAINER_CLASS_TESTER,\n".Length, result.Constants.Length);
        }
        else
        {
            // Aligned like the neighbours, directly before the count.
            int i = Array.FindIndex(lines, l => l.StartsWith("#define TRAINER_CLASS_TESTER"));
            Assert.StartsWith("#define TRAINER_CLASS_COUNT", lines[i + 1]);
            Assert.Equal(lines[i + 1].IndexOf("0x", StringComparison.Ordinal), lines[i].IndexOf("0x", StringComparison.Ordinal));
        }
        // Only one line is added (the count changes in place for #defines).
        Assert.Equal(constants.Split('\n').Length + 1, lines.Length);

        var parsed = CTable.Parse(result.Table, "gTrainerClasses", "TRAINER_CLASS_");
        var old = CTable.Parse(table, "gTrainerClasses", "TRAINER_CLASS_");
        Assert.Equal(old.Entries.Count + 1, parsed.Entries.Count);
        var e = parsed.Entry("TRAINER_CLASS_TESTER")!;
        Assert.Equal("TESTER", CString.TryRead(e.Field("#0")!.Raw));
        Assert.Equal("12", e.Field("#1")!.Raw);
        Assert.Contains("    [TRAINER_CLASS_TESTER] = { _(\"TESTER\"), 12 },\n", result.Table);
        Assert.Equal(table.Length + "    [TRAINER_CLASS_TESTER] = { _(\"TESTER\"), 12 },\n".Length, result.Table.Length);

        Assert.NotNull(TrainerClasses.CheckConstant(constants, "TRAINER_CLASS_HIKER"));
        Assert.NotNull(TrainerClasses.CheckConstant(constants, "trainer_class_x"));
        Assert.Empty(TrainerClasses.OtherClassTables(Ref));
    }

    [Fact]
    public void Adding_a_trainer_class_extends_a_count_expression()
    {
        var constants = "#define TRAINER_CLASS_HIKER    1\n#define TRAINER_CLASS_LASS     2\n\n#define TRAINER_CLASS_COUNT    (TRAINER_CLASS_LASS + 1)\n";
        var table = "const struct TrainerClass gTrainerClasses[TRAINER_CLASS_COUNT] =\n{\n    [TRAINER_CLASS_HIKER] = { _(\"HIKER\"), 10 },\n    [TRAINER_CLASS_LASS] = { _(\"LASS\"), 4 },\n};\n";
        var r = TrainerClasses.Add(constants, table, "TRAINER_CLASS_NEW", "NEW", null, null);
        Assert.Equal(3, r.Value);
        Assert.Equal("#define TRAINER_CLASS_HIKER    1\n#define TRAINER_CLASS_LASS     2\n#define TRAINER_CLASS_NEW      3\n\n#define TRAINER_CLASS_COUNT    (TRAINER_CLASS_NEW + 1)\n", r.Constants);
        Assert.EndsWith("    [TRAINER_CLASS_NEW] = { _(\"NEW\") },\n};\n", r.Table);
    }

    private const string ConfigDependentItems = """
        #define TREASURE_FACTOR 2
        const struct ItemInfo gItemsInfo[] =
        {
            [ITEM_A] =
            {
                .name = ITEM_NAME("Alpha"),
                .price = (I_PRICE >= GEN_7) ? 800 : 1200,
                .description = COMPOUND_STRING("A."),
            },
            [ITEM_B] =
            {
                .name = ITEM_NAME("Beta"),
            #if I_PRICE >= GEN_8
                .price = 200,
            #elif I_PRICE == GEN_7
                .price = 300,
            #else
                .price = 250,
            #endif
                .description = COMPOUND_STRING(
                    "Line one\n"
                #if B_FLAG
                    "flag on."),
                #else
                    "flag off."),
                #endif
                .pocket = POCKET_ITEMS,
            },
            [ITEM_C] =
            {
                .name = ITEM_NAME("Gamma"),
                .price = 10 * TREASURE_FACTOR,
                .description = COMPOUND_STRING("C."),
            },
        };

        """;

    private static string ConfigDependentProject()
    {
        var root = TestPaths.NewTempDir("config-items");
        Directory.CreateDirectory(Path.Combine(root, "include", "config"));
        Directory.CreateDirectory(Path.Combine(root, "src", "data"));
        File.WriteAllText(Path.Combine(root, "include", "config", "item.h"),
            "#define GEN_7 6\n#define GEN_8 7\n#define GEN_LATEST GEN_8\n#define I_PRICE GEN_LATEST\n#define B_FLAG FALSE\n");
        File.WriteAllText(Path.Combine(root, "src", "data", "items.h"), ConfigDependentItems);
        return root;
    }

    [Fact]
    public void Config_dependent_values_show_the_current_setting_and_become_fixed_values()
    {
        var root = ConfigDependentProject();
        var items = GameDataCatalog.Load(root, GameDataCatalog.Get("items"));
        int price = Col(items, "Price"), desc = Col(items, "Description");
        GameDataCell C(string key, int col) => Row(items, key).Cells[col];
        Assert.Equal(("800", true), (C("ITEM_A", price).Value, C("ITEM_A", price).Editable));    // ternary, I_PRICE = GEN_8
        Assert.Equal(("200", true), (C("ITEM_B", price).Value, C("ITEM_B", price).Editable));    // #if/#elif/#else
        Assert.Equal(("20", true), (C("ITEM_C", price).Value, C("ITEM_C", price).Editable));     // 10 * TREASURE_FACTOR
        Assert.Equal((@"Line one\nflag off.", true), (C("ITEM_B", desc).Value, C("ITEM_B", desc).Editable)); // #if inside the text
        Assert.Contains("I_PRICE", C("ITEM_B", price).Note);

        C("ITEM_A", price).Value = "900";
        C("ITEM_B", price).Value = "150";
        C("ITEM_B", desc).Value = @"New\ntext.";
        C("ITEM_C", price).Value = "25";
        var text = File.ReadAllText(Path.Combine(root, "src", "data", "items.h"));
        var written = GameDataCatalog.Write(items, "src/data/items.h", text);
        Assert.Contains("        .price = 900,\n", written);
        Assert.Contains("        .name = ITEM_NAME(\"Beta\"),\n        .price = 150,\n        .description = COMPOUND_STRING(\n            \"New\\n\"\n            \"text.\"),\n        .pocket = POCKET_ITEMS,", written);
        Assert.Contains("        .price = 25,\n", written);
        Assert.DoesNotContain("#if", written);
        Assert.Contains("#define TREASURE_FACTOR 2", written);

        File.WriteAllText(Path.Combine(root, "src", "data", "items.h"), written);
        var again = GameDataCatalog.Load(root, GameDataCatalog.Get("items"));
        Assert.Equal(["900", "150", "25"], new[] { "ITEM_A", "ITEM_B", "ITEM_C" }.Select(k => Row(again, k).Cells[price].Value));
        Assert.Equal(@"New\ntext.", Row(again, "ITEM_B").Cells[desc].Value);
        Assert.Null(Row(again, "ITEM_B").Cells[price].Note); // a plain value now
    }

    [Fact]
    public void Config_dependent_values_of_the_reference_project_can_be_changed()
    {
        // #if alternatives (power, abilities), a text that branches inside, a ternary price – each change touches only its entry.
        var cases = new (string Table, string Key, string Column, string Value)[]
        {
            ("moves", "MOVE_JUMP_KICK", "Power", "111"),
            ("moves", "MOVE_ICE_PUNCH", "Description", @"A very cold punch.\nBrr."),
            ("items", "ITEM_ULTRA_BALL", "Price", "999"),
            ("species", "SPECIES_PIDGEY", "Ability 2", "ABILITY_INTIMIDATE"),
        };
        foreach (var (id, key, column, value) in cases)
        {
            var t = Load(id);
            var row = Row(t, key);
            var cell = row.Cells[Col(t, column)];
            Assert.True(cell.Editable, $"{key}.{column}: {cell.Note}");
            var before = t.Rows.Where(r => r.File == row.File && r.Key != key).ToDictionary(r => r.Key, r => string.Join("|", r.Cells.Select(c => c.Value)));
            cell.Value = value;
            var written = Write(t, row.File);
            var reparsed = GameDataCatalog.Load(Ref, t.Def); // the reference stays unchanged; parse the written text instead
            var parsed = CTable.Parse(written, t.Def.Marker, t.Def.KeyPrefix);
            Assert.Equal(CTable.Parse(GameDataCatalog.ReadText(Ref, row.File), t.Def.Marker, t.Def.KeyPrefix).Entries.Count, parsed.Entries.Count);
            var temp = TestPaths.NewTempDir("gd-" + id);
            foreach (var f in new[] { "include/gba/defines.h", "include/constants/global.h" })
                if (File.Exists(Path.Combine(Ref, f))) { Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(temp, f))!); File.Copy(Path.Combine(Ref, f), Path.Combine(temp, f)); }
            Directory.CreateDirectory(Path.Combine(temp, "include", "config"));
            foreach (var f in Directory.EnumerateFiles(Path.Combine(Ref, "include", "config"))) File.Copy(f, Path.Combine(temp, "include", "config", Path.GetFileName(f)));
            foreach (var f in GameDataCatalog.ResolveFiles(Ref, t.Def))
            {
                var dst = Path.Combine(temp, f);
                Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
                File.WriteAllText(dst, f == row.File ? written : GameDataCatalog.ReadText(Ref, f));
            }
            var after = GameDataCatalog.Load(temp, t.Def);
            Assert.Equal(value, Row(after, key).Cells[Col(after, column)].Value);
            foreach (var r in after.Rows.Where(r => r.File == row.File && r.Key != key))
                Assert.Equal(before[r.Key], string.Join("|", r.Cells.Select(c => c.Value)));
            _ = reparsed;
        }
    }

    [Fact]
    public void Adding_a_trainer_class_extends_an_enum()
    {
        var constants = "enum TrainerClassID\n{\n    TRAINER_CLASS_PKMN_TRAINER_1, // Unused\n    TRAINER_CLASS_HIKER,\n#if X\n    TRAINER_CLASS_LASS,\n#endif\n    TRAINER_CLASS_COUNT,\n};\n";
        var table = "const struct TrainerClass gTrainerClasses[TRAINER_CLASS_COUNT] =\n{\n    [TRAINER_CLASS_HIKER] = { _(\"HIKER\"), 10 },\n};\n";
        var r = TrainerClasses.Add(constants, table, "TRAINER_CLASS_NEW", "NEW", 3, null);
        Assert.Equal(3, r.Value);
        Assert.Equal(constants.Replace("    TRAINER_CLASS_COUNT,", "    TRAINER_CLASS_NEW,\n    TRAINER_CLASS_COUNT,"), r.Constants);
        Assert.Contains("    [TRAINER_CLASS_NEW] = { _(\"NEW\"), 3 },\n};\n", r.Table);
        Assert.Throws<InvalidOperationException>(() => TrainerClasses.Add(r.Constants, r.Table, "TRAINER_CLASS_NEW", "NEW", null, null));
    }

    [Fact]
    public void Texts_are_checked_against_charmap_and_length()
    {
        var gt = GameText.Load(Ref);
        Assert.True(gt.HasCharmap);
        Assert.Null(gt.Check("HIKER", 12, false, false).Error);
        Assert.Null(gt.Check("{PKMN} TRAINER", 12, false, false).Error);
        Assert.Equal(10, gt.ByteLength("{PKMN} TRAINER"));
        Assert.NotNull(gt.Check("ABCDEFGHIJKLM", 12, false, false).Error);
        Assert.Null(gt.Check("ABCDEFGHIJKLM", 12, true, false).Error);
        Assert.NotNull(gt.Check("ABCDEFGHIJKLM", 12, true, false).Warning);
        Assert.NotNull(gt.Check("SAY \"HI\"", 20, false, false).Error);
        Assert.NotNull(gt.Check(@"TWO\nLINES", 20, false, false).Error);
        Assert.Null(gt.Check(@"TWO\nLINES", 0, false, true).Error);
        Assert.NotNull(gt.Check(@"BAD\xCODE", 0, false, true).Error);
        Assert.NotNull(gt.Check("日本", 20, false, false).Error);
        Assert.NotNull(gt.Check("{NOT_A_PLACEHOLDER}", 20, false, false).Error);
        Assert.NotNull(gt.Check("", 20, false, false).Error);

        var items = Load("items");
        var row = Row(items, "ITEM_POTION");
        int name = Col(items, "Name"), price = Col(items, "Price");
        row.Cells[name].Value = new string('A', 20);
        Assert.NotNull(items.Validate(row, name).Error);
        row.Cells[name].Value = new string('A', 19);
        Assert.Null(items.Validate(row, name).Error);
        row.Cells[price].Value = "-1";
        Assert.NotNull(items.Validate(row, price).Error);
        row.Cells[price].Value = "1000000";
        Assert.NotNull(items.Validate(row, price).Error);
        var moves = Load("moves");
        var m = Row(moves, "MOVE_POUND");
        m.Cells[Col(moves, "Type")].Value = "TYPE_BOGUS";
        Assert.NotNull(moves.Validate(m, Col(moves, "Type")).Error);
    }

    [Fact]
    public void Conditional_fields_and_duplicate_keys_are_read_only()
    {
        var text = "const struct X gT[] =\n{\n    [K_A] =\n    {\n        .name = _(\"A\"),\n#if CFG\n        .power = 1,\n#else\n        .power = 2,\n#endif\n        .pp = 5,\n    },\n#if Y\n    [K_B] = { .name = _(\"B1\") },\n#else\n    [K_B] = { .name = _(\"B2\") },\n#endif\n};\n";
        var p = CTable.Parse(text, "gT", "K_");
        var a = p.Entry("K_A")!;
        Assert.False(a.Field("name")!.Conditional);
        Assert.True(a.Field("power")!.Conditional);
        Assert.False(a.Field("pp")!.Conditional);
        Assert.Null(p.Entry("K_B"));
        Assert.All(p.Entries.Where(e => e.Key == "K_B"), e => Assert.NotNull(e.ReadOnlyReason));
    }
}
