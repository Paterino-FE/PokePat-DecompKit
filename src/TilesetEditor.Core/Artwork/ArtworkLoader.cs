using TilesetEditor.Core.Imaging;

namespace TilesetEditor.Core.Artwork;

public sealed class ArtworkImportException(string message) : Exception(message);

public static class ArtworkLoader
{
    /// <summary>Checks the dimensions and returns a user-facing error, or null if valid.</summary>
    public static string? ValidateSize(int width, int height, string fileName)
    {
        if (width % Artwork.MetatileSize == 0 && height % Artwork.MetatileSize == 0 && width > 0 && height > 0)
            return null;
        int nw = Math.Max(16, (width + 15) / 16 * 16), nh = Math.Max(16, (height + 15) / 16 * 16);
        return $"'{fileName}' is {width}×{height} pixels. Width and height must be multiples of 16 " +
               $"(a metatile is 16×16 pixels, a tile 8×8). Extend the image to e.g. {nw}×{nh} " +
               "(pad the border with transparency) and import again.";
    }

    public static RgbaImage LoadImage(string path)
    {
        PngImage png;
        try
        {
            png = Png.Load(path);
        }
        catch (PngFormatException ex)
        {
            throw new ArtworkImportException(ex.Message);
        }
        var err = ValidateSize(png.Width, png.Height, Path.GetFileName(path));
        if (err != null) throw new ArtworkImportException(err);
        return png.Rgba;
    }

    /// <summary>Imports a single image as one layer.</summary>
    public static Artwork LoadSingle(string path, Layer layer, Rgba32? transparentColor = null)
    {
        var img = LoadImage(path);
        var art = new Artwork(img.Width, img.Height) { TransparentColor = transparentColor ?? Rgba32.Magenta };
        art.SetLayer(layer, img);
        art.SourceFiles[layer] = path;
        return art;
    }

    /// <summary>Imports separate layer images. All must have the same size.</summary>
    public static Artwork LoadLayers(IReadOnlyDictionary<Layer, string> files, Rgba32? transparentColor = null)
    {
        if (files.Count == 0) throw new ArtworkImportException("No layer files selected.");
        var images = files.ToDictionary(kv => kv.Key, kv => LoadImage(kv.Value));
        var first = images.First();
        foreach (var (layer, img) in images)
        {
            if (img.Width != first.Value.Width || img.Height != first.Value.Height)
                throw new ArtworkImportException(
                    $"The layers have different sizes: {LayerNames.German(first.Key)} is {first.Value.Width}×{first.Value.Height}, " +
                    $"{LayerNames.German(layer)} is {img.Width}×{img.Height}. All layers must be the same size.");
        }
        var art = new Artwork(first.Value.Width, first.Value.Height) { TransparentColor = transparentColor ?? Rgba32.Magenta };
        foreach (var (layer, img) in images)
        {
            art.SetLayer(layer, img);
            art.SourceFiles[layer] = files[layer];
        }
        return art;
    }

    /// <summary>Assigns files to layers by name (bottom/middle/top, unten/mitte/oben), otherwise in the given order.</summary>
    public static Dictionary<Layer, string> AssignLayers(IReadOnlyList<string> paths)
    {
        if (paths.Count > 3) throw new ArtworkImportException("At most three layer files (Bottom, Middle, Top) are allowed.");
        var result = new Dictionary<Layer, string>();
        var rest = new List<string>();
        foreach (var p in paths)
        {
            var g = LayerNames.GuessFromFileName(p);
            if (g != null && !result.ContainsKey(g.Value)) result[g.Value] = p;
            else rest.Add(p);
        }
        // Remaining files fill the free layers in order Bottom, Middle, Top.
        foreach (var p in rest)
        {
            var free = Enum.GetValues<Layer>().First(l => !result.ContainsKey(l));
            result[free] = p;
        }
        return result;
    }
}
