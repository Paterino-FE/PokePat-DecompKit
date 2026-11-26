using TilesetEditor.Core.Imaging;
using TilesetEditor.Core.Project;

namespace TilesetEditor.Core.Platinum;

/// <summary>One healthbox picture of res/graphics/battle/healthbox and what it must look like.</summary>
public sealed record HealthboxFile(string Name, string Title, int Width, int Height, string Role)
{
    public string Path => PlatinumHealthbox.Folder + "/" + Name;
}

/// <summary>The state of a healthbox picture: problems block the build or show wrong colours in-game.</summary>
public sealed record HealthboxCheck(HealthboxFile File, PngImage? Png, List<string> Problems, List<string> Notes)
{
    public bool Ok => Problems.Count == 0;
    /// <summary>The file only needs to be saved again as 4-bit (indices and colours stay).</summary>
    public bool CanFix4bpp { get; init; }
    /// <summary>The colours match the master palette but in a different order: re-indexing fixes it.</summary>
    public bool CanSyncPalette { get; init; }
}

/// <summary>
/// The battle healthboxes of pokeplatinum (res/graphics/battle/healthbox). All four pictures must be true 4-bit indexed PNGs
/// (an 8-bit PNG with 16 colours breaks the graphics in-game) and use the palette of player_singles.png in the same order;
/// indices 14 (white text) and 15 (background/mask) are used by the game at runtime.
/// </summary>
public static class PlatinumHealthbox
{
    public const string Folder = "res/graphics/battle/healthbox";
    public const string MasterName = "player_singles.png";

    public static readonly HealthboxFile[] Files =
    [
        new("player_singles.png", "Player – single battle", 128, 64, "Master palette: the other pictures use its colours in the same order."),
        new("player_doubles.png", "Player – double battle", 128, 64, "Same palette and order as player_singles.png."),
        new("enemy.png", "Opponent", 128, 64, "Same palette and order as player_singles.png."),
        new("healthbox_parts.png", "Parts (HP/Exp. bars, status, numbers …)", 624, 8,
            "78 tiles of 8×8 copied into the box while battling: HP green/yellow/red, Exp., status, gender, Lv, numbers. Same palette order as player_singles.png."),
    ];

    public static Rgba32[]? MasterPalette(string root)
    {
        var path = ProjectPaths.Combine(root, $"{Folder}/{MasterName}");
        if (!File.Exists(path)) return null;
        var png = Png.Load(path);
        return png.IsIndexed && png.Palette != null ? png.Palette.Take(16).Select(c => c with { A = 255 }).ToArray() : null;
    }

    public static List<HealthboxCheck> CheckAll(string root)
    {
        var master = MasterPalette(root);
        return Files.Select(f => Check(root, f, master)).ToList();
    }

    public static HealthboxCheck Check(string root, HealthboxFile f, Rgba32[]? master)
    {
        var problems = new List<string>();
        var notes = new List<string>();
        var path = ProjectPaths.Combine(root, f.Path);
        if (!File.Exists(path)) return new HealthboxCheck(f, null, [$"{f.Path} is missing."], notes);
        PngImage png;
        try { png = Png.Load(path); }
        catch (PngFormatException ex) { return new HealthboxCheck(f, null, [$"Not a readable PNG: {ex.Message}"], notes); }
        bool fix4 = false, sync = false;
        if (png.Width != f.Width || png.Height != f.Height) problems.Add($"{png.Width}×{png.Height} – must be {f.Width}×{f.Height}.");
        if (!png.IsIndexed || png.Indices == null || png.Palette == null)
        {
            problems.Add("Not an indexed PNG (it is RGB/RGBA). Use \"Replace…\" – PokePat maps its colours to the master palette.");
            return new HealthboxCheck(f, png, problems, notes);
        }
        int maxIndex = png.Indices.Length == 0 ? 0 : png.Indices.Max();
        if (maxIndex > 15) problems.Add($"Uses palette index {maxIndex}; only 0–15 are possible (16 colours).");
        if (png.BitDepth != 4)
        {
            problems.Add($"Saved as {png.BitDepth}-bit indexed PNG – the game needs a true 4-bit PNG (otherwise stripes and scrambled blocks in-game).");
            fix4 = maxIndex <= 15;
        }
        var pal = png.Palette.Take(16).Select(c => c with { A = 255 }).ToArray();
        if (master != null && f.Name != MasterName)
        {
            var used = png.Indices.Where(i => i < 16).Distinct().OrderBy(i => i).ToList();
            var wrong = used.Where(i => i >= pal.Length || i >= master.Length || !pal[i].SameRgb(master[i])).ToList();
            if (wrong.Count > 0)
            {
                problems.Add("Palette order differs from player_singles.png at index " + string.Join(", ", wrong.Take(8)) +
                             (wrong.Count > 8 ? " …" : "") + " – the game would show other colours there.");
                sync = used.All(i => i < pal.Length && master.Any(m => m.SameRgb(pal[i])));
                if (!sync) notes.Add("Some colours are not in the master palette at all; replace the picture with one that uses only the master colours.");
            }
        }
        if (f.Name == MasterName && pal.Length >= 16) notes.Add($"Index 14 = {pal[14].ToHex()} (text), 15 = {pal[15].ToHex()} (background/mask) – used by the game at runtime.");
        return new HealthboxCheck(f, png, problems, notes) { CanFix4bpp = fix4, CanSyncPalette = sync };
    }

    /// <summary>The same picture (indices and colours) saved as a true 4-bit indexed PNG.</summary>
    public static byte[] To4bpp(PngImage png)
    {
        if (!png.IsIndexed || png.Indices == null || png.Palette == null) throw new InvalidDataException("The picture is not indexed.");
        if (png.Indices.Any(i => i > 15)) throw new InvalidDataException("The picture uses palette indices above 15.");
        var pal = png.Palette.Take(16).ToList();
        while (pal.Count < 16) pal.Add(new Rgba32(0, 0, 0));
        return Png.EncodeIndexed(png.Width, png.Height, png.Indices, pal, 4);
    }

    /// <summary>
    /// The picture with the master palette: every colour is mapped to the master index with exactly the same RGB colour;
    /// fully transparent pixels become index 0. Throws with the list of colours that are not in the master palette.
    /// </summary>
    public static byte[] MapToMaster(RgbaImage picture, Rgba32[] master)
    {
        var lookup = new Dictionary<(byte, byte, byte), byte>();
        for (int i = 0; i < Math.Min(16, master.Length); i++) lookup.TryAdd((master[i].R, master[i].G, master[i].B), (byte)i);
        var idx = new byte[picture.Pixels.Length];
        var missing = new SortedSet<string>(StringComparer.Ordinal);
        bool partial = false;
        for (int i = 0; i < idx.Length; i++)
        {
            var c = picture.Pixels[i];
            if (c.A == 0) { idx[i] = 0; continue; }
            if (c.A != 255) partial = true;
            if (lookup.TryGetValue((c.R, c.G, c.B), out var m)) idx[i] = m;
            else missing.Add(c.ToHex());
        }
        if (partial) throw new InvalidDataException("The picture has semi-transparent pixels – the healthboxes only know fully visible or fully transparent pixels.");
        if (missing.Count > 0)
            throw new InvalidDataException($"{missing.Count} colour(s) are not in the palette of player_singles.png: " + string.Join(", ", missing.Take(12)) +
                                           (missing.Count > 12 ? " …" : "") + ". Use only the master colours (see the palette above).");
        var pal = master.Take(16).ToList();
        while (pal.Count < 16) pal.Add(new Rgba32(0, 0, 0));
        return Png.EncodeIndexed(picture.Width, picture.Height, idx, pal, 4);
    }

    /// <summary>A new master picture: an indexed PNG with at most 16 colours, saved as 4-bit with its own palette.</summary>
    public static byte[] NewMaster(PngImage png, out List<string> warnings)
    {
        warnings = [];
        if (!png.IsIndexed) throw new InvalidDataException("player_singles.png defines the palette: choose an indexed PNG (16 colours) for it.");
        var bytes = To4bpp(png);
        var pal = png.Palette!.Take(16).ToArray();
        warnings.Add("The other three pictures must use these colours in the same order – check them after replacing (\"Use master palette\").");
        if (pal.Length >= 16) warnings.Add($"Index 14 is now {pal[14].ToHex()} and 15 is {pal[15].ToHex()}; the game draws text with 14 and uses 15 as background/mask.");
        return bytes;
    }

    /// <summary>Renders an indexed check result for the preview (index 0 shown as is: the healthboxes have no transparency).</summary>
    public static RgbaImage? Preview(HealthboxCheck c) => c.Png?.Rgba;
}
