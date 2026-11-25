namespace TilesetEditor.Core.Imaging;

/// <summary>A simple mutable RGBA raster.</summary>
public sealed class RgbaImage
{
    public int Width { get; }
    public int Height { get; }
    public Rgba32[] Pixels { get; }

    public RgbaImage(int width, int height)
    {
        if (width <= 0 || height <= 0) throw new ArgumentOutOfRangeException(nameof(width), "Image dimensions must be positive.");
        Width = width;
        Height = height;
        Pixels = new Rgba32[width * height];
    }

    public RgbaImage(int width, int height, Rgba32[] pixels)
    {
        if (pixels.Length != width * height) throw new ArgumentException("Pixel count does not match the image dimensions.", nameof(pixels));
        Width = width;
        Height = height;
        Pixels = pixels;
    }

    public Rgba32 this[int x, int y]
    {
        get => Pixels[y * Width + x];
        set => Pixels[y * Width + x] = value;
    }

    public RgbaImage Clone() => new(Width, Height, (Rgba32[])Pixels.Clone());

    public void Fill(Rgba32 color) => Array.Fill(Pixels, color);

    /// <summary>Copies a rectangle from <paramref name="src"/> into this image.</summary>
    public void Blit(RgbaImage src, int srcX, int srcY, int dstX, int dstY, int w, int h)
    {
        for (int y = 0; y < h; y++)
        {
            Array.Copy(src.Pixels, (srcY + y) * src.Width + srcX, Pixels, (dstY + y) * Width + dstX, w);
        }
    }

    /// <summary>Alpha-composites <paramref name="top"/> over this image (straight alpha).</summary>
    public void CompositeOver(RgbaImage top)
    {
        if (top.Width != Width || top.Height != Height) throw new ArgumentException("Layers have different dimensions.");
        for (int i = 0; i < Pixels.Length; i++)
        {
            var t = top.Pixels[i];
            if (t.A == 0) continue;
            if (t.A == 255) { Pixels[i] = t; continue; }
            var b = Pixels[i];
            float ta = t.A / 255f, ba = b.A / 255f;
            float oa = ta + ba * (1 - ta);
            byte Mix(byte tc, byte bc) => (byte)Math.Clamp((int)Math.Round((tc * ta + bc * ba * (1 - ta)) / oa), 0, 255);
            Pixels[i] = new Rgba32(Mix(t.R, b.R), Mix(t.G, b.G), Mix(t.B, b.B), (byte)Math.Round(oa * 255));
        }
    }

    public bool IsRegionEmpty(int x0, int y0, int w, int h, Func<Rgba32, bool> isTransparent)
    {
        for (int y = y0; y < y0 + h; y++)
            for (int x = x0; x < x0 + w; x++)
                if (!isTransparent(Pixels[y * Width + x])) return false;
        return true;
    }
}
