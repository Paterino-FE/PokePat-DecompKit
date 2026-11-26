using TilesetEditor.Core.Gba;

namespace TilesetEditor.Core.Imaging;

/// <summary>Reduces the colors of an image to a small palette (median cut on GBA colors), for sprites with one 16-color palette.</summary>
public static class ColorQuantizer
{
    /// <summary>
    /// A copy of <paramref name="image"/> with at most <paramref name="maxColors"/> opaque colors (GBA-snapped); transparent
    /// pixels (alpha below 128) stay transparent. Images that already fit are only snapped.
    /// </summary>
    public static RgbaImage Reduce(RgbaImage image, int maxColors)
    {
        var result = new RgbaImage(image.Width, image.Height);
        var counts = new Dictionary<Rgba32, int>();
        for (int i = 0; i < image.Pixels.Length; i++)
        {
            var p = image.Pixels[i];
            if (p.A < 128) continue;
            var c = GbaColor.Snap(p.Opaque);
            counts[c] = counts.GetValueOrDefault(c) + 1;
        }
        var palette = counts.Count <= maxColors ? counts.Keys.ToList() : MedianCut(counts, maxColors);
        var map = new Dictionary<Rgba32, Rgba32>();
        for (int i = 0; i < image.Pixels.Length; i++)
        {
            var p = image.Pixels[i];
            if (p.A < 128) { result.Pixels[i] = Rgba32.Transparent; continue; }
            var c = GbaColor.Snap(p.Opaque);
            if (!map.TryGetValue(c, out var q)) map[c] = q = Nearest(palette, c);
            result.Pixels[i] = q;
        }
        return result;
    }

    private static Rgba32 Nearest(List<Rgba32> palette, Rgba32 c)
    {
        Rgba32 best = palette[0];
        int bd = int.MaxValue;
        foreach (var p in palette)
        {
            int d = (p.R - c.R) * (p.R - c.R) * 3 + (p.G - c.G) * (p.G - c.G) * 4 + (p.B - c.B) * (p.B - c.B) * 2;
            if (d < bd) { bd = d; best = p; }
        }
        return best;
    }

    private static List<Rgba32> MedianCut(Dictionary<Rgba32, int> counts, int max)
    {
        var boxes = new List<List<(Rgba32 C, int N)>> { counts.Select(kv => (kv.Key, kv.Value)).ToList() };
        while (boxes.Count < max)
        {
            // Split the box with the widest channel range (weighted by pixels).
            var box = boxes.Where(b => b.Count > 1).OrderByDescending(b => Range(b) * Math.Log(1 + b.Sum(x => x.N))).FirstOrDefault();
            if (box == null) break;
            int rr = box.Max(x => x.C.R) - box.Min(x => x.C.R), gr = box.Max(x => x.C.G) - box.Min(x => x.C.G), br = box.Max(x => x.C.B) - box.Min(x => x.C.B);
            Func<(Rgba32 C, int N), int> key = rr >= gr && rr >= br ? x => x.C.R : gr >= br ? x => x.C.G : x => x.C.B;
            var sorted = box.OrderBy(key).ToList();
            int total = sorted.Sum(x => x.N), acc = 0, cut = 1;
            for (int i = 0; i < sorted.Count - 1; i++) { acc += sorted[i].N; if (acc * 2 >= total) { cut = i + 1; break; } }
            boxes.Remove(box);
            boxes.Add(sorted.Take(cut).ToList());
            boxes.Add(sorted.Skip(cut).ToList());
        }
        return boxes.Select(b =>
        {
            long n = b.Sum(x => (long)x.N);
            return GbaColor.Snap(new Rgba32((byte)(b.Sum(x => (long)x.C.R * x.N) / n), (byte)(b.Sum(x => (long)x.C.G * x.N) / n), (byte)(b.Sum(x => (long)x.C.B * x.N) / n)));
        }).Distinct().ToList();
    }

    private static int Range(List<(Rgba32 C, int N)> b) =>
        Math.Max(b.Max(x => x.C.R) - b.Min(x => x.C.R), Math.Max(b.Max(x => x.C.G) - b.Min(x => x.C.G), b.Max(x => x.C.B) - b.Min(x => x.C.B)));
}
