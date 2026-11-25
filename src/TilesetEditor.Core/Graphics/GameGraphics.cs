using TilesetEditor.Core.Gba;
using TilesetEditor.Core.Imaging;

namespace TilesetEditor.Core.Graphics;

/// <summary>Where the in-game colors of an image come from (pokeemerald builds x.gbapal from x.pal, else from x.png).</summary>
public enum PaletteSource
{
    /// <summary>x.pal next to x.png: the game uses that file.</summary>
    OwnPalFile,
    /// <summary>x.gbapal is built from the PNG's own palette.</summary>
    ImagePalette,
    /// <summary>No palette with the same name: colors come from another (shared) palette file.</summary>
    Shared,
}

public sealed record GraphicsFolder(string Rel, string Title, bool Featured, int Count)
{
    public override string ToString() => $"{Title}  ({Count})";
}

public sealed record GraphicInfo(string Rel, int Width, int Height, int BitDepth, bool Indexed, int PaletteSize, int UsedColors,
    PaletteSource Source, string? PalRel)
{
    public string SourceText => Source switch
    {
        PaletteSource.OwnPalFile => $"Colors from {PalRel}",
        PaletteSource.ImagePalette => "Colors from the image's own palette",
        _ => "Colors from a shared palette file (not named like this image)",
    };
}

public sealed record GraphicColorChange(Rgba32 From, Rgba32 To);

public sealed record ReplacementResult(byte[] Png, byte[]? Pal, RgbaImage Preview, IReadOnlyList<GraphicColorChange> Changes, IReadOnlyList<string> Errors);

/// <summary>In-game graphics under graphics/ (title screen, menus, text windows …): browse and replace with checks.</summary>
public static class GameGraphics
{
    /// <summary>Folders with their own modules (or not meaningful as single images).</summary>
    private static readonly string[] Excluded = ["pokemon", "trainers", "object_events", "tilesets"];

    private static readonly (string Folder, string Title)[] Featured =
    [
        ("title_screen", "Title screen"), ("ui_main_menu", "Main menu"), ("text_window", "Text windows & frames"), ("interface", "Menus & interface"),
        ("map_popup", "Map name pop-ups"), ("birch_speech", "Intro (professor speech)"), ("intro", "Intro movie"), ("expansion_intro", "Expansion intro"),
        ("party_menu", "Party menu"), ("bag", "Bag"), ("summary_screen", "Summary screen"), ("pokedex", "Pokédex"), ("pokemon_storage", "PC storage"),
        ("battle_interface", "Battle interface"), ("battle_terrain", "Battle backgrounds"), ("battle_environment", "Battle backgrounds"),
        ("trainer_card", "Trainer card"), ("naming_screen", "Naming screen"), ("shop", "Shop"), ("starter_choose", "Starter selection"),
        ("types", "Type icons"), ("items", "Item icons"), ("fonts", "Fonts"), ("misc", "Misc"), ("credits", "Credits"),
    ];

    public static List<GraphicsFolder> Folders(string root)
    {
        var dir = Path.Combine(root, "graphics");
        if (!Directory.Exists(dir)) return [];
        var result = new List<GraphicsFolder>();
        foreach (var sub in Directory.EnumerateDirectories(dir).Select(Path.GetFileName).OfType<string>().Where(n => !Excluded.Contains(n)))
        {
            int count = Directory.EnumerateFiles(Path.Combine(dir, sub), "*.png", SearchOption.AllDirectories).Count();
            if (count == 0) continue;
            var f = Featured.FirstOrDefault(x => x.Folder == sub);
            result.Add(new GraphicsFolder("graphics/" + sub, f.Title ?? Title(sub), f.Title != null, count));
        }
        int Rank(GraphicsFolder g) { int i = Array.FindIndex(Featured, x => "graphics/" + x.Folder == g.Rel); return i < 0 ? 999 : i; }
        return result.OrderBy(Rank).ThenBy(g => g.Title, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static string Title(string folder) =>
        string.Join(" ", folder.Split('_', StringSplitOptions.RemoveEmptyEntries).Select(w => char.ToUpperInvariant(w[0]) + w[1..]));

    public static List<string> Files(string root, string folderRel) =>
        Directory.EnumerateFiles(Path.Combine(root, folderRel.Replace('/', Path.DirectorySeparatorChar)), "*.png", SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(root, f).Replace('\\', '/'))
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToList();

    public static GraphicInfo Inspect(string root, string rel, PngImage png)
    {
        var full = Path.Combine(root, rel.Replace('/', Path.DirectorySeparatorChar));
        var pal = Path.ChangeExtension(full, ".pal");
        var gbapal = Path.ChangeExtension(full, ".gbapal");
        var source = File.Exists(pal) ? PaletteSource.OwnPalFile
            : File.Exists(gbapal) || File.Exists(gbapal + ".lz") ? PaletteSource.ImagePalette
            : PaletteSource.Shared;
        int used;
        if (png.Indices != null) used = png.Indices.Distinct().Count();
        else
        {
            var set = new HashSet<Rgba32>();
            for (int y = 0; y < png.Height; y++) for (int x = 0; x < png.Width; x++) set.Add(png.Rgba[x, y]);
            used = set.Count;
        }
        return new GraphicInfo(rel, png.Width, png.Height, png.BitDepth, png.IsIndexed, png.Palette?.Length ?? 0, used, source,
            source == PaletteSource.OwnPalFile ? Path.ChangeExtension(rel, ".pal") : null);
    }

    /// <summary>Image with index 0 shown transparent (as in game).</summary>
    public static RgbaImage Render(PngImage png, IReadOnlyList<Rgba32>? palette = null)
    {
        var img = new RgbaImage(png.Width, png.Height);
        for (int y = 0; y < png.Height; y++)
            for (int x = 0; x < png.Width; x++)
            {
                if (png.Indices != null)
                {
                    int i = png.Indices[y * png.Width + x];
                    var pal = palette ?? png.Palette!;
                    img[x, y] = i == 0 ? default : GbaColor.ToDisplayed(i < pal.Count ? pal[i] with { A = 255 } : default);
                }
                else img[x, y] = GbaColor.ToDisplayed(png.Rgba[x, y]);
            }
        return img;
    }

    private static int Distance(Rgba32 a, Rgba32 b)
    {
        int dr = (a.R >> 3) - (b.R >> 3), dg = (a.G >> 3) - (b.G >> 3), db = (a.B >> 3) - (b.B >> 3);
        return dr * dr * 3 + dg * dg * 4 + db * db * 2;
    }

    /// <summary>
    /// Converts <paramref name="replacement"/> into an image the build accepts in place of <paramref name="original"/>:
    /// same size and bit depth, indexed. With <paramref name="newColors"/> the replacement's colors become the palette
    /// (written into the PNG and, if present, into x.pal); otherwise its colors are mapped onto the current palette.
    /// Transparent pixels become index 0.
    /// </summary>
    public static ReplacementResult Convert(PngImage original, GraphicInfo info, IReadOnlyList<Rgba32> currentPalette, PngImage replacement,
        bool newColors, bool allowNearest)
    {
        var errors = new List<string>();
        if (replacement.Width != original.Width || replacement.Height != original.Height)
            errors.Add($"The new image is {replacement.Width}×{replacement.Height}, it must be {original.Width}×{original.Height} like the original.");
        if (!original.IsIndexed) errors.Add("The original is not an indexed PNG; this kind of image cannot be replaced here yet.");
        if (newColors && info.Source == PaletteSource.Shared)
            errors.Add("This image uses a shared palette; its colors cannot be changed from here. Use \"map to current colors\".");
        if (errors.Count > 0) return new ReplacementResult([], null, new RgbaImage(1, 1), [], errors);

        int maxColors = original.BitDepth >= 8 ? 256 : 1 << original.BitDepth;
        int palSize = Math.Max(currentPalette.Count, Math.Min(maxColors, 16));
        var palette = currentPalette.Select(c => c with { A = 255 }).ToList();
        while (palette.Count < palSize) palette.Add(new Rgba32(0, 0, 0));
        var src = replacement.Rgba;
        var opaque = new List<Rgba32>();
        for (int y = 0; y < src.Height; y++)
            for (int x = 0; x < src.Width; x++)
                if (src[x, y].A >= 128) opaque.Add(GbaColor.Snap(src[x, y] with { A = 255 }));
        var colors = opaque.Distinct().ToList();
        var changes = new List<GraphicColorChange>();
        var map = new Dictionary<Rgba32, int>();
        if (newColors)
        {
            if (colors.Count > palSize - 1)
            {
                errors.Add($"The new image has {colors.Count} colors; at most {palSize - 1} plus transparency fit.");
                return new ReplacementResult([], null, new RgbaImage(1, 1), [], errors);
            }
            for (int i = 0; i < colors.Count; i++) { palette[i + 1] = colors[i]; map[colors[i]] = i + 1; }
            for (int i = colors.Count + 1; i < palSize; i++) palette[i] = new Rgba32(0, 0, 0);
        }
        else
        {
            foreach (var c in colors)
            {
                int exact = palette.FindIndex(1, p => GbaColor.Snap(p) == c);
                if (exact >= 1) { map[c] = exact; continue; }
                int best = 1;
                for (int i = 2; i < palette.Count; i++) if (Distance(palette[i], c) < Distance(palette[best], c)) best = i;
                map[c] = best;
                changes.Add(new GraphicColorChange(c, palette[best]));
            }
            if (changes.Count > 0 && !allowNearest)
                errors.Add($"{changes.Count} color(s) of the new image are not in the current palette. Allow replacing them with the closest palette color, or use the new image's colors.");
        }
        if (errors.Count > 0) return new ReplacementResult([], null, new RgbaImage(1, 1), changes, errors);

        var indices = new byte[src.Width * src.Height];
        var preview = new RgbaImage(src.Width, src.Height);
        for (int y = 0; y < src.Height; y++)
            for (int x = 0; x < src.Width; x++)
            {
                var p = src[x, y];
                int idx = p.A < 128 ? 0 : map[GbaColor.Snap(p with { A = 255 })];
                indices[y * src.Width + x] = (byte)idx;
                preview[x, y] = idx == 0 ? default : GbaColor.ToDisplayed(palette[idx]);
            }
        var png = Png.EncodeIndexed(src.Width, src.Height, indices, palette, original.BitDepth);
        byte[]? pal = newColors && info.Source == PaletteSource.OwnPalFile
            ? System.Text.Encoding.ASCII.GetBytes(JascPalette.Serialize(palette))
            : null;
        return new ReplacementResult(png, pal, preview, changes, []);
    }
}
