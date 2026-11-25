using TilesetEditor.Compiler;
using TilesetEditor.Core.Artwork;
using TilesetEditor.Core.Project;
using TilesetEditor.Export;

namespace TilesetEditor.Tests;

/// <summary>
/// Runs the real Porytiles (pinned version, via WSL) on the test art. Requires tools/setup-porytiles.ps1.
/// Excluded from the default test run: build.ps1 -IntegrationTests (or --filter Category=Integration).
/// </summary>
[Trait("Category", "Integration")]
public class IntegrationTests
{
    private static SandboxCompiler Compiler() => new(new PorytilesClient(new PorytilesSettings()));

    [Fact]
    public async Task PinnedPorytilesIsInstalled()
    {
        var v = await new PorytilesClient(new PorytilesSettings()).GetVersionAsync(CancellationToken.None);
        Assert.Equal(PorytilesSettings.PinnedVersion, v.Version);
    }

    [Fact]
    public async Task CompileAndExport_EndToEndInTempProject()
    {
        var root = TestPaths.CopyMiniProject();
        var project = ProjectLoader.Load(root);
        var art = ArtworkLoader.LoadLayers(new Dictionary<Layer, string>
        {
            [Layer.Middle] = TestPaths.Asset("test_middle.png"),
            [Layer.Top] = TestPaths.Asset("test_top.png"),
        });
        var result = await Compiler().CompileAsync(new CompileRequest
        {
            Project = project, Primary = project.FindTileset("gTileset_General")!, Artwork = art, TilesetName = "gTileset_IntegrationTown",
            WorkRoot = TestPaths.NewTempDir("work"),
        }, null, CancellationToken.None);
        Assert.True(result.Success, result.Log);
        Assert.Equal(32, result.Compiled!.MetatileCount);
        Assert.All(result.Compiled.MetatileEntries, e => Assert.True(e.Palette == 0 || e.Palette is >= 6 and <= 12));

        var plan = ExportPlanner.Plan(project, new ExportInput
        {
            TilesetName = "gTileset_IntegrationTown", PrimaryName = "gTileset_General", CompiledDir = result.OutputDir!,
            Compiled = result.Compiled, Primary = result.Primary!, CompilerVersion = result.CompilerVersion!.Version,
            CompileTransparency = result.SandboxTransparency, SourceArtwork = art,
        });
        var applied = new ExportApplier(backupRoot: TestPaths.NewTempDir("backup")).Apply(plan);
        Assert.True(applied.Success, applied.Error);
        var t = ProjectLoader.Load(root).FindTileset("gTileset_IntegrationTown")!;
        Assert.True(t.IsFullyResolved);
        Assert.True(File.Exists(Path.Combine(root, "data", "tilesets", "secondary", "integration_town", "tileset_editor", "middle.png")));
    }

    [Fact]
    public async Task CompileFailure_IsMappedToGrid()
    {
        var project = ProjectLoader.Load(TestPaths.ReferenceProject);
        var art = ArtworkLoader.LoadSingle(TestPaths.Asset("test_too_many_colors.png"), Layer.Middle);
        var result = await Compiler().CompileAsync(new CompileRequest
        {
            Project = project, Primary = project.FindTileset("gTileset_General")!, Artwork = art, TilesetName = "gTileset_BrokenArt",
            WorkRoot = TestPaths.NewTempDir("work"),
        }, null, CancellationToken.None);
        Assert.False(result.Success);
        Assert.Contains(result.Issues, i => i.Source == "Porytiles" && i.Location is { TileX: 1, TileY: 2, Layer: Layer.Middle });
    }
}
