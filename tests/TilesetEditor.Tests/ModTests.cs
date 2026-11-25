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
        Assert.Contains("    HidePortrait();\n", File.ReadAllText(Path.Combine(root, "src", "field_message_box.c")).Replace("\r\n", "\n"));
        Assert.EndsWith("\tdef_special ShowPortrait\n\tdef_special HidePortrait\n", File.ReadAllText(Path.Combine(root, "data", "specials.inc")).Replace("\r\n", "\n"));
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
