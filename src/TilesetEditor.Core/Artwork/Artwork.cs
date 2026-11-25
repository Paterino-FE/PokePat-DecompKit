using TilesetEditor.Core.Imaging;

namespace TilesetEditor.Core.Artwork;

public enum Layer
{
    Bottom = 0,
    Middle = 1,
    Top = 2,
}

public static class LayerNames
{
    public static string German(Layer l) => l switch
    {
        Layer.Bottom => "Bottom",
        Layer.Middle => "Middle",
        Layer.Top => "Top",
        _ => l.ToString(),
    };

    /// <summary>Porytiles source file name for the layer.</summary>
    public static string PorytilesFile(Layer l) => l switch
    {
        Layer.Bottom => "bottom.png",
        Layer.Middle => "middle.png",
        _ => "top.png",
    };

    public static Layer? FromPorytilesName(string? name) => name?.ToLowerInvariant() switch
    {
        "bottom" => Layer.Bottom,
        "middle" => Layer.Middle,
        "top" => Layer.Top,
        _ => null,
    };

    /// <summary>Guesses the layer from a file name like "town_top.png" or "haus-unten.png".</summary>
    public static Layer? GuessFromFileName(string path)
    {
        var n = Path.GetFileNameWithoutExtension(path).ToLowerInvariant();
        if (n.Contains("bottom") || n.Contains("unten") || n.EndsWith("_bg") || n.EndsWith("_0")) return Layer.Bottom;
        if (n.Contains("middle") || n.Contains("mitte") || n.EndsWith("_1")) return Layer.Middle;
        if (n.Contains("top") || n.Contains("oben") || n.EndsWith("_2")) return Layer.Top;
        return null;
    }
}

/// <summary>
/// The imported RGBA artwork: up to three layers of identical size. Metatiles are read in row-major order
/// from the source image (any width that is a multiple of 16).
/// </summary>
public sealed class Artwork
{
    public const int MetatileSize = 16;
    public const int TileSize = 8;
    public const int SheetMetatilesPerRow = 8;

    private readonly RgbaImage?[] _layers = new RgbaImage?[3];

    public int Width { get; }
    public int Height { get; }
    /// <summary>Pixels with this RGB (any alpha) count as transparent, as do pixels with alpha 0.</summary>
    public Rgba32 TransparentColor { get; init; } = Rgba32.Magenta;
    /// <summary>Source files per layer, for display only.</summary>
    public Dictionary<Layer, string> SourceFiles { get; } = [];

    public Artwork(int width, int height)
    {
        Width = width;
        Height = height;
    }

    public int Columns => Width / MetatileSize;
    public int Rows => Height / MetatileSize;
    public int MetatileCount => Columns * Rows;
    public int TileColumns => Width / TileSize;
    public int TileRows => Height / TileSize;

    public RgbaImage? GetLayer(Layer l) => _layers[(int)l];

    public void SetLayer(Layer l, RgbaImage? img)
    {
        if (img != null && (img.Width != Width || img.Height != Height))
            throw new ArgumentException($"Layer {LayerNames.German(l)} is {img.Width}×{img.Height}, expected {Width}×{Height}.");
        _layers[(int)l] = img;
    }

    public IEnumerable<(Layer Layer, RgbaImage Image)> PresentLayers()
    {
        for (int i = 0; i < 3; i++)
            if (_layers[i] != null) yield return ((Layer)i, _layers[i]!);
    }

    public bool IsTransparent(Rgba32 c) => c.A == 0 || c.SameRgb(TransparentColor);

    public Artwork Clone()
    {
        var a = new Artwork(Width, Height) { TransparentColor = TransparentColor };
        for (int i = 0; i < 3; i++) a._layers[i] = _layers[i]?.Clone();
        foreach (var kv in SourceFiles) a.SourceFiles[kv.Key] = kv.Value;
        return a;
    }

    public (int X, int Y) MetatileOrigin(int index) => ((index % Columns) * MetatileSize, (index / Columns) * MetatileSize);

    public int MetatileIndexAt(int px, int py) => (py / MetatileSize) * Columns + px / MetatileSize;

    /// <summary>Composite of all visible layers, transparent pixels cleared, for the preview.</summary>
    public RgbaImage Composite(bool[]? visible = null)
    {
        var result = new RgbaImage(Width, Height);
        foreach (var (layer, img) in PresentLayers())
        {
            if (visible != null && !visible[(int)layer]) continue;
            var clean = img.Clone();
            for (int i = 0; i < clean.Pixels.Length; i++)
                if (IsTransparent(clean.Pixels[i])) clean.Pixels[i] = Rgba32.Transparent;
            result.CompositeOver(clean);
        }
        return result;
    }

    public bool MetatileHasContent(Layer l, int metatile)
    {
        var img = GetLayer(l);
        if (img == null) return false;
        var (x, y) = MetatileOrigin(metatile);
        return !img.IsRegionEmpty(x, y, MetatileSize, MetatileSize, IsTransparent);
    }

    /// <summary>
    /// Builds the 128-pixel-wide Porytiles source sheet for a layer (8 metatiles per row, in metatile order).
    /// Transparent pixels are written as <paramref name="transparency"/> with full alpha.
    /// </summary>
    public RgbaImage ToPorytilesSheet(Layer l, Rgba32 transparency)
    {
        int rows = Math.Max(1, (MetatileCount + SheetMetatilesPerRow - 1) / SheetMetatilesPerRow);
        var sheet = new RgbaImage(SheetMetatilesPerRow * MetatileSize, rows * MetatileSize);
        sheet.Fill(transparency with { A = 255 });
        var src = GetLayer(l);
        if (src == null) return sheet;
        for (int m = 0; m < MetatileCount; m++)
        {
            var (sx, sy) = MetatileOrigin(m);
            int dx = (m % SheetMetatilesPerRow) * MetatileSize, dy = (m / SheetMetatilesPerRow) * MetatileSize;
            for (int y = 0; y < MetatileSize; y++)
                for (int x = 0; x < MetatileSize; x++)
                {
                    var c = src[sx + x, sy + y];
                    sheet[dx + x, dy + y] = IsTransparent(c) ? transparency with { A = 255 } : c with { A = 255 };
                }
        }
        return sheet;
    }

    /// <summary>All distinct opaque colors (RGB) used by any layer.</summary>
    public HashSet<Rgba32> OpaqueColors()
    {
        var set = new HashSet<Rgba32>();
        foreach (var (_, img) in PresentLayers())
            foreach (var c in img.Pixels)
                if (!IsTransparent(c)) set.Add(c.Opaque);
        return set;
    }
}
