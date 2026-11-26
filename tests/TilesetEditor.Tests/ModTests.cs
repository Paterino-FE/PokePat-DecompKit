using TilesetEditor.Core.Imaging;
using TilesetEditor.Export;
using TilesetEditor.Export.Mods;

namespace TilesetEditor.Tests;

public class ModTests
{
    private static string RepoMods()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "TilesetEditor.sln"))) return Path.Combine(d.FullName, "mods");
        throw new DirectoryNotFoundException("mods folder of the repository");
    }

    /// <summary>The files the dialog portraits mod changes, copied from the reference project.</summary>
    private static string CopyProject()
    {
        var root = TestPaths.NewTempDir("mods");
        foreach (var rel in new[] { "src/field_message_box.c", "data/specials.inc", "data/event_scripts.s", "include/constants/trainers.h", "src/data/graphics/trainers.h" })
        {
            var s = Path.Combine(TestPaths.ReferenceProject, rel);
            var t = Path.Combine(root, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(t)!);
            File.Copy(s, t);
        }
        return root;
    }

    [Fact]
    public void Dialog_portraits_installs_adds_a_portrait_and_uninstalls_without_traces()
    {
        var root = CopyProject();
        var before = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).ToDictionary(f => f, File.ReadAllBytes);
        var (mods, problems) = ModPackage.LoadAll([RepoMods()]);
        Assert.Empty(problems);
        var mod = Assert.Single(mods, m => m.Manifest.Id == DialogPortraits.ModId);
        var check = ModPlanner.Check(root, mod, "1.17.2");
        Assert.True(check.CanInstall, string.Join("; ", check.Problems));

        var install = ModPlanner.PlanInstall(root, mod, "1.17.2");
        Assert.True(new ExportApplier(backupRoot: TestPaths.NewTempDir("backup")).Apply(install).Success);
        Assert.True(DialogPortraits.IsInstalled(root));
        Assert.Contains("    HidePortrait();\n    HidePicture();\n", File.ReadAllText(Path.Combine(root, "src", "field_message_box.c")).Replace("\r\n", "\n"));
        Assert.EndsWith("\tdef_special ShowPortrait\n\tdef_special HidePortrait\n\tdef_special ShowPortraitAt\n\tdef_special ShowPicture\n\tdef_special HidePicture\n", File.ReadAllText(Path.Combine(root, "data", "specials.inc")).Replace("\r\n", "\n"));
        Assert.Single(ModRegistry.Installed(root));
        Assert.False(ModPlanner.Check(root, mod, "1.17.2").CanInstall); // already installed

        // A portrait from any picture (smaller ones are placed bottom-centered).
        var img = new RgbaImage(40, 48);
        for (int y = 4; y < 48; y++) for (int x = 4; x < 36; x++) img[x, y] = new Rgba32((byte)(x * 6), 80, (byte)(y * 4));
        var add = DialogPortraits.PlanAdd(root, "Prof Birch", Png.Decode(Png.EncodeRgba(img)));
        Assert.True(new ExportApplier(backupRoot: TestPaths.NewTempDir("backup")).Apply(add).Success);
        var portraits = DialogPortraits.List(root);
        var birch = Assert.Single(portraits, p => p.Constant == "PORTRAIT_PROF_BIRCH");
        Assert.Equal((1, "graphics/portraits/prof_birch.png"), (birch.Id, birch.PngRel));
        var png = Png.Load(Path.Combine(root, "graphics", "portraits", "prof_birch.png"));
        Assert.Equal((64, 64, 4), (png.Width, png.Height, png.BitDepth)); // 528 colors were reduced to 15 + transparency
        Assert.Equal(0, png.Indices![0]);
        Assert.Throws<ExportValidationException>(() => DialogPortraits.PlanAdd(root, "Prof Birch", Png.Decode(Png.EncodeRgba(img))));
        Assert.Throws<ExportValidationException>(() => DialogPortraits.PlanAdd(root, "Big", Png.Decode(Png.EncodeRgba(new RgbaImage(80, 64)))));

        // A picture: made smaller to 128×96 and stored as six 64×32 pieces one below the other.
        Assert.True(DialogPortraits.SupportsPictures(root));
        var map = new RgbaImage(256, 192);
        for (int y = 0; y < 192; y++) for (int x = 0; x < 256; x++) map[x, y] = x < 128 ? new Rgba32(240, 220, 160) : y < 96 ? new Rgba32(40, 100, 200) : new Rgba32(200, 40, 40);
        Assert.True(new ExportApplier(backupRoot: TestPaths.NewTempDir("backup")).Apply(DialogPortraits.PlanAddPicture(root, "Treasure map", map)).Success);
        var pic = Assert.Single(DialogPortraits.ListPictures(root));
        Assert.Equal(("PICTURE_TREASURE_MAP", 1, "graphics/portraits/pictures/treasure_map.png"), (pic.Constant, pic.Id, pic.PngRel));
        var sheet = Png.Load(Path.Combine(root, pic.PngRel!));
        Assert.Equal((64, 192, 4), (sheet.Width, sheet.Height, sheet.BitDepth));
        var shown = DialogPortraits.PictureForDisplay(sheet);
        // (colours snapped to the GBA's 5 bits per channel)
        Assert.True(shown[10, 10].SameRgb(new Rgba32(240, 216, 160)));    // left half: paper
        Assert.True(shown[100, 10].SameRgb(new Rgba32(40, 96, 200)));    // top right: blue
        Assert.True(shown[100, 90].SameRgb(new Rgba32(200, 40, 40)));     // bottom right: red
        // The portrait table and the picture table each got their own entry; portrait numbers ignore the PORTRAIT_POS_ values.
        var data = File.ReadAllText(Path.Combine(root, DialogPortraits.DataFile));
        Assert.True(data.IndexOf("[PORTRAIT_PROF_BIRCH]") < data.IndexOf("sDialogPictures[]"));
        Assert.True(data.IndexOf("[PICTURE_TREASURE_MAP]") > data.IndexOf("sDialogPictures[]"));
        Assert.Equal(1, DialogPortraits.PlanAdd(root, "Mom", Png.Decode(Png.EncodeRgba(img))).Files.Count(f => f.Description.Contains("= 2")));

        // Uninstall: every changed file is byte-identical again, the mod's files and the record are gone.
        var installed = Assert.Single(ModRegistry.Installed(root));
        var (uninstall, kept) = ModPlanner.PlanUninstall(root, installed);
        Assert.Empty(kept);
        Assert.True(new ExportApplier(backupRoot: TestPaths.NewTempDir("backup")).Apply(uninstall).Success);
        foreach (var (file, bytes) in before) Assert.Equal(bytes, File.ReadAllBytes(file));
        Assert.False(File.Exists(Path.Combine(root, "src", "dialog_portraits.c")));
        Assert.Empty(ModRegistry.Installed(root));
    }

    [Fact]
    public void Edits_follow_the_file_line_endings_and_refuse_ambiguous_places()
    {
        var crlf = "a\r\nb\r\nc\r\n";
        var e = new ModEdit("x.c", "insert_after", "b\n", "new\n");
        var done = ModPlanner.ApplyEdit(crlf, e);
        Assert.Equal("a\r\nb\r\nnew\r\nc\r\n", done);
        Assert.Equal(crlf, ModPlanner.UndoEdit(done, e));
        Assert.Throws<InvalidOperationException>(() => ModPlanner.ApplyEdit("b\nb\n", e));
        Assert.Throws<InvalidOperationException>(() => ModPlanner.ApplyEdit("a\n", e));
        var r = new ModEdit("x.c", "replace", "old()", "new()");
        Assert.Equal("x = old();", ModPlanner.UndoEdit(ModPlanner.ApplyEdit("x = old();", r), r));
    }
}
