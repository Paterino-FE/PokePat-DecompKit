using TilesetEditor.Core.Project;
using TilesetEditor.Toolkit;

namespace TilesetEditor.Tests;

public class ProjectTests
{
    [Fact]
    public void Loader_DetectsReferenceProject()
    {
        var p = ProjectLoader.Load(TestPaths.ReferenceProject);
        Assert.Equal(new FieldmapConstants(512, 1024, 512, 1024, 6, 13, 8, 10240), p.Constants);
        Assert.Equal(LayerMode.Dual, p.Constants.LayerMode);
        Assert.Equal(2, p.Attributes.SizeBytes);
        Assert.Equal(0x00FFu, p.Attributes.BehaviorMask);
        Assert.Equal(0xF000u, p.Attributes.LayerTypeMask);
        Assert.Equal("1.10.2", p.ExpansionVersion);
        Assert.Equal(["gTileset_General", "gTileset_Building", "gTileset_SecretBase"], p.PrimaryTilesets.Select(t => t.Name));
        Assert.True(p.Style.HeadersHaveIsCompressed);
        Assert.True(p.Style.HeadersHaveCallback);
        Assert.Equal(16, p.Style.PaletteIncbinCount);
        Assert.Equal(".4bpp.lz", p.Style.TilesOutputExtension);
        Assert.Empty(p.Warnings);
    }

    [Fact]
    public void Loader_ResolvesPrimaryDeclaredInGraphicsC()
    {
        var p = ProjectLoader.Load(TestPaths.ReferenceProject);
        var g = p.FindTileset("gTileset_General")!;
        Assert.Equal("src/graphics.c", g.GraphicsDeclaredIn);
        Assert.Equal("data/tilesets/primary/general/tiles.png", g.TilesImagePath);
        Assert.Equal("data/tilesets/primary/general/palettes/00.pal", g.PalettePaths[0]);
        var data = p.LoadTilesetData(g);
        Assert.Equal(512, data.MetatileCount);
    }

    [Fact]
    public void Loader_ResolvesSecondary()
    {
        var p = ProjectLoader.Load(TestPaths.ReferenceProject);
        var f = p.FindTileset("gTileset_Fortree")!;
        Assert.True(f.IsSecondary);
        Assert.Equal("data/tilesets/secondary/fortree/metatiles.bin", f.MetatilesPath);
        Assert.Equal(16, f.PalettePaths.Count);
    }

    [Fact]
    public void Loader_RejectsNonProject()
    {
        var d = TestPaths.NewTempDir("empty");
        Assert.Throws<ProjectLoadException>(() => ProjectLoader.Load(d));
    }

    [Fact]
    public void Environment_ReadsGitBranchAndRomName()
    {
        var d = TestPaths.NewTempDir("git");
        Directory.CreateDirectory(Path.Combine(d, ".git", "refs", "heads", "feature"));
        File.WriteAllText(Path.Combine(d, ".git", "HEAD"), "ref: refs/heads/feature/tiles\n");
        File.WriteAllText(Path.Combine(d, ".git", "refs", "heads", "feature", "tiles"), "0123456789abcdef0123\n");
        File.WriteAllText(Path.Combine(d, "Makefile"), "TITLE := X\nFILE_NAME := reyamon\n");
        var env = ProjectEnvironment.Detect(d);
        Assert.True(env.IsGitRepository);
        Assert.Equal("feature/tiles", env.GitBranch);
        Assert.Equal("0123456789", env.GitCommit);
        Assert.Equal("reyamon.gba", env.RomFileName);
    }

    [Fact]
    public void Environment_DetectsWslShare()
    {
        var env = ProjectEnvironment.Detect(@"\\wsl.localhost\Ubuntu\home\nobody\does-not-exist");
        Assert.Equal("Ubuntu", env.WslDistro);
        Assert.False(env.IsGitRepository);
    }
}
