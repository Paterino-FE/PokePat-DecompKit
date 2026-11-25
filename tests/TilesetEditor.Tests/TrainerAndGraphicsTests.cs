using TilesetEditor.Core.Gba;
using TilesetEditor.Core.Graphics;
using TilesetEditor.Core.Imaging;
using TilesetEditor.Core.Trainers;

namespace TilesetEditor.Tests;

public class TrainerTests
{
    private static string TrainersText => File.ReadAllText(Path.Combine(TestPaths.ReferenceProject, "src", "data", "trainers.h"));

    [Fact]
    public void EveryTrainerRoundTripsUnchanged()
    {
        var text = TrainersText;
        var all = TrainerFile.Parse(text);
        Assert.True(all.Count > 800);
        foreach (var p in all)
        {
            var d = TrainerFile.ToData(p);
            Assert.Equal(text, TrainerFile.Write(text, p, d, d.Clone()));
            if (p.Fields.FirstOrDefault(f => f.Name == "partySize") is { } size) Assert.Equal(size.Raw, d.Party.Count.ToString());
        }
    }

    [Fact]
    public void EditsOnlyTheChangedTrainer()
    {
        var text = TrainersText;
        var all = TrainerFile.Parse(text);
        var p = all.First(t => t.Party.Count >= 2 && t.Party.All(m => m.Fields.Any(f => f.Name == "moves")));
        var original = TrainerFile.ToData(p);
        var edit = original.Clone();
        edit.Name = "TESTER";
        edit.Items = ["ITEM_POTION"];
        edit.Party[0].Level = 42;
        edit.Party.RemoveAt(1);
        edit.Party.Add(TrainerFile.NewMon(all, "SPECIES_EEVEE", 9));
        edit.Party[^1].Moves = ["MOVE_TACKLE", "MOVE_GROWL"];
        var result = TrainerFile.Write(text, p, original, edit);

        var after = TrainerFile.Parse(result);
        Assert.Equal(all.Count, after.Count);
        var q = after.Single(t => t.Id == p.Id);
        var d = TrainerFile.ToData(q);
        Assert.Equal("TESTER", d.Name);
        Assert.Equal(["ITEM_POTION"], d.Items);
        Assert.Equal(original.Party.Count, d.Party.Count);
        Assert.Equal(42, d.Party[0].Level);
        Assert.Equal(original.Party[0].Moves, d.Party[0].Moves);
        Assert.Equal(("SPECIES_EEVEE", 9), (d.Party[^1].Species, d.Party[^1].Level));
        Assert.Equal(["MOVE_TACKLE", "MOVE_GROWL"], d.Party[^1].Moves);
        Assert.Equal(d.Party.Count.ToString(), q.Fields.Single(f => f.Name == "partySize").Raw);
        // Text before and after the trainer is untouched.
        Assert.Equal(text[..p.Start], result[..q.Start]);
        Assert.Equal(text[(p.Close + 1)..], result[(q.Close + 1)..]);
    }

    [Fact]
    public void NewTrainerGetsAFreeNumberAndParses()
    {
        var opp = "#define TRAINER_NONE      0\n#define TRAINER_A         1\n#define TRAINER_B         3\n\n#define TRAINERS_COUNT    4\n#define MAX_TRAINERS_COUNT 6\n";
        var slot = TrainerFile.FindFreeId(opp, []);
        Assert.Equal((2, false), (slot.Id, slot.RaisesCount));
        Assert.Contains("#define TRAINER_A         1\n#define TRAINER_NEW       2\n#define TRAINER_B", TrainerFile.AddConstant(opp, "TRAINER_NEW", slot));
        var full = opp.Replace("TRAINER_B         3", "TRAINER_B         3\n#define TRAINER_C         2");
        var slot2 = TrainerFile.FindFreeId(full, []);
        Assert.Equal((4, true), (slot2.Id, slot2.RaisesCount));
        Assert.Contains("#define TRAINERS_COUNT    5", TrainerFile.AddConstant(full, "TRAINER_D", slot2));
        Assert.NotNull(TrainerFile.FindFreeId(full.Replace("MAX_TRAINERS_COUNT 6", "MAX_TRAINERS_COUNT 4"), []).Problem);

        var text = TrainersText;
        var all = TrainerFile.Parse(text);
        var d = new TrainerData { Id = "TRAINER_TK_TEST", Name = "GUARD", TrainerClass = "TRAINER_CLASS_HIKER", Pic = "TRAINER_PIC_HIKER", AiFlags = "AI_FLAG_BASIC_TRAINER" };
        d.Party.Add(TrainerFile.NewMon(all, "SPECIES_ZIGZAGOON", 5));
        var appended = TrainerFile.Append(text, all, d);
        var parsed = TrainerFile.Parse(appended);
        Assert.Equal(all.Count + 1, parsed.Count);
        var back = TrainerFile.ToData(parsed.Single(t => t.Id == "TRAINER_TK_TEST"));
        Assert.Equal(("GUARD", "SPECIES_ZIGZAGOON", 5), (back.Name, back.Party.Single().Species, back.Party.Single().Level));
    }

    [Fact]
    public void FindsPreviewPictures()
    {
        var root = TestPaths.ReferenceProject;
        Assert.NotEmpty(TrainerGraphics.TrainerPics(root));
        Assert.NotNull(TrainerGraphics.SpeciesFront(root, "SPECIES_VULPIX"));
        Assert.NotNull(TrainerSymbols.Load(root).Moves.FirstOrDefault(m => m == "MOVE_TACKLE"));
    }
}

public class GameGraphicsTests
{
    private static PngImage Indexed(int w, int h, byte[] idx, Rgba32[] pal) => Png.Decode(Png.EncodeIndexed(w, h, idx, pal, 4));

    [Fact]
    public void MapsOntoTheCurrentPaletteOrTakesNewColors()
    {
        var pal = new[] { new Rgba32(255, 0, 255), new Rgba32(0, 0, 0), new Rgba32(248, 248, 248), new Rgba32(248, 0, 0) };
        var original = Indexed(8, 8, new byte[64], pal);
        var info = new GraphicInfo("x.png", 8, 8, 4, true, 4, 1, PaletteSource.OwnPalFile, "x.pal");

        var img = new RgbaImage(8, 8);
        img[1, 1] = new Rgba32(248, 0, 0);
        img[2, 2] = new Rgba32(0, 0, 250); // not in palette
        var replacement = Png.Decode(Png.EncodeRgba(img));

        var strict = GameGraphics.Convert(original, info, pal, replacement, newColors: false, allowNearest: false);
        Assert.NotEmpty(strict.Errors);
        Assert.Single(strict.Changes);
        var mapped = GameGraphics.Convert(original, info, pal, replacement, newColors: false, allowNearest: true);
        Assert.Empty(mapped.Errors);
        var back = Png.Decode(mapped.Png);
        Assert.Equal(4, back.BitDepth);
        Assert.Equal(3, back.Indices![1 * 8 + 1]);
        Assert.Equal(0, back.Indices[0]);
        Assert.Null(mapped.Pal);

        var fresh = GameGraphics.Convert(original, info, pal, replacement, newColors: true, allowNearest: false);
        Assert.Empty(fresh.Errors);
        Assert.NotNull(fresh.Pal);
        var newPal = JascPalette.Parse(System.Text.Encoding.ASCII.GetString(fresh.Pal!));
        Assert.Contains(new Rgba32(0, 0, 248), newPal);

        var wrongSize = Png.Decode(Png.EncodeRgba(new RgbaImage(16, 8)));
        Assert.NotEmpty(GameGraphics.Convert(original, info, pal, wrongSize, false, true).Errors);
        Assert.NotEmpty(GameGraphics.Convert(original, info with { Source = PaletteSource.Shared }, pal, replacement, true, false).Errors);
    }

    [Fact]
    public void ListsGameGraphicsFolders()
    {
        var folders = GameGraphics.Folders(TestPaths.ReferenceProject);
        Assert.Equal("Title screen", folders[0].Title);
        Assert.DoesNotContain(folders, f => f.Rel == "graphics/pokemon");
        var title = GameGraphics.Files(TestPaths.ReferenceProject, "graphics/title_screen");
        Assert.Contains("graphics/title_screen/pokemon_logo.png", title);
        var png = Png.Load(Path.Combine(TestPaths.ReferenceProject, "graphics", "title_screen", "pokemon_logo.png"));
        Assert.Equal(PaletteSource.OwnPalFile, GameGraphics.Inspect(TestPaths.ReferenceProject, "graphics/title_screen/pokemon_logo.png", png).Source);
    }
}
