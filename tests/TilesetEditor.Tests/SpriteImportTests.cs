using System.Text;
using TilesetEditor.Core.Gba;
using TilesetEditor.Core.Imaging;
using TilesetEditor.Core.Sprites;
using TilesetEditor.Export;

namespace TilesetEditor.Tests;

public class SpriteImportTests
{
    private static string CopySpriteProject()
    {
        var src = TestPaths.ReferenceProject;
        var dst = TestPaths.NewTempDir("sprites");
        foreach (var rel in new[]
                 {
                     SpriteImportPlanner.ConstantsFile, SpriteImportPlanner.GraphicsFile, SpriteImportPlanner.PicTablesFile, SpriteImportPlanner.InfoFile,
                     SpriteImportPlanner.PointersFile, SpriteImportPlanner.RulesFile, "src/event_object_movement.c",
                 })
        {
            var t = Path.Combine(dst, rel.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(t)!);
            File.Copy(Path.Combine(src, rel.Replace('/', Path.DirectorySeparatorChar)), t);
        }
        foreach (var dir in new[] { "graphics/object_events/palettes", "graphics/object_events/pics/people" })
        {
            Directory.CreateDirectory(Path.Combine(dst, dir));
            foreach (var f in Directory.EnumerateFiles(Path.Combine(src, dir), "*.p*"))
                File.Copy(f, Path.Combine(dst, dir, Path.GetFileName(f)));
        }
        return dst;
    }

    /// <summary>A 9-frame RGBA sheet using colors of npc_1.pal (one slightly off color to test the nearest-color option).</summary>
    private static string MakeSheet(string dir, Rgba32[] pal, bool offColor)
    {
        var img = new RgbaImage(144, 32);
        for (int f = 0; f < 9; f++)
            for (int y = 4; y < 30; y++)
                for (int x = 3; x < 13; x++)
                    img[f * 16 + x, y] = pal[1 + (x + y + f) % 6] with { A = 255 };
        if (offColor) img[5, 5] = new Rgba32(1, 2, 250);
        var path = Path.Combine(dir, "guard_sheet.png");
        File.WriteAllBytes(path, Png.EncodeRgba(img));
        return path;
    }

    [Fact]
    public void PlansAllRegistrationFilesAndTheProjectStillParses()
    {
        var root = CopySpriteProject();
        var sprites = OverworldSprites.Load(root);
        var pals = OverworldSprites.Palettes(sprites).ToDictionary(kv => kv.Key, kv => JascPalette.Load(Path.Combine(root, kv.Value)));
        var sheet = MakeSheet(root, pals["OBJ_EVENT_PAL_TAG_NPC_1"], offColor: false);
        var constsPath = Path.Combine(root, "include", "constants", "event_objects.h");
        var countBefore = new TilesetEditor.Core.Project.CDefines();
        countBefore.AddFromText(File.ReadAllText(constsPath));
        long num = countBefore.TryEvaluate("NUM_OBJ_EVENT_GFX")!.Value;
        bool incGfx = File.ReadAllText(Path.Combine(root, SpriteImportPlanner.GraphicsFile.Replace('/', Path.DirectorySeparatorChar))).Contains("INCGFX_");
        var result = SpriteImportPlanner.Plan(root, new SpriteImportRequest("TownGuard", sheet, 16, 32, "OBJ_EVENT_PAL_TAG_NPC_1", false), sprites, pals);
        Assert.Equal("OBJ_EVENT_GFX_TOWN_GUARD", result.Constant);
        Assert.Equal(incGfx ? 6 : 7, result.Plan.Files.Count); // INCGFX carries -mwidth/-mheight: no make rule
        Assert.True(new ExportApplier(backupRoot: TestPaths.NewTempDir("backup")).Apply(result.Plan).Success);

        var after = OverworldSprites.Load(root);
        var guard = Assert.Single(after, s => s.GfxId == "OBJ_EVENT_GFX_TOWN_GUARD");
        Assert.Equal((16, 32, "OBJ_EVENT_PAL_TAG_NPC_1", "graphics/object_events/pics/people/town_guard.png"), (guard.Width, guard.Height, guard.PaletteTag, guard.PngPath));
        var consts = File.ReadAllText(constsPath);
        var countAfter = new TilesetEditor.Core.Project.CDefines();
        countAfter.AddFromText(consts);
        Assert.Equal(num, countAfter.TryEvaluate("OBJ_EVENT_GFX_TOWN_GUARD"));
        Assert.Equal(num + 1, countAfter.TryEvaluate("NUM_OBJ_EVENT_GFX"));
        if (consts.Contains("#define NUM_OBJ_EVENT_GFX")) Assert.Matches(@"\n#define OBJ_EVENT_GFX_TOWN_GUARD +" + num + "\n", consts);
        else Assert.Contains("    OBJ_EVENT_GFX_TOWN_GUARD,\n    NUM_OBJ_EVENT_GFX,", consts);
        var pointers = File.ReadAllText(Path.Combine(root, "src", "data", "object_events", "object_event_graphics_info_pointers.h"));
        Assert.Contains("extern const struct ObjectEventGraphicsInfo gObjectEventGraphicsInfo_TownGuard;", pointers);
        Assert.Contains("    [OBJ_EVENT_GFX_TOWN_GUARD] = &gObjectEventGraphicsInfo_TownGuard,", pointers);
        if (incGfx)
            Assert.Contains("const u32 gObjectEventPic_TownGuard[] = INCGFX_U32(\"graphics/object_events/pics/people/town_guard.png\", \".4bpp\", \"-mwidth 2 -mheight 4\");",
                File.ReadAllText(Path.Combine(root, SpriteImportPlanner.GraphicsFile.Replace('/', Path.DirectorySeparatorChar))));
        else
            Assert.EndsWith("$(OBJEVENTGFXDIR)/people/town_guard.4bpp: %.4bpp: %.png\n\t$(GFX) $< $@ -mwidth 2 -mheight 4\n",
                File.ReadAllText(Path.Combine(root, "spritesheet_rules.mk")));
        // The written PNG is 4-bit indexed with npc_1's colors and checks clean.
        var written = Png.Load(Path.Combine(root, "graphics", "object_events", "pics", "people", "town_guard.png"));
        Assert.True(written.IsIndexed);
        Assert.Equal(4, written.BitDepth);
        Assert.False(OverworldSprites.Check(written, 16, 32, pals).HasErrors);
        // Name collision on a second import
        Assert.Throws<ExportValidationException>(() => SpriteImportPlanner.Plan(root, new SpriteImportRequest("TownGuard", sheet, 16, 32, "OBJ_EVENT_PAL_TAG_NPC_1", false), after, pals));
    }

    [Fact]
    public void ForeignColorsNeedExplicitPermission()
    {
        var root = CopySpriteProject();
        var sprites = OverworldSprites.Load(root);
        var pals = OverworldSprites.Palettes(sprites).ToDictionary(kv => kv.Key, kv => JascPalette.Load(Path.Combine(root, kv.Value)));
        var sheet = MakeSheet(root, pals["OBJ_EVENT_PAL_TAG_NPC_1"], offColor: true);
        var ex = Assert.Throws<ExportValidationException>(() =>
            SpriteImportPlanner.Plan(root, new SpriteImportRequest("Guard2", sheet, 16, 32, "OBJ_EVENT_PAL_TAG_NPC_1", false), sprites, pals));
        Assert.Contains(ex.Errors, e => e.Contains("closest color"));
        var ok = SpriteImportPlanner.Plan(root, new SpriteImportRequest("Guard2", sheet, 16, 32, "OBJ_EVENT_PAL_TAG_NPC_1", true), sprites, pals);
        Assert.Single(ok.ColorChanges);
    }

    [Fact]
    public void CreatesItsOwnPaletteFromTheSheet()
    {
        var root = CopySpriteProject();
        var sprites = OverworldSprites.Load(root);
        var pals = OverworldSprites.Palettes(sprites).ToDictionary(kv => kv.Key, kv => JascPalette.Load(Path.Combine(root, kv.Value)));
        var img = new RgbaImage(144, 32);
        var colors = new[] { new Rgba32(200, 40, 40), new Rgba32(40, 200, 40), new Rgba32(40, 40, 200), new Rgba32(248, 248, 0) };
        for (int f = 0; f < 9; f++)
            for (int y = 4; y < 30; y++)
                for (int x = 3; x < 13; x++)
                    img[f * 16 + x, y] = colors[(x + y) % colors.Length];
        var sheet = Path.Combine(root, "own.png");
        File.WriteAllBytes(sheet, Png.EncodeRgba(img));
        bool incGfx = File.ReadAllText(Path.Combine(root, SpriteImportPlanner.GraphicsFile.Replace('/', Path.DirectorySeparatorChar))).Contains("INCGFX_");
        var result = SpriteImportPlanner.Plan(root, new SpriteImportRequest("TkKnight", sheet, 16, 32, "", false, "TkKnight"), sprites, pals);
        Assert.Equal(incGfx ? 8 : 9, result.Plan.Files.Count);
        Assert.True(new ExportApplier(backupRoot: TestPaths.NewTempDir("backup")).Apply(result.Plan).Success);

        var pal = JascPalette.Load(Path.Combine(root, "graphics", "object_events", "palettes", "tk_knight.pal"));
        Assert.Equal(16, pal.Length);
        foreach (var c in colors) Assert.Contains(TilesetEditor.Core.Gba.GbaColor.Snap(c), pal);
        var consts = File.ReadAllText(Path.Combine(root, "include", "constants", "event_objects.h"));
        Assert.Matches(@"#define OBJ_EVENT_PAL_TAG_TK_KNIGHT\s+0x11[0-9A-F]{2}\n", consts);
        Assert.DoesNotMatch(@"OBJ_EVENT_PAL_TAG_NONE[^\n]*\n#define OBJ_EVENT_PAL_TAG_TK_KNIGHT", consts);
        var movement = File.ReadAllText(Path.Combine(root, "src", "event_object_movement.c"));
        Assert.Matches(@"\{gObjectEventPal_TkKnight,\s+OBJ_EVENT_PAL_TAG_TK_KNIGHT\},", movement);
        var gfx = File.ReadAllText(Path.Combine(root, "src", "data", "object_events", "object_event_graphics.h"));
        Assert.Contains(incGfx
            ? "const u16 gObjectEventPal_TkKnight[] = INCGFX_U16(\"graphics/object_events/palettes/tk_knight.pal\", \".gbapal\");"
            : "const u16 gObjectEventPal_TkKnight[] = INCBIN_U16(\"graphics/object_events/palettes/tk_knight.gbapal\");", gfx);
        var after = OverworldSprites.Load(root);
        Assert.Equal("OBJ_EVENT_PAL_TAG_TK_KNIGHT", after.Single(s => s.GfxId == "OBJ_EVENT_GFX_TK_KNIGHT").PaletteTag);
        Assert.Contains("OBJ_EVENT_PAL_TAG_TK_KNIGHT", OverworldSprites.Palettes(after).Keys);
    }

    [Fact]
    public void RegistersANewTrainerPicture()
    {
        var src = TestPaths.ReferenceProject;
        var root = TestPaths.NewTempDir("trainerpic");
        foreach (var rel in new[] { CharacterGraphics.TrainerConstantsFile, CharacterGraphics.TrainerGraphicsFile })
        {
            var t = Path.Combine(root, rel.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(t)!);
            File.Copy(Path.Combine(src, rel.Replace('/', Path.DirectorySeparatorChar)), t);
        }
        var before = CharacterGraphics.TrainerPics(root);
        var img = new RgbaImage(64, 64);
        for (int y = 0; y < 64; y++) for (int x = 0; x < 64; x++) img[x, y] = new Rgba32(255, 0, 255);
        for (int y = 10; y < 60; y++) for (int x = 20; x < 44; x++) img[x, y] = new Rgba32((byte)(x * 4), 80, 120);
        var sheet = Path.Combine(root, "captain.png");
        File.WriteAllBytes(sheet, Png.EncodeRgba(img));
        Assert.Empty(TrainerPicPlanner.Check(Png.Load(sheet)));

        var result = TrainerPicPlanner.Plan(root, "SeaCaptain", sheet);
        Assert.Equal("TRAINER_PIC_SEA_CAPTAIN", result.Constant);
        Assert.True(new ExportApplier(backupRoot: TestPaths.NewTempDir("backup")).Apply(result.Plan).Success);
        var after = CharacterGraphics.TrainerPics(root);
        var pic = Assert.Single(after, p => p.Constant == "TRAINER_PIC_SEA_CAPTAIN");
        Assert.Equal(before.Max(p => p.Id) + 1, pic.Id);
        Assert.Equal(("graphics/trainers/front_pics/sea_captain.png", "graphics/trainers/palettes/sea_captain.pal"), (pic.PngRel, pic.PalRel));
        var consts = File.ReadAllText(Path.Combine(root, "include", "constants", "trainers.h"));
        var counted = new TilesetEditor.Core.Project.CDefines();
        counted.AddFromText(consts);
        Assert.Equal(pic.Id + 1, counted.TryEvaluate("TRAINER_PIC_COUNT"));
        Assert.Equal(before.Count + 1, after.Count);
        var written = Png.Load(Path.Combine(root, "graphics", "trainers", "front_pics", "sea_captain.png"));
        Assert.Equal((64, 64, 4, (byte)0), (written.Width, written.Height, written.BitDepth, written.Indices![0]));
        // Too big / too many colors is refused
        File.WriteAllBytes(sheet, Png.EncodeRgba(new RgbaImage(80, 64)));
        Assert.NotEmpty(TrainerPicPlanner.Check(Png.Load(sheet)));
    }

    [Fact]
    public void ListsThePlayersImages()
    {
        var root = TestPaths.ReferenceProject;
        var player = CharacterGraphics.Player(root, OverworldSprites.Load(root));
        Assert.Contains(player, g => g.Character == "Brendan" && g.Title == "Mach Bike" && g.PalRel == "graphics/object_events/palettes/brendan.pal");
        Assert.Contains(player, g => g.Character == "May" && g.Title == "Surfing");
        Assert.Contains(player, g => g.Title.StartsWith("Field move", StringComparison.Ordinal));
        Assert.Contains(player, g => g.Title == "Battle: back (throwing animation)");
        Assert.Contains(CharacterGraphics.TrainerPics(root), p => p.Constant == "TRAINER_PIC_HIKER" && p.PngRel == "graphics/trainers/front_pics/hiker.png");
    }

    [Fact]
    public void InsertsOutsideOfIfBlocks()
    {
        var text = "const u32 gObjectEventPic_A[] = X;\n#if FOO\nconst u32 gObjectEventPic_B[] = Y;\n#endif\n";
        var r = SpriteImportPlanner.InsertAfterLastTopLevel(text, @"^const u32 gObjectEventPic_\w+\[\]", "const u32 gObjectEventPic_New[] = Z;\n");
        Assert.Equal("const u32 gObjectEventPic_A[] = X;\nconst u32 gObjectEventPic_New[] = Z;\n#if FOO\nconst u32 gObjectEventPic_B[] = Y;\n#endif\n", r);
    }
}
