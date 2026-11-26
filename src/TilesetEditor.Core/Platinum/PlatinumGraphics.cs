using TilesetEditor.Core.Imaging;
using TilesetEditor.Core.Project;

namespace TilesetEditor.Core.Platinum;

/// <summary>A picture of res/graphics with what the build needs to know about it.</summary>
public sealed record GraphicInfo(string Path, string Group, int Width, int Height, int BitDepth, bool Indexed, int PaletteSize, int MaxIndex, List<string> Problems, List<string> Notes);

/// <summary>
/// All pictures of res/graphics (title screen, bag, Pokédex, summary, menus, battle, town map …): listing, checks and
/// replacing in the picture's own format. The DS reads 4-bit pictures with 16 colours and 8-bit ones with up to 256;
/// many pictures of one screen share a palette, so a new picture is by default drawn in the colours of the old one.
/// </summary>
public static class PlatinumGraphics
{
    public const string Folder = "res/graphics";

    public static List<string> List(string root)
    {
        var dir = ProjectPaths.Combine(root, Folder);
        if (!Directory.Exists(dir)) return [];
        return Directory.EnumerateFiles(dir, "*.png", SearchOption.AllDirectories)
            .Select(f => ProjectPaths.Normalize(System.IO.Path.GetRelativePath(root, f)))
            .OrderBy(f => f, StringComparer.Ordinal).ToList();
    }

    public static string GroupOf(string rel)
    {
        var parts = rel.Split('/');
        return parts.Length > 3 ? parts[2] : "(other)";
    }

    public static GraphicInfo Inspect(string root, string rel)
    {
        var problems = new List<string>();
        var notes = new List<string>();
        var png = Png.Load(ProjectPaths.Combine(root, rel));
        int max = png.Indices is { Length: > 0 } idx ? idx.Max() : 0;
        int pal = png.Palette?.Length ?? 0;
        if (png.IsIndexed)
        {
            if (png.BitDepth == 8 && max < 16 && SiblingsAre4Bit(root, rel))
                notes.Add("Saved as 8-bit with only 16 colours, while most pictures next to it are 4-bit. That is how the original has it; if a changed picture shows up scrambled in-game, \"Save as 4-bit\" is the usual fix.");
            if (max >= pal && pal > 0) problems.Add($"Uses palette index {max}, but the palette has only {pal} colours.");
        }
        else notes.Add("A full-colour picture: the build turns it into DS colours with the palette that belongs to it. A replacement keeps that form.");
        return new GraphicInfo(rel, GroupOf(rel), png.Width, png.Height, png.BitDepth, png.IsIndexed, pal, max, problems, notes);
    }

    private static bool SiblingsAre4Bit(string root, string rel)
    {
        var dir = System.IO.Path.GetDirectoryName(ProjectPaths.Combine(root, rel))!;
        int four = 0, other = 0;
        foreach (var f in Directory.EnumerateFiles(dir, "*.png").Take(40))
        {
            try
            {
                var p = Png.Load(f);
                if (p.IsIndexed && p.BitDepth == 4) four++; else other++;
            }
            catch (PngFormatException) { }
        }
        return four > other;
    }

    /// <summary>The picture as true 4-bit PNG (same indices and colours).</summary>
    public static byte[] To4Bit(string root, string rel) => PlatinumHealthbox.To4bpp(Png.Load(ProjectPaths.Combine(root, rel)));

    /// <summary>
    /// A replacement in the old picture's format: same size and bit depth. <paramref name="keepPalette"/>: every pixel gets the
    /// nearest colour of the old palette (index 0 for transparent) – the palette stays, so pictures that share it keep their
    /// colours. Otherwise the picture gets its own palette of the same size.
    /// </summary>
    /// <summary>
    /// Like the other overload, for a PNG file: with its own colours (<paramref name="keepPalette"/> false) an indexed PNG
    /// that fits the file's bit depth is kept exactly – its palette order and colour 0 (transparent) stay as drawn.
    /// </summary>
    public static byte[] Replace(string root, string rel, PngImage png, bool keepPalette, Rgba32[]? matchPalette = null)
    {
        var old = Png.Load(ProjectPaths.Combine(root, rel));
        // Overworld sheets: the background colour becomes colour 0; sheets exported from the game's textures (only the
        // different frames, in the stored order) are spread to the full frame order.
        if (rel.Contains("field_sprites", StringComparison.Ordinal)) png = IndexedPicture.BackgroundFirst(png, png.Width);
        if (old.Width == png.Width && old.Height % Math.Max(1, old.Width) == 0 && PlatinumOverworld.ExpandUniqueFrames(old, png) is { } full) png = full;
        if (png.Width != old.Width || png.Height != old.Height)
            throw new InvalidDataException($"The picture is {png.Width}×{png.Height}; {System.IO.Path.GetFileName(rel)} is {old.Width}×{old.Height}.");
        if (!keepPalette && old.IsIndexed && IndexedPicture.KeepOwnPalette(png, old.BitDepth is 4 or 8 ? old.BitDepth : 8) is { } own) return own;
        return Replace(root, rel, IndexedPicture.WithTransparentIndex0(png), keepPalette, matchPalette);
    }

    public static byte[] Replace(string root, string rel, RgbaImage picture, bool keepPalette, Rgba32[]? matchPalette = null)
    {
        var old = Png.Load(ProjectPaths.Combine(root, rel));
        if (picture.Width != old.Width || picture.Height != old.Height)
            throw new InvalidDataException($"The picture is {picture.Width}×{picture.Height}; {System.IO.Path.GetFileName(rel)} is {old.Width}×{old.Height}.");
        if (!old.IsIndexed) return Png.EncodeRgba(picture);
        int depth = old.BitDepth is 4 or 8 ? old.BitDepth : 8;
        int colors = depth == 4 ? 16 : Math.Min(256, Math.Max(16, old.Palette?.Length ?? 16));
        Rgba32[] palette;
        Rgba32[]? written = null;
        if (keepPalette && (matchPalette ?? old.Palette) is { Length: > 0 } p)
        {
            // Colours are matched against the palette the game shows (e.g. the day palette of a battle field); the file keeps its own.
            palette = p.Take(colors).Select(c => c with { A = 255 }).ToArray();
            if (matchPalette != null && old.Palette != null) written = old.Palette.Take(colors).ToArray();
        }
        else
        {
            var reduced = ColorQuantizer.Reduce(picture, colors - 1);
            var used = reduced.Pixels.Where(c => c.A >= 128).Select(c => c with { A = 255 }).Distinct().Take(colors - 1).ToList();
            palette = new Rgba32[colors];
            palette[0] = old.Palette is { Length: > 0 } op ? op[0] with { A = 255 } : new Rgba32(0, 0, 0);
            for (int i = 1; i < colors; i++) palette[i] = i - 1 < used.Count ? used[i - 1] : new Rgba32(0, 0, 0);
            picture = reduced;
        }
        var idx = new byte[picture.Pixels.Length];
        for (int i = 0; i < idx.Length; i++)
        {
            var c = picture.Pixels[i];
            if (c.A < 128) { idx[i] = 0; continue; }
            int best = keepPalette ? 0 : 1;
            long bestD = long.MaxValue;
            for (int k = keepPalette ? 0 : 1; k < palette.Length; k++)
            {
                long d = (long)(palette[k].R - c.R) * (palette[k].R - c.R) + (long)(palette[k].G - c.G) * (palette[k].G - c.G) + (long)(palette[k].B - c.B) * (palette[k].B - c.B);
                if (d < bestD) { bestD = d; best = k; }
            }
            idx[i] = (byte)best;
        }
        return Png.EncodeIndexed(picture.Width, picture.Height, idx, written ?? palette, depth);
    }
}
