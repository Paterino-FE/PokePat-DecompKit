namespace TilesetEditor.Core.Imaging;

/// <summary>
/// Indexed PNGs made for the game (own palette, colour 0 = transparent): kept as they are instead of being re-coloured.
/// Such files often have no transparency info – colour 0 is simply the background colour of the sheet.
/// </summary>
public static class IndexedPicture
{
    /// <summary>
    /// The picture written unchanged – same indices, same palette order – as a PNG of <paramref name="depth"/> bits, when it
    /// is indexed and uses only colours 0 … 2^depth − 1; otherwise null.
    /// </summary>
    public static byte[]? KeepOwnPalette(PngImage png, int depth)
    {
        if (png.Indices == null || png.Palette is not { Length: > 0 } pal) return null;
        int colors = 1 << depth;
        if (png.Indices.Any(i => i >= colors)) return null;
        var palette = Enumerable.Range(0, colors).Select(i => i < pal.Length ? pal[i] with { A = 255 } : new Rgba32(0, 0, 0)).ToArray();
        return Png.EncodeIndexed(png.Width, png.Height, png.Indices, palette, depth);
    }

    /// <summary>
    /// Sprite sheets whose background is not colour 0 (every frame has the same colour k in all four corners, k ≠ 0): colours
    /// k and 0 swap places – in the pixels and in the palette – so the background becomes transparent and colour 0, which
    /// the person used, stays visible. Other pictures are returned unchanged.
    /// </summary>
    public static PngImage BackgroundFirst(PngImage png, int frameSize)
    {
        if (png.Indices == null || png.Palette == null || frameSize <= 0 || png.Width < frameSize) return png;
        var corners = new HashSet<byte>();
        for (int fy = 0; fy + frameSize <= png.Height; fy += frameSize)
            for (int fx = 0; fx + frameSize <= png.Width; fx += frameSize)
                foreach (var (x, y) in new[] { (fx, fy), (fx + frameSize - 1, fy), (fx, fy + frameSize - 1), (fx + frameSize - 1, fy + frameSize - 1) })
                    corners.Add(png.Indices[y * png.Width + x]);
        if (corners.Count != 1 || corners.First() is var k && k == 0 || k >= png.Palette.Length) return png;
        var idx = png.Indices.Select(i => i == k ? (byte)0 : i == 0 ? k : i).ToArray();
        var pal = png.Palette.ToArray();
        (pal[0], pal[k]) = (pal[k], pal[0]);
        return Png.Decode(Png.EncodeIndexed(png.Width, png.Height, idx, pal, png.BitDepth is 4 or 8 ? (pal.Length <= 16 ? 4 : 8) : 8));
    }

    /// <summary>The picture in colour, with colour 0 of an indexed PNG transparent (as the game draws it).</summary>
    public static RgbaImage WithTransparentIndex0(PngImage png)
    {
        var img = png.Rgba.Clone();
        if (png.Indices != null)
            for (int i = 0; i < img.Pixels.Length; i++)
                if (png.Indices[i] == 0) img.Pixels[i] = Rgba32.Transparent;
        return img;
    }
}
