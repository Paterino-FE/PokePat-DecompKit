using TilesetEditor.Core.Trainers;

namespace TilesetEditor.Tests;

public class TrainerPartyTests
{
    /// <summary>Upstream pokeemerald-expansion checkout with src/data/trainers.party. Override with TILESETEDITOR_EXPANSION_REFERENCE.</summary>
    private static string ExpansionRoot =>
        TestPaths.Setting("TILESETEDITOR_EXPANSION_REFERENCE", "expansion-reference");

    /// <summary>The upstream file, or null (test passes without checking) when the reference checkout is missing.</summary>
    private static string? UpstreamText
    {
        get
        {
            var p = Path.Combine(ExpansionRoot, "src", "data", "trainers.party");
            return File.Exists(p) ? File.ReadAllText(p) : null;
        }
    }

    private static TrainerData Data(PartyDocument doc, string id) => TrainerPartyFile.ToData(doc, doc.Trainers.Single(t => t.Id == id));

    private const string Sample = """
        /* header comment
           spanning lines */
        === TRAINER_NONE ===
        Name:
        Class: Pkmn Trainer 1
        Pic: Hiker
        Gender: Male
        Music: Male
        Double Battle: No

        === TRAINER_TEST_A ===
        Name: MAY
        Class: Pkmn Trainer 3
        Pic: May
        Gender: Female
        Music: Female
        Items: Full Restore / Hyper Potion
        Double Battle: Yes
        AI: Basic Trainer / Check Bad Move
        Mugshot: Purple

        Mimey (Mr. Mime) (F) @ King Rock
        Level: 30
        Ability: Soundproof
        IVs: 0 HP / 31 Atk / 10 Def / 20 SpA / 21 SpD / 25 Spe
        EVs: 252 HP / 4 Spe
        Timid Nature
        Shiny: Yes
        Ball: Ultra Ball /* comment after a value */
        - Psychic
        - U-turn

        Farfetch’d @ Leek
        Happiness: 255
        - Leaf Blade

        Nidoran (M)
        Level: 5
        Nature: Rash

        Arceus-Fire
        Level: 80
        Tera Type: Fire

        """;

    [Fact]
    public void ConvertsNamesLikeTrainerproc()
    {
        var doc = TrainerPartyFile.Parse(Sample.Replace("\r\n", "\n"));
        Assert.Equal(2, doc.Trainers.Count);
        Assert.All(doc.Trainers, t => Assert.Null(t.Problem));
        var none = Data(doc, "TRAINER_NONE");
        Assert.Equal(("", "TRAINER_CLASS_PKMN_TRAINER_1", "TRAINER_PIC_HIKER", "TRAINER_ENCOUNTER_MUSIC_MALE"), (none.Name, none.TrainerClass, none.Pic, none.Music));
        Assert.Empty(none.Party);

        var a = Data(doc, "TRAINER_TEST_A");
        Assert.Equal("MAY", a.Name);
        Assert.Equal("F_TRAINER_FEMALE | TRAINER_ENCOUNTER_MUSIC_FEMALE", a.Music);
        Assert.Equal(["ITEM_FULL_RESTORE", "ITEM_HYPER_POTION"], a.Items);
        Assert.True(a.DoubleBattle);
        Assert.Equal("AI_FLAG_BASIC_TRAINER | AI_FLAG_CHECK_BAD_MOVE", a.AiFlags);
        Assert.Equal(4, a.Party.Count);

        var mime = a.Party[0];
        Assert.Equal(("SPECIES_MR_MIME", "Mimey", "ITEM_KING_ROCK", 30), (mime.Species, mime.Nickname, mime.HeldItem, mime.Level));
        Assert.Equal(("ABILITY_SOUNDPROOF", "NATURE_TIMID", true), (mime.Ability, mime.Nature, mime.IsShiny));
        Assert.Equal("0,31,10,25,20,21", mime.Ivs); // HP, Atk, Def, Speed, SpAtk, SpDef
        Assert.Equal("252,0,0,4,0,0", mime.Evs);
        Assert.Equal(["MOVE_PSYCHIC", "MOVE_U_TURN"], mime.Moves);
        Assert.Contains(mime.Extra, e => e.Key == "Ball" && e.Value == "Ultra Ball");
        Assert.Contains(mime.Extra, e => e.Key == "Gender" && e.Value == "F");

        var farfetchd = a.Party[1];
        Assert.Equal(("SPECIES_FARFETCHD", "ITEM_LEEK", 100, ""), (farfetchd.Species, farfetchd.HeldItem, farfetchd.Level, farfetchd.Ivs)); // level default 100, IVs default (31)
        Assert.Equal("SPECIES_NIDORAN_M", a.Party[2].Species);
        Assert.Equal(("SPECIES_ARCEUS", "ITEM_FLAME_PLATE"), (a.Party[3].Species, a.Party[3].HeldItem));

        Assert.Equal("SPECIES_FLABEBE", TrainerPartyFile.SpeciesConstant("Flabébé"));
        Assert.Equal("SPECIES_TYPE_NULL", TrainerPartyFile.SpeciesConstant("Type: Null"));
        Assert.Equal("SPECIES_NIDORAN_F", TrainerPartyFile.SpeciesConstant("Nidoran♀"));
        Assert.Equal("ITEM_POKE_BALL", TrainerPartyFile.Constant("ITEM", "Poke Ball"));
        Assert.Equal("ITEM_POKE_BALL", TrainerPartyFile.Constant("ITEM", "ITEM_POKE_BALL"));
    }

    [Fact]
    public void SampleEditsKeepReadableNamesAndUntouchedLines()
    {
        var text = Sample.Replace("\r\n", "\n");
        var doc = TrainerPartyFile.Parse(text);
        var p = doc.Trainers.Single(t => t.Id == "TRAINER_TEST_A");
        var original = TrainerPartyFile.ToData(doc, p);
        Assert.Equal(text, TrainerPartyFile.Write(doc, p, original, original.Clone()));

        var edit = original.Clone();
        edit.Party[0].Level = 31;
        edit.Party[0].Nature = "NATURE_BOLD";
        edit.Party[0].Moves = ["MOVE_PSYCHIC", "MOVE_U_TURN", "MOVE_LEAF_BLADE"];
        edit.Party[1].Ivs = "1,2,3,4,5,6";
        edit.Party[2].Species = "SPECIES_MR_MIME";
        edit.Music = "TRAINER_ENCOUNTER_MUSIC_MALE";
        edit.AiFlags = "AI_FLAG_CHECK_BAD_MOVE";
        var result = TrainerPartyFile.Write(doc, p, original, edit);

        Assert.Contains("Mimey (Mr. Mime) (F) @ King Rock\nLevel: 31\n", result);
        Assert.Contains("Bold Nature\n", result);
        Assert.Contains("- Psychic\n- U-turn\n- Leaf Blade\n\nFarfetch’d @ Leek\nIVs: 1 HP / 2 Atk / 3 Def / 5 SpA / 6 SpD / 4 Spe\nHappiness: 255", result);
        Assert.Contains("\nMr. Mime\nLevel: 5\n", result); // gendered species: (M) dropped together with Nidoran
        Assert.Contains("Gender: Male\nMusic: Male\n", result);
        Assert.Contains("AI: Check Bad Move\n", result);
        Assert.Contains("Ball: Ultra Ball /* comment after a value */", result);

        var back = TrainerPartyFile.Parse(result);
        var d = Data(back, "TRAINER_TEST_A");
        Assert.Equal(edit.Music, d.Music);
        Assert.Equal(edit.AiFlags, d.AiFlags);
        Assert.Equal((31, "NATURE_BOLD"), (d.Party[0].Level, d.Party[0].Nature));
        Assert.Equal(edit.Party[0].Moves, d.Party[0].Moves);
        Assert.Equal("1,2,3,4,5,6", d.Party[1].Ivs);
        Assert.Equal("SPECIES_MR_MIME", d.Party[2].Species);
        Assert.Equal(("SPECIES_ARCEUS", "ITEM_FLAME_PLATE"), (d.Party[3].Species, d.Party[3].HeldItem));
        Assert.Equal(text[..p.Start], result[..p.Start]);
    }

    [Fact]
    public void EveryUpstreamTrainerRoundTripsUnchanged()
    {
        if (UpstreamText is not { } text) return;
        var doc = TrainerPartyFile.Parse(text);
        Assert.True(doc.Trainers.Count > 800);
        Assert.All(doc.Trainers, t => Assert.Null(t.Problem));
        foreach (var p in doc.Trainers)
        {
            var d = TrainerPartyFile.ToData(doc, p);
            Assert.Equal(text, TrainerPartyFile.Write(doc, p, d, d.Clone()));

            // Writing the trainer from scratch gives the same constants (name ↔ constant conversion is consistent).
            var fresh = TrainerPartyFile.Parse(TrainerPartyFile.FormatTrainer(doc, d) + "\n");
            var e = TrainerPartyFile.ToData(fresh, fresh.Trainers.Single());
            Assert.Equal((d.Name, d.TrainerClass, d.Pic, d.Music, d.DoubleBattle, d.AiFlags), (e.Name, e.TrainerClass, e.Pic, e.Music, e.DoubleBattle, e.AiFlags));
            Assert.Equal(d.Items, e.Items);
            Assert.Equal(d.Party.Select(m => (m.Species, m.Level, m.HeldItem, m.Ivs, string.Join(",", m.Moves))),
                         e.Party.Select(m => (m.Species, m.Level, m.HeldItem, m.Ivs, string.Join(",", m.Moves))));
        }
    }

    [Fact]
    public void UpstreamEditChangesOnlyThatBlock()
    {
        if (UpstreamText is not { } text) return;
        var doc = TrainerPartyFile.Parse(text);
        var p = doc.Trainers.First(t => t.Party.Count >= 3 && t.Party.All(m => m.Moves.Any()));
        var original = TrainerPartyFile.ToData(doc, p);
        var edit = original.Clone();
        edit.Name = "TESTER";
        edit.Items = ["ITEM_POTION", "ITEM_FULL_RESTORE"];
        edit.Party[0].Level = 42;
        edit.Party[0].Moves = ["MOVE_TACKLE", "MOVE_GROWL"];
        edit.Party.RemoveAt(1);
        var added = TrainerPartyFile.NewMon("SPECIES_EEVEE", 9);
        added.Moves = ["MOVE_TACKLE", "MOVE_TAIL_WHIP"];
        added.HeldItem = "ITEM_ORAN_BERRY";
        edit.Party.Add(added);
        var result = TrainerPartyFile.Write(doc, p, original, edit);

        var after = TrainerPartyFile.Parse(result);
        Assert.Equal(doc.Trainers.Count, after.Trainers.Count);
        var q = after.Trainers.Single(t => t.Id == p.Id);
        Assert.Null(q.Problem);
        var d = TrainerPartyFile.ToData(after, q);
        Assert.Equal("TESTER", d.Name);
        Assert.Equal(["ITEM_POTION", "ITEM_FULL_RESTORE"], d.Items);
        Assert.Equal(original.Party.Count, d.Party.Count);
        Assert.Equal((42, original.Party[0].Species, original.Party[0].Ivs), (d.Party[0].Level, d.Party[0].Species, d.Party[0].Ivs));
        Assert.Equal(["MOVE_TACKLE", "MOVE_GROWL"], d.Party[0].Moves);
        Assert.Equal(original.Party[2].Key, d.Party[1].Key); // untouched member re-parses identically
        Assert.Equal(("SPECIES_EEVEE", 9, "ITEM_ORAN_BERRY", ""), (d.Party[^1].Species, d.Party[^1].Level, d.Party[^1].HeldItem, d.Party[^1].Ivs));
        Assert.Equal(["MOVE_TACKLE", "MOVE_TAIL_WHIP"], d.Party[^1].Moves);
        Assert.Contains("Eevee @ Oran Berry\nLevel: 9\n- Tackle\n- Tail Whip", result);
        Assert.Contains("Items: Potion / Full Restore\n", result);
        // Text before and after the trainer is untouched; inside, the unchanged member keeps its exact text.
        Assert.Equal(text[..p.Start], result[..q.Start]);
        Assert.Equal(text[p.BlockEnd..], result[q.BlockEnd..]);
        Assert.Contains(text[p.Party[2].Start..p.Party[2].End], result[q.Start..q.BlockEnd]);

        // Changing one level touches exactly that line.
        var e2 = original.Clone();
        e2.Party[1].Level += 1;
        var r2 = TrainerPartyFile.Write(doc, p, original, e2);
        var diff = text.Split('\n').Zip(r2.Split('\n')).Count(x => x.First != x.Second);
        Assert.Equal(text.Split('\n').Length, r2.Split('\n').Length);
        Assert.Equal(1, diff);
    }

    [Fact]
    public void AbsentDefaultsStayAbsent()
    {
        var text = "=== TRAINER_X ===\nName: X\nPic: Hiker\n\nGeodude\n- Tackle\n";
        var doc = TrainerPartyFile.Parse(text);
        var p = doc.Trainers.Single();
        var d = TrainerPartyFile.ToData(doc, p);
        Assert.Equal((100, ""), (d.Party[0].Level, d.Party[0].Ivs));
        var e = d.Clone();
        e.Name = "Y";
        e.Party[0].Moves = ["MOVE_TACKLE", "MOVE_DIG"];
        Assert.Equal("=== TRAINER_X ===\nName: Y\nPic: Hiker\n\nGeodude\n- Tackle\n- Dig\n", TrainerPartyFile.Write(doc, p, d, e));

        // #pragma defaults are honoured; CRLF files stay CRLF.
        var crlf = "#pragma trainerproc level 50\r\n\r\n=== TRAINER_X ===\r\nName: X\r\nPic: Hiker\r\n\r\nGeodude\r\n";
        var doc2 = TrainerPartyFile.Parse(crlf);
        var d2 = TrainerPartyFile.ToData(doc2, doc2.Trainers.Single());
        Assert.Equal(50, d2.Party[0].Level);
        var e2 = d2.Clone();
        e2.Party[0].IsShiny = true;
        e2.DoubleBattle = true;
        Assert.Equal("#pragma trainerproc level 50\r\n\r\n=== TRAINER_X ===\r\nName: X\r\nPic: Hiker\r\nDouble Battle: Yes\r\n\r\nGeodude\r\nShiny: Yes\r\n",
            TrainerPartyFile.Write(doc2, doc2.Trainers.Single(), d2, e2));
    }

    [Fact]
    public void NewTrainerIsAppendedAndParses()
    {
        var text = UpstreamText ?? Sample.Replace("\r\n", "\n");
        var source = TrainerSource.FromText(TrainerFormat.Party, text);
        var d = new TrainerData
        {
            Id = "TRAINER_TK_TEST", Name = "GUARD", TrainerClass = "TRAINER_CLASS_HIKER", Pic = "TRAINER_PIC_HIKER",
            Music = "F_TRAINER_FEMALE | TRAINER_ENCOUNTER_MUSIC_HIKER", AiFlags = "AI_FLAG_BASIC_TRAINER",
        };
        var mon = source.NewMon("SPECIES_ZIGZAGOON", 5);
        mon.Moves = ["MOVE_TACKLE"];
        d.Party.Add(mon);
        var appended = source.Append(text, d);
        Assert.StartsWith(text, appended);
        Assert.Contains("\n\n=== TRAINER_TK_TEST ===\nName: GUARD\nClass: Hiker\nPic: Hiker\nGender: Female\nMusic: Hiker\nDouble Battle: No\nAI: Basic Trainer\n\nZigzagoon\nLevel: 5\n- Tackle\n", appended);
        var back = TrainerSource.FromText(TrainerFormat.Party, appended);
        Assert.Equal(source.Trainers.Count + 1, back.Trainers.Count);
        var t = back.Trainers.Single(x => x.Id == "TRAINER_TK_TEST");
        Assert.Equal(("GUARD", "SPECIES_ZIGZAGOON", 5, d.Music), (t.Name, t.Party.Single().Species, t.Party.Single().Level, t.Music));

        // The new trainer can be edited afterwards.
        var e = t.Clone();
        e.Party.Add(back.NewMon("SPECIES_POOCHYENA", 6));
        var written = back.Write(appended, t, e);
        Assert.EndsWith("Zigzagoon\nLevel: 5\n- Tackle\n\nPoochyena\nLevel: 6\n", written);
    }

    [Fact]
    public void FacadePicksTheFormatFromTheConfig()
    {
        var root = TestPaths.NewTempDir("trainers");
        Directory.CreateDirectory(Path.Combine(root, "include", "config"));
        Directory.CreateDirectory(Path.Combine(root, "src", "data"));
        File.WriteAllText(Path.Combine(root, "src", "data", "trainers.party"), Sample);
        File.WriteAllText(Path.Combine(root, "src", "data", "trainers.h"), "");
        File.WriteAllText(Path.Combine(root, "include", "config", "general.h"), "#define COMPETITIVE_PARTY_SYNTAX TRUE\n");
        var s = TrainerSource.Load(root);
        Assert.Equal((TrainerFormat.Party, "src/data/trainers.party", 2), (s.Format, s.RelativePath, s.Trainers.Count));

        File.WriteAllText(Path.Combine(root, "include", "config", "general.h"), "#define COMPETITIVE_PARTY_SYNTAX FALSE\n");
        Assert.Equal(TrainerFormat.C, TrainerSource.Detect(root));
        File.Delete(Path.Combine(root, "include", "config", "general.h"));
        Assert.Equal(TrainerFormat.Party, TrainerSource.Detect(root)); // no setting, trainers.h empty
        File.WriteAllText(Path.Combine(root, "src", "data", "trainers.party"), "");
        Assert.Equal(TrainerFormat.C, TrainerSource.Detect(root));
    }
}
