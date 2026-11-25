using TilesetEditor.Core.Gba;
using TilesetEditor.Core.Imaging;
using TilesetEditor.Core.Sprites;

namespace TilesetEditor.Tests;

public class SpriteTests
{
    [Fact]
    public void ReadsRegisteredOverworldSprites()
    {
        var sprites = OverworldSprites.Load(TestPaths.ReferenceProject);
        Assert.True(sprites.Count > 200, $"nur {sprites.Count}");
        var y = sprites.Single(s => s.GfxId == "OBJ_EVENT_GFX_YOUNGSTER");
        Assert.Equal((16, 32, "OBJ_EVENT_PAL_TAG_NPC_1"), (y.Width, y.Height, y.PaletteTag));
        Assert.Equal("graphics/object_events/pics/people/youngster.png", y.PngPath);
        Assert.Equal("graphics/object_events/palettes/npc_1.pal", y.PalettePath);
    }

    [Fact]
    public void ChecksAnExistingSpriteAsValidAndFindsItsPalette()
    {
        var sprites = OverworldSprites.Load(TestPaths.ReferenceProject);
        var pals = OverworldSprites.Palettes(sprites).ToDictionary(kv => kv.Key,
            kv => JascPalette.Load(Path.Combine(TestPaths.ReferenceProject, kv.Value)));
        var png = Png.Load(Path.Combine(TestPaths.ReferenceProject, "graphics", "object_events", "pics", "people", "youngster.png"));
        var r = OverworldSprites.Check(png, 16, 32, pals);
        Assert.False(r.HasErrors, string.Join("; ", r.Issues.Select(i => i.Message)));
        Assert.Equal(9, r.Frames);
        Assert.Contains("OBJ_EVENT_PAL_TAG_NPC_1", r.MatchingPalettes);
    }

    [Fact]
    public void ReportsWrongSizeAndTooManyColors()
    {
        var img = new RgbaImage(40, 32);
        for (int i = 0; i < img.Pixels.Length; i++) img.Pixels[i] = new Rgba32((byte)(i * 7), (byte)(i * 3), (byte)i);
        var png = Png.Decode(Png.EncodeRgba(img));
        var r = OverworldSprites.Check(png, 16, 32, new Dictionary<string, Rgba32[]>());
        Assert.True(r.HasErrors);
        Assert.Contains(r.Issues, i => i.Message.Contains("not a multiple"));
        Assert.Contains(r.Issues, i => i.Message.Contains("at most 15"));
    }
}
