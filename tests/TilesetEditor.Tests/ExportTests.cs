using System.Buffers.Binary;
using System.Text;
using TilesetEditor.Core.Gba;
using TilesetEditor.Core.Imaging;
using TilesetEditor.Core.Project;
using TilesetEditor.Export;

namespace TilesetEditor.Tests;

public class ExportTests
{
    private static readonly Rgba32 Cyan = new(0, 255, 255);

    /// <summary>Writes a small, valid Porytiles-style output folder (2 metatiles, 16 tiles).</summary>
    private static string FakeCompiledDir()
    {
        var d = TestPaths.NewTempDir("compiled");
        var idx = Enumerable.Range(0, 128 * 8).Select(i => (byte)(0x60 | (i % 16))).ToArray();
        File.WriteAllBytes(Path.Combine(d, "tiles.png"),
            Png.EncodeIndexed(128, 8, idx, Enumerable.Range(0, 256).Select(i => new Rgba32((byte)i, 0, 0)).ToList(), 8));
        var mt = new byte[2 * 8 * 2];
        for (int i = 0; i < 16; i++) BinaryPrimitives.WriteUInt16LittleEndian(mt.AsSpan(i * 2), MetatileEntry.Create(512 + i % 4, 6).Raw);
        File.WriteAllBytes(Path.Combine(d, "metatiles.bin"), mt);
        File.WriteAllBytes(Path.Combine(d, "metatile_attributes.bin"), new byte[4]);
        Directory.CreateDirectory(Path.Combine(d, "palettes"));
        for (int i = 0; i < 16; i++)
        {
            var cols = Enumerable.Range(0, 16).Select(k => k == 0 ? Cyan : new Rgba32((byte)(k * 8), (byte)(i * 8), 0)).ToArray();
            File.WriteAllText(Path.Combine(d, "palettes", $"{i:00}.pal"), JascPalette.Serialize(cols));
        }
        return d;
    }

    private static (ProjectInfo Project, ExportInput Input) Setup(string name = "gTileset_ToolkitTest")
    {
        var root = TestPaths.CopyMiniProject();
        var project = ProjectLoader.Load(root);
        var dir = FakeCompiledDir();
        var compiled = TilesetData.Load(name, Path.Combine(dir, "tiles.png"),
            Enumerable.Range(0, 16).Select(i => Path.Combine(dir, "palettes", $"{i:00}.pal")).ToList(),
            Path.Combine(dir, "metatiles.bin"), Path.Combine(dir, "metatile_attributes.bin"), 8, 2);
        var primary = project.LoadTilesetData(project.FindTileset("gTileset_General")!);
        return (project, new ExportInput
        {
            TilesetName = name, PrimaryName = "gTileset_General", CompiledDir = dir, Compiled = compiled, Primary = primary,
            CompilerVersion = "2.0.0", CompileTransparency = Cyan,
        });
    }

    [Fact]
    public void Plan_ListsNativeFilesAndPorymapStyleRegistration()
    {
        var (project, input) = Setup();
        var plan = ExportPlanner.Plan(project, input);
        Assert.Equal("data/tilesets/secondary/toolkit_test", plan.TilesetFolder);
        Assert.Equal(3 + 16, plan.Created.Count());
        Assert.Equal(["src/data/tilesets/headers.h", "src/data/tilesets/graphics.h", "src/data/tilesets/metatiles.h"], plan.Modified.Select(f => f.RelativePath));

        var headers = Encoding.UTF8.GetString(plan.Modified.First().Content);
        Assert.EndsWith(
            "\nconst struct Tileset gTileset_ToolkitTest =\n{\n    .isCompressed = TRUE,\n    .isSecondary = TRUE,\n" +
            "    .tiles = gTilesetTiles_ToolkitTest,\n    .palettes = gTilesetPalettes_ToolkitTest,\n    .metatiles = gMetatiles_ToolkitTest,\n" +
            "    .metatileAttributes = gMetatileAttributes_ToolkitTest,\n    .callback = NULL,\n};\n", headers);
        Assert.DoesNotContain("\r\n", headers);
        var gfx = Encoding.UTF8.GetString(plan.Modified.ElementAt(1).Content);
        Assert.Contains("INCBIN_U16(\"data/tilesets/secondary/toolkit_test/palettes/15.gbapal\")", gfx);
        Assert.Contains("const u32 gTilesetTiles_ToolkitTest[] = INCBIN_U32(\"data/tilesets/secondary/toolkit_test/tiles.4bpp.lz\");", gfx);

        // Slot 0 normalized away from the sandbox color, CRLF kept for .pal.
        var pal = Encoding.ASCII.GetString(plan.Files.First(f => f.RelativePath.EndsWith("palettes/06.pal")).Content);
        Assert.StartsWith("JASC-PAL\r\n0100\r\n16\r\n255 0 255\r\n", pal);
    }

    [Fact]
    public void Plan_RejectsCollisions()
    {
        var (project, input) = Setup("gTileset_Fortree");
        var ex = Assert.Throws<ExportValidationException>(() => ExportPlanner.Plan(project, input));
        Assert.Contains(ex.Errors, e => e.Contains("already exists"));
    }

    [Fact]
    public void Plan_RejectsInvalidTileReferences()
    {
        var (project, input) = Setup();
        var bad = new TilesetData
        {
            Name = input.Compiled.Name, Tiles = input.Compiled.Tiles, Palettes = input.Compiled.Palettes, TilesPerMetatile = 8,
            Attributes = input.Compiled.Attributes,
            MetatileEntries = input.Compiled.MetatileEntries.Select((e, i) => i == 3 ? MetatileEntry.Create(900, 14) : e).ToList(),
        };
        var ex = Assert.Throws<ExportValidationException>(() => ExportPlanner.Plan(project, new ExportInput
        {
            TilesetName = input.TilesetName, PrimaryName = input.PrimaryName, CompiledDir = input.CompiledDir, Compiled = bad,
            Primary = input.Primary, CompilerVersion = "2.0.0", CompileTransparency = Cyan,
        }));
        Assert.Contains(ex.Errors, e => e.Contains("tile ID 900"));
        Assert.Contains(ex.Errors, e => e.Contains("palette 14"));
    }

    [Fact]
    public void Apply_WritesFilesAndProjectStillParses()
    {
        var (project, input) = Setup();
        var plan = ExportPlanner.Plan(project, input);
        var result = new ExportApplier(backupRoot: TestPaths.NewTempDir("backup")).Apply(plan);
        Assert.True(result.Success, result.Error);
        var reloaded = ProjectLoader.Load(project.Root);
        var t = reloaded.FindTileset("gTileset_ToolkitTest")!;
        Assert.True(t.IsSecondary && t.IsFullyResolved);
        Assert.Equal(project.Tilesets.Count + 1, reloaded.Tilesets.Count);
        Assert.Equal(2, reloaded.LoadTilesetData(t).MetatileCount);
        // A second export with the same name is refused.
        Assert.Throws<ExportValidationException>(() => ExportPlanner.Plan(reloaded, input));
    }

    [Fact]
    public void Apply_RollsBackOnWriteFailure()
    {
        var (project, input) = Setup();
        var plan = ExportPlanner.Plan(project, input);
        var before = plan.Modified.ToDictionary(f => f.RelativePath, f => File.ReadAllBytes(project.FullPath(f.RelativePath)));
        // Fail on the 21st write: all asset files and the first registration file are already in place.
        var fs = new FailingFileSystem(failOnWrite: 3 + 21);
        var result = new ExportApplier(fs, TestPaths.NewTempDir("backup")).Apply(plan);
        Assert.False(result.Success);
        Assert.True(result.RolledBack);
        Assert.Empty(result.RollbackProblems);
        Assert.False(Directory.Exists(project.FullPath(plan.TilesetFolder)));
        foreach (var (rel, bytes) in before) Assert.Equal(bytes, File.ReadAllBytes(project.FullPath(rel)));
        Assert.Empty(Directory.EnumerateFiles(project.Root, "*.tileseteditor-tmp", SearchOption.AllDirectories));
    }

    [Fact]
    public void Apply_RefusesWhenFileChangedAfterPlanning()
    {
        var (project, input) = Setup();
        var plan = ExportPlanner.Plan(project, input);
        File.AppendAllText(project.FullPath("src/data/tilesets/headers.h"), "\n// edited in Porymap\n");
        var result = new ExportApplier(backupRoot: TestPaths.NewTempDir("backup")).Apply(plan);
        Assert.False(result.Success);
        Assert.False(result.RolledBack);
        Assert.Contains("was modified since planning", result.Error);
        Assert.False(Directory.Exists(project.FullPath(plan.TilesetFolder)));
    }

    private sealed class FailingFileSystem(int failOnWrite) : IExportFileSystem
    {
        private readonly PhysicalExportFileSystem _inner = new();
        private int _writes;
        public bool FileExists(string path) => _inner.FileExists(path);
        public bool DirectoryExists(string path) => _inner.DirectoryExists(path);
        public byte[] ReadAllBytes(string path) => _inner.ReadAllBytes(path);

        public void WriteAllBytes(string path, byte[] data)
        {
            if (++_writes == failOnWrite) throw new IOException("Simulated write error (disk full)");
            _inner.WriteAllBytes(path, data);
        }

        public void CreateDirectory(string path) => _inner.CreateDirectory(path);
        public void DeleteFile(string path) => _inner.DeleteFile(path);
        public void DeleteEmptyDirectory(string path) => _inner.DeleteEmptyDirectory(path);
        public void Move(string source, string destination, bool overwrite) => _inner.Move(source, destination, overwrite);
    }
}
