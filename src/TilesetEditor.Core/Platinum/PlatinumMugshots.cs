using System.Text;
using System.Text.RegularExpressions;
using TilesetEditor.Core.Imaging;
using TilesetEditor.Core.Project;

namespace TilesetEditor.Core.Platinum;

public sealed record PlatinumMugshot(string Constant, int Number, string PngRel);

/// <summary>A full-screen picture for ShowFullscreenPicture (256×192, 255 colours, a screen of its own).</summary>
public sealed record PlatinumFullscreen(string Constant, int Number, string PngRel);

/// <summary>A picture for ShowPicture (e.g. a treasure map): its size in tiles of 8×8 pixels.</summary>
public sealed record PlatinumPicture(string Constant, int Number, string PngRel, int TilesW, int TilesH)
{
    public int Width => TilesW * 8;
    public int Height => TilesH * 8;
}

/// <summary>
/// The pictures of the mod platinum-mugshots: include/constants/mugshots.h numbers the mugshots (MUGSHOT_X) and the bigger
/// pictures (PICTURE_X); include/data/field_pictures.h has the pictures' sizes. The folder res/graphics/map_popups/mugshots
/// holds the PNGs, their build list (meson.build) and the archive order: after the 9 map popups first every mugshot, then
/// every picture (graphics + palette each) – the game finds mugshot N at member 18 + 2·(N−1) and picture P after the last
/// mugshot. PokéPat writes the order and the build list completely, so they always match the constants.
/// </summary>
public static partial class PlatinumMugshots
{
    public const string ModId = "platinum-mugshots";
    public const string ConstantsFile = "include/constants/mugshots.h";
    public const string PicturesFile = "include/data/field_pictures.h";
    public const string Folder = "res/graphics/map_popups/mugshots";
    public const string MesonFile = Folder + "/meson.build";
    public const string OrderFile = Folder + "/map_popup.order";
    public const int Size = 64;
    /// <summary>Pictures share the tiles of the map-name drop-down with the mugshot: 256 − 64 tiles.</summary>
    public const int MaxPictureTiles = 192;
    public const int MaxPictureWidth = 256, MaxPictureHeight = 152;

    [GeneratedRegex(@"^#define[ \t]+MUGSHOT_(\w+)[ \t]+(\d+)[ \t]*$", RegexOptions.Multiline)]
    private static partial Regex DefineLine();

    [GeneratedRegex(@"^#define[ \t]+PICTURE_(\w+)[ \t]+(\d+)[ \t]*$", RegexOptions.Multiline)]
    private static partial Regex PictureLine();

    [GeneratedRegex(@"^#define[ \t]+FULLSCREEN_(\w+)[ \t]+(\d+)[ \t]*$", RegexOptions.Multiline)]
    private static partial Regex FullscreenLine();

    public const int FullscreenWidth = 256, FullscreenHeight = 192;

    [GeneratedRegex(@"\{\s*(\d+)\s*,\s*(\d+)\s*\}\s*,\s*//\s*(PICTURE_\w+)")]
    private static partial Regex SizeLine();

    private static bool IsMugshotName(string key) => key is not ("NONE" or "COUNT") && !key.StartsWith("POS_", StringComparison.Ordinal);

    public static bool IsInstalled(string root) => File.Exists(ProjectPaths.Combine(root, ConstantsFile)) && File.Exists(ProjectPaths.Combine(root, MesonFile));

    /// <summary>Pictures and the left/right position came with version 1.1 of the mod (switch it off and on to update).</summary>
    public static bool SupportsPictures(string root) => IsInstalled(root) && File.Exists(ProjectPaths.Combine(root, PicturesFile));

    /// <summary>Full-screen pictures came with version 1.2 of the mod.</summary>
    public static bool SupportsFullscreen(string root) =>
        IsInstalled(root) && File.ReadAllText(ProjectPaths.Combine(root, ConstantsFile)).Contains("FULLSCREEN_COUNT", StringComparison.Ordinal);

    public static List<PlatinumFullscreen> ListFullscreen(string root)
    {
        var path = ProjectPaths.Combine(root, ConstantsFile);
        if (!File.Exists(path)) return [];
        return FullscreenLine().Matches(File.ReadAllText(path))
            .Where(m => m.Groups[1].Value is not ("NONE" or "COUNT"))
            .Select(m => new PlatinumFullscreen("FULLSCREEN_" + m.Groups[1].Value, int.Parse(m.Groups[2].Value), $"{Folder}/fullscreen_{m.Groups[1].Value.ToLowerInvariant()}.png"))
            .OrderBy(m => m.Number).ToList();
    }

    /// <summary>
    /// Adds a full-screen picture: scaled down to fit 256×192 if bigger (proportions kept) and centred on black, reduced to
    /// 255 colours (index 0 is the black backdrop), stored as an 8-bit PNG.
    /// </summary>
    public static (List<PlatinumFileChange> Files, string Constant) PlanAddFullscreen(string root, string name, RgbaImage picture)
    {
        if (!SupportsFullscreen(root)) throw new InvalidDataException("Full-screen pictures need version 1.2 of the mod \"Dialog mugshots\" – update it in Mods.");
        var key = Key(name, "FULLSCREEN_");
        var existing = ListFullscreen(root);
        if (existing.Any(p => p.Constant == "FULLSCREEN_" + key)) throw new InvalidDataException($"FULLSCREEN_{key} already exists.");
        int number = existing.Count == 0 ? 1 : existing.Max(p => p.Number) + 1;
        var file = $"fullscreen_{key.ToLowerInvariant()}";
        var original = $"{picture.Width}×{picture.Height}";
        bool scaled = picture.Width > FullscreenWidth || picture.Height > FullscreenHeight;
        var canvas = Fit(picture, FullscreenWidth, FullscreenHeight, pad: true);
        var utf8 = new UTF8Encoding(false);

        var constants = File.ReadAllText(ProjectPaths.Combine(root, ConstantsFile));
        var last = FullscreenLine().Matches(constants).Where(m => m.Groups[1].Value != "COUNT").Last();
        int at = last.Index + last.Length;
        constants = constants[..at] + $"\n#define FULLSCREEN_{key} {number}" + constants[at..];
        constants = Regex.Replace(constants, @"(#define\s+FULLSCREEN_COUNT\s+)\d+", m => m.Groups[1].Value + (number + 1));

        var mugshots = List(root).Select(m => Path.GetFileNameWithoutExtension(m.PngRel)).ToList();
        var pictures = ListPictures(root).Select(p => Path.GetFileNameWithoutExtension(p.PngRel)).ToList();
        var fullscreens = existing.Select(p => Path.GetFileNameWithoutExtension(p.PngRel)).Append(file).ToList();
        return (
        [
            new($"{Folder}/{file}.png", EncodeFullscreen(canvas), $"new full-screen picture 256×192" + (scaled ? $", scaled from {original}" : "") + " (255 colours)"),
            new(ConstantsFile, utf8.GetBytes(constants), $"FULLSCREEN_{key} = {number}"),
            new(MesonFile, utf8.GetBytes(Meson(root, mugshots, pictures, fullscreens)), $"{file}.png in the build"),
            new(OrderFile, utf8.GetBytes(Order(root, mugshots, pictures, fullscreens)), $"{file} in arc/area_win_gra.narc after the pictures"),
        ], "FULLSCREEN_" + key);
    }

    /// <summary>256×192, 8-bit: index 0 is black (the backdrop, also for transparent pixels), up to 255 colours after it.</summary>
    public static byte[] EncodeFullscreen(RgbaImage picture)
    {
        var img = new RgbaImage(FullscreenWidth, FullscreenHeight);
        for (int y = 0; y < FullscreenHeight; y++)
            for (int x = 0; x < FullscreenWidth; x++)
            {
                var c = x < picture.Width && y < picture.Height ? picture[x, y] : Rgba32.Transparent;
                img[x, y] = c.A < 128 ? Rgba32.Transparent : Gba.GbaColor.Snap(c with { A = 255 });
            }
        var reduced = ColorQuantizer.Reduce(img, 255);
        var colors = reduced.Pixels.Where(p => p.A != 0).Select(p => p with { A = 255 }).Distinct().Take(255).ToList();
        var index = new Dictionary<Rgba32, int>();
        for (int i = 0; i < colors.Count; i++) index[colors[i]] = i + 1;
        var palette = new Rgba32[256];
        palette[0] = new Rgba32(0, 0, 0);
        for (int i = 1; i < 256; i++) palette[i] = i - 1 < colors.Count ? colors[i - 1] : new Rgba32(0, 0, 0);
        var idx = new byte[FullscreenWidth * FullscreenHeight];
        for (int i = 0; i < idx.Length; i++)
        {
            var c = reduced.Pixels[i];
            if (c.A != 0) idx[i] = (byte)index.GetValueOrDefault(c with { A = 255 }, 1);
        }
        return Png.EncodeIndexed(FullscreenWidth, FullscreenHeight, idx, palette, 8);
    }

    public static List<PlatinumMugshot> List(string root)
    {
        var path = ProjectPaths.Combine(root, ConstantsFile);
        if (!File.Exists(path)) return [];
        return DefineLine().Matches(File.ReadAllText(path))
            .Where(m => IsMugshotName(m.Groups[1].Value))
            .Select(m => new PlatinumMugshot("MUGSHOT_" + m.Groups[1].Value, int.Parse(m.Groups[2].Value), $"{Folder}/mugshot_{m.Groups[1].Value.ToLowerInvariant()}.png"))
            .OrderBy(m => m.Number).ToList();
    }

    public static List<PlatinumPicture> ListPictures(string root)
    {
        var path = ProjectPaths.Combine(root, ConstantsFile);
        var sizesPath = ProjectPaths.Combine(root, PicturesFile);
        if (!File.Exists(path) || !File.Exists(sizesPath)) return [];
        var sizes = SizeLine().Matches(File.ReadAllText(sizesPath)).ToDictionary(m => m.Groups[3].Value, m => (int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value)), StringComparer.Ordinal);
        return PictureLine().Matches(File.ReadAllText(path))
            .Where(m => m.Groups[1].Value is not ("NONE" or "COUNT"))
            .Select(m =>
            {
                var c = "PICTURE_" + m.Groups[1].Value;
                var (w, h) = sizes.GetValueOrDefault(c);
                return new PlatinumPicture(c, int.Parse(m.Groups[2].Value), $"{Folder}/picture_{m.Groups[1].Value.ToLowerInvariant()}.png", w, h);
            })
            .OrderBy(p => p.Number).ToList();
    }

    /// <summary>A 64×64 picture from a trainer class's 80×80 battle picture: the upper middle (head and shoulders).</summary>
    public static RgbaImage FromTrainerPicture(RgbaImage front)
    {
        var img = new RgbaImage(Size, Size);
        int ox = Math.Max(0, (front.Width - Size) / 2);
        img.Blit(front, ox, 0, 0, 0, Math.Min(Size, front.Width), Math.Min(Size, front.Height));
        return img;
    }

    /// <summary>The mugshot as the game shows it on the left: mirrored, so the person looks towards the text.</summary>
    public static RgbaImage Mirror(RgbaImage img)
    {
        var m = new RgbaImage(img.Width, img.Height);
        for (int y = 0; y < img.Height; y++)
            for (int x = 0; x < img.Width; x++)
                m[img.Width - 1 - x, y] = img[x, y];
        return m;
    }

    /// <summary>Whether a picture fits as it is (size in whole tiles within the limits).</summary>
    public static bool PictureFits(int width, int height)
    {
        int tw = (width + 7) / 8, th = (height + 7) / 8;
        return tw * 8 <= MaxPictureWidth && th * 8 <= MaxPictureHeight && tw * th <= MaxPictureTiles;
    }

    /// <summary>The largest size with the same proportions that fits as a picture (whole tiles, ≤ 192 tiles, ≤ 256×152).</summary>
    public static (int Width, int Height) FitPicture(int width, int height)
    {
        double scale = Math.Min(1.0, Math.Min((double)MaxPictureWidth / width, (double)MaxPictureHeight / height));
        scale = Math.Min(scale, Math.Sqrt(MaxPictureTiles * 64.0 / (width * (double)height)));
        while (true)
        {
            int w = Math.Max(8, (int)Math.Floor(width * scale)), h = Math.Max(8, (int)Math.Floor(height * scale));
            if (PictureFits(w, h)) return (w, h);
            scale *= 0.98;
        }
    }

    /// <summary>
    /// A bigger picture made smaller for the game: scaled (average of the covered pixels, so details stay readable) to fit
    /// <paramref name="maxW"/>×<paramref name="maxH"/> with its proportions; with <paramref name="pad"/> centred on a
    /// transparent canvas of exactly that size (mugshots), else just scaled.
    /// </summary>
    public static RgbaImage Fit(RgbaImage src, int maxW, int maxH, bool pad)
    {
        double scale = Math.Min(1.0, Math.Min((double)maxW / src.Width, (double)maxH / src.Height));
        int w = Math.Max(1, (int)Math.Round(src.Width * scale)), h = Math.Max(1, (int)Math.Round(src.Height * scale));
        var scaled = new RgbaImage(w, h);
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int x0 = (int)(x / scale), x1 = Math.Max(x0 + 1, (int)((x + 1) / scale)), y0 = (int)(y / scale), y1 = Math.Max(y0 + 1, (int)((y + 1) / scale));
                long r = 0, g = 0, b = 0, a = 0, n = 0;
                for (int yy = y0; yy < Math.Min(y1, src.Height); yy++)
                    for (int xx = x0; xx < Math.Min(x1, src.Width); xx++)
                    {
                        var c = src[xx, yy];
                        n++;
                        a += c.A;
                        r += c.R * c.A; g += c.G * c.A; b += c.B * c.A;
                    }
                scaled[x, y] = a == 0 ? Rgba32.Transparent : new Rgba32((byte)(r / a), (byte)(g / a), (byte)(b / a), (byte)(a / Math.Max(1, n)));
            }
        if (!pad) return scaled;
        var canvas = new RgbaImage(maxW, maxH);
        canvas.Blit(scaled, 0, 0, (maxW - w) / 2, (maxH - h) / 2, w, h);
        return canvas;
    }

    private static string Key(string name, string prefix)
    {
        var key = Regex.Replace(name.Trim().ToUpperInvariant(), @"[^A-Z0-9]+", "_").Trim('_');
        if (key.StartsWith(prefix, StringComparison.Ordinal)) key = key[prefix.Length..];
        if (key.Length == 0 || char.IsDigit(key[0])) throw new InvalidDataException("The name must start with a letter (letters, digits and _).");
        if (key is "NONE" or "COUNT" || key.StartsWith("POS_", StringComparison.Ordinal)) throw new InvalidDataException($"{prefix}{key} is reserved.");
        return key;
    }

    /// <summary>Adds a picture as the next number; returns the files to write.</summary>
    public static (List<PlatinumFileChange> Files, string Constant) PlanAdd(string root, string name, RgbaImage picture)
    {
        if (!IsInstalled(root)) throw new InvalidDataException("Switch on the mod \"Dialog mugshots\" first.");
        var key = Key(name, "MUGSHOT_");
        // Any size is fine: other sizes are scaled (proportions kept) and centred on a transparent 64×64 picture.
        var original = $"{picture.Width}×{picture.Height}";
        bool scaled = picture.Width != Size || picture.Height != Size;
        if (scaled) picture = Fit(picture, Size, Size, pad: true);
        var existing = List(root);
        if (existing.Any(m => m.Constant == "MUGSHOT_" + key)) throw new InvalidDataException($"MUGSHOT_{key} already exists.");
        int number = existing.Count == 0 ? 1 : existing.Max(m => m.Number) + 1;
        var file = $"mugshot_{key.ToLowerInvariant()}";
        var utf8 = new UTF8Encoding(false);

        var constants = File.ReadAllText(ProjectPaths.Combine(root, ConstantsFile));
        if (!Regex.IsMatch(constants, @"\n#define\s+MUGSHOT_COUNT\s+\d+")) throw new InvalidDataException($"{ConstantsFile} has no MUGSHOT_COUNT.");
        var lastDefine = DefineLine().Matches(constants).Where(m => IsMugshotName(m.Groups[1].Value) || m.Groups[1].Value == "NONE").Last();
        int insertAt = lastDefine.Index + lastDefine.Length;
        constants = constants[..insertAt] + $"\n#define MUGSHOT_{key} {number}" + constants[insertAt..];
        constants = Regex.Replace(constants, @"(#define\s+MUGSHOT_COUNT\s+)\d+", m => m.Groups[1].Value + (number + 1));

        var mugshots = existing.Select(m => Path.GetFileNameWithoutExtension(m.PngRel)).Append(file).ToList();
        var pictures = ListPictures(root).Select(p => Path.GetFileNameWithoutExtension(p.PngRel)).ToList();
        return (
        [
            new($"{Folder}/{file}.png", Encode(picture, Size, Size, true), scaled ? $"new 64×64 picture, scaled from {original} (16 colours)" : "new 64×64 picture (16 colours)"),
            new(ConstantsFile, utf8.GetBytes(constants), $"MUGSHOT_{key} = {number}"),
            new(MesonFile, utf8.GetBytes(Meson(root, mugshots, pictures)), $"{file}.png in the build"),
            new(OrderFile, utf8.GetBytes(Order(root, mugshots, pictures)), $"{file} in arc/area_win_gra.narc (member {18 + 2 * (number - 1)}); the pictures move one place up"),
        ], "MUGSHOT_" + key);
    }

    /// <summary>
    /// Adds a bigger picture for ShowPicture. Its size is rounded up to whole tiles (transparent border); at most
    /// <see cref="MaxPictureTiles"/> tiles (e.g. 128×96), <see cref="MaxPictureWidth"/>×<see cref="MaxPictureHeight"/> pixels.
    /// </summary>
    public static (List<PlatinumFileChange> Files, string Constant) PlanAddPicture(string root, string name, RgbaImage picture)
    {
        if (!SupportsPictures(root)) throw new InvalidDataException("Pictures need version 1.1 of the mod \"Dialog mugshots\" – switch it off and on again in Mods.");
        var key = Key(name, "PICTURE_");
        // Too big pictures are scaled to the largest size that fits (proportions kept).
        var original = $"{picture.Width}×{picture.Height}";
        bool scaled = !PictureFits(picture.Width, picture.Height);
        if (scaled)
        {
            var (fw, fh) = FitPicture(picture.Width, picture.Height);
            picture = Fit(picture, fw, fh, pad: false);
        }
        int tw = (picture.Width + 7) / 8, th = (picture.Height + 7) / 8;
        var existing = ListPictures(root);
        if (existing.Any(p => p.Constant == "PICTURE_" + key)) throw new InvalidDataException($"PICTURE_{key} already exists.");
        int number = existing.Count == 0 ? 1 : existing.Max(p => p.Number) + 1;
        var file = $"picture_{key.ToLowerInvariant()}";
        var utf8 = new UTF8Encoding(false);

        var constants = File.ReadAllText(ProjectPaths.Combine(root, ConstantsFile));
        if (!Regex.IsMatch(constants, @"\n#define\s+PICTURE_COUNT\s+\d+")) throw new InvalidDataException($"{ConstantsFile} has no PICTURE_COUNT.");
        var last = PictureLine().Matches(constants).Where(m => m.Groups[1].Value != "COUNT").Last();
        int at = last.Index + last.Length;
        constants = constants[..at] + $"\n#define PICTURE_{key} {number}" + constants[at..];
        constants = Regex.Replace(constants, @"(#define\s+PICTURE_COUNT\s+)\d+", m => m.Groups[1].Value + (number + 1));

        var sizes = File.ReadAllText(ProjectPaths.Combine(root, PicturesFile));
        int end = sizes.IndexOf("};", StringComparison.Ordinal);
        if (end < 0) throw new InvalidDataException($"{PicturesFile} has no size table.");
        sizes = sizes[..end] + $"    {{ {tw}, {th} }}, // PICTURE_{key}\n" + sizes[end..];

        var padded = new RgbaImage(tw * 8, th * 8);
        padded.Blit(picture, 0, 0, (tw * 8 - picture.Width) / 2, (th * 8 - picture.Height) / 2, picture.Width, picture.Height);
        var mugshots = List(root).Select(m => Path.GetFileNameWithoutExtension(m.PngRel)).ToList();
        var pictures = existing.Select(p => Path.GetFileNameWithoutExtension(p.PngRel)).Append(file).ToList();
        return (
        [
            new($"{Folder}/{file}.png", Encode(padded, padded.Width, padded.Height, false), $"new picture {padded.Width}×{padded.Height}" + (scaled ? $", scaled from {original}" : "") + " (16 colours)"),
            new(ConstantsFile, utf8.GetBytes(constants), $"PICTURE_{key} = {number}"),
            new(PicturesFile, utf8.GetBytes(sizes), $"size of PICTURE_{key}: {tw}×{th} tiles"),
            new(MesonFile, utf8.GetBytes(Meson(root, mugshots, pictures)), $"{file}.png in the build"),
            new(OrderFile, utf8.GetBytes(Order(root, mugshots, pictures)), $"{file} in arc/area_win_gra.narc after the mugshots"),
        ], "PICTURE_" + key);
    }

    /// <summary>The build list: every mugshot, then every picture; since 1.2 a second list with the full-screen pictures.</summary>
    private static string Meson(string root, IEnumerable<string> mugshots, IEnumerable<string> pictures, IEnumerable<string>? fullscreens = null)
    {
        var old = File.ReadAllText(ProjectPaths.Combine(root, MesonFile));
        int open = old.IndexOf("files(", StringComparison.Ordinal);
        var head = open >= 0 ? old[..(open + 6)] : "mugshot_files = files(";
        var sb = new StringBuilder(head).Append('\n');
        foreach (var f in mugshots.Concat(pictures)) sb.Append($"    '{f}.png',\n");
        sb.Append(")\n");
        if (old.Contains("fullscreen_files", StringComparison.Ordinal))
        {
            fullscreens ??= ListFullscreen(root).Select(p => Path.GetFileNameWithoutExtension(p.PngRel));
            sb.Append("\n# Full-screen pictures (ShowFullscreenPicture), in the order of the FULLSCREEN_ constants. Managed by PokePat.\nfullscreen_files = files(\n");
            foreach (var f in fullscreens) sb.Append($"    '{f}.png',\n");
            sb.Append(")\n");
        }
        return sb.ToString();
    }

    /// <summary>The archive order: the map popups as they are, then every mugshot, then every picture.</summary>
    private static string Order(string root, IEnumerable<string> mugshots, IEnumerable<string> pictures, IEnumerable<string>? fullscreens = null)
    {
        var all = File.ReadAllLines(ProjectPaths.Combine(root, OrderFile));
        if (all.Any(l => l.StartsWith("fullscreen_", StringComparison.Ordinal)) || SupportsFullscreen(root))
            fullscreens ??= ListFullscreen(root).Select(p => Path.GetFileNameWithoutExtension(p.PngRel));
        var lines = all.Where(l => l.Trim().Length > 0 && !l.StartsWith("mugshot_", StringComparison.Ordinal) && !l.StartsWith("picture_", StringComparison.Ordinal)
                                   && !l.StartsWith("fullscreen_", StringComparison.Ordinal)).ToList();
        foreach (var f in mugshots.Concat(pictures).Concat(fullscreens ?? [])) { lines.Add(f + ".NCGR"); lines.Add(f + ".NCLR"); }
        return string.Join("\n", lines) + "\n";
    }

    /// <summary>The picture with up to 15 colours; transparent (or the top-left colour) becomes index 0, which the game does not draw.</summary>
    public static byte[] Encode(RgbaImage picture) => Encode(picture, Size, Size, true);

    /// <param name="topLeftIsBackground">Mugshots: the top-left colour is the background (transparent). Pictures: only
    /// transparent pixels are – a map's paper colour in the corner must stay.</param>
    public static byte[] Encode(RgbaImage picture, int width, int height, bool topLeftIsBackground)
    {
        var bg = topLeftIsBackground ? picture[0, 0] : Rgba32.Transparent;
        var img = new RgbaImage(width, height);
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
            {
                var c = x < picture.Width && y < picture.Height ? picture[x, y] : Rgba32.Transparent;
                img[x, y] = c.A < 128 || (bg.A == 255 && c.SameRgb(bg)) ? Rgba32.Transparent : c with { A = 255 };
            }
        var reduced = ColorQuantizer.Reduce(img, 15);
        var colors = reduced.Pixels.Where(p => p.A != 0).Select(p => p with { A = 255 }).Distinct().Take(15).ToList();
        var palette = new Rgba32[16];
        palette[0] = new Rgba32(255, 255, 255);
        for (int i = 0; i < 15; i++) palette[i + 1] = i < colors.Count ? colors[i] : new Rgba32(0, 0, 0);
        var idx = new byte[width * height];
        for (int i = 0; i < idx.Length; i++)
        {
            var c = reduced.Pixels[i];
            if (c.A == 0) continue;
            int k = colors.FindIndex(x => x.SameRgb(c));
            idx[i] = (byte)(k < 0 ? 1 : k + 1);
        }
        return Png.EncodeIndexed(width, height, idx, palette, 8);
    }
}
