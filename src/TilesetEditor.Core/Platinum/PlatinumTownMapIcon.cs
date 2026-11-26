using System.Text;
using TilesetEditor.Core.Gba;
using TilesetEditor.Core.Imaging;
using TilesetEditor.Core.Project;

namespace TilesetEditor.Core.Platinum;

/// <summary>
/// The player's head on the Town Map (res/graphics/town_map/player_icons.png): 16×16 for the boy (rows 0–15) and the girl
/// (rows 16–31), stored as a 4-bit grayscale PNG whose grey values are the colour numbers. The boy's colours are
/// player_male.pal; the girl shares cursor_player_female.pal with the map cursor, so the cursor's colours stay.
/// </summary>
public static class PlatinumTownMapIcon
{
    public const string Folder = "res/graphics/town_map";
    public const string IconsFile = Folder + "/player_icons.png";
    public const string CursorFile = Folder + "/cursor.png";
    public const int Size = 16;

    public static string PaletteFile(bool female) => Folder + (female ? "/cursor_player_female.pal" : "/player_male.pal");

    public static bool Exists(string root) =>
        File.Exists(ProjectPaths.Combine(root, IconsFile)) && File.Exists(ProjectPaths.Combine(root, PaletteFile(false))) && File.Exists(ProjectPaths.Combine(root, PaletteFile(true)));

    /// <summary>Colour numbers of a grayscale-coded picture (grey 0, 17, 34 … 255 → 0 … 15), or the indices of an indexed one.</summary>
    private static byte[] Numbers(PngImage png) =>
        png.Indices ?? png.Rgba.Pixels.Select(c => (byte)Math.Clamp((c.R + 8) / 17, 0, 15)).ToArray();

    /// <summary>The head as the game shows it (colour 0 transparent).</summary>
    public static RgbaImage Icon(string root, bool female)
    {
        var png = Png.Load(ProjectPaths.Combine(root, IconsFile));
        var nums = Numbers(png);
        var pal = JascPalette.Load(ProjectPaths.Combine(root, PaletteFile(female)));
        var img = new RgbaImage(Size, Size);
        int top = female ? Size : 0;
        for (int y = 0; y < Size; y++)
            for (int x = 0; x < Size; x++)
            {
                int n = nums[(top + y) * png.Width + x];
                img[x, y] = n == 0 || n >= pal.Length ? Rgba32.Transparent : pal[n] with { A = 255 };
            }
        return img;
    }

    private static string SameLineEnds(string text, string like) =>
        like.Contains("\r\n") ? text.Replace("\r\n", "\n").Replace("\n", "\r\n") : text.Replace("\r\n", "\n");

    /// <summary>The colour numbers the girl's head must not change (used by the map cursor).</summary>
    public static HashSet<int> Reserved(string root, bool female)
    {
        if (!female || !File.Exists(ProjectPaths.Combine(root, CursorFile))) return [];
        return Numbers(Png.Load(ProjectPaths.Combine(root, CursorFile))).Where(n => n != 0).Select(n => (int)n).ToHashSet();
    }

    /// <summary>
    /// A new head: other sizes are scaled to 16×16 (proportions kept); transparent pixels (or colour 0 of an indexed PNG,
    /// or the top-left colour of a picture without transparency) stay see-through. Its colours go into the free places of
    /// the palette; with more colours than places it is reduced.
    /// </summary>
    public static List<PlatinumFileChange> PlanReplace(string root, bool female, PngImage picture)
    {
        if (!Exists(root)) throw new InvalidDataException($"{IconsFile} or its palettes are missing.");
        RgbaImage img = IndexedPicture.WithTransparentIndex0(picture);
        if (picture.Indices == null && img.Pixels.All(c => c.A == 255))
        {
            var bg = img[0, 0];
            img = img.Clone();
            for (int i = 0; i < img.Pixels.Length; i++) if (img.Pixels[i].SameRgb(bg)) img.Pixels[i] = Rgba32.Transparent;
        }
        var original = $"{img.Width}×{img.Height}";
        bool scaled = img.Width != Size || img.Height != Size;
        if (scaled) img = PlatinumMugshots.Fit(img, Size, Size, pad: true);
        for (int i = 0; i < img.Pixels.Length; i++)
            img.Pixels[i] = img.Pixels[i].A < 128 ? Rgba32.Transparent : GbaColor.Snap(img.Pixels[i] with { A = 255 });

        var reserved = Reserved(root, female);
        var free = Enumerable.Range(1, 15).Where(n => !reserved.Contains(n)).ToList();
        var colors = img.Pixels.Where(c => c.A != 0).Distinct().ToList();
        bool reduced = colors.Count > free.Count;
        if (reduced)
        {
            img = ColorQuantizer.Reduce(img, free.Count);
            colors = img.Pixels.Where(c => c.A != 0).Select(c => c with { A = 255 }).Distinct().Take(free.Count).ToList();
        }
        var palPath = ProjectPaths.Combine(root, PaletteFile(female));
        var palette = JascPalette.Load(palPath).Select(c => c with { A = 255 }).ToArray();
        var number = new Dictionary<Rgba32, int>();
        for (int i = 0; i < colors.Count; i++) { palette[free[i]] = colors[i]; number[colors[i]] = free[i]; }

        var iconsPath = ProjectPaths.Combine(root, IconsFile);
        var sheet = Png.Load(iconsPath);
        var nums = Numbers(sheet).ToArray();
        int top = female ? Size : 0;
        for (int y = 0; y < Size; y++)
            for (int x = 0; x < Size; x++)
            {
                var c = img[x, y];
                nums[(top + y) * sheet.Width + x] = (byte)(c.A == 0 ? 0 : number.GetValueOrDefault(c with { A = 255 }, free[0]));
            }
        var bytes = sheet.Indices != null
            ? Png.EncodeIndexed(sheet.Width, sheet.Height, nums, sheet.Palette!, sheet.BitDepth is 4 or 8 ? sheet.BitDepth : 8)
            : Png.EncodeGray(sheet.Width, sheet.Height, nums, 4);
        var who = female ? "girl" : "boy";
        return
        [
            new(IconsFile, bytes, $"town map head of the {who} (16×16" + (scaled ? $", scaled from {original}" : "") + ")"),
            new(PaletteFile(female), Encoding.ASCII.GetBytes(SameLineEnds(JascPalette.Serialize(palette), File.ReadAllText(palPath))),
                $"{colors.Count} colour(s) of the head" + (reserved.Count > 0 ? $"; the cursor's colours ({string.Join(", ", reserved.Order())}) stay" : "") + (reduced ? " – reduced to fit" : "")),
        ];
    }
}
