using System.Text;
using System.Text.RegularExpressions;
using TilesetEditor.Core.Gba;
using TilesetEditor.Core.Imaging;
using TilesetEditor.Core.Project;

namespace TilesetEditor.Export.Mods;

public sealed record Portrait(string Constant, int Id, string? PngRel);

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

    [GeneratedRegex(@"^#define[ \t]+(PORTRAIT_\w+)[ \t]+(\d+)", RegexOptions.Multiline)]
    private static partial Regex Define();

    public static bool IsInstalled(string root) => File.Exists(ProjectPaths.Combine(root, ConstantsFile)) && File.Exists(ProjectPaths.Combine(root, DataFile));

    public static List<Portrait> List(string root)
    {
        if (!IsInstalled(root)) return [];
        var data = File.ReadAllText(ProjectPaths.Combine(root, DataFile));
        return Define().Matches(File.ReadAllText(ProjectPaths.Combine(root, ConstantsFile)))
            .Select(m => new Portrait(m.Groups[1].Value, int.Parse(m.Groups[2].Value),
                Regex.Match(data, @"\[" + m.Groups[1].Value + @"\]\s*=\s*\{\s*(\w+)") is { Success: true } e
                && Regex.Match(data, Regex.Escape(e.Groups[1].Value) + @"\[\]\s*=\s*INCBIN_U32\(""([^""]+)\.4bpp""") is { Success: true } g ? g.Groups[1].Value + ".png" : null))
            .ToList();
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
        int id = Define().Matches(constants).Select(m => int.Parse(m.Groups[2].Value)).DefaultIfEmpty(0).Max() + 1;
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
        int close = newData.LastIndexOf("};", StringComparison.Ordinal);
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
