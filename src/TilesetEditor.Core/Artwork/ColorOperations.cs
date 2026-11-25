using TilesetEditor.Core.Gba;
using TilesetEditor.Core.Imaging;

namespace TilesetEditor.Core.Artwork;

public sealed record ColorUsage(Rgba32 Color, int Pixels, int Tiles)
{
    public string Hex => Color.ToHex();
    public string GbaHex => $"0x{GbaColor.ToBgr555(Color):X4}";
}

/// <summary>Result of a color edit. <see cref="LosesColors"/> is true when distinct colors are merged.</summary>
public sealed record ColorChange(Artwork Result, int UniqueBefore, int UniqueAfter, IReadOnlyList<(Rgba32 From, Rgba32 To)> Mapping, int ChangedPixels)
{
    public bool LosesColors => UniqueAfter < UniqueBefore;
    public int LostColors => UniqueBefore - UniqueAfter;
}

/// <summary>Simple manual color mapping for the MVP. Operations return a new artwork; the caller decides whether to apply.</summary>
public static class ColorOperations
{
    public static List<ColorUsage> Usage(Artwork art)
    {
        var pixels = new Dictionary<Rgba32, int>();
        var tiles = new Dictionary<Rgba32, HashSet<(int, int, int)>>();
        foreach (var (layer, img) in art.PresentLayers())
            for (int y = 0; y < art.Height; y++)
                for (int x = 0; x < art.Width; x++)
                {
                    var c = img[x, y];
                    if (art.IsTransparent(c)) continue;
                    c = c.Opaque;
                    pixels[c] = pixels.GetValueOrDefault(c) + 1;
                    if (!tiles.TryGetValue(c, out var set)) tiles[c] = set = [];
                    set.Add(((int)layer, x / 8, y / 8));
                }
        return pixels.Select(kv => new ColorUsage(kv.Key, kv.Value, tiles[kv.Key].Count))
            .OrderByDescending(u => u.Pixels).ToList();
    }

    /// <summary>Replaces <paramref name="from"/> by <paramref name="to"/> in every layer.</summary>
    public static ColorChange Replace(Artwork art, Rgba32 from, Rgba32 to) =>
        Map(art, c => c.SameRgb(from) ? to.Opaque : c);

    /// <summary>Snaps all colors to the 15-bit GBA grid (merges colors the GBA cannot distinguish anyway).</summary>
    public static ColorChange SnapToGba(Artwork art) => Map(art, GbaColor.Snap);

    private static ColorChange Map(Artwork art, Func<Rgba32, Rgba32> f)
    {
        var before = art.OpaqueColors();
        var result = art.Clone();
        int changed = 0;
        var mapping = new Dictionary<Rgba32, Rgba32>();
        foreach (var (layer, img) in result.PresentLayers())
        {
            for (int i = 0; i < img.Pixels.Length; i++)
            {
                var c = img.Pixels[i];
                if (art.IsTransparent(c)) continue;
                var n = f(c.Opaque) with { A = 255 };
                if (n != c)
                {
                    if (!n.SameRgb(c)) mapping[c.Opaque] = n;
                    img.Pixels[i] = n;
                    changed++;
                }
            }
        }
        var after = result.OpaqueColors();
        return new ColorChange(result, before.Count, after.Count, mapping.Select(kv => (kv.Key, kv.Value)).ToList(), changed);
    }
}
