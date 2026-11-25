using TilesetEditor.Core.Imaging;
using TilesetEditor.Core.Project;

namespace TilesetEditor.Core.Gba;

/// <summary>
/// Renders metatiles of a secondary tileset the way the game composes them: tile ids below
/// NUM_TILES_IN_PRIMARY come from the primary, palettes below NUM_PALS_IN_PRIMARY come from the primary.
/// Colors are shown as the GBA displays them (BGR555).
/// </summary>
public sealed class TilesetRenderer(FieldmapConstants constants, TilesetData primary, TilesetData secondary)
{
    public int MetatilesPerRow { get; init; } = 8;

    public Rgba32 ResolveColor(int palette, int colorIndex)
    {
        var src = palette < constants.NumPalsInPrimary ? primary : secondary;
        var pal = palette < src.Palettes.Count ? src.Palettes[palette] : null;
        var c = pal != null && colorIndex < pal.Length ? pal[colorIndex] : new Rgba32(0, 0, 0);
        return GbaColor.ToDisplayed(c);
    }

    public byte[]? ResolveTile(int tileId)
    {
        if (tileId < constants.NumTilesInPrimary)
            return tileId < primary.Tiles.Count ? primary.Tiles[tileId] : null;
        int local = tileId - constants.NumTilesInPrimary;
        return local < secondary.Tiles.Count ? secondary.Tiles[local] : null;
    }

    /// <summary>Renders the secondary's metatiles in a sheet. Layer slots: 4 tiles per layer (dual = 2 layers).</summary>
    public RgbaImage RenderSheet(bool[]? layerVisible = null)
    {
        int count = secondary.MetatileCount;
        int rows = Math.Max(1, (count + MetatilesPerRow - 1) / MetatilesPerRow);
        var img = new RgbaImage(MetatilesPerRow * 16, rows * 16);
        for (int m = 0; m < count; m++)
            DrawMetatile(img, m, (m % MetatilesPerRow) * 16, (m / MetatilesPerRow) * 16, layerVisible);
        return img;
    }

    public void DrawMetatile(RgbaImage target, int metatile, int ox, int oy, bool[]? layerVisible = null)
    {
        int layers = secondary.TilesPerMetatile / 4;
        for (int layer = 0; layer < layers; layer++)
        {
            if (layerVisible != null && layer < layerVisible.Length && !layerVisible[layer]) continue;
            for (int q = 0; q < 4; q++)
            {
                var e = secondary.GetEntry(metatile, layer * 4 + q);
                var tile = ResolveTile(e.TileId);
                if (tile == null) continue;
                int qx = ox + (q % 2) * 8, qy = oy + (q / 2) * 8;
                for (int y = 0; y < 8; y++)
                    for (int x = 0; x < 8; x++)
                    {
                        int sx = e.HFlip ? 7 - x : x, sy = e.VFlip ? 7 - y : y;
                        int ci = tile[sy * 8 + sx];
                        if (ci == 0) continue; // color 0 is transparent on every BG layer
                        target[qx + x, qy + y] = ResolveColor(e.Palette, ci);
                    }
            }
        }
    }
}
