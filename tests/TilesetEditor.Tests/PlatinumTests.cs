using System.Text.RegularExpressions;
using System.Text;
using TilesetEditor.Core.GameData;
using TilesetEditor.Core.Imaging;
using TilesetEditor.Core.Platinum;
using TilesetEditor.Core.Project;

namespace TilesetEditor.Tests;

public class PlatinumTests
{
    /// <summary>A pret/pokeplatinum checkout (read only). Override with TILESETEDITOR_PLATINUM_REFERENCE; tests pass without checking when missing.</summary>
    private static string PlatinumRoot => TestPaths.Setting("TILESETEDITOR_PLATINUM_REFERENCE", "pokeplatinum");

    private static bool HasPlatinum => GameDetector.Detect(PlatinumRoot) == GameKind.Platinum;

    // ------------------------------------------------------------------ JSON text

    [Fact]
    public void LocateFindsNestedValuesWithCharOffsets()
    {
        const string text = "{\n    \"name\": \"Pokémon’s\",\n    \"stats\": { \"hp\": 45, \"list\": [ 1, \"x\" ] },\n    \"ok\": true\n}";
        var spans = JsonText.Locate(text);
        Assert.Equal("\"Pokémon’s\"", spans["name"].Raw);
        Assert.Equal("45", spans["stats.hp"].Raw);
        Assert.Equal("\"x\"", spans["stats.list[1]"].Raw);
        Assert.Equal("true", spans["ok"].Raw);
        Assert.Equal("Pokémon’s", JsonText.Display(spans, "name"));
        var changed = JsonText.Replace(text, [(spans["stats.hp"], "50"), (spans["name"], JsonText.Quote("Évoli"))]);
        Assert.Equal(text.Replace("45", "50").Replace("Pokémon’s", "Évoli"), changed);
    }

    [Fact]
    public void ReplaceRefusesAStaleSpan()
    {
        var spans = JsonText.Locate("{ \"a\": 1 }");
        Assert.Throws<InvalidOperationException>(() => JsonText.Replace("{ \"a\": 22 }", [(spans["a"], "3")]));
    }

    [Fact]
    public void DumpMatchesPythonIndentStyle()
    {
        const string text = "{\n    \"name\": \"A\\nB\",\n    \"items\": [],\n    \"party\": [\n        {\n            \"level\": 5,\n            \"item\": null\n        }\n    ],\n    \"f\": 15.20\n}";
        Assert.Equal(text, JsonText.Dump(JsonText.Parse(text), 4));
    }

    [Fact]
    public void LinesRoundTripThroughEscapes()
    {
        var real = "First line\nsecond\rnext box\fscroll";
        var shown = JsonText.ToEscapes(real);
        Assert.Equal("First line\\nsecond\\rnext box\\fscroll", shown);
        Assert.Equal(real, JsonText.FromEscapes(shown));
        Assert.Equal(["First line\n", "second\r", "next box\f", "scroll"], JsonText.SplitLines(real));
    }

    [Fact]
    public void PlatinumTextChecksControlsAndCharmap()
    {
        var t = GameText.ForPlatinum("// comment\n012B=A\n012C=B\nE000=\\n\n0100={STRVAR_1}\n");
        Assert.Null(t.Check("AB", 10, false, false).Error);
        Assert.NotNull(t.Check("AC", 10, false, false).Error);       // C is not in the charmap
        Assert.Null(t.Check("A\\nB", 10, false, true).Error);
        Assert.Null(t.Check("A\\rB\\fA", 10, false, true).Error);
        Assert.NotNull(t.Check("A\\pB", 10, false, true).Error);     // \p is Emerald
        Assert.NotNull(t.Check("A\\nB", 10, false, false).Error);    // no line breaks in names
        Assert.Null(t.Check("A{STRVAR_1}", 10, false, false).Error);
    }

    // ------------------------------------------------------------------ trainers, learnsets, land data

    private const string TrainerFile = """
        {
            "name": "Dennis",
            "class": "TRAINER_CLASS_ACE_TRAINER_MALE",
            "items": [],
            "ai_flags": [
                "AI_FLAG_BASIC"
            ],
            "double_battle": false,
            "party": [
                {
                    "species": "SPECIES_GLIGAR",
                    "form": 0,
                    "level": 24,
                    "item": null,
                    "moves": [
                        "MOVE_SCREECH"
                    ],
                    "iv_scale": 50,
                    "ball_seal": 0
                }
            ],
            "messages": [
                {
                    "type": "TRMSG_PRE_BATTLE",
                    "en_US": [
                        "I’ll take on anyone!\n",
                        "It can only make me stronger!\r"
                    ],
                    "de_DE": "Ich!"
                }
            ]
        }
        """;

    [Fact]
    public void TrainerRoundTripsAndChangesOnlyEditedValues()
    {
        var text = TrainerFile.Replace("\r\n", "\n");
        var t = PlatinumTrainers.Parse("res/trainers/data/x.json", text);
        Assert.Equal(text, PlatinumTrainers.Serialize(t));
        Assert.Equal("I’ll take on anyone!\nIt can only make me stronger!\r", t.Messages[0].Text);

        t.Name = "Denise";
        t.DoubleBattle = true;
        t.Party.Add(new PlatinumTrainerMon { Species = "SPECIES_BIDOOF", Level = 12 });
        t.Party[0].Item = "ITEM_ORAN_BERRY";
        t.Messages[0] = (t.Messages[0].Type, "Hello!\nBye.");
        var outText = PlatinumTrainers.Serialize(t);
        Assert.Contains("\"name\": \"Denise\"", outText);
        Assert.Contains("\"double_battle\": true", outText);
        Assert.Contains("\"item\": \"ITEM_ORAN_BERRY\"", outText);
        Assert.Contains("\"species\": \"SPECIES_BIDOOF\"", outText);
        Assert.Contains("\"moves\": null", outText);
        Assert.Contains("\"de_DE\": \"Ich!\"", outText);       // other languages stay
        Assert.Contains("\"Hello!\\n\",\n                \"Bye.\"", outText);
    }

    [Fact]
    public void LearnsetIsWrittenInTheSpeciesFileStyle()
    {
        const string data = "{\n    \"learnset\": {\n        \"by_level\": [\n            [ 1, \"MOVE_TACKLE\" ],\n            [ 3, \"MOVE_GROWL\" ]\n        ],\n        \"by_tm\": [  ]\n    }\n}\n";
        var entries = PlatinumLearnsets.Read(data);
        Assert.Equal(data, PlatinumLearnsets.Write(data, entries));
        var more = entries.Append(new PlatinumLearnsets.Entry(9, "MOVE_VINE_WHIP")).ToList();
        Assert.Contains("            [ 9, \"MOVE_VINE_WHIP\" ]\n        ],", PlatinumLearnsets.Write(data, more));
        Assert.Contains("\"by_level\": [  ]", PlatinumLearnsets.Write(data, []));
    }

    [Fact]
    public void LandDataAttributesAndProps()
    {
        var data = new byte[16 + 0x800 + 0x30];
        BitConverter.GetBytes(0x800).CopyTo(data, 0);
        BitConverter.GetBytes(0x30).CopyTo(data, 4);
        // tile (1, 0): tall grass; tile (0, 1): blocked
        BitConverter.GetBytes((ushort)0x0002).CopyTo(data, 16 + 2);
        BitConverter.GetBytes((ushort)0x8000).CopyTo(data, 16 + 32 * 2);
        int o = 16 + 0x800;
        BitConverter.GetBytes(7).CopyTo(data, o);
        BitConverter.GetBytes(-88 * 4096).CopyTo(data, o + 4);   // x = -5.5 tiles from the centre
        BitConverter.GetBytes(16 * 4096).CopyTo(data, o + 8);
        BitConverter.GetBytes(0).CopyTo(data, o + 12);
        var land = PlatinumLandData.Parse(data, ["a", "b", "c", "d", "e", "f", "g", "pokecenter"]);
        Assert.Equal(2, land.Behavior(1, 0));
        Assert.False(land.Collision(1, 0));
        Assert.True(land.Collision(0, 1));
        var prop = Assert.Single(land.Props);
        Assert.Equal((7, 10.5, 1.0, 16.0, "pokecenter"), (prop.ModelId, prop.X, prop.Y, prop.Z, prop.ModelName));
        Assert.Equal("Tall grass", PlatinumMaps.BehaviorGroup(2));
    }

    [Fact]
    public void DetectorTellsTheGamesApart()
    {
        var dir = TestPaths.NewTempDir("detect");
        Assert.Null(GameDetector.Detect(dir));
        foreach (var f in GameDetector.PlatinumMarkers.Take(2))
        {
            var p = Path.Combine(dir, f.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(p)!);
            File.WriteAllText(p, "");
        }
        Assert.Equal(GameKind.Platinum, GameDetector.Detect(dir));
    }

    // ------------------------------------------------------------------ against a real pokeplatinum checkout

    [Fact]
    public void EveryTrainerOfTheReferenceRoundTrips()
    {
        if (!HasPlatinum) return;
        foreach (var t in PlatinumTrainers.LoadAll(PlatinumRoot))
            Assert.True(PlatinumTrainers.Serialize(t) == t.OriginalText, t.File);
    }

    [Fact]
    public void EveryLearnsetOfTheReferenceRoundTrips()
    {
        if (!HasPlatinum) return;
        foreach (var dir in Directory.GetDirectories(Path.Combine(PlatinumRoot, "res", "pokemon")))
        {
            var f = Path.Combine(dir, "data.json");
            if (!File.Exists(f)) continue;
            var text = File.ReadAllText(f, Encoding.UTF8);
            Assert.True(PlatinumLearnsets.Write(text, PlatinumLearnsets.Read(text)) == text, f);
        }
    }

    [Fact]
    public void GameDataTablesLoadAndWriteSingleValues()
    {
        if (!HasPlatinum) return;
        var gd = PlatinumGameData.Instance;
        var text = gd.LoadText(PlatinumRoot);
        foreach (var def in gd.Tables)
        {
            var t = gd.Load(PlatinumRoot, def, text);
            Assert.NotEmpty(t.Rows);
            var row = t.Rows[1];
            int col = Array.FindIndex(row.Cells, c => c.Editable && def.Columns[Array.IndexOf(row.Cells, c)].Kind != GameDataKind.Constant);
            if (col < 0) continue;
            var file = def.Columns[col].SourceFile ?? row.File;
            var original = File.ReadAllText(Path.Combine(PlatinumRoot, file), Encoding.UTF8);
            row.Cells[col].Value = def.Columns[col].Kind == GameDataKind.Number ? "77" : "Zz";
            var written = gd.Write(t, file, original);
            var diff = original.Split('\n').Zip(written.Split('\n')).Count(z => z.First != z.Second);
            Assert.True(diff == 1, $"{def.Id}: {diff} lines changed");
        }
    }

    [Fact]
    public void MapsOfTheReferenceRender()
    {
        if (!HasPlatinum) return;
        var p = PlatinumProject.Load(PlatinumRoot);
        var maps = new PlatinumMaps(PlatinumRoot);
        var h = p.MapHeaders.First(x => x.Id == "MAP_HEADER_JUBILIFE_CITY");
        var m = maps.Matrix(h.Matrix!)!;
        var area = maps.Area(h, m)!.Value;
        Assert.Equal((4, 23, 2, 2), area);
        var img = maps.Render(m, area, 2);
        Assert.Equal((128, 128), (img.Width, img.Height));
        Assert.Contains(maps.Events(h), e => e.Kind == PlatinumEventKind.Warp && e.X >= 128 && e.X < 192);
    }

    [Fact]
    public void MapModelsOfTheReferenceRenderWithTextures()
    {
        if (!HasPlatinum) return;
        var p = PlatinumProject.Load(PlatinumRoot);
        var maps = new PlatinumMaps(PlatinumRoot);
        var h = p.MapHeaders.First(x => x.Id == "MAP_HEADER_JUBILIFE_CITY");
        var m = maps.Matrix(h.Matrix!)!;
        var area = maps.Area(h, m)!.Value;
        var model = NitroModel.Load(maps.Land(m.MapAt(area.X, area.Y))!.Model)!;
        Assert.NotEmpty(model.Triangles);
        Assert.InRange(model.Triangles.Max(t => t.X0) - model.Triangles.Min(t => t.X0), 400, 600); // one chunk = 512 units
        var img = maps.RenderTextured(m, area, 2, (_, _) => h.AreaData);
        // Most of the city is drawn from textures (not the dark background).
        int drawn = img.Pixels.Count(px => px.R + px.G + px.B > 3 * 40);
        Assert.True(drawn > img.Pixels.Length * 0.9, $"{drawn} of {img.Pixels.Length} pixels drawn");
    }

    [Fact]
    public void SpriteReplacementKeepsOnePaletteForAllSheets()
    {
        if (!HasPlatinum) return;
        var set = PlatinumSpriteSet.Load(PlatinumRoot, "SPECIES_BULBASAUR");
        var picture = set.Frame(PlatinumSpritePart.MaleBack, 0, false)!;
        var (files, _) = PlatinumSpriteImport.Replace(set, back: false, picture, femaleToo: false);
        Assert.Contains(files, f => f.Path.EndsWith("/normal.pal"));
        Assert.Contains(files, f => f.Path.EndsWith("/shiny.pal"));
        foreach (var f in files.Where(f => f.Path.EndsWith(".png")))
        {
            var png = Png.Decode(f.Content);
            Assert.True(png.IsIndexed);
            Assert.Equal((160, 80, 4), (png.Width, png.Height, png.BitDepth));
            Assert.All(png.Indices!, i => Assert.True(i < 16));
        }
    }

    // ------------------------------------------------------------------ trainer classes, healthboxes, mods

    [Fact]
    public void NewTrainerClassTableEntryIsAlignedAndLast()
    {
        const string table = "static u8 sTrainerClassPrizeMul[] = {\n    [TRAINER_CLASS_A]        = 0,\n    [TRAINER_CLASS_LONGER_B] = 4,\n};\n// clang-format on\n";
        var result = PlatinumTrainerClasses.AppendTableEntry(table, "TRAINER_CLASS_REYA", "20");
        Assert.Equal("static u8 sTrainerClassPrizeMul[] = {\n    [TRAINER_CLASS_A]        = 0,\n    [TRAINER_CLASS_LONGER_B] = 4,\n    [TRAINER_CLASS_REYA]     = 20,\n};\n// clang-format on\n", result);
    }

    [Fact]
    public void NewTrainerClassMessageContinuesTheIds()
    {
        const string bank = "{\n  \"key\": 1,\n  \"messages\": [\n    {\n      \"id\": \"pl_msg_00000619_00000\",\n      \"en_US\": \"A\"\n    },\n    {\n      \"id\": \"pl_msg_00000619_00001\",\n      \"en_US\": \"B\"\n    }\n  ]\n}";
        var result = PlatinumTrainerClasses.AppendMessage(bank, 2, "Empress");
        Assert.Contains("    },\n    {\n      \"id\": \"pl_msg_00000619_00002\",\n      \"en_US\": \"Empress\"\n    }\n  ]\n}", result);
        Assert.Throws<InvalidDataException>(() => PlatinumTrainerClasses.AppendMessage(bank, 5, "X")); // lists out of step
    }

    [Fact]
    public void HealthboxEightBitIsRepairedLosslessly()
    {
        var pal = Enumerable.Range(0, 16).Select(i => new Rgba32((byte)(i * 16), 0, 0)).ToArray();
        var idx = Enumerable.Range(0, 128 * 64).Select(i => (byte)(i % 16)).ToArray();
        var eight = Png.Decode(Png.EncodeIndexed(128, 64, idx, pal, 8));
        Assert.Equal(8, eight.BitDepth);
        var four = Png.Decode(PlatinumHealthbox.To4bpp(eight));
        Assert.Equal(4, four.BitDepth);
        Assert.Equal(idx, four.Indices);
        // Mapping to the master palette uses the master's numbers for the same colours.
        var shuffled = pal.Reverse().ToArray();
        var picture = Png.Decode(Png.EncodeIndexed(128, 64, idx, shuffled, 4)).Rgba;
        var mapped = Png.Decode(PlatinumHealthbox.MapToMaster(picture, pal));
        Assert.Equal(idx.Select(i => (byte)(15 - i)), mapped.Indices);
        picture.Pixels[0] = new Rgba32(1, 2, 3);
        Assert.Throws<InvalidDataException>(() => PlatinumHealthbox.MapToMaster(picture, pal));
    }

    [Fact]
    public void PlatinumModsAreValidAndTheirSettingsRoundTrip()
    {
        var mods = "mods";
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "TilesetEditor.sln"))) { mods = Path.Combine(d.FullName, "mods"); break; }
        var (all, problems) = TilesetEditor.Export.Mods.ModPackage.LoadAll([mods]);
        Assert.Empty(problems);
        var platinum = all.Where(m => m.Manifest.GameKind == GameKind.Platinum).ToList();
        Assert.Equal(["platinum-defined-protagonist", "platinum-exp-share", "platinum-hms-from-bag", "platinum-level-caps", "platinum-mugshots", "platinum-reusable-tms"], platinum.Select(m => m.Manifest.Id).OrderBy(x => x).ToArray());
        Assert.All(all.Where(m => m.Manifest.Id == "dialog-portraits"), m => Assert.Equal(GameKind.Emerald, m.Manifest.GameKind));

        var caps = platinum.Single(m => m.Manifest.Id == "platinum-level-caps");
        var root = TestPaths.NewTempDir("caps");
        Directory.CreateDirectory(Path.Combine(root, "src"));
        File.Copy(Path.Combine(caps.Folder, "files", "src", "level_caps.c"), Path.Combine(root, "src", "level_caps.c"));
        Assert.Equal(9, caps.Manifest.Settings.Count);
        Assert.Equal([14, 22, 26, 32, 37, 41, 44, 50, 62], caps.Manifest.Settings.Select(s => TilesetEditor.Export.Mods.ModPlanner.ReadSetting(root, s)).ToArray());
        var file = Path.Combine(root, "src", "level_caps.c");
        var text = File.ReadAllText(file);
        var changed = TilesetEditor.Export.Mods.ModPlanner.WriteSetting(text, caps.Manifest.Settings[0], 16);
        changed = TilesetEditor.Export.Mods.ModPlanner.WriteSetting(changed, caps.Manifest.Settings[8], 100);
        Assert.Contains("{ BADGE_ID_COAL, 16 }", changed);
        Assert.Contains("#define LEVEL_CAP_LEAGUE 100", changed);
        Assert.Equal(text.Split('\n').Length, changed.Split('\n').Length);
        Assert.Throws<InvalidOperationException>(() => TilesetEditor.Export.Mods.ModPlanner.WriteSetting(text, caps.Manifest.Settings[0], 101));
    }

    [Fact]
    public void DefinedProtagonistNamesAndGenderAreSettings()
    {
        var mods = "mods";
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "TilesetEditor.sln"))) { mods = Path.Combine(d.FullName, "mods"); break; }
        var mod = TilesetEditor.Export.Mods.ModPackage.Load(Path.Combine(mods, "platinum-defined-protagonist"));
        var root = TestPaths.NewTempDir("protagonist");
        Directory.CreateDirectory(Path.Combine(root, "include"));
        File.Copy(Path.Combine(mod.Folder, "files", "include", "defined_protagonist.h"), Path.Combine(root, "include", "defined_protagonist.h"));
        var s = mod.Manifest.Settings;
        Assert.Equal(["Lucas", "Barry", "GENDER_MALE"], s.Select(x => TilesetEditor.Export.Mods.ModPlanner.ReadSettingText(root, x)).ToArray());
        var file = Path.Combine(root, "include", "defined_protagonist.h");
        var text = File.ReadAllText(file);
        text = TilesetEditor.Export.Mods.ModPlanner.WriteSettingText(text, s[0], "Reya");
        text = TilesetEditor.Export.Mods.ModPlanner.WriteSettingText(text, s[1], "Vyral");
        text = TilesetEditor.Export.Mods.ModPlanner.WriteSettingText(text, s[2], "GENDER_FEMALE");
        Assert.Contains("sDefinedPlayerName[] = { CHAR_R, CHAR_e, CHAR_y, CHAR_a, CHAR_EOS };", text);
        Assert.Contains("#define DEFINED_PROTAGONIST_GENDER GENDER_FEMALE", text);
        File.WriteAllText(file, text);
        Assert.Equal(["Reya", "Vyral", "GENDER_FEMALE"], s.Select(x => TilesetEditor.Export.Mods.ModPlanner.ReadSettingText(root, x)).ToArray());
        Assert.Throws<InvalidOperationException>(() => TilesetEditor.Export.Mods.ModPlanner.WriteSettingText(text, s[0], "TooLongName"));
        Assert.Throws<InvalidOperationException>(() => TilesetEditor.Export.Mods.ModPlanner.WriteSettingText(text, s[0], "Ré"));
        Assert.Throws<InvalidOperationException>(() => TilesetEditor.Export.Mods.ModPlanner.WriteSettingText(text, s[2], "GENDER_OTHER"));
        Assert.Equal("Dr. K", TilesetEditor.Export.Mods.ModPlanner.CharcodesToText(TilesetEditor.Export.Mods.ModPlanner.TextToCharcodes("Dr. K")));
    }

    [Fact]
    public void BackupFolderNamesHaveNoInvalidCharacters()
    {
        var name = TilesetEditor.Export.ExportApplier.SafeFolderName("Switch on: Dialog mugshots");
        Assert.DoesNotContain(':', name);
        Assert.Contains("Dialog mugshots", name);
        Assert.DoesNotContain('/', TilesetEditor.Export.ExportApplier.SafeFolderName("a/b\\c*?\"<>|"));
    }

    [Fact]
    public void DialogTextIsWrittenLikeAWriterThinks()
    {
        // Enter = next line, empty line = next box; third line of a box scrolls (\f).
        Assert.Equal("Hello!\nHow are you?\rFine.\r", PlatinumDialogText.FromNatural("Hello!\nHow are you?\n\nFine.", true));
        Assert.Equal("a\nb\fc", PlatinumDialogText.FromNatural("a\nb\nc", false));
        // The game's font has only typographic quotes.
        Assert.Equal("Let’s go, “Reya”! ‘Hi’ {STRVAR_1 3, 0, 0}", PlatinumDialogText.FromNatural("Let's go, \"Reya\"! 'Hi' {STRVAR_1 3, 0, 0}", false));
        Assert.Equal("Hello!\nHow are you?\n\nFine.", PlatinumDialogText.ToNatural("Hello!\nHow are you?\rFine.\r"));
        foreach (var real in new[] { "x\ny\rz\r", "one\ntwo\fthree" })
            Assert.Equal(real, PlatinumDialogText.FromNatural(PlatinumDialogText.ToNatural(real), real.EndsWith('\r')));
    }

    [Fact]
    public void ScriptBlocksBecomeAScriptWithMessages()
    {
        var d = new PlatinumScriptBlocks.Dialect("SEQ_SE_CONFIRM", false);
        var blocks = new List<ScriptBlock>
        {
            new() { Kind = ScriptBlockKind.OnlyOnce, Constant = "FLAG_UNUSED_0x0001", Text = "Good luck!" },
            new() { Kind = ScriptBlockKind.Mugshot, Constant = "MUGSHOT_EXAMPLE" },
            new() { Kind = ScriptBlockKind.Message, Text = "Hi " + PlatinumDialogText.PlayerName + "!\n\nTake this." },
            new() { Kind = ScriptBlockKind.GiveItem, Constant = "ITEM_POTION", Number = 2 },
            new() { Kind = ScriptBlockKind.Question, Text = "Battle?", YesText = "Go!", NoText = "Pity." },
            new() { Kind = ScriptBlockKind.TrainerBattle, Constant = "TRAINER_YOUNGSTER_TRISTAN" },
        };
        var built = PlatinumScriptBlocks.Build("Town_Kid", "Town_Text_Kid", blocks, d);
        Assert.Equal(["Town_Text_Kid1", "Town_Text_Kid2", "Town_Text_Kid3", "Town_Text_Kid4", "Town_Text_Kid5"], built.Messages.Select(m => m.Id).ToArray());
        Assert.Equal("Hi " + PlatinumDialogText.PlayerName + "!\rTake this.", built.Messages[1].Text);
        Assert.Equal(["constants/mugshots.h"], built.Includes);
        Assert.StartsWith("    PlaySE SEQ_SE_CONFIRM\n    LockAll\n    FacePlayer\n    GoToIfSet FLAG_UNUSED_0x0001, Town_Kid_AlreadyDone\n", built.Body);
        Assert.Contains("    BufferPlayerName 0\n    Message Town_Text_Kid2\n", built.Body);
        Assert.Contains("    CallCommonScript CommonScript_AddItemQuantity\n", built.Body);
        Assert.Contains("    CloseMessage\n    StartTrainerBattle TRAINER_YOUNGSTER_TRISTAN\n", built.Body);
        Assert.Contains("    SetFlag FLAG_UNUSED_0x0001\n", built.Body);
        Assert.Contains("Town_Kid_AlreadyDone:\n    Message Town_Text_Kid1\n", built.Body);
        Assert.Contains("Town_Kid_LostBattle:\n    BlackOutFromBattle\n", built.Body);
        Assert.Contains("    Common_GiveItemQuantity\n", PlatinumScriptBlocks.Build("A", "B", blocks, d with { OldItemMacro = true }).Body);
        Assert.Throws<InvalidDataException>(() => PlatinumScriptBlocks.Build("A", "B", [new ScriptBlock { Kind = ScriptBlockKind.GiveItem }], d));
        Assert.Equal("SEQ_FANFA4_sseq", new PlatinumScriptBlocks.Dialect("SEQ_SE_CONFIRM_sseq_3", false).Fanfare("SEQ_FANFA4"));
    }

    [Fact]
    public void BlockScriptsAreKeptAsBlocksAndReplacedInPlace()
    {
        var d = new PlatinumScriptBlocks.Dialect("SEQ_SE_CONFIRM", false);
        var dir = TestPaths.NewTempDir("blocks");
        File.WriteAllText(Path.Combine(dir, "t.json"), "{\n  \"key\": 1,\n  \"messages\": [\n    {\n      \"id\": \"Town_Text_Kid\",\n      \"en_US\": \"Old\"\n    }\n  ]\n}\n");
        var bank = PlatinumJsonFile.Load(dir, "t.json", 2);
        var blocks = new List<ScriptBlock> { new() { Kind = ScriptBlockKind.Message, Text = "Hi!" }, new() { Kind = ScriptBlockKind.Question, Text = "Ok?", YesText = "Yes!", NoText = "No…" } };
        var prefix = PlatinumScriptBlocks.FreeTextPrefix(bank, "Town_Text_Kid");
        Assert.Equal("Town_Text_Kid", prefix);   // "Town_Text_Kid" alone is no numbered id
        var built = PlatinumScriptBlocks.Build("Town_OldMan", prefix, blocks, d, ScriptStart.Person);
        var (script, number) = PlatinumScriptBlocks.WriteScript(MapScript, "Town_OldMan", prefix, blocks, built);
        Assert.Equal(3, number);
        PlatinumScriptBlocks.WriteMessages(bank, prefix, built.Messages);
        Assert.Contains(PlatinumScriptBlocks.Marker, script);
        Assert.Contains("Town_OldMan:\n    PlaySE SEQ_SE_CONFIRM\n", script);
        var read = PlatinumScriptBlocks.ReadBlocks(script, "Town_OldMan");
        Assert.NotNull(read);
        Assert.Equal(prefix, read!.Value.TextPrefix);
        Assert.Equal([ScriptBlockKind.Message, ScriptBlockKind.Question], read.Value.Blocks.Select(b => b.Kind).ToArray());
        Assert.Equal("No…", read.Value.Blocks[1].NoText);
        Assert.Null(PlatinumScriptBlocks.ReadBlocks(script, "Town_Kid"));

        // Changed blocks replace the script with its helper labels; the next script stays.
        var fewer = new List<ScriptBlock> { new() { Kind = ScriptBlockKind.Message, Text = "Bye!" } };
        var built2 = PlatinumScriptBlocks.Build("Town_OldMan", prefix, fewer, d, ScriptStart.Person);
        var (script2, added) = PlatinumScriptBlocks.WriteScript(script, "Town_OldMan", prefix, fewer, built2);
        Assert.Equal(0, added);
        Assert.DoesNotContain("Town_OldMan_No1", script2);
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(script2, "PokePat blocks"));
        Assert.Contains("Town_Sign:\n    End\n", script2);
        Assert.EndsWith("    .balign 4, 0\n", script2);
        PlatinumScriptBlocks.WriteMessages(bank, prefix, built2.Messages);
        var texts = PlatinumMapScripts.Messages(bank).OfType<System.Text.Json.Nodes.JsonObject>().ToDictionary(m => m["id"]!.GetValue<string>(), PlatinumMapScripts.MessageText);
        Assert.Equal("Bye!", texts[prefix + "1"]);
        Assert.Equal("", texts[prefix + "2"]);       // no longer needed: emptied, not removed
        Assert.Equal("Old", texts["Town_Text_Kid"]);
        Assert.Equal("Town_Text_KidV2_", PlatinumScriptBlocks.FreeTextPrefix(bank, prefix));

        // A hand-written script: only its own lines are replaced.
        var hand = MapScript.Replace("Town_Kid:\n    End\n", "Town_Kid:\n    GoTo Town_Kid_Helper\n\nTown_Kid_Helper:\n    End\n");
        var built3 = PlatinumScriptBlocks.Build("Town_Kid", "Town_Text_KidNew", fewer, d, ScriptStart.Sign);
        var (script3, _) = PlatinumScriptBlocks.WriteScript(hand, "Town_Kid", "Town_Text_KidNew", fewer, built3);
        Assert.Contains("Town_Kid_Helper:\n    End\n", script3);
        Assert.DoesNotContain("GoTo Town_Kid_Helper", script3);
        Assert.Contains("Town_Kid:\n    PlaySE SEQ_SE_CONFIRM\n    LockAll\n    Message Town_Text_KidNew1\n", script3);
    }

    [Fact]
    public void HandWrittenScriptsAreReadAsSteps()
    {
        const string script = "Town_Kid:\n    PlaySE SEQ_SE_CONFIRM\n    LockAll\n    FacePlayer\n    GoToIfSet FLAG_X, Town_Kid_Done\n    Message Town_Text_Hello\n    ShowYesNoMenu VAR_RESULT\n" +
                              "    GoToIfEq VAR_RESULT, MENU_NO, Town_Kid_No\n    SetVar VAR_0x8004, ITEM_POTION\n    SetVar VAR_0x8005, 2\n    CallCommonScript CommonScript_AddItemQuantity\n" +
                              "    StartTrainerBattle TRAINER_YOUNGSTER_TRISTAN\n    SetFlag FLAG_X\n    End\n\nTown_Kid_No:\n    Message Town_Text_Pity\n    End\n\nTown_Kid_Done:\n    End\n";
        var texts = new Dictionary<string, string> { ["Town_Text_Hello"] = "Hello!\nWant a Potion?\r", ["Town_Text_Pity"] = "Pity." };
        var steps = PlatinumScriptFlow.Steps(script, "Town_Kid", id => texts.GetValueOrDefault(id));
        var main = steps.Where(s => s.Kind != ScriptStepKind.Technical).ToList();
        Assert.Equal("If FLAG_X is set, continue at Town_Kid_Done", main[0].Text);
        Assert.Equal("Hello! Want a Potion?", main[1].Text);
        Assert.Equal("Town_Text_Hello", main[1].MessageId);
        Assert.Equal(ScriptStepKind.Question, main[2].Kind);
        Assert.Equal("If the player says NO, continue at Town_Kid_No", main[3].Text);
        Assert.Equal("Pity.", main[3].Children.Single(c => c.Kind == ScriptStepKind.Message).Text);
        Assert.Equal("Give item Potion ×2", main[4].Text);
        Assert.Equal("Trainer battle: Youngster Tristan", main[5].Text);
        Assert.Equal("Set FLAG_X", main[6].Text);
    }

    [Fact]
    public void FlagsAreNumberedAndTrainerFlagsFollowTheTrainers()
    {
        const string text = "FLAG_UNUSED_0x0000\nMAP_LOCAL_FLAGS_START\nFLAG_MAP_LOCAL_0x01 = MAP_LOCAL_FLAGS_START\nFLAG_STORY\nFLAG_HIDE_ROUTE_201_RIVAL\n" +
                            "TRAINER_DEFEATED_FLAGS_START\nFLAG_DEFEATED_TRAINER_NONE = TRAINER_DEFEATED_FLAGS_START\nFLAG_DEFEATED_TRAINER_A\n" +
                            "TRAINER_DEFEATED_FLAGS_END = FLAG_DEFEATED_TRAINER_A\nFLAG_UNK_0x0007\nFLAG_UNK_0x0008\nVARS_START = 16384\nVAR_STORY = VARS_START\nVAR_UNUSED_0x4001\n";
        var all = PlatinumFlags.Parse(text);
        Assert.Equal([("FLAG_UNUSED_0x0000", 0), ("FLAG_MAP_LOCAL_0x01", 1), ("FLAG_STORY", 2), ("FLAG_HIDE_ROUTE_201_RIVAL", 3), ("FLAG_DEFEATED_TRAINER_NONE", 4), ("FLAG_DEFEATED_TRAINER_A", 5),
            ("FLAG_UNK_0x0007", 6), ("FLAG_UNK_0x0008", 7), ("VAR_STORY", 16384), ("VAR_UNUSED_0x4001", 16385)], all.Select(f => (f.Name, f.Value)).ToArray());
        Assert.Equal(["Free flags", "Map-local flags", "Story & events", "Hide people", "Trainers defeated", "Trainers defeated", "Free flags", "Free flags", "Story variables", "Free variables"],
            all.Select(f => f.Group).ToArray());

        var root = TestPaths.NewTempDir("flags");
        Directory.CreateDirectory(Path.Combine(root, "generated"));
        File.WriteAllText(Path.Combine(root, PlatinumFlags.File), text);
        File.WriteAllText(Path.Combine(root, PlatinumFlags.TrainersFile), "TRAINER_NONE\nTRAINER_A\nTRAINER_B\n");
        var trainers = PlatinumFlags.TrainerFlags(root, all);
        Assert.Equal([false, false, true], trainers.Select(t => t.Missing).ToArray());
        Assert.Equal("FLAG_UNK_0x0007", trainers[2].Flag);
        var fixedText = PlatinumFlags.FixTrainerFlags(text, trainers, _ => false);
        var after = PlatinumFlags.Parse(fixedText);
        Assert.Equal(6, after.Single(f => f.Name == "FLAG_DEFEATED_TRAINER_B").Value);
        Assert.Equal(7, after.Single(f => f.Name == "FLAG_UNK_0x0008").Value);
        Assert.Equal(6, PlatinumFlags.ValueOf(fixedText, "TRAINER_DEFEATED_FLAGS_END"));
        File.WriteAllText(Path.Combine(root, PlatinumFlags.File), fixedText);
        Assert.All(PlatinumFlags.TrainerFlags(root, after), t => Assert.False(t.Missing));
        Assert.Throws<InvalidDataException>(() => PlatinumFlags.FixTrainerFlags(text, trainers, n => n == "FLAG_UNK_0x0007"));

        var renamed = PlatinumFlags.Rename(text, all[0], "FLAG_GOT_REYA_GIFT");
        Assert.Equal(0, PlatinumFlags.Parse(renamed).Single(f => f.Name == "FLAG_GOT_REYA_GIFT").Value);
        Assert.Throws<InvalidDataException>(() => PlatinumFlags.Rename(text, all[0], "FLAG_STORY"));
        Assert.Throws<InvalidDataException>(() => PlatinumFlags.Rename(text, all[0], "flag_lower"));
    }

    [Fact]
    public void NewTrainersGetTheirFlagAndNewFlagsTakeFreePlaces()
    {
        const string text = "FLAG_UNUSED_0x0000\nFLAG_STORY\nTRAINER_DEFEATED_FLAGS_START\nFLAG_DEFEATED_TRAINER_NONE = TRAINER_DEFEATED_FLAGS_START\nFLAG_DEFEATED_TRAINER_A\n" +
                            "TRAINER_DEFEATED_FLAGS_END = FLAG_DEFEATED_TRAINER_A\nFLAG_UNK_0x0006\nFLAG_UNK_0x0007\nVAR_UNUSED_0x4000 = 16384\n";
        var root = TestPaths.NewTempDir("newtrainer");
        Directory.CreateDirectory(Path.Combine(root, "generated"));
        Directory.CreateDirectory(Path.Combine(root, "res", "trainers", "data"));
        File.WriteAllText(Path.Combine(root, PlatinumFlags.File), text);
        File.WriteAllText(Path.Combine(root, PlatinumFlags.TrainersFile), "TRAINER_NONE\nTRAINER_A\nMAX_TRAINERS\n");
        const string json = "{\n    \"name\": \"Anna\",\n    \"class\": \"TRAINER_CLASS_LASS\",\n    \"items\": [],\n    \"ai_flags\": [],\n    \"double_battle\": false,\n    \"party\": [\n        {\n            \"species\": \"SPECIES_BIDOOF\",\n            \"form\": 0,\n            \"level\": 5,\n            \"item\": null,\n            \"moves\": null,\n            \"iv_scale\": 0,\n            \"ball_seal\": 0\n        }\n    ],\n    \"messages\": []\n}";
        File.WriteAllText(Path.Combine(root, "res", "trainers", "data", "a.json"), json);
        var template = PlatinumTrainers.Parse("res/trainers/data/a.json", json);
        var (files, constant) = PlatinumNewTrainer.Plan(root, template, "TRAINER_CLASS_YOUNGSTER", "Reya", _ => false);
        Assert.Equal("TRAINER_YOUNGSTER_REYA", constant);
        string Of(string path) => Encoding.UTF8.GetString(files.Single(f => f.Path == path).Content);
        Assert.Equal("TRAINER_NONE\nTRAINER_A\nTRAINER_YOUNGSTER_REYA\nMAX_TRAINERS\n", Of(PlatinumFlags.TrainersFile));
        var flags = PlatinumFlags.Parse(Of(PlatinumFlags.File));
        Assert.Equal(4, flags.Single(f => f.Name == "FLAG_DEFEATED_TRAINER_YOUNGSTER_REYA").Value);
        Assert.Equal(5, flags.Single(f => f.Name == "FLAG_UNK_0x0007").Value);
        var created = PlatinumTrainers.Parse("x.json", Of("res/trainers/data/youngster_reya.json"));
        Assert.Equal(("Reya", "TRAINER_CLASS_YOUNGSTER", "SPECIES_BIDOOF"), (created.Name, created.Class, created.Party[0].Species));
        Assert.Throws<InvalidDataException>(() => PlatinumNewTrainer.Plan(root, template, "TRAINER_CLASS_YOUNGSTER", "TooLongName", _ => false));
        Assert.Throws<InvalidDataException>(() => PlatinumNewTrainer.Plan(root, template, "TRAINER_CLASS_YOUNGSTER", "Reya", n => n == "FLAG_UNK_0x0006"));

        // New flags: FLAG_UNUSED first; the places right after the trainer flags stay for new trainers.
        Assert.Null(PlatinumFlags.NextFree(text, true, _ => false));   // flag 0 means "no flag"; the FLAG_UNK_ places are kept for trainers
        Assert.Equal("FLAG_UNUSED_0x0100", PlatinumFlags.NextFree(text + "FLAG_UNUSED_0x0100 = 256\n", true, _ => false)!.Name);
        Assert.Equal("VAR_UNUSED_0x4000", PlatinumFlags.NextFree(text, false, _ => false)!.Name);

        var all = PlatinumFlags.Parse(text);
        var uses = new Dictionary<string, List<PlatinumVarFlagUse>> { ["FLAG_STORY"] = [new("res/field/x.s", 1, "SetFlag FLAG_STORY", "sets")] };
        Assert.Empty(PlatinumFlags.NeverUsed(all, uses));
        Assert.Equal(["FLAG_STORY"], PlatinumFlags.SetButNeverChecked(all, uses).Select(f => f.Name).ToArray());
        Assert.Equal(["FLAG_STORY"], PlatinumFlags.NeverUsed(all, []).Select(f => f.Name).ToArray());
    }

    [Fact]
    public void NewerPokeplatinumFlagsAndScriptSteps()
    {
        if (!HasNew) return;
        var all = PlatinumFlags.Load(NewRoot);
        Assert.Contains(all, f => f.Name == "FLAG_UNUSED_0x006D" && f.Group == "Free flags");
        Assert.Equal(16384, all.Single(f => f.Name == "VAR_MAP_LOCAL_0x00").Value);
        Assert.All(PlatinumFlags.TrainerFlags(NewRoot, all), t => Assert.False(t.Missing));
        var script = File.ReadAllText(Path.Combine(NewRoot, "res", "field", "maps", "jubilife_city", "scripts.s"));
        var bank = PlatinumJsonFile.Load(NewRoot, "res/text/jubilife_city.json", 2);
        var texts = PlatinumMapScripts.Messages(bank).OfType<System.Text.Json.Nodes.JsonObject>().ToDictionary(m => m["id"]!.GetValue<string>(), PlatinumMapScripts.MessageText);
        var sign = PlatinumScriptFlow.Steps(script, "JubilifeCity_SignboardJubilifeTV", id => texts.GetValueOrDefault(id));
        Assert.Equal("JubilifeCity_Text_SignJubilifeTV", sign.Single(s => s.Kind == ScriptStepKind.Message).MessageId);
        // Every script entry of the map can be read.
        foreach (var e in PlatinumMapScripts.Entries(script)) PlatinumScriptFlow.Steps(script, e.Label, id => texts.GetValueOrDefault(id));
    }

    // ------------------------------------------------------------------ events & dialogs, mugshots, switching mods off

    private const string MapScript = "#include \"macros/scrcmd.inc\"\n#include \"res/text/bank/x.h\"\n\n    ScriptEntry Town_Kid\n    ScriptEntry Town_Sign\n    ScriptEntryEnd\n\nTown_Kid:\n    End\n\nTown_Sign:\n    End\n\n    .balign 4, 0\n";

    [Fact]
    public void ScriptsAreNumberedAndAddedBeforeTheAlignment()
    {
        var entries = PlatinumMapScripts.Entries(MapScript);
        Assert.Equal([(1, "Town_Kid"), (2, "Town_Sign")], entries.Select(e => (e.Number, e.Label)).ToArray());
        Assert.Equal("Town", PlatinumMapScripts.Prefix(MapScript, "X"));
        var (text, number) = PlatinumMapScripts.AddScript(MapScript, "Town_OldMan", PlatinumMapScripts.TalkScript(["Town_Text_OldMan"], "MUGSHOT_EXAMPLE"), ["constants/mugshots.h"]);
        Assert.Equal(3, number);
        Assert.Contains("    ScriptEntry Town_Sign\n    ScriptEntry Town_OldMan\n    ScriptEntryEnd", text);
        Assert.Contains("#include \"res/text/bank/x.h\"\n#include \"constants/mugshots.h\"\n", text);
        Assert.Contains("Town_OldMan:\n    PlaySE SEQ_SE_CONFIRM\n    LockAll\n    FacePlayer\n    ShowMugshot MUGSHOT_EXAMPLE\n    Message Town_Text_OldMan\n    WaitButton\n    CloseMessage\n    ReleaseAll\n    End\n\n    .balign 4, 0\n", text);
        Assert.Throws<InvalidDataException>(() => PlatinumMapScripts.AddScript(text, "Town_OldMan", "    End\n"));
    }

    [Fact]
    public void JsonFilesKeepTheirEnding()
    {
        var dir = TestPaths.NewTempDir("json");
        File.WriteAllText(Path.Combine(dir, "a.json"), "{\n    \"x\": [],\n    \"y\": 1\n}\n\n");
        var f = PlatinumJsonFile.Load(dir, "a.json", 4);
        Assert.False(f.IsChanged);
        Assert.False(f.Reformats);
        f.Root["y"] = 2;
        Assert.Equal("{\n    \"x\": [],\n    \"y\": 2\n}\n\n", f.Serialize());
        File.WriteAllText(Path.Combine(dir, "b.json"), "{\n    \"y\": 1, \n    \"z\": 2\n}");
        Assert.True(PlatinumJsonFile.Load(dir, "b.json", 4).Reformats);
    }

    [Fact]
    public void MugshotsAreNumberedInAllThreeLists()
    {
        var mods = "mods";
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "TilesetEditor.sln"))) { mods = Path.Combine(d.FullName, "mods"); break; }
        var files = Path.Combine(mods, "platinum-mugshots", "files");
        var root = TestPaths.NewTempDir("mugshots");
        foreach (var f in Directory.GetFiles(files, "*", SearchOption.AllDirectories))
        {
            var t = Path.Combine(root, Path.GetRelativePath(files, f));
            Directory.CreateDirectory(Path.GetDirectoryName(t)!);
            File.Copy(f, t);
        }
        Assert.Equal(["MUGSHOT_EXAMPLE"], PlatinumMugshots.List(root).Select(m => m.Constant).ToArray());
        var picture = new RgbaImage(64, 64);
        picture.Fill(new Rgba32(10, 200, 30));
        var (changes, constant) = PlatinumMugshots.PlanAdd(root, "Reya", picture);
        Assert.Equal("MUGSHOT_REYA", constant);
        foreach (var c in changes) File.WriteAllBytes(Path.Combine(root, c.Path), c.Content);
        Assert.Equal([("MUGSHOT_EXAMPLE", 1), ("MUGSHOT_REYA", 2)], PlatinumMugshots.List(root).Select(m => (m.Constant, m.Number)).ToArray());
        var constants = File.ReadAllText(Path.Combine(root, PlatinumMugshots.ConstantsFile));
        Assert.Contains("#define MUGSHOT_EXAMPLE 1\n#define MUGSHOT_REYA 2\n\n#define MUGSHOT_COUNT 3", constants);
        // Since 1.2 the full-screen pictures follow the mugshots and pictures.
        Assert.EndsWith("mugshot_example.NCLR\nmugshot_reya.NCGR\nmugshot_reya.NCLR\nfullscreen_example.NCGR\nfullscreen_example.NCLR\n", File.ReadAllText(Path.Combine(root, PlatinumMugshots.OrderFile)));
        Assert.Contains("    'mugshot_example.png',\n    'mugshot_reya.png',\n)", File.ReadAllText(Path.Combine(root, PlatinumMugshots.MesonFile)));
        Assert.Throws<InvalidDataException>(() => PlatinumMugshots.PlanAdd(root, "reya", picture));
    }

    [Fact]
    public void PicturesComeAfterTheMugshotsAndBigOnesAreScaled()
    {
        var mods = "mods";
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "TilesetEditor.sln"))) { mods = Path.Combine(d.FullName, "mods"); break; }
        var files = Path.Combine(mods, "platinum-mugshots", "files");
        var root = TestPaths.NewTempDir("pictures");
        foreach (var f in Directory.GetFiles(files, "*", SearchOption.AllDirectories))
        {
            var t = Path.Combine(root, Path.GetRelativePath(files, f));
            Directory.CreateDirectory(Path.GetDirectoryName(t)!);
            File.Copy(f, t);
        }
        File.WriteAllText(Path.Combine(root, PlatinumMugshots.OrderFile), "city_popup.NCGR\ncity_popup.NCLR\nmugshot_example.NCGR\nmugshot_example.NCLR\n");
        Assert.True(PlatinumMugshots.SupportsPictures(root));
        void Apply(List<PlatinumFileChange> changes) { foreach (var c in changes) File.WriteAllBytes(Path.Combine(root, c.Path), c.Content); }

        // A full-screen picture (256×192 = 768 tiles) is scaled to the largest size that fits.
        var map = new RgbaImage(256, 192);
        map.Fill(new Rgba32(200, 170, 110));
        var (changes, constant) = PlatinumMugshots.PlanAddPicture(root, "Treasure map", map);
        Assert.Equal("PICTURE_TREASURE_MAP", constant);
        Assert.Contains(changes, c => c.Description.Contains("scaled from 256×192"));
        Apply(changes);
        var pic = PlatinumMugshots.ListPictures(root).Single();
        Assert.True(pic.TilesW * pic.TilesH <= PlatinumMugshots.MaxPictureTiles);
        Assert.Equal((16, 12), (pic.TilesW, pic.TilesH));
        var png = Png.Load(Path.Combine(root, pic.PngRel));
        Assert.Equal((128, 96), (png.Width, png.Height));
        Assert.DoesNotContain(png.Indices!, i => i == 0);   // the paper colour in the corner is not made transparent
        Assert.Contains("{ 16, 12 }, // PICTURE_TREASURE_MAP", File.ReadAllText(Path.Combine(root, PlatinumMugshots.PicturesFile)));
        Assert.Contains("#define PICTURE_TREASURE_MAP 1\n\n#define PICTURE_COUNT 2", File.ReadAllText(Path.Combine(root, PlatinumMugshots.ConstantsFile)));

        // A wide picture keeps its proportions; a big mugshot becomes 64×64.
        Assert.Equal((256, 48), PlatinumMugshots.FitPicture(512, 96));
        var face = new RgbaImage(200, 100);
        face.Fill(new Rgba32(10, 120, 200));
        var (mugChanges, mug) = PlatinumMugshots.PlanAdd(root, "Reya", face);
        Apply(mugChanges);
        Assert.Equal("MUGSHOT_REYA", mug);
        Assert.Contains(mugChanges, c => c.Description.Contains("scaled from 200×100"));
        // Order: popups, every mugshot, then every picture.
        Assert.Equal("city_popup.NCGR\ncity_popup.NCLR\nmugshot_example.NCGR\nmugshot_example.NCLR\nmugshot_reya.NCGR\nmugshot_reya.NCLR\npicture_treasure_map.NCGR\npicture_treasure_map.NCLR\nfullscreen_example.NCGR\nfullscreen_example.NCLR\n",
            File.ReadAllText(Path.Combine(root, PlatinumMugshots.OrderFile)));
        Assert.Contains("    'mugshot_example.png',\n    'mugshot_reya.png',\n    'picture_treasure_map.png',\n)", File.ReadAllText(Path.Combine(root, PlatinumMugshots.MesonFile)));
        Assert.Equal(["MUGSHOT_EXAMPLE", "MUGSHOT_REYA"], PlatinumMugshots.List(root).Select(m => m.Constant).ToArray());

        // Mirroring for the left side; blocks write the new commands.
        var arrow = new RgbaImage(2, 1);
        arrow[0, 0] = new Rgba32(255, 0, 0);
        Assert.Equal(new Rgba32(255, 0, 0), PlatinumMugshots.Mirror(arrow)[1, 0]);
        var dialect = new PlatinumScriptBlocks.Dialect("SEQ_SE_CONFIRM", false);
        var body = PlatinumScriptBlocks.Build("A", "B", [new ScriptBlock { Kind = ScriptBlockKind.Mugshot, Constant = "MUGSHOT_REYA", X = 1 },
            new ScriptBlock { Kind = ScriptBlockKind.Picture, Constant = "PICTURE_TREASURE_MAP" }, new ScriptBlock { Kind = ScriptBlockKind.Picture, Constant = "PICTURE_NONE" }], dialect).Body;
        Assert.Contains("    ShowMugshot MUGSHOT_REYA, MUGSHOT_POS_LEFT\n    ShowPicture PICTURE_TREASURE_MAP\n    HidePicture\n", body);
    }

    [Fact]
    public void FullscreenPicturesAre8BitAndComeLast()
    {
        var mods = "mods";
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "TilesetEditor.sln"))) { mods = Path.Combine(d.FullName, "mods"); break; }
        var files = Path.Combine(mods, "platinum-mugshots", "files");
        var root = TestPaths.NewTempDir("fullscreen");
        foreach (var f in Directory.GetFiles(files, "*", SearchOption.AllDirectories))
        {
            var t = Path.Combine(root, Path.GetRelativePath(files, f));
            Directory.CreateDirectory(Path.GetDirectoryName(t)!);
            File.Copy(f, t);
        }
        void Apply(List<PlatinumFileChange> changes) { foreach (var c in changes) File.WriteAllBytes(Path.Combine(root, c.Path), c.Content); }
        Assert.True(PlatinumMugshots.SupportsFullscreen(root));
        Assert.Equal(["FULLSCREEN_EXAMPLE"], PlatinumMugshots.ListFullscreen(root).Select(p => p.Constant).ToArray());

        // A bigger picture is scaled to 256×192 and centred on black; colours are kept up to 255.
        var photo = new RgbaImage(512, 256);
        for (int y = 0; y < 256; y++) for (int x = 0; x < 512; x++) photo[x, y] = new Rgba32((byte)(x / 2), (byte)y, 90);
        var (changes, constant) = PlatinumMugshots.PlanAddFullscreen(root, "Old photo", photo);
        Assert.Equal("FULLSCREEN_OLD_PHOTO", constant);
        Assert.Contains(changes, c => c.Description.Contains("scaled from 512×256"));
        Apply(changes);
        Assert.Equal([("FULLSCREEN_EXAMPLE", 1), ("FULLSCREEN_OLD_PHOTO", 2)], PlatinumMugshots.ListFullscreen(root).Select(p => (p.Constant, p.Number)).ToArray());
        var png = Png.Load(Path.Combine(root, PlatinumMugshots.Folder, "fullscreen_old_photo.png"));
        Assert.Equal((256, 192, 8), (png.Width, png.Height, png.BitDepth));
        Assert.Equal(0, png.Indices![0]);                  // 256×128 centred: the top rows are the black backdrop
        Assert.NotEqual(0, png.Indices[96 * 256 + 128]);   // the middle is the photo
        Assert.True(png.Indices.Distinct().Count() > 16);
        Assert.Contains("#define FULLSCREEN_OLD_PHOTO 2\n\n#define FULLSCREEN_COUNT 3", File.ReadAllText(Path.Combine(root, PlatinumMugshots.ConstantsFile)));
        Assert.EndsWith("fullscreen_example.NCGR\nfullscreen_example.NCLR\nfullscreen_old_photo.NCGR\nfullscreen_old_photo.NCLR\n", File.ReadAllText(Path.Combine(root, PlatinumMugshots.OrderFile)));
        var meson = File.ReadAllText(Path.Combine(root, PlatinumMugshots.MesonFile));
        Assert.Contains("fullscreen_files = files(\n    'fullscreen_example.png',\n    'fullscreen_old_photo.png',\n)", meson);
        Assert.Contains("mugshot_files = files(\n    'mugshot_example.png',\n)", meson);

        // A mugshot added later keeps the full-screen list.
        var face = new RgbaImage(64, 64);
        face.Fill(new Rgba32(10, 120, 200));
        Apply(PlatinumMugshots.PlanAdd(root, "Reya", face).Files);
        Assert.Contains("'fullscreen_old_photo.png'", File.ReadAllText(Path.Combine(root, PlatinumMugshots.MesonFile)));
        Assert.EndsWith("mugshot_reya.NCLR\nfullscreen_example.NCGR\nfullscreen_example.NCLR\nfullscreen_old_photo.NCGR\nfullscreen_old_photo.NCLR\n", File.ReadAllText(Path.Combine(root, PlatinumMugshots.OrderFile)));

        // The block closes an open message box first.
        var body = PlatinumScriptBlocks.Build("A", "B", [new ScriptBlock { Kind = ScriptBlockKind.Message, Text = "Look!" },
            new ScriptBlock { Kind = ScriptBlockKind.Picture, Constant = "FULLSCREEN_OLD_PHOTO" }], new PlatinumScriptBlocks.Dialect("SEQ_SE_CONFIRM", false)).Body;
        Assert.Matches(@"WaitButton\n    CloseMessage\n    ShowFullscreenPicture FULLSCREEN_OLD_PHOTO\n", body);
    }

    [Fact]
    public void ReplacedSpritesKeepTheirOwnPalette()
    {
        var root = TestPaths.NewTempDir("ownpal");
        Directory.CreateDirectory(Path.Combine(root, "player"));
        var oldPal = Enumerable.Range(0, 16).Select(i => new Rgba32((byte)(i * 16), 0, 0)).ToArray();
        File.WriteAllBytes(Path.Combine(root, "player", "a.png"), Png.EncodeIndexed(32, 64, new byte[32 * 64], oldPal, 4));
        // A sheet as drawn by hand: colour 0 is an opaque background colour, no transparency info, own colour order.
        var pal = new[] { new Rgba32(96, 152, 248), new Rgba32(10, 20, 30), new Rgba32(250, 240, 230) };
        var idx = new byte[32 * 64];
        for (int i = 0; i < idx.Length; i++) idx[i] = (byte)(i % 7 == 0 ? 2 : i % 5 == 0 ? 1 : 0);
        var png = Png.Decode(Png.EncodeIndexed(32, 64, idx, pal, 8));
        var change = PlatinumOverworld.PlanReplace(root, new OverworldSprite("OBJ_EVENT_GFX_X", null, "player/a.png"), png);
        var result = Png.Decode(change.Content);
        Assert.Equal(4, result.BitDepth);
        Assert.Equal(idx, result.Indices);                         // same indices: colour 0 stays the (transparent) background
        Assert.Equal(pal, result.Palette!.Take(3).ToArray());      // same palette order
        // The same through the protagonist pictures ("own colours").
        var bytes = PlatinumGraphics.Replace(root, "player/a.png", png, keepPalette: false);
        Assert.Equal(idx, Png.Decode(bytes).Indices);

        // A sheet with only the different frames, in the game's stored order (textures sorted by name: .1, .10, .11, .2 …).
        // 10 frames where frame 2 repeats frame 0 → 9 different ones; names sorted: 1,10,2,3,…,9 → stored order 0,9,1,…
        var oldIdx = new byte[32 * 32 * 10];
        for (int f = 0; f < 10; f++) for (int i = 0; i < 1024; i++) oldIdx[f * 1024 + i] = (byte)(i < 100 * ((f == 2 ? 0 : f) + 1) ? 1 : 0);
        var sheet = Png.Decode(Png.EncodeIndexed(32, 320, oldIdx, oldPal, 4));
        var order = PlatinumOverworld.StoredFrameOrder(sheet);
        Assert.Equal([0, 2, 0, 3, 4, 5, 6, 7, 8, 1], order);
        var uniqueIdx = new byte[32 * 32 * 9];
        for (int k = 0; k < 9; k++) for (int i = 0; i < 1024; i++) uniqueIdx[k * 1024 + i] = (byte)(k + 1);
        var expanded = PlatinumOverworld.ExpandUniqueFrames(sheet, Png.Decode(Png.EncodeIndexed(32, 288, uniqueIdx, pal.Concat(oldPal).Take(16).ToArray(), 4)))!;
        Assert.Equal(320, expanded.Height);
        Assert.Equal(new byte[] { 1, 3, 1, 4, 5, 6, 7, 8, 9, 2 }, Enumerable.Range(0, 10).Select(f => expanded.Indices![f * 1024]).ToArray());
        Assert.Null(PlatinumOverworld.ExpandUniqueFrames(sheet, sheet));   // a sheet in frame order stays as it is

        // Background drawn with colour 3 instead of 0 (colour 0 used inside the person): the two swap.
        var bgIdx = new byte[32 * 64];
        for (int i = 0; i < bgIdx.Length; i++) bgIdx[i] = (byte)((i % 32) is > 8 and < 24 ? (i % 3 == 0 ? 0 : 1) : 3);
        var bgPal = new[] { new Rgba32(65, 43, 43), new Rgba32(1, 2, 3), new Rgba32(4, 5, 6), new Rgba32(65, 49, 0) };
        var fixedSheet = Png.Decode(PlatinumOverworld.PlanReplace(root, new OverworldSprite("OBJ_EVENT_GFX_X", null, "player/a.png"), Png.Decode(Png.EncodeIndexed(32, 64, bgIdx, bgPal, 4))).Content);
        Assert.Equal(0, fixedSheet.Indices![0]);
        Assert.Equal(new Rgba32(65, 49, 0), fixedSheet.Palette![0]);
        Assert.Equal(3, fixedSheet.Indices[9]);   // pixel 9: colour 0 of the person, now colour 3
        Assert.Equal(new Rgba32(65, 43, 43), fixedSheet.Palette[3]);
    }

    [Fact]
    public void TownMapIconsAreReplacedWithoutTouchingTheCursorColours()
    {
        var root = TestPaths.NewTempDir("townmap");
        var dir = Path.Combine(root, "res", "graphics", "town_map");
        Directory.CreateDirectory(dir);
        var sheet = new byte[16 * 33];
        for (int i = 0; i < 16 * 16; i++) sheet[i] = 3;              // boy: colour 3
        for (int i = 16 * 16; i < 32 * 16; i++) sheet[i] = 5;        // girl: colour 5
        File.WriteAllBytes(Path.Combine(dir, "player_icons.png"), Png.EncodeGray(16, 33, sheet, 4));
        var cursor = new byte[16 * 33];
        cursor[0] = 9; cursor[1] = 15;
        File.WriteAllBytes(Path.Combine(dir, "cursor.png"), Png.EncodeGray(16, 33, cursor, 4));
        var pal = Enumerable.Range(0, 16).Select(i => new Rgba32((byte)(i * 8), 0, 0)).ToArray();
        File.WriteAllText(Path.Combine(dir, "player_male.pal"), TilesetEditor.Core.Gba.JascPalette.Serialize(pal).Replace("\n", "\r\n"));
        File.WriteAllText(Path.Combine(dir, "cursor_player_female.pal"), TilesetEditor.Core.Gba.JascPalette.Serialize(pal).Replace("\n", "\r\n"));
        Assert.Equal(new Rgba32(40, 0, 0), PlatinumTownMapIcon.Icon(root, true)[0, 0]);

        // A 32×32 head with 16 colours on a transparent corner: scaled to 16×16, colours into the free places only.
        var head = new RgbaImage(32, 32);
        for (int y = 4; y < 32; y++) for (int x = 4; x < 28; x++) head[x, y] = new Rgba32((byte)(x * 8), (byte)(y * 8), 200);
        var files = PlatinumTownMapIcon.PlanReplace(root, true, Png.Decode(Png.EncodeRgba(head)));
        foreach (var f in files) File.WriteAllBytes(Path.Combine(root, f.Path), f.Content);
        var newPal = TilesetEditor.Core.Gba.JascPalette.Load(Path.Combine(dir, "cursor_player_female.pal"));
        Assert.Equal(pal[9], newPal[9] with { A = 255 });                // the cursor's colours stay
        Assert.Equal(pal[15], newPal[15] with { A = 255 });
        Assert.Contains("\r\n", File.ReadAllText(Path.Combine(dir, "cursor_player_female.pal")));
        var png = Png.Load(Path.Combine(dir, "player_icons.png"));
        Assert.Equal((PngColorType.Grayscale, 4, 33), (png.ColorType, png.BitDepth, png.Height));
        var nums = png.Rgba.Pixels.Select(c => c.R / 17).ToArray();
        Assert.All(nums.Take(256), n => Assert.Equal(3, n));                     // the boy is untouched
        Assert.DoesNotContain(nums.Skip(256).Take(256), n => n is 9 or 15);      // the girl does not use the cursor's numbers
        Assert.Equal(0, nums[256]);                                              // corner stays see-through
        Assert.Equal(0, PlatinumTownMapIcon.Icon(root, true)[0, 0].A);
    }

    [Fact]
    public void TrainerAnimationsCanBeReplacedAndRetimed()
    {
        if (!HasNew) return;
        var root = TestPaths.NewTempDir("trainer_anim");
        var src = Path.Combine(NewRoot, "res", "trainers", "classes", "leader_roark");
        var dst = Path.Combine(root, "res", "trainers", "classes", "leader_roark");
        Directory.CreateDirectory(dst);
        foreach (var f in Directory.GetFiles(src)) File.Copy(f, Path.Combine(dst, Path.GetFileName(f)));
        const string cls = "TRAINER_CLASS_LEADER_ROARK";
        var anim = PlatinumTrainerAnimations.Load(root, cls);
        Assert.Equal(7, anim.Frames.Count);
        Assert.True(anim.IsAnimated);
        var main = anim.MainSequence;
        Assert.Equal(7, anim.Sequences[main].Count);
        var json = File.ReadAllText(Path.Combine(dst, "front_anim.json"));
        // Unchanged steps write the file byte for byte (tabs like the build tools).
        Assert.Equal(json, System.Text.Encoding.UTF8.GetString(PlatinumTrainerAnimations.PlanTiming(root, cls, main, anim.Sequences[main]).Content));
        // New timing: fewer steps, a cell used twice.
        var steps = new List<TrainerAnimStep> { new(0, 20), new(3, 10), new(0, 10), new(6, 40) };
        var change = PlatinumTrainerAnimations.PlanTiming(root, cls, main, steps);
        File.WriteAllBytes(Path.Combine(root, change.Path), change.Content);
        var again = PlatinumTrainerAnimations.Load(root, cls);
        Assert.Equal(steps, again.Sequences[main]);
        var node = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(Path.Combine(dst, "front_anim.json")))!;
        Assert.Equal(1 + 4, (int)node["frameCount"]!);
        Assert.Equal(5, node["uaatData"]!["frameAttributes"]!.AsArray().Count);
        Assert.Throws<InvalidDataException>(() => PlatinumTrainerAnimations.PlanTiming(root, cls, main, [new(9, 4)]));

        // A sheet without the lines between the frames, own palette.
        var pal = Enumerable.Range(0, 16).Select(i => new Rgba32((byte)(i * 16), 50, 60)).ToArray();
        var idx = new byte[80 * 80 * 7];
        for (int k = 0; k < 7; k++) for (int i = 0; i < 6400; i++) idx[k * 6400 + i] = (byte)(i < 3200 ? k + 1 : 0);
        var sheet = PlatinumTrainerAnimations.PlanReplaceSheet(root, cls, Png.Decode(Png.EncodeIndexed(80, 560, idx, pal, 8)));
        File.WriteAllBytes(Path.Combine(root, sheet.Path), sheet.Content);
        var png = Png.Load(Path.Combine(dst, "front.png"));
        Assert.Equal((80, 566), (png.Width, png.Height));
        Assert.Equal(5, png.Indices![(4 * 81) * 80]);          // frame 4 (top-left) has colour 5
        Assert.Equal(pal[5], png.Palette![5]);
        // One frame in the sheet's colours.
        var frame = new RgbaImage(80, 80);
        frame.Fill(pal[9]);
        var one = PlatinumTrainerAnimations.PlanReplaceFrame(root, cls, 2, Png.Decode(Png.EncodeRgba(frame)));
        File.WriteAllBytes(Path.Combine(root, one.Path), one.Content);
        png = Png.Load(Path.Combine(dst, "front.png"));
        Assert.Equal(0, png.Indices![(2 * 81) * 80]);          // top-left colour = background → transparent
        Assert.Equal(5, png.Indices![(4 * 81) * 80]);          // other frames untouched
    }

    private static List<ScriptBlock> BranchedEvent() =>
    [
        new() { Kind = ScriptBlockKind.Message, Text = "Hello!" },
        new() { Kind = ScriptBlockKind.IfFlag, Constant = "FLAG_UNUSED_0x0100" },
        new() { Kind = ScriptBlockKind.Message, Text = "Back again?" },
        new() { Kind = ScriptBlockKind.Else },
        new() { Kind = ScriptBlockKind.Ask, Text = "Want a Potion?" },
        new() { Kind = ScriptBlockKind.GiveItem, Constant = "ITEM_POTION", Number = 2 },
        new() { Kind = ScriptBlockKind.SetFlag, Constant = "FLAG_UNUSED_0x0100" },
        new() { Kind = ScriptBlockKind.Else },
        new() { Kind = ScriptBlockKind.Message, Text = "Too bad." },
        new() { Kind = ScriptBlockKind.EndIf },
        new() { Kind = ScriptBlockKind.EndIf },
        new() { Kind = ScriptBlockKind.Message, Text = "Bye!" },
    ];

    [Fact]
    public void BranchBlocksMakeLabelsAndKeepTheMessageBoxRight()
    {
        var blocks = BranchedEvent();
        ScriptBlockTree.UpdateDepths(blocks);
        Assert.Equal([0, 0, 1, 0, 1, 2, 2, 1, 2, 1, 0, 0], blocks.Select(b => b.Depth).ToArray());
        Assert.Equal((3, 10), ScriptBlockTree.PartsOf(blocks, 1));
        Assert.Equal(4, ScriptBlockTree.OpenerOf(blocks, 9));
        var body = PlatinumScriptBlocks.Build("S", "S_Text_", blocks, new PlatinumScriptBlocks.Dialect("SEQ_SE_CONFIRM", false)).Body;
        Assert.Contains("    GoToIfUnset FLAG_UNUSED_0x0100, S_Else1\n", body);
        Assert.Contains("    ShowYesNoMenu VAR_RESULT\n    GoToIfEq VAR_RESULT, MENU_NO, S_Else2\n", body);
        foreach (var l in new[] { "S_Else1:", "S_EndIf1:", "S_Else2:", "S_EndIf2:" }) Assert.Equal(1, Regex.Matches(body, "^" + l, RegexOptions.Multiline).Count);
        // Every CloseMessage has an open box before it: count opens/closes along the text (no jumps between them here).
        Assert.DoesNotContain("CloseMessage\n    CloseMessage", body);
        Assert.EndsWith("    ReleaseAll\n    End\n", body);
        // Missing End / stray Else are reported.
        Assert.Throws<InvalidDataException>(() => PlatinumScriptBlocks.Build("S", "T", blocks.Take(10).ToList(), new PlatinumScriptBlocks.Dialect("X", false)));
        Assert.Throws<InvalidDataException>(() => PlatinumScriptBlocks.Build("S", "T", [new ScriptBlock { Kind = ScriptBlockKind.Else }], new PlatinumScriptBlocks.Dialect("X", false)));
    }

    [Fact]
    public void EventsCanBePlayedThroughTheirBranches()
    {
        var blocks = BranchedEvent();
        var run = new PlatinumEventRunner(blocks);
        Assert.Equal("Hello!", run.Next().Text);
        var q = run.Next();
        Assert.Equal((PlayKind.Question, "Want a Potion?"), (q.Kind, q.Text));
        Assert.Contains("Potion", run.Next(answer: true).Text);           // obtained message
        Assert.Equal(PlayKind.Log, run.Next().Kind);                        // flag set
        Assert.Equal("Bye!", run.Next().Text);
        Assert.Equal(PlayKind.End, run.Next().Kind);
        Assert.True(run.Flags["FLAG_UNUSED_0x0100"]);

        // Second time: the flag is set → the other branch.
        var again = new PlatinumEventRunner(blocks, run.Flags);
        Assert.Equal("Hello!", again.Next().Text);
        Assert.Equal("Back again?", again.Next().Text);
        Assert.Equal("Bye!", again.Next().Text);

        // Answer No.
        var no = new PlatinumEventRunner(blocks);
        no.Next(); no.Next();
        Assert.Equal("Too bad.", no.Next(answer: false).Text);
        Assert.Equal("Bye!", no.Next().Text);
    }

    [Fact]
    public void HandWrittenScriptsBecomeBlocksWithBranches()
    {
        const string script = """
            Town_Man:
                PlaySE SE_CONFIRM_sseq_3
                LockAll
                FacePlayer
                GoToIfSet FLAG_GOT_GIFT, Town_Man_AlreadyGot
                Message Town_Text_Hello
                ShowYesNoMenu VAR_RESULT
                GoToIfEq VAR_RESULT, MENU_NO, Town_Man_No
                SetVar VAR_0x8004, ITEM_POTION
                SetVar VAR_0x8005, 3
                CallCommonScript CommonScript_AddItemQuantity
                SetFlag FLAG_GOT_GIFT
                ApplyMovement LOCALID_MAN, Town_Movement_Walk
                WaitMovement
                SomethingSpecial 7, VAR_RESULT
                StartTrainerBattle TRAINER_YOUNGSTER_TRISTAN
                CheckWonBattle VAR_RESULT
                GoToIfEq VAR_RESULT, FALSE, Town_Man_Lost
                GoTo Town_Man_Bye

            Town_Man_Bye:
                Message Town_Text_Bye
                WaitButton
                CloseMessage
                ReleaseAll
                End

            Town_Man_No:
                Message Town_Text_Pity
                WaitButton
                CloseMessage
                ReleaseAll
                End

            Town_Man_AlreadyGot:
                Message Town_Text_Again
                WaitButton
                CloseMessage
                ReleaseAll
                End

                .balign 4, 0
            Town_Movement_Walk:
                WalkNormalSouth 2
                EmoteExclamationMark
                EndMovement
            """;
        var texts = new Dictionary<string, string> { ["Town_Text_Hello"] = "Want a gift?", ["Town_Text_Bye"] = "Bye!", ["Town_Text_Pity"] = "Pity.", ["Town_Text_Again"] = "Hi again." };
        var conv = PlatinumScriptConverter.Convert(script.Replace("\r\n", "\n"), "Town_Man", id => texts.GetValueOrDefault(id));
        var kinds = conv.Blocks.Select(b => b.Kind).ToArray();
        Assert.Equal(new[]
        {
            ScriptBlockKind.IfFlag, ScriptBlockKind.Message, ScriptBlockKind.EndEvent, ScriptBlockKind.EndIf,
            ScriptBlockKind.Ask, ScriptBlockKind.GiveItem, ScriptBlockKind.SetFlag, ScriptBlockKind.Move, ScriptBlockKind.Code, ScriptBlockKind.TrainerBattle, ScriptBlockKind.Message,
            ScriptBlockKind.Else, ScriptBlockKind.Message, ScriptBlockKind.EndIf,
        }, kinds);
        Assert.Equal(("ITEM_POTION", 3), (conv.Blocks[5].Constant, conv.Blocks[5].Number));
        Assert.Equal(("LOCALID_MAN", "WalkNormalSouth 2;EmoteExclamationMark", 1), (conv.Blocks[7].Constant, conv.Blocks[7].Text, conv.Blocks[7].Number));
        Assert.Equal("SomethingSpecial 7, VAR_RESULT", conv.Blocks[8].Text);   // unknown commands stay as they are
        Assert.Equal(1, conv.CodeLines);
        // A script that fills the name slots itself keeps its choice (the game swaps them in some cutscenes).
        var swapped = PlatinumScriptBlocks.Build("X", "X_T", [new ScriptBlock { Kind = ScriptBlockKind.Code, Text = "BufferRivalName 0\nBufferPlayerName 1" },
            new ScriptBlock { Kind = ScriptBlockKind.Message, Text = PlatinumDialogText.PlayerName + " and " + PlatinumDialogText.RivalName }], new PlatinumScriptBlocks.Dialect("SE", false)).Body;
        Assert.DoesNotContain("BufferPlayerName 0", swapped);
        Assert.DoesNotContain("BufferRivalName 1", swapped);
        // The blocks give a working script again, and the event plays like the original.
        PlatinumScriptBlocks.Build("Town_Man", "Town_Text_Man", conv.Blocks, new PlatinumScriptBlocks.Dialect("SE_CONFIRM_sseq_3", false));
        var run = new PlatinumEventRunner(conv.Blocks, new Dictionary<string, bool> { ["FLAG_GOT_GIFT"] = true });
        Assert.Equal("Hi again.", run.Next().Text);
        Assert.Equal(PlayKind.Log, run.Next().Kind);   // the event ends here
    }

    [Fact]
    public void MapInitScriptsAreRead()
    {
        var init = PlatinumMapScripts.ParseInit("""
                InitScriptEntry_OnTransition 1
                InitScriptEntry_OnFrameTable InitScriptFrameTable
                InitScriptEntryEnd

            InitScriptFrameTable:
                InitScriptGoToIfEqual VAR_PLAYER_HOUSE_STATE, 0, 2
                InitScriptGoToIfEqual VAR_PLAYER_HOUSE_STATE, 3, 3
                InitScriptFrameTableEnd
            """);
        Assert.Equal([new MapInitScript("OnTransition", 1), new MapInitScript("OnFrame", 2, "VAR_PLAYER_HOUSE_STATE", "0"), new MapInitScript("OnFrame", 3, "VAR_PLAYER_HOUSE_STATE", "3")], init);
        Assert.Equal("Cutscene: as soon as VAR_PLAYER_HOUSE_STATE = 3", init[2].Describe);
        // Scripts of entering the map neither lock the player nor release; parts run with Call go back with Return.
        var silent = PlatinumScriptBlocks.Build("M", "M_T", [new ScriptBlock { Kind = ScriptBlockKind.SetFlag, Constant = "FLAG_X" }], new PlatinumScriptBlocks.Dialect("SE", false), ScriptStart.Silent).Body;
        Assert.Equal("    SetFlag FLAG_X\n    End\n", silent);
        var sub = PlatinumScriptBlocks.Build("M", "M_T", [new ScriptBlock { Kind = ScriptBlockKind.SetFlag, Constant = "FLAG_X" }], new PlatinumScriptBlocks.Dialect("SE", false), ScriptStart.Subroutine).Body;
        Assert.Equal("    SetFlag FLAG_X\n    Return\n", sub);
    }

    [Fact]
    public void AMugshotCanBePutBeforeAnyText()
    {
        var script = "#include \"macros/scrcmd.inc\"\n\nTown_Man:\n    LockAll\n    Message Town_Text_Hi\n    WaitButton\n    ShowMugshot MUGSHOT_OLD\n    Message Town_Text_Bye\n    End\n\n" +
                     PlatinumScriptBlocks.Marker + "AAAA */\nTown_Blocks:\n    Message Town_Text_Hi\n    End\n";
        var r = PlatinumMapScripts.AddMugshotBefore(script, "Town_Text_Hi", "MUGSHOT_REYA", left: true);
        Assert.Equal((1, 0, 1), (r.Added, r.Replaced, r.SkippedBlockScripts));   // the block script is left alone
        Assert.Contains("    ShowMugshot MUGSHOT_REYA, MUGSHOT_POS_LEFT\n    Message Town_Text_Hi\n", r.Script);
        Assert.Contains("#include \"macros/scrcmd.inc\"\n#include \"constants/mugshots.h\"\n", r.Script);
        // A mugshot right above is replaced, not doubled.
        var again = PlatinumMapScripts.AddMugshotBefore(r.Script, "Town_Text_Bye", "MUGSHOT_REYA", left: false);
        Assert.Equal((0, 1), (again.Added, again.Replaced));
        Assert.DoesNotContain("MUGSHOT_OLD", again.Script);
        // Only before one line (a step of an event).
        var lines = script.Split('\n');
        int line = Array.FindIndex(lines, l => l.Contains("Message Town_Text_Hi"));
        Assert.Equal(1, PlatinumMapScripts.AddMugshotBefore(script, "Town_Text_Hi", "MUGSHOT_REYA", false, line).Added);
        Assert.Equal(0, PlatinumMapScripts.AddMugshotBefore(script, "Town_Text_Hi", "MUGSHOT_REYA", false, line + 1).Added);
    }

    [Fact]
    public void ModEditsPreferTheSourceOverAGeneratedFile()
    {
        var root = TestPaths.NewTempDir("generated");
        Directory.CreateDirectory(Path.Combine(root, "src"));
        Directory.CreateDirectory(Path.Combine(root, "platinum.us"));
        File.WriteAllText(Path.Combine(root, "src", "meson.build"), "# Generated by tools/lsfgen from platinum.us/main.lsf.in. DO NOT EDIT!\n    'field_message.c',\n");
        File.WriteAllText(Path.Combine(root, "platinum.us", "main.lsf.in"), "\tObject src/field_message.c\n");
        var modDir = TestPaths.NewTempDir("genmod");
        File.WriteAllText(Path.Combine(modDir, "mod.json"), "{ \"id\": \"g\", \"name\": \"G\", \"game\": \"platinum\", \"edits\": [ { \"file\": \"src/meson.build\", \"type\": \"insert_after\", \"anchor\": \"    'field_message.c',\\n\", \"text\": \"    'x.c',\\n\", " +
            "\"alt\": [ { \"file\": \"platinum.us/main.lsf.in\", \"type\": \"insert_after\", \"anchor\": \"\\tObject src/field_message.c\\n\", \"text\": \"\\tObject src/x.c\\n\" } ] } ] }");
        var mod = TilesetEditor.Export.Mods.ModPackage.Load(modDir);
        var chosen = TilesetEditor.Export.Mods.ModPlanner.Resolve(root, mod.Manifest.Edits);
        Assert.Equal("platinum.us/main.lsf.in", chosen.Single().File);
        Assert.True(TilesetEditor.Export.Mods.ModPlanner.IsGenerated(File.ReadAllText(Path.Combine(root, "src", "meson.build"))));
    }

    [Fact]
    public void TrainersOnTheMapInBothForms()
    {
        var dir = TestPaths.NewTempDir("trainerevents");
        Directory.CreateDirectory(Path.Combine(dir, "res", "field", "maps", "route_x"));
        Directory.CreateDirectory(Path.Combine(dir, "res", "field", "events"));
        const string person = "{ \"id\": \"LOCALID_A\", \"graphics_id\": \"OBJ_EVENT_GFX_LASS\", \"movement_type\": \"MOVEMENT_TYPE_LOOK_SOUTH\", \"trainer_type\": \"TRAINER_TYPE_NONE\", \"hidden_flag\": \"0\", \"script\": \"Route_A\", \"initial_dir\": \"DIR_SOUTH\", \"data\": [], \"movement_range_x\": 0, \"movement_range_z\": 0, \"x\": 10, \"z\": 20, \"y\": 1 }";
        File.WriteAllText(Path.Combine(dir, "res/field/maps/route_x/events.json"), "{\n    \"object_events\": [\n        " + person + "\n    ]\n}\n");
        var newer = PlatinumJsonFile.Load(dir, "res/field/maps/route_x/events.json", 4);
        var t = PlatinumMapScripts.AddTrainer(newer, "TRAINER_YOUNGSTER_REYA", "OBJ_EVENT_GFX_YOUNGSTER", 1, 5, 11, 20);
        Assert.Equal(("TRAINER_YOUNGSTER_REYA", 5), PlatinumMapScripts.TrainerOf(t));
        Assert.Equal("LOCALID_YOUNGSTER_REYA", t["id"]!.GetValue<string>());
        Assert.False(t.ContainsKey("script"));
        Assert.Equal("TRAINER_TYPE_NORMAL", t["trainer_type"]!.GetValue<string>());
        Assert.Contains("\"trainer\": {\n                \"id\": \"TRAINER_YOUNGSTER_REYA\",\n                \"sight_range\": 5\n            },", newer.Serialize());

        File.WriteAllText(Path.Combine(dir, "res/field/events/events_route_x.json"), "{\n    \"object_events\": [\n        " + person.Replace("\"DIR_SOUTH\"", "1").Replace("\"Route_A\"", "\"TRAINER_LASS_ANNA\"").Replace("\"data\": []", "\"data\": [3]") + "\n    ]\n}\n");
        var older = PlatinumJsonFile.Load(dir, "res/field/events/events_route_x.json", 4);
        Assert.False(PlatinumMapScripts.TrainersAsObject(older));
        Assert.Equal(("TRAINER_LASS_ANNA", 3), PlatinumMapScripts.TrainerOf((System.Text.Json.Nodes.JsonObject)older.Root["object_events"]![0]!));
        var t2 = PlatinumMapScripts.AddTrainer(older, "TRAINER_YOUNGSTER_REYA", "OBJ_EVENT_GFX_YOUNGSTER", 2, 6, 12, 20);
        Assert.Equal("TRAINER_YOUNGSTER_REYA", t2["script"]!.GetValue<string>());
        Assert.Equal(6, t2["data"]![0]!.GetValue<int>());
        Assert.Equal(2, t2["initial_dir"]!.GetValue<int>());

        Assert.Equal("OBJ_EVENT_GFX_ACE_TRAINER_F", PlatinumMapScripts.GraphicsForClass("TRAINER_CLASS_ACE_TRAINER_F", ["OBJ_EVENT_GFX_LASS", "OBJ_EVENT_GFX_ACE_TRAINER_F"]));
        Assert.Null(PlatinumMapScripts.GraphicsForClass("TRAINER_CLASS_ELITE_FOUR_AARON", ["OBJ_EVENT_GFX_LASS"]));
    }

    [Fact]
    public void EncountersRoundTripAndShowTheirChances()
    {
        int checkedRoots = 0;
        foreach (var root in new[] { NewRoot, PlatinumRoot })
        {
            if (GameDetector.Detect(root) != GameKind.Platinum) continue;
            checkedRoots++;
            var p = PlatinumProject.Load(root);
            var files = p.MapHeaders.Select(h => PlatinumEncounters.FileOf(root, h)).OfType<string>().Distinct().ToList();
            Assert.True(files.Count > 100, $"{root}: only {files.Count} encounter files");
            foreach (var f in files)
            {
                var file = PlatinumEncounters.Load(root, f);
                Assert.False(file.IsChanged, f);   // read and written again: byte for byte the same
                Assert.Empty(PlatinumEncounters.Check(file.Root, p.Species.ToHashSet()));
            }
            var route = p.MapHeaders.First(h => h.Id == "MAP_HEADER_ROUTE_201");
            var r201 = PlatinumEncounters.Load(root, PlatinumEncounters.FileOf(root, route)!).Root;
            var chances = PlatinumEncounters.GrassChances(r201);
            Assert.Equal(100, chances.Sum(c => c.Percent));
            Assert.Contains(chances, c => c.Species == "SPECIES_STARLY");
            Assert.True(PlatinumEncounters.HasAny(r201));
            r201["land_encounters"]![0]!["level"] = 0;
            Assert.Contains(PlatinumEncounters.Check(r201, p.Species.ToHashSet()), x => x.Contains("Grass slot 1"));
        }
        if (HasNew) Assert.True(checkedRoots >= 1);
    }

    [Fact]
    public void MapEditorAddsWarpsAndEventsWithoutScript()
    {
        var dir = TestPaths.NewTempDir("mapedit");
        Directory.CreateDirectory(Path.Combine(dir, "res", "field", "maps", "a"));
        Directory.CreateDirectory(Path.Combine(dir, "res", "field", "events"));
        File.WriteAllText(Path.Combine(dir, "res/field/maps/a/events.json"),
            "{\n    \"bg_events\": [],\n    \"object_events\": [],\n    \"warp_events\": [\n        {\n            \"id\": \"WARP_B\",\n            \"x\": 1,\n            \"z\": 2,\n            \"dest_header_id\": \"MAP_HEADER_B\",\n            \"dest_warp_id\": \"WARP_A\"\n        }\n    ],\n    \"coord_events\": []\n}\n");
        var newer = PlatinumJsonFile.Load(dir, "res/field/maps/a/events.json", 4);
        Assert.True(PlatinumMapScripts.WarpsByName(newer));
        var w = PlatinumMapScripts.AddWarp(newer, 5, 6, "MAP_HEADER_B", "WARP_A_2");
        Assert.Equal("WARP_B_2", w["id"]!.GetValue<string>());
        Assert.Equal("WARP_A_2", w["dest_warp_id"]!.GetValue<string>());
        var person = PlatinumMapScripts.AddPersonWithoutScript(newer, "OBJ_EVENT_GFX_YOUNGSTER", 3, 3);
        Assert.Equal(0, person["script"]!.GetValue<int>());                 // never "" – the build rejects empty labels
        Assert.Equal(0, PlatinumMapScripts.AddSign(newer, 4, 4, "", 0)["script"]!.GetValue<int>());
        Assert.Equal(0, PlatinumMapScripts.AddTrigger(newer, 4, 5, "VAR_X", 0, "", 0)["script"]!.GetValue<int>());

        File.WriteAllText(Path.Combine(dir, "res/field/events/events_a.json"),
            "{\n    \"warp_events\": [\n        {\n            \"x\": 1,\n            \"z\": 2,\n            \"dest_header_id\": \"MAP_HEADER_B\",\n            \"dest_warp_id\": 0\n        }\n    ]\n}\n");
        // A map without warps of the newer layout: names, too.
        File.WriteAllText(Path.Combine(dir, "res/field/maps/a/events.json"), "{\n    \"warp_events\": []\n}\n");
        Assert.True(PlatinumMapScripts.WarpsByName(PlatinumJsonFile.Load(dir, "res/field/maps/a/events.json", 4)));
        var older = PlatinumJsonFile.Load(dir, "res/field/events/events_a.json", 4);
        Assert.False(PlatinumMapScripts.WarpsByName(older));
        // Copy and paste: a free id, the new position.
        var copy = PlatinumMapScripts.CopyEvent(newer, "object_events", person, 9, 9);
        Assert.Equal(person["id"]!.GetValue<string>() + "_2", copy["id"]!.GetValue<string>());
        Assert.Equal((9, 9), (copy["x"]!.GetValue<int>(), copy["z"]!.GetValue<int>()));
        Assert.Equal(person["id"]!.GetValue<string>() + "_3", PlatinumMapScripts.CopyEvent(newer, "object_events", copy, 10, 9)["id"]!.GetValue<string>());
        var snap = newer.Snapshot();
        newer.Root["object_events"]!.AsArray().Clear();
        Assert.Equal(snap, newer.Restored(snap).Snapshot());
        var w2 = PlatinumMapScripts.AddWarp(older, 7, 8, "MAP_HEADER_B", "1");
        Assert.False(w2.ContainsKey("id"));
        Assert.Equal(1, w2["dest_warp_id"]!.GetValue<int>());
    }

    [Fact]
    public void EveryTextOfTheGameCanBeSearched()
    {
        if (!HasNew) return;
        var p = PlatinumProject.Load(NewRoot);
        var search = PlatinumTextSearch.Load(p);
        Assert.True(search.Banks > 600);
        // All words must appear (any case); line codes and {COMMANDS} do not get in the way.
        var hits = search.Search("tv jubilife");
        Assert.Contains(hits, h => h.Id == "JubilifeCity_Text_SignJubilifeTV" && h.MapHeaderId == "MAP_HEADER_JUBILIFE_CITY");
        Assert.All(hits.TakeWhile(h => h.MapHeaderId != null), h => Assert.NotNull(h.MapName));
        Assert.Contains(search.Search("JubilifeCity_Text_SignJubilifeTV"), h => h.Index >= 0);
        Assert.Empty(search.Search("x"[..0]));
        Assert.Contains(search.Search("potion", onlyMaps: false), h => h.MapHeaderId == null);   // e.g. the item names
        Assert.Contains(search.Search("pokemon center"), h => h.Text.Contains("Pokémon Center"));   // accents do not matter
        Assert.Equal("Hi !", System.Text.RegularExpressions.Regex.Replace(PlatinumTextSearch.Plain("Hi {STRVAR_1 3, 0, 0}!"), @"\s+", " "));
    }

    [Fact]
    public void TwoWayWarpsPointAtEachOther()
    {
        var dir = TestPaths.NewTempDir("twoway");
        foreach (var m in new[] { "a", "b" })
        {
            Directory.CreateDirectory(Path.Combine(dir, "res", "field", "maps", m));
            File.WriteAllText(Path.Combine(dir, "res/field/maps", m, "events.json"), "{\n    \"warp_events\": []\n}\n");
        }
        var a = PlatinumJsonFile.Load(dir, "res/field/maps/a/events.json", 4);
        var b = PlatinumJsonFile.Load(dir, "res/field/maps/b/events.json", 4);
        var (here, back) = PlatinumMapScripts.AddTwoWayWarp(a, "MAP_HEADER_A", 1, 2, b, "MAP_HEADER_B", 5, 6);
        Assert.Equal(("WARP_B", "WARP_A"), (here["id"]!.GetValue<string>(), back["id"]!.GetValue<string>()));
        Assert.Equal("WARP_A", here["dest_warp_id"]!.GetValue<string>());   // arrives at the way back …
        Assert.Equal("WARP_B", back["dest_warp_id"]!.GetValue<string>());   // … which arrives here
        Assert.Equal(5, back["x"]!.GetValue<int>());
        Assert.Throws<InvalidDataException>(() => PlatinumMapScripts.AddTwoWayWarp(a, "MAP_HEADER_A", 1, 2, a, "MAP_HEADER_A", 5, 6));

        Directory.CreateDirectory(Path.Combine(dir, "res", "field", "events"));
        foreach (var m in new[] { "c", "d" })
            File.WriteAllText(Path.Combine(dir, "res/field/events", $"events_{m}.json"), "{\n    \"warp_events\": [\n        {\n            \"x\": 0,\n            \"z\": 0,\n            \"dest_header_id\": \"MAP_HEADER_X\",\n            \"dest_warp_id\": 0\n        }\n    ]\n}\n");
        var c = PlatinumJsonFile.Load(dir, "res/field/events/events_c.json", 4);
        var d = PlatinumJsonFile.Load(dir, "res/field/events/events_d.json", 4);
        var (h2, b2) = PlatinumMapScripts.AddTwoWayWarp(c, "MAP_HEADER_C", 1, 1, d, "MAP_HEADER_D", 2, 2);
        Assert.Equal(1, h2["dest_warp_id"]!.GetValue<int>());   // the second warp of d
        Assert.Equal(1, b2["dest_warp_id"]!.GetValue<int>());   // the second warp of c
    }

    [Fact]
    public void MoveBlocksWriteAlignedMovementData()
    {
        var d = new PlatinumScriptBlocks.Dialect("SEQ_SE_CONFIRM", false) { MovementSteps = new HashSet<string> { "WalkNormalSouth", "EmoteExclamationMark", "FaceWest" } };
        var blocks = new List<ScriptBlock>
        {
            new() { Kind = ScriptBlockKind.Move, Constant = "LOCALID_OLD_MAN", Text = "EmoteExclamationMark;WalkNormalSouth 3", Number = 1 },
            new() { Kind = ScriptBlockKind.Message, Text = "Hi!" },
            new() { Kind = ScriptBlockKind.Move, Constant = "LOCALID_PLAYER", Text = "FaceWest", Number = 0 },
        };
        var built = PlatinumScriptBlocks.Build("Town_OldMan", "T", blocks, d, ScriptStart.Person);
        Assert.Contains("    ApplyMovement LOCALID_OLD_MAN, Town_OldMan_Movement1\n    WaitMovement\n", built.Body);
        Assert.Contains("    ApplyMovement LOCALID_PLAYER, Town_OldMan_Movement2\n    CloseMessage", built.Body);   // no WaitMovement: the script goes on
        Assert.Contains("    .balign 4, 0\nTown_OldMan_Movement1:\n    EmoteExclamationMark\n    WalkNormalSouth 3\n    EndMovement\n", built.Body);
        Assert.Equal("! ↓3", PlatinumScriptBlocks.MoveSummary(blocks[0].Text));
        Assert.Throws<InvalidDataException>(() => PlatinumScriptBlocks.Build("A", "B", [new ScriptBlock { Kind = ScriptBlockKind.Move, Constant = "LOCALID_X", Text = "Teleport" }], d));

        // Written twice (edited again): the movement data is replaced, not left behind; the next script stays.
        var (script, _) = PlatinumScriptBlocks.WriteScript(MapScript, "Town_OldMan", "T", blocks, built);
        var fewer = blocks.Take(2).ToList();
        var (script2, _) = PlatinumScriptBlocks.WriteScript(script, "Town_OldMan", "T", fewer, PlatinumScriptBlocks.Build("Town_OldMan", "T", fewer, d, ScriptStart.Person));
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(script2, "Town_OldMan_Movement1:"));
        Assert.DoesNotContain("Town_OldMan_Movement2", script2);
        Assert.Contains("Town_Sign:\n    End\n", script2);
        Assert.EndsWith("    .balign 4, 0\n", script2);
    }

    [Fact]
    public void ShopListsAreReadAndOnlyChangedOnesWritten()
    {
        foreach (var root in new[] { NewRoot, PlatinumRoot })
        {
            if (!PlatinumMarts.Exists(root)) continue;
            var text = File.ReadAllText(Path.Combine(root, PlatinumMarts.File));
            var lists = PlatinumMarts.Parse(text);
            var common = lists.Single(l => l.Common);
            Assert.Contains(common.Entries, e => e.Item == "ITEM_POKE_BALL" && e.Level == 1);
            Assert.Equal("MART_SPECIALTIES_ID_JUBILIFE", lists.Single(l => l.Name == "JubilifeMartSpecialties").Id);
            Assert.Contains(lists, l => l.Kind == MartKind.Seals);
            Assert.Contains(lists, l => l.Kind == MartKind.Goods);
            // Written unchanged: the same text. Lists written as they are: only spacing may differ, the values not.
            Assert.Equal(text, PlatinumMarts.Write(text, []));
            var again = PlatinumMarts.Parse(PlatinumMarts.Write(text, lists));
            Assert.Equal(lists.Select(l => (l.Name, string.Join(",", l.Entries))), again.Select(l => (l.Name, string.Join(",", l.Entries))));
            // A change to one list: the others stay byte for byte.
            var jubilife = lists.Single(l => l.Name == "JubilifeMartSpecialties");
            jubilife.Entries = [("ITEM_RARE_CANDY", 0), .. jubilife.Entries];
            common.Entries = [.. common.Entries, ("ITEM_PP_UP", 6)];
            var written = PlatinumMarts.Write(text, [jubilife, common]);
            Assert.Contains("const u16 JubilifeMartSpecialties[] = {\n    ITEM_RARE_CANDY,\n    ITEM_AIR_MAIL,", written);
            Assert.Contains("{ ITEM_MAX_REPEL, 0x4 },\n    { ITEM_PP_UP, 0x6 }\n};", written);
            Assert.Equal(text[text.IndexOf("const u16 OreburghMartSpecialties", StringComparison.Ordinal)..], written[written.IndexOf("const u16 OreburghMartSpecialties", StringComparison.Ordinal)..]);
            Assert.Contains("JubilifeMartSpecialties", PlatinumMarts.UsedBy(root, lists).Keys);
            Assert.Empty(PlatinumMarts.Check(jubilife, new HashSet<string>()));
            Assert.NotEmpty(PlatinumMarts.Check(new MartList { Name = "X", Common = false, Kind = MartKind.Items, Entries = [], BodyStart = 0, BodyEnd = 0 }, new HashSet<string>()));
        }
    }

    [Fact]
    public void ModAddedByHandIsFoundAndCanBeSwitchedOff()
    {
        var root = TestPaths.NewTempDir("detect-mod");
        var modDir = TestPaths.NewTempDir("mod");
        File.WriteAllText(Path.Combine(modDir, "mod.json"), "{ \"id\": \"t\", \"name\": \"T\", \"game\": \"platinum\", \"edits\": [ { \"file\": \"a.c\", \"type\": \"insert_after\", \"anchor\": \"int a;\\n\", \"text\": \"int b;\\n\" } ] }");
        var mod = TilesetEditor.Export.Mods.ModPackage.Load(modDir);
        File.WriteAllText(Path.Combine(root, "a.c"), "int a;\nint z;\n");
        Assert.Null(TilesetEditor.Export.Mods.ModPlanner.DetectApplied(root, mod));
        File.WriteAllText(Path.Combine(root, "a.c"), "int a;\nint b;\nint z;\n");   // the same change, made by hand
        var found = TilesetEditor.Export.Mods.ModPlanner.DetectApplied(root, mod);
        Assert.NotNull(found);
        Assert.True(found!.Detected);
        var (plan, kept) = TilesetEditor.Export.Mods.ModPlanner.PlanUninstall(root, found);
        Assert.Empty(kept);
        Assert.Equal("int a;\nint z;\n", System.Text.Encoding.UTF8.GetString(plan.Files.Single(f => f.RelativePath == "a.c").Content));
    }

    // ------------------------------------------------------------------ newer pokeplatinum, alternatives, items, overworld

    private static string NewRoot => TestPaths.Setting("TILESETEDITOR_PLATINUM_NEW_REFERENCE", "pokeplatinum-new");
    private static bool HasNew => GameDetector.Detect(NewRoot) == GameKind.Platinum && Directory.Exists(Path.Combine(NewRoot, "res", "field", "maps", "jubilife_city"));

    [Fact]
    public void ModEditTakesTheFormThatFitsTheProject()
    {
        var root = TestPaths.NewTempDir("alt");
        File.WriteAllText(Path.Combine(root, "new.txt"), "a\nb\n");
        var edit = new TilesetEditor.Export.Mods.ModEdit("old.txt", "insert_after", "a\n", "X\n")
        {
            Alt = [new TilesetEditor.Export.Mods.ModEdit("new.txt", "insert_after", "a\n", "X\n")],
        };
        var resolved = TilesetEditor.Export.Mods.ModPlanner.Resolve(root, [edit]);
        Assert.Equal("new.txt", Assert.Single(resolved).File);
        var bad = new TilesetEditor.Export.Mods.ModEdit("old.txt", "insert_after", "zzz\n", "X\n") { Alt = [new("new.txt", "insert_after", "zzz\n", "X\n")] };
        var ex = Assert.Throws<TilesetEditor.Export.ExportValidationException>(() => TilesetEditor.Export.Mods.ModPlanner.Resolve(root, [bad]));
        Assert.Contains("checked old.txt, new.txt", ex.Errors[0]);
    }

    [Fact]
    public void ItemPluralsAndConstants()
    {
        Assert.Equal("Super Candies", PlatinumItems.Plural("Super Candy"));
        Assert.Equal("Potions", PlatinumItems.Plural("Potion"));
        Assert.Equal("Boxes", PlatinumItems.Plural("Box"));
        Assert.Equal("res/items/data/super_candy.json", PlatinumItems.FileOf("ITEM_SUPER_CANDY"));
    }

    [Fact]
    public void NewerPokeplatinumMapsRender()
    {
        if (!HasNew) return;
        var p = PlatinumProject.Load(NewRoot);
        Assert.True(p.MapHeaders.Count > 500);
        var maps = new PlatinumMaps(NewRoot);
        var h = p.MapHeaders.First(x => x.Id == "MAP_HEADER_JUBILIFE_CITY");
        Assert.Equal("res/field/maps/jubilife_city/events.json", h.EventsFile);
        var m = maps.Matrix(h.Matrix!)!;
        Assert.Equal((4, 23, 2, 2), maps.Area(h, m));
        var img = maps.RenderTextured(m, (4, 23, 2, 2), 2, (_, _) => h.AreaData);
        Assert.True(img.Pixels.Count(px => px.R + px.G + px.B > 120) > img.Pixels.Length * 0.9);
        Assert.NotEmpty(maps.Events(h));
    }

    [Fact]
    public void NewerPokeplatinumItemsAndOverworldPlans()
    {
        if (!HasNew) return;
        var (files, _) = PlatinumItems.PlanAdd(NewRoot, new NewItem("ITEM_SUPER_CANDY", "ITEM_RARE_CANDY", "Super Candy", "", "Test.", 4800, null));
        var constants = System.Text.Encoding.UTF8.GetString(files[0].Content);
        Assert.EndsWith("ITEM_SECRET_KEY\nITEM_SUPER_CANDY\nMAX_ITEMS\n", constants);
        Assert.Contains("\"Super Candies\"", System.Text.Encoding.UTF8.GetString(files[1].Content));

        var sprites = PlatinumOverworld.List(NewRoot);
        Assert.Contains(sprites, s => s.Gfx == "OBJ_EVENT_GFX_YOUNGSTER" && s.PngRel == "res/graphics/field_sprites/npc/youngster.png");
        var picture = Png.Load(Path.Combine(NewRoot, "res/graphics/field_sprites/npc/beauty.png")).Rgba;
        var (ow, notes) = PlatinumOverworld.PlanAdd(NewRoot, "OBJ_EVENT_GFX_REYA", "reya", "OBJ_EVENT_GFX_YOUNGSTER", picture);
        var data = System.Text.Encoding.UTF8.GetString(ow.Single(f => f.Path == PlatinumOverworld.DataFile).Content);
        Assert.Contains("    OBJ_EVENT_TEXTURE(REYA,                                 reya_nsbtx),\n    OBJ_EVENT_TEXTURE(SENTINEL_ID,", data);
        Assert.Equal(4, System.Text.RegularExpressions.Regex.Matches(data, @"\(REYA,").Count);
        Assert.Contains(notes, n => n.Contains("OBJ_EVENT_GFX_REYA = "));
    }

    [Fact]
    public void NewerPokeplatinumGraphicsProtagonistAndItemIcons()
    {
        if (!HasNew) return;
        var all = PlatinumGraphics.List(NewRoot);
        // Every picture is in exactly one category.
        var counted = PlatinumGraphicsCatalog.Categories.Sum(c => PlatinumGraphicsCatalog.Pictures(NewRoot, c, all).Count);
        var overworld = all.Count(p => p.StartsWith(PlatinumOverworld.Folder + "/", StringComparison.Ordinal));
        Assert.Equal(all.Count - overworld, counted);
        var grass = PlatinumGraphicsCatalog.Terrains(NewRoot).Single(t => t.Name == "grass");
        Assert.Equal(["day", "evening", "night"], grass.Palettes.Select(Path.GetFileNameWithoutExtension).ToArray());
        Assert.NotEqual(PlatinumGraphicsCatalog.Render(NewRoot, grass.PlayerPng!, grass.Palettes[0]).Pixels, PlatinumGraphicsCatalog.Render(NewRoot, grass.PlayerPng!, grass.Palettes[2]).Pixels);

        var player = PlatinumProtagonist.List(NewRoot);
        Assert.Contains(player, p => !p.Female && p.Label == "Battle – front");
        Assert.Contains(player, p => p.Female && p.PngRel.EndsWith("player_f_surf.png", StringComparison.Ordinal));

        var tm = PlatinumItemIcons.Of(NewRoot, "res/items/data/tm01.json")!;
        Assert.Equal("tm_NCGR", tm.Sprite);
        Assert.True(PlatinumItemIcons.Users(NewRoot, tm.Sprite).Count > 50);
        var potion = PlatinumItemIcons.Render(NewRoot, PlatinumItemIcons.Of(NewRoot, "res/items/data/potion.json")!)!;
        var own = PlatinumItemIcons.PlanOwnIcon(NewRoot, tm, potion);
        Assert.Contains(own, f => f.Path == "res/items/icons/tm01.png");
        Assert.Contains("\"sprite\": \"tm01_NCGR\"", System.Text.Encoding.UTF8.GetString(own.Single(f => f.Path == tm.ItemFile).Content));
        Assert.EndsWith("tm01.NCGR\ntm01.NCLR\n", System.Text.Encoding.UTF8.GetString(own.Single(f => f.Path == PlatinumItemIcons.OrderFile).Content));
    }
}
