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
    [InlineData("trainer_classes", 80)]
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
            if (t.Def.Columns[i].Kind == GameDataKind.Constant) Assert.NotEmpty(t.Options[i]!);
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
        // An expression stays read-only.
        Assert.False(Row(tc, "TRAINER_CLASS_PKMN_BREEDER").Cells[Col(tc, "Poké Ball")].Editable);

        var items = Load("items");
        Assert.Equal("POKé BALL", Cell(items, "ITEM_POKE_BALL", "Name"));
        Assert.Equal("200", Cell(items, "ITEM_POKE_BALL", "Price"));
        Assert.Equal(@"A tool used for\ncatching wild\nPOKéMON.", Cell(items, "ITEM_POKE_BALL", "Description"));
        Assert.False(Row(items, "ITEM_ULTRA_BALL").Cells[Col(items, "Price")].Editable);
        Assert.Equal(19, items.MaxLength[Col(items, "Name")]);
        Assert.Contains(items.Rows, r => r.Cells[Col(items, "Description")].Note?.StartsWith("Shared text") == true);

        var moves = Load("moves");
        Assert.Equal("POUND", Cell(moves, "MOVE_POUND", "Name"));
        Assert.Equal("40", Cell(moves, "MOVE_POUND", "Power"));
        Assert.Equal("TYPE_NORMAL", Cell(moves, "MOVE_POUND", "Type"));
        Assert.Equal("DAMAGE_CATEGORY_PHYSICAL", Cell(moves, "MOVE_POUND", "Category"));
        Assert.Contains("TYPE_FIRE", moves.Options[Col(moves, "Type")]!);

        var species = Load("species");
        Assert.Equal("BULBASAUR", Cell(species, "SPECIES_BULBASAUR", "Name"));
        Assert.Equal("45", Cell(species, "SPECIES_BULBASAUR", "HP"));
        Assert.Equal("TYPE_GRASS", Cell(species, "SPECIES_BULBASAUR", "Type 1"));
        Assert.Equal("TYPE_POISON", Cell(species, "SPECIES_BULBASAUR", "Type 2"));
        Assert.Equal("ABILITY_CHLOROPHYLL", Cell(species, "SPECIES_BULBASAUR", "Hidden ability"));
        Assert.Equal("TYPE_FIRE", Cell(species, "SPECIES_CHARMANDER", "Type 2"));
        Assert.Equal(12, species.MaxLength[Col(species, "Name")]);

        Assert.Equal("LITTLEROOT TOWN", Cell(Load("locations"), "MAPSEC_LITTLEROOT_TOWN", "Name"));
        Assert.Equal("STENCH", Cell(Load("abilities"), "ABILITY_STENCH", "Name"));
        Assert.Equal("FIRE", Cell(Load("types"), "TYPE_FIRE", "Name"));
        var natures = Load("natures");
        Assert.Equal("HARDY", Cell(natures, "NATURE_HARDY", "Name"));
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
        var row = items.Rows.First(r => r.Cells[d].Note?.StartsWith("Shared text") == true && r.ReadOnlyReason == null);
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
        Row(tc, "TRAINER_CLASS_HIKER").Cells[Col(tc, "Poké Ball")].Value = "BALL_ULTRA";
        Row(tc, "TRAINER_CLASS_TEAM_ROCKET").Cells[Col(tc, "Poké Ball")].Value = "BALL_NET";
        Row(tc, "TRAINER_CLASS_LADY").Cells[Col(tc, "Name")].Value = "NOBLE";
        var updated = Write(tc, "src/battle_main.c");
        Assert.Contains("[TRAINER_CLASS_PKMN_TRAINER_1] = { _(\"{PKMN} TRAINER\"), 7 },", updated);
        Assert.Contains("[TRAINER_CLASS_HIKER] = { _(\"HIKER\"), 10, BALL_ULTRA },", updated);
        Assert.Contains("[TRAINER_CLASS_TEAM_ROCKET] = { _(\"TEAM ROCKET\"), 0, BALL_NET },", updated);
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
        var oldCount = System.Text.RegularExpressions.Regex.Match(constants, @"#define TRAINER_CLASS_COUNT\s+(\S+)").Groups[1].Value;
        long n = Convert.ToInt64(oldCount, oldCount.StartsWith("0x") ? 16 : 10);

        var result = TrainerClasses.Add(constants, table, "TRAINER_CLASS_TESTER", "TESTER", 12, null);
        Assert.Equal(n, result.Value);
        var defines = new TilesetEditor.Core.Project.CDefines();
        defines.AddFromText(result.Constants);
        Assert.Equal(n, defines.TryEvaluate("TRAINER_CLASS_TESTER"));
        Assert.Equal(n + 1, defines.TryEvaluate("TRAINER_CLASS_COUNT"));
        // Aligned like the neighbours, directly before the count.
        var lines = result.Constants.Split('\n');
        int i = Array.FindIndex(lines, l => l.StartsWith("#define TRAINER_CLASS_TESTER"));
        Assert.StartsWith("#define TRAINER_CLASS_COUNT", lines[i + 1]);
        Assert.Equal(lines[i + 1].IndexOf("0x", StringComparison.Ordinal), lines[i].IndexOf("0x", StringComparison.Ordinal));
        // Only two lines differ: the new define and the count.
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
