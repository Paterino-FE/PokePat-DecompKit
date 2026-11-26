using TilesetEditor.Core.Artwork;
using TilesetEditor.Core.Gba;
using TilesetEditor.Core.Imaging;
using TilesetEditor.Core.Project;

namespace TilesetEditor.Core.Tilesets;

/// <summary>
/// Turns a tileset back into editable layer images – bottom, middle and top, 8 metatiles per row, transparent where a layer
/// is empty – in the colors of its palettes. Edited and compiled again with Porytiles, the metatiles keep their order, so
/// maps still show the same blocks.
/// </summary>
public static class TilesetLayers
{
    public const int MetatilesPerRow = Artwork.Artwork.SheetMetatilesPerRow;

    /// <summary>The layers of <paramref name="secondary"/>; tiles and palettes below the primary's range come from <paramref name="primary"/>.</summary>
    public static Dictionary<Layer, RgbaImage> Decompile(ProjectInfo project, TilesetData primary, TilesetData secondary)
    {
        var c = project.Constants;
        int count = secondary.MetatileCount;
        int rows = Math.Max(1, (count + MetatilesPerRow - 1) / MetatilesPerRow);
        var layers = new Dictionary<Layer, RgbaImage>
        {
            [Layer.Bottom] = new(MetatilesPerRow * 16, rows * 16),
            [Layer.Middle] = new(MetatilesPerRow * 16, rows * 16),
            [Layer.Top] = new(MetatilesPerRow * 16, rows * 16),
        };
        int tpm = secondary.TilesPerMetatile;
        for (int m = 0; m < count; m++)
        {
            int ox = m % MetatilesPerRow * 16, oy = m / MetatilesPerRow * 16;
            // Which picture layer each group of four entries goes to.
            Layer[] targets;
            if (tpm == 12) targets = [Layer.Bottom, Layer.Middle, Layer.Top];
            else
            {
                var type = m < secondary.Attributes.Count ? (secondary.Attributes[m] & project.Attributes.LayerTypeMask) >> project.Attributes.LayerTypeShift : 0;
                targets = type switch { 1 => [Layer.Bottom, Layer.Middle], 2 => [Layer.Bottom, Layer.Top], _ => [Layer.Middle, Layer.Top] };
            }
            for (int group = 0; group < targets.Length; group++)
                for (int q = 0; q < 4; q++)
                {
                    var e = secondary.GetEntry(m, group * 4 + q);
                    DrawTile(layers[targets[group]], ox + q % 2 * 8, oy + q / 2 * 8, e, c, primary, secondary);
                }
        }
        return layers;
    }

    /// <summary>Empty layers for a new tileset of <paramref name="metatiles"/> metatiles.</summary>
    public static Dictionary<Layer, RgbaImage> Blank(int metatiles)
    {
        int rows = Math.Max(1, (metatiles + MetatilesPerRow - 1) / MetatilesPerRow);
        return new Dictionary<Layer, RgbaImage>
        {
            [Layer.Bottom] = new(MetatilesPerRow * 16, rows * 16),
            [Layer.Middle] = new(MetatilesPerRow * 16, rows * 16),
            [Layer.Top] = new(MetatilesPerRow * 16, rows * 16),
        };
    }

    /// <summary>Writes bottom.png, middle.png and top.png (Porytiles' names) into <paramref name="folder"/>; returns the paths.</summary>
    public static Dictionary<Layer, string> Save(Dictionary<Layer, RgbaImage> layers, string folder)
    {
        Directory.CreateDirectory(folder);
        var paths = new Dictionary<Layer, string>();
        foreach (var (layer, img) in layers)
        {
            var path = Path.Combine(folder, LayerNames.PorytilesFile(layer));
            File.WriteAllBytes(path, Png.EncodeRgba(img));
            paths[layer] = path;
        }
        return paths;
    }

    private static void DrawTile(RgbaImage img, int x0, int y0, MetatileEntry e, FieldmapConstants c, TilesetData primary, TilesetData secondary)
    {
        int id = e.TileId;
        byte[]? tile = id < c.NumTilesInPrimary
            ? (id < primary.Tiles.Count ? primary.Tiles[id] : null)
            : (id - c.NumTilesInPrimary < secondary.Tiles.Count ? secondary.Tiles[id - c.NumTilesInPrimary] : null);
        if (tile == null) return;
        var source = e.Palette < c.NumPalsInPrimary ? primary : secondary;
        var pal = e.Palette < source.Palettes.Count ? source.Palettes[e.Palette] : null;
        if (pal == null) return;
        for (int y = 0; y < 8; y++)
            for (int x = 0; x < 8; x++)
            {
                int sx = e.HFlip ? 7 - x : x, sy = e.VFlip ? 7 - y : y;
                int idx = tile[sy * 8 + sx];
                if (idx == 0 || idx >= pal.Length) continue; // index 0 is transparent
                img[x0 + x, y0 + y] = pal[idx] with { A = 255 };
            }
    }
}
