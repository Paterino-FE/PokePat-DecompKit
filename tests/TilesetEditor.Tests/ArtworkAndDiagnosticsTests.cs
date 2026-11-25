using TilesetEditor.Compiler;
using TilesetEditor.Core.Artwork;
using TilesetEditor.Core.Imaging;
using TilesetEditor.Core.Project;

namespace TilesetEditor.Tests;

public class ArtworkAndDiagnosticsTests
{
    private static readonly FieldmapConstants C = FieldmapConstants.EmeraldDefaults;

    [Fact]
    public void Import_RejectsBadSizeWithReadableMessage()
    {
        var ex = Assert.Throws<ArtworkImportException>(() => ArtworkLoader.LoadSingle(TestPaths.Asset("test_bad_size.png"), Layer.Middle));
        Assert.Contains("120×50", ex.Message);
        Assert.Contains("multiples of 16", ex.Message);
        Assert.Contains("128×64", ex.Message);
    }

    [Fact]
    public void Analyzer_CleanArtHasNoErrors()
    {
        var art = ArtworkLoader.LoadLayers(new Dictionary<Layer, string>
        {
            [Layer.Middle] = TestPaths.Asset("test_middle.png"),
            [Layer.Top] = TestPaths.Asset("test_top.png"),
        });
        Assert.Equal(32, art.MetatileCount);
        Assert.DoesNotContain(ArtworkAnalyzer.Analyze(art, C, null), i => i.Severity == IssueSeverity.Error);
    }

    [Fact]
    public void Analyzer_FlagsTooManyColorsAtExactTile()
    {
        var art = ArtworkLoader.LoadSingle(TestPaths.Asset("test_too_many_colors.png"), Layer.Middle);
        var issue = Assert.Single(ArtworkAnalyzer.Analyze(art, C, null), i => i.Code == "TILE_COLORS");
        Assert.Equal(1, issue.Location!.TileX);
        Assert.Equal(2, issue.Location.TileY);
        Assert.Equal(8, issue.Location.Metatile);
    }

    [Fact]
    public void Analyzer_FlagsTripleLayerContentInDualMode()
    {
        var full = new RgbaImage(16, 16);
        full.Fill(new Rgba32(10, 20, 30));
        var art = new Artwork(16, 16);
        art.SetLayer(Layer.Bottom, full.Clone());
        art.SetLayer(Layer.Middle, full.Clone());
        art.SetLayer(Layer.Top, full.Clone());
        Assert.Contains(ArtworkAnalyzer.Analyze(art, C, null), i => i.Code == "LAYER_MODE" && i.Location!.Metatile == 0);
    }

    [Fact]
    public void Analyzer_FlagsPaletteCapacity()
    {
        var art = ArtworkLoader.LoadSingle(TestPaths.Asset("test_capacity.png"), Layer.Middle);
        var issues = ArtworkAnalyzer.Analyze(art, C, null);
        Assert.Contains(issues, i => i.Code == "PALETTE_CAPACITY" && i.Severity == IssueSeverity.Error);
        Assert.Contains(issues, i => i.Code == "PALETTE_PRESSURE" && i.Location?.TileX != null);
    }

    [Fact]
    public void Sheet_ReflowsWideImagesIntoEightColumns()
    {
        var img = new RgbaImage(32 * 16, 16);
        img[17 * 16, 0] = new Rgba32(1, 2, 3); // metatile 17
        var art = new Artwork(img.Width, img.Height);
        art.SetLayer(Layer.Middle, img);
        var sheet = art.ToPorytilesSheet(Layer.Middle, new Rgba32(0, 255, 255));
        Assert.Equal(128, sheet.Width);
        Assert.Equal(64, sheet.Height);
        Assert.Equal(new Rgba32(1, 2, 3), sheet[(17 % 8) * 16, (17 / 8) * 16]);
        Assert.Equal(new Rgba32(0, 255, 255), sheet[0, 0]);
    }

    [Fact]
    public void ColorOps_ReportLossOnlyWhenMerging()
    {
        var img = new RgbaImage(16, 16);
        img.Fill(new Rgba32(100, 0, 0));
        img[0, 0] = new Rgba32(0, 100, 0);
        var art = new Artwork(16, 16);
        art.SetLayer(Layer.Middle, img);
        var recolor = ColorOperations.Replace(art, new Rgba32(0, 100, 0), new Rgba32(0, 0, 200));
        Assert.False(recolor.LosesColors);
        var merge = ColorOperations.Replace(art, new Rgba32(0, 100, 0), new Rgba32(100, 0, 0));
        Assert.True(merge.LosesColors);
        Assert.Equal(1, merge.UniqueAfter);
        Assert.Equal(new Rgba32(0, 100, 0), art.GetLayer(Layer.Middle)![0, 0]); // original untouched
    }

    [Fact]
    public void Diagnostics_ParseTileColorViolationWithLocation()
    {
        var log = File.ReadAllText(TestPaths.Asset(Path.Combine("porytiles-logs", "err_too_many_colors.log")));
        var d = PorytilesDiagnostics.Parse(log);
        var err = Assert.Single(d, x => x.Tag == "tile-color-count-violation");
        Assert.Equal(DiagnosticKind.Error, err.Kind);
        Assert.Equal(new DiagnosticLocation(8, Layer.Middle, 1, 1, 7), err.Location);
        Assert.Contains("color counts", err.Notes);
        Assert.Contains(d, x => x.Kind == DiagnosticKind.Fatal);

        var art = new Artwork(128, 64);
        var loc = PorytilesDiagnostics.ToIssueLocation(err.Location, art)!;
        Assert.Equal((1, 2, 15, 17), (loc.TileX, loc.TileY, loc.PixelX, loc.PixelY));
    }

    [Fact]
    public void Diagnostics_ParseGlobalColorViolation()
    {
        var log = File.ReadAllText(TestPaths.Asset(Path.Combine("porytiles-logs", "err_capacity.log")));
        var d = PorytilesDiagnostics.Parse(log);
        Assert.Contains(d, x => x.Tag == "global-color-count-violation" && x.Location?.Metatile == 3);
        var fatal = PorytilesDiagnostics.ToIssue(d.Last(x => x.Kind == DiagnosticKind.Fatal), null);
        Assert.Contains("limit is '195'", fatal.Message);
    }

    [Fact]
    public void Diagnostics_RecognizeCrash()
    {
        var log = File.ReadAllText(TestPaths.Asset(Path.Combine("porytiles-logs", "crash_create.log")));
        Assert.True(PorytilesDiagnostics.LooksLikeCrash(log));
    }

    [Fact]
    public void Transparency_AvoidsColorsOfPrimaryAndArt()
    {
        var pals = new[] { new[] { new Rgba32(1, 1, 1), new Rgba32(255, 0, 255) } };
        var t = SandboxCompiler.ChooseTransparency(pals, new HashSet<Rgba32> { new(0, 255, 255) });
        Assert.NotEqual(new Rgba32(255, 0, 255), t);
        Assert.NotEqual(new Rgba32(0, 255, 255), t);
    }

    [Theory]
    [InlineData(@"C:\Users\a b\proj", "Ubuntu", "/mnt/c/Users/a b/proj")]
    [InlineData(@"\\wsl.localhost\Ubuntu\home\p\x", "Ubuntu", "/home/p/x")]
    [InlineData(@"\\wsl$\ubuntu\home\p", "Ubuntu", "/home/p")]
    public void WslPath_Translates(string win, string distro, string linux) => Assert.Equal(linux, WslPath.ToLinux(win, distro));

    [Fact]
    public void WslPath_RejectsOtherDistro() =>
        Assert.Throws<ArgumentException>(() => WslPath.ToLinux(@"\\wsl.localhost\Debian\home", "Ubuntu"));
}
