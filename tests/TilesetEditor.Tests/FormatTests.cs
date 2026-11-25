using TilesetEditor.Core.Gba;
using TilesetEditor.Core.Imaging;
using TilesetEditor.Core.Project;

namespace TilesetEditor.Tests;

public class FormatTests
{
    [Fact]
    public void Png_RgbaRoundTrip()
    {
        var img = new RgbaImage(5, 3);
        for (int i = 0; i < img.Pixels.Length; i++) img.Pixels[i] = new Rgba32((byte)(i * 10), (byte)(255 - i), (byte)(i * 3), (byte)(i % 2 == 0 ? 255 : 0));
        var back = Png.Decode(Png.EncodeRgba(img));
        Assert.Equal(img.Pixels, back.Rgba.Pixels);
    }

    [Theory]
    [InlineData(4)]
    [InlineData(8)]
    public void Png_IndexedRoundTrip(int depth)
    {
        var idx = Enumerable.Range(0, 16 * 8).Select(i => (byte)(i % 16)).ToArray();
        var pal = Enumerable.Range(0, 16).Select(i => new Rgba32((byte)(i * 16), 0, 0)).ToArray();
        var png = Png.Decode(Png.EncodeIndexed(16, 8, idx, pal, depth));
        Assert.True(png.IsIndexed);
        Assert.Equal(idx, png.Indices);
    }

    [Fact]
    public void Png_ReadsProjectTilesetImage()
    {
        var p = Path.Combine(TestPaths.ReferenceProject, "data", "tilesets", "secondary", "fortree", "tiles.png");
        var png = Png.Load(p);
        Assert.True(png.IsIndexed);
        Assert.Equal(4, png.BitDepth);
        Assert.Equal(128, png.Width);
    }

    [Fact]
    public void Png_RejectsGarbage() => Assert.Throws<PngFormatException>(() => Png.Decode([1, 2, 3, 4, 5, 6, 7, 8, 9]));

    [Fact]
    public void Jasc_ParsesProjectPaletteAndWritesCrlf()
    {
        var p = Path.Combine(TestPaths.ReferenceProject, "data", "tilesets", "primary", "general", "palettes", "04.pal");
        var colors = JascPalette.Load(p);
        Assert.Equal(16, colors.Length);
        Assert.Equal(new Rgba32(255, 0, 255), colors[3]); // the magenta slots that break Porytiles' default transparency
        var text = JascPalette.Serialize(colors);
        Assert.StartsWith("JASC-PAL\r\n0100\r\n16\r\n", text);
        Assert.Equal(colors, JascPalette.Parse(text));
    }

    [Fact]
    public void GbaColor_TruncatesLikeGbagfx()
    {
        Assert.Equal(0x7FFF, GbaColor.ToBgr555(new Rgba32(255, 255, 255)));
        Assert.Equal(GbaColor.ToBgr555(new Rgba32(248, 0, 0)), GbaColor.ToBgr555(new Rgba32(255, 7, 7)));
        Assert.Equal(new Rgba32(255, 0, 0), GbaColor.FromBgr555(0x001F));
    }

    [Fact]
    public void CDefines_EvaluatesExpressions()
    {
        var d = new CDefines();
        d.AddFromText("#define A 512\n#define B (A * 2)\n#define C 0xF000 // mask\n#define D (B - A) >> 1\n");
        Assert.Equal(1024, d.TryEvaluate("B"));
        Assert.Equal(0xF000, d.TryEvaluate("C"));
        Assert.Equal(256, d.TryEvaluate("D"));
        Assert.Null(d.TryEvaluate("E"));
    }

    [Theory]
    [InlineData("gTileset_General", "general")]
    [InlineData("gTileset_BattleFrontierOutsideWest", "battle_frontier_outside_west")]
    [InlineData("gTileset_Route119", "route_119")]
    [InlineData("gTileset_MyTown", "my_town")]
    public void Naming_MatchesPorymapFolderRule(string name, string folder) => Assert.Equal(folder, TilesetNaming.ToFolderName(name));

    [Theory]
    [InlineData("gTileset_MyTown", true)]
    [InlineData("MyTown", false)]
    [InlineData("gTileset_myTown", false)]
    [InlineData("gTileset_My Town", false)]
    public void Naming_Validates(string name, bool ok) => Assert.Equal(ok, TilesetNaming.Validate(name) == null);
}
