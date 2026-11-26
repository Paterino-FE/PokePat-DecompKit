using System.Text;
using System.Text.RegularExpressions;
using TilesetEditor.Core.Gba;
using TilesetEditor.Core.Imaging;
using TilesetEditor.Core.Project;

namespace TilesetEditor.Export.Mods;

public sealed record Portrait(string Constant, int Id, string? PngRel);

/// <summary>A picture of the mod (128×96, stored as a 64×192 sheet of six 64×32 pieces).</summary>
public sealed record DialogPicture(string Constant, int Id, string? PngRel);

/// <summary>
/// Content of the "Dialog portraits" mod: the list of portraits and adding new ones (64×64, 15 colors + transparency) from any
/// PNG – smaller pictures are placed bottom-centered, larger ones are refused.
/// </summary>
public static partial class DialogPortraits
{
    public const string ModId = "dialog-portraits";
    public const string ConstantsFile = "include/constants/dialog_portraits.h";
    public const string DataFile = "src/data/dialog_portraits.h";
    public const string Folder = "graphics/portraits";
    public const int Size = 64;
    private const string GraphicsMarker = "// PokePat: portrait graphics";
    private const string PictureMarker = "// PokePat: picture graphics";
    public const int PictureWidth = 128, PictureHeight = 96;
    private const int PieceWidth = 64, PieceHeight = 32, Pieces = 6;

    [GeneratedRegex(@"^#define[ \t]+(PICTURE_\w+)[ \t]+(\d+)", RegexOptions.Multiline)]
    private static partial Regex PictureDefine();

    [GeneratedRegex(@"^#define[ \t]+(PORTRAIT_\w+)[ \t]+(\d+)", RegexOptions.Multiline)]
    private static partial Regex Define();

    public static bool IsInstalled(string root) => File.Exists(ProjectPaths.Combine(root, ConstantsFile)) && File.Exists(ProjectPaths.Combine(root, DataFile));

    public static List<Portrait> List(string root)
    {
        if (!IsInstalled(root)) return [];
        var data = File.ReadAllText(ProjectPaths.Combine(root, DataFile));
        return Define().Matches(File.ReadAllText(ProjectPaths.Combine(root, ConstantsFile)))
            .Where(m => !m.Groups[1].Value.StartsWith("PORTRAIT_POS_", StringComparison.Ordinal) && m.Groups[1].Value != "PORTRAIT_NONE")
            .Select(m => new Portrait(m.Groups[1].Value, int.Parse(m.Groups[2].Value),
                Regex.Match(data, @"\[" + m.Groups[1].Value + @"\]\s*=\s*\{\s*(\w+)") is { Success: true } e
                && Regex.Match(data, Regex.Escape(e.Groups[1].Value) + @"\[\]\s*=\s*INCBIN_U32\(""([^""]+)\.4bpp""") is { Success: true } g ? g.Groups[1].Value + ".png" : null))
            .ToList();
    }

    /// <summary>A stored portrait in its real colours (index 0 transparent), from its .pal when there is one.</summary>
    public static RgbaImage WithColors(PngImage png, Rgba32[]? palette)
    {
        var img = png.Rgba.Clone();
        if (png.Indices == null) return img;
        var pal = palette ?? png.Palette;
        for (int i = 0; i < img.Pixels.Length; i++)
        {
            int idx = png.Indices[i] & 0xF;
            img.Pixels[i] = idx == 0 ? Rgba32.Transparent : pal != null && idx < pal.Length ? pal[idx] with { A = 255 } : img.Pixels[i];
        }
        return img;
    }

    /// <summary>Whether the installed mod knows pictures (version 1.1 and newer).</summary>
    public static bool SupportsPictures(string root) =>
        IsInstalled(root) && File.ReadAllText(ProjectPaths.Combine(root, DataFile)).Contains(PictureMarker, StringComparison.Ordinal);

    public static List<DialogPicture> ListPictures(string root)
    {
        if (!SupportsPictures(root)) return [];
        var data = File.ReadAllText(ProjectPaths.Combine(root, DataFile));
        return PictureDefine().Matches(File.ReadAllText(ProjectPaths.Combine(root, ConstantsFile)))
            .Where(m => m.Groups[1].Value != "PICTURE_NONE")
            .Select(m => new DialogPicture(m.Groups[1].Value, int.Parse(m.Groups[2].Value),
                Regex.Match(data, @"\[" + m.Groups[1].Value + @"\]\s*=\s*\{\s*(\w+)") is { Success: true } e
                && Regex.Match(data, Regex.Escape(e.Groups[1].Value) + @"\[\]\s*=\s*INCBIN_U32\(""([^""]+)\.4bpp""") is { Success: true } g ? g.Groups[1].Value + ".png" : null))
            .ToList();
    }

    /// <summary>
    /// A picture as the game stores it: scaled down to fit 128×96 if bigger (proportions kept), centred on a transparent
    /// 128×96 canvas, 15 colours + transparency, cut into six 64×32 pieces placed one below the other (64×192).
    /// </summary>
    public static (byte[] Png, Rgba32[] Palette) ConvertPicture(RgbaImage picture)
    {
        var canvas = Core.Platinum.PlatinumMugshots.Fit(picture, PictureWidth, PictureHeight, pad: true);
        var full = Png.Decode(Core.Platinum.PlatinumMugshots.Encode(canvas, PictureWidth, PictureHeight, false));
        var pal = full.Palette!.Take(16).Select(GbaColor.Snap).Concat(Enumerable.Repeat(new Rgba32(0, 0, 0), 16)).Take(16).ToArray();
        var strip = new byte[PieceWidth * PieceHeight * Pieces];
        for (int piece = 0; piece < Pieces; piece++)
        {
            int sx = piece % 2 * PieceWidth, sy = piece / 2 * PieceHeight;
            for (int y = 0; y < PieceHeight; y++)
                for (int x = 0; x < PieceWidth; x++)
                    strip[(piece * PieceHeight + y) * PieceWidth + x] = (byte)(full.Indices![(sy + y) * PictureWidth + sx + x] & 0xF);
        }
        return (Png.EncodeIndexed(PieceWidth, PieceHeight * Pieces, strip, pal, 4), pal);
    }

    /// <summary>The picture as the player sees it, from the stored 64×192 sheet (index 0 transparent).</summary>
    public static RgbaImage PictureForDisplay(PngImage sheet, Rgba32[]? palette = null)
    {
        var img = new RgbaImage(PictureWidth, PictureHeight);
        var pal = palette ?? sheet.Palette;
        for (int piece = 0; piece < Pieces; piece++)
            for (int y = 0; y < PieceHeight; y++)
                for (int x = 0; x < PieceWidth; x++)
                {
                    int i = (piece * PieceHeight + y) * sheet.Width + x;
                    if (i >= sheet.Rgba.Pixels.Length || x >= sheet.Width) continue;
                    int idx = sheet.Indices != null ? sheet.Indices[i] & 0xF : -1;
                    var c = idx == 0 ? Rgba32.Transparent : idx > 0 && pal != null && idx < pal.Length ? pal[idx] with { A = 255 } : sheet.Rgba.Pixels[i];
                    img[piece % 2 * PieceWidth + x, piece / 2 * PieceHeight + y] = c;
                }
        return img;
    }

    /// <summary>Adds a picture: sheet + palette in graphics/portraits/pictures, the PICTURE_ constant and the table entry.</summary>
    public static ExportPlan PlanAddPicture(string root, string name, RgbaImage picture)
    {
        if (!SupportsPictures(root)) throw new ExportValidationException(["Pictures need version 1.1 of the \"Dialog portraits\" mod – update it in Mods."]);
        var constant = "PICTURE_" + ToConstant(name)["PORTRAIT_".Length..];
        var file = constant["PICTURE_".Length..].ToLowerInvariant();
        var pascal = string.Concat(file.Split('_', StringSplitOptions.RemoveEmptyEntries).Select(p => char.ToUpperInvariant(p[0]) + p[1..]));
        var constantsFull = ProjectPaths.Combine(root, ConstantsFile);
        var dataFull = ProjectPaths.Combine(root, DataFile);
        var constants = File.ReadAllText(constantsFull);
        var data = File.ReadAllText(dataFull);
        var errors = new List<string>();
        if (constant == "PICTURE_" || constant == "PICTURE_NONE") errors.Add("Choose a name for the picture (e.g. Treasure map).");
        if (Regex.IsMatch(constants, @"\b" + constant + @"\b")) errors.Add($"{constant} already exists.");
        var pngRel = $"{Folder}/pictures/{file}.png";
        if (File.Exists(ProjectPaths.Combine(root, pngRel))) errors.Add($"{pngRel} already exists.");
        if (errors.Count > 0) throw new ExportValidationException(errors);

        var (pngBytes, pal) = ConvertPicture(picture);
        int id = PictureDefine().Matches(constants).Select(m => int.Parse(m.Groups[2].Value)).DefaultIfEmpty(0).Max() + 1;
        string nl = constants.Contains("\r\n") ? "\r\n" : "\n";
        int endif = constants.LastIndexOf("#endif", StringComparison.Ordinal);
        if (endif < 0) throw new ExportValidationException([$"{ConstantsFile} has no #endif."]);
        var newConstants = constants[..endif].TrimEnd('\r', '\n') + nl + $"#define {constant} {id}" + nl + nl + constants[endif..];

        var dnl = data.Contains("\r\n") ? "\r\n" : "\n";
        int marker = data.IndexOf(PictureMarker, StringComparison.Ordinal) + PictureMarker.Length;
        int lineEnd = data.IndexOf('\n', marker) + 1;
        var gfx = $"static const u32 sPictureGfx_{pascal}[] = INCBIN_U32(\"{Folder}/pictures/{file}.4bpp\");{dnl}" +
                  $"static const u16 sPicturePal_{pascal}[] = INCBIN_U16(\"{Folder}/pictures/{file}.gbapal\");{dnl}";
        var newData = data[..lineEnd] + gfx + data[lineEnd..];
        int table = newData.IndexOf("sDialogPictures[]", StringComparison.Ordinal);
        int close = newData.IndexOf("};", table, StringComparison.Ordinal);
        newData = newData[..close] + $"    [{constant}] = {{sPictureGfx_{pascal}, sPicturePal_{pascal}}},{dnl}" + newData[close..];

        var files = new List<PlannedFile>
        {
            new(pngRel, PlannedAction.Create, pngBytes, "Picture 128×96 as six 64×32 pieces (64×192), 4-bit indexed (index 0 transparent)"),
            new($"{Folder}/pictures/{file}.pal", PlannedAction.Create, Encoding.ASCII.GetBytes(JascPalette.Serialize(pal)), "Its 16 colors"),
            new(ConstantsFile, PlannedAction.Modify, new UTF8Encoding(false).GetBytes(newConstants), $"{constant} = {id}", Hashing.Sha256(File.ReadAllBytes(constantsFull))),
            new(DataFile, PlannedAction.Modify, new UTF8Encoding(false).GetBytes(newData), $"Graphics and table entry for {constant}", Hashing.Sha256(File.ReadAllBytes(dataFull))),
        };
        var plan = ExportPlan.ForFiles(root, $"Picture {constant}", Folder, files);
        plan.Notes.Add($"Use it in an event with the block \"Show picture\" ({constant}).");
        return plan;
    }

    public static string ToConstant(string name) => "PORTRAIT_" + Regex.Replace(Regex.Replace(name.Trim(), "([a-z0-9])([A-Z])", "$1_$2"), @"[^A-Za-z0-9]+", "_").Trim('_').ToUpperInvariant();

    /// <summary>The picture as the game will show it: 64×64, 4-bit, own palette, index 0 transparent.</summary>
    public static (byte[] Png, Rgba32[] Palette) Convert(PngImage png)
    {
        if (png.Width > Size || png.Height > Size)
            throw new ExportValidationException([$"A portrait is at most {Size}×{Size} pixels (this one is {png.Width}×{png.Height})."]);
        // Pictures with more colors (artwork, photos of sprites) are reduced to 15 colors + transparency automatically.
        bool hasAlpha = png.Rgba.Pixels.Any(p => p.A < 128);
        if (!png.IsIndexed && !SpriteFits(png))
            png = Png.Decode(Png.EncodeRgba(hasAlpha ? ColorQuantizer.Reduce(png.Rgba, 15) : ColorQuantizer.Reduce(BackgroundToAlpha(png.Rgba), 15)));
        var pal = SpriteImportPlanner.PaletteFromSheet(png);
        var indices = new byte[Size * Size];
        int ox = (Size - png.Width) / 2, oy = Size - png.Height;
        var px = png.Rgba.Pixels;
        for (int y = 0; y < png.Height; y++)
            for (int x = 0; x < png.Width; x++)
            {
                int i = y * png.Width + x;
                bool transparent = png.IsIndexed ? png.Indices![i] == 0 || px[i].A == 0 : px[i].A == 0 || px[i].SameRgb(px[0]);
                if (transparent) continue;
                indices[(oy + y) * Size + ox + x] = (byte)Math.Max(1, Array.IndexOf(pal, GbaColor.Snap(px[i].Opaque), 1));
            }
        return (Png.EncodeIndexed(Size, Size, indices, pal, 4), pal);
    }

    private static bool SpriteFits(PngImage png)
    {
        try { SpriteImportPlanner.PaletteFromSheet(png); return true; }
        catch (ExportValidationException) { return false; }
    }

    /// <summary>Without alpha the top-left color is the background (as everywhere in PokePat): make it transparent.</summary>
    private static RgbaImage BackgroundToAlpha(RgbaImage img)
    {
        var copy = img.Clone();
        var bg = img.Pixels[0];
        for (int i = 0; i < copy.Pixels.Length; i++) if (copy.Pixels[i].SameRgb(bg)) copy.Pixels[i] = Rgba32.Transparent;
        return copy;
    }

    /// <summary>Adds a portrait: PNG + palette in graphics/portraits, the constant and the table entry.</summary>
    public static ExportPlan PlanAdd(string root, string name, PngImage picture, Rgba32[]? palette = null)
    {
        if (!IsInstalled(root)) throw new ExportValidationException(["Install the \"Dialog portraits\" mod first."]);
        var constant = ToConstant(name);
        var file = constant["PORTRAIT_".Length..].ToLowerInvariant();
        var pascal = string.Concat(file.Split('_', StringSplitOptions.RemoveEmptyEntries).Select(p => char.ToUpperInvariant(p[0]) + p[1..]));
        var constantsFull = ProjectPaths.Combine(root, ConstantsFile);
        var dataFull = ProjectPaths.Combine(root, DataFile);
        var constants = File.ReadAllText(constantsFull);
        var data = File.ReadAllText(dataFull);
        var errors = new List<string>();
        if (constant == "PORTRAIT_" || constant == "PORTRAIT_NONE") errors.Add("Choose a name for the portrait (e.g. Birch).");
        if (Regex.IsMatch(constants, @"\b" + constant + @"\b")) errors.Add($"{constant} already exists.");
        var pngRel = $"{Folder}/{file}.png";
        if (File.Exists(ProjectPaths.Combine(root, pngRel))) errors.Add($"{pngRel} already exists.");
        if (!data.Contains(GraphicsMarker, StringComparison.Ordinal)) errors.Add($"{DataFile} has no \"{GraphicsMarker}\" line.");
        if (errors.Count > 0) throw new ExportValidationException(errors);

        var (pngBytes, pal) = Convert(picture);
        if (palette != null) pal = palette;
        int id = Define().Matches(constants).Where(m => !m.Groups[1].Value.StartsWith("PORTRAIT_POS_", StringComparison.Ordinal))
            .Select(m => int.Parse(m.Groups[2].Value)).DefaultIfEmpty(0).Max() + 1;
        string nl = constants.Contains("\r\n") ? "\r\n" : "\n";
        int endif = constants.LastIndexOf("#endif", StringComparison.Ordinal);
        if (endif < 0) throw new ExportValidationException([$"{ConstantsFile} has no #endif."]);
        var newConstants = constants[..endif].TrimEnd('\r', '\n') + nl + $"#define {constant} {id}" + nl + nl + constants[endif..];

        var dnl = data.Contains("\r\n") ? "\r\n" : "\n";
        int marker = data.IndexOf(GraphicsMarker, StringComparison.Ordinal) + GraphicsMarker.Length;
        int lineEnd = data.IndexOf('\n', marker) + 1;
        var gfx = $"static const u32 sPortraitGfx_{pascal}[] = INCBIN_U32(\"{Folder}/{file}.4bpp\");{dnl}" +
                  $"static const u16 sPortraitPal_{pascal}[] = INCBIN_U16(\"{Folder}/{file}.gbapal\");{dnl}";
        var newData = data[..lineEnd] + gfx + data[lineEnd..];
        // The portrait table (since 1.1 the picture table follows it).
        int table = newData.IndexOf("sDialogPortraits[]", StringComparison.Ordinal);
        int close = table >= 0 ? newData.IndexOf("};", table, StringComparison.Ordinal) : newData.LastIndexOf("};", StringComparison.Ordinal);
        newData = newData[..close] + $"    [{constant}] = {{sPortraitGfx_{pascal}, sPortraitPal_{pascal}}},{dnl}" + newData[close..];

        var files = new List<PlannedFile>
        {
            new(pngRel, PlannedAction.Create, pngBytes, "Portrait 64×64, 4-bit indexed (index 0 transparent)"),
            new($"{Folder}/{file}.pal", PlannedAction.Create, Encoding.ASCII.GetBytes(JascPalette.Serialize(pal)), "Its 16 colors"),
            new(ConstantsFile, PlannedAction.Modify, new UTF8Encoding(false).GetBytes(newConstants), $"{constant} = {id}", Hashing.Sha256(File.ReadAllBytes(constantsFull))),
            new(DataFile, PlannedAction.Modify, new UTF8Encoding(false).GetBytes(newData), $"Graphics and table entry for {constant}", Hashing.Sha256(File.ReadAllBytes(dataFull))),
        };
        var plan = ExportPlan.ForFiles(root, $"Portrait {constant}", Folder, files);
        plan.Notes.Add($"Use it in an event with the block \"Show portrait\" ({constant}) before a message.");
        return plan;
    }
}
