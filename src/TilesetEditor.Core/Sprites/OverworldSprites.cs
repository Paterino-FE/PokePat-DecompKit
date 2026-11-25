using System.Text.RegularExpressions;
using TilesetEditor.Core.Gba;
using TilesetEditor.Core.Imaging;

namespace TilesetEditor.Core.Sprites;

/// <summary>An overworld sprite (OBJ_EVENT_GFX_…) as registered in the branch.</summary>
public sealed record OverworldSprite(string GfxId, string InfoSymbol, int Width, int Height, string PaletteTag, string? PicSymbol,
    string? PngPath, string? PalettePath)
{
    public string Display => $"{GfxId.Replace("OBJ_EVENT_GFX_", "")}  ({Width}×{Height})";
    public override string ToString() => Display;
}

public sealed record SpriteIssue(bool IsError, string Message);

public sealed class SpriteCheckResult
{
    public required int FrameWidth { get; init; }
    public required int FrameHeight { get; init; }
    public required int Frames { get; init; }
    public required IReadOnlyList<Rgba32> Colors { get; init; }
    public required IReadOnlyList<SpriteIssue> Issues { get; init; }
    /// <summary>Existing palettes (tag → path) that contain every color of the sprite.</summary>
    public required IReadOnlyList<string> MatchingPalettes { get; init; }
    public bool HasErrors => Issues.Any(i => i.IsError);
}

/// <summary>
/// Reads the overworld sprite registration of pokeemerald-expansion: graphics info pointers, graphics info structs,
/// pic tables, pic/palette INCBINs and the palette tag table in src/event_object_movement.c.
/// </summary>
public static partial class OverworldSprites
{
    public static readonly (int W, int H)[] FrameSizes = [(16, 16), (16, 32), (32, 32), (64, 64), (32, 16), (48, 48)];
    /// <summary>sAnimTable_Standard uses 9 frames: face S/N/W and two walking frames each (E is mirrored).</summary>
    public const int StandardFrames = 9;

    [GeneratedRegex(@"\[(OBJ_EVENT_GFX_\w+)\]\s*=\s*&(gObjectEventGraphicsInfo_\w+)")]
    private static partial Regex PointerRegex();

    [GeneratedRegex(@"const\s+struct\s+ObjectEventGraphicsInfo\s+(gObjectEventGraphicsInfo_\w+)\s*=\s*\{(.*?)\};", RegexOptions.Singleline)]
    private static partial Regex InfoRegex();

    [GeneratedRegex(@"static\s+const\s+struct\s+SpriteFrameImage\s+(sPicTable_\w+)\[\]\s*=\s*\{(.*?)\};", RegexOptions.Singleline)]
    private static partial Regex PicTableRegex();

    // Several files in one INCBIN (e.g. the player: walking.4bpp, running.4bpp) → the first file stands for the sprite.
    [GeneratedRegex(@"(gObjectEvent(?:Pic|Pal)_\w+)\[\]\s*=\s*INCBIN_U\d+\(\s*""([^""]+)""")]
    private static partial Regex IncbinRegex();

    [GeneratedRegex(@"\{\s*(gObjectEventPal_\w+)\s*,\s*(OBJ_EVENT_PAL_TAG_\w+)\s*\}")]
    private static partial Regex PalTagRegex();

    public static List<OverworldSprite> Load(string root)
    {
        string Read(params string[] parts)
        {
            var p = Path.Combine([root, .. parts]);
            return File.Exists(p) ? File.ReadAllText(p) : "";
        }
        var dir = new[] { "src", "data", "object_events" };
        var pointers = PointerRegex().Matches(Read([.. dir, "object_event_graphics_info_pointers.h"]));
        var infos = InfoRegex().Matches(Read([.. dir, "object_event_graphics_info.h"]))
            .GroupBy(m => m.Groups[1].Value).ToDictionary(g => g.Key, g => g.First().Groups[2].Value);
        var picTables = PicTableRegex().Matches(Read([.. dir, "object_event_pic_tables.h"]))
            .GroupBy(m => m.Groups[1].Value).ToDictionary(g => g.Key, g => g.First().Groups[2].Value);
        var incbins = IncbinRegex().Matches(Read([.. dir, "object_event_graphics.h"]))
            .GroupBy(m => m.Groups[1].Value).ToDictionary(g => g.Key, g => g.First().Groups[2].Value);
        var palByTag = PalTagRegex().Matches(Read("src", "event_object_movement.c"))
            .GroupBy(m => m.Groups[2].Value).ToDictionary(g => g.Key, g => g.First().Groups[1].Value);

        var result = new List<OverworldSprite>();
        foreach (Match p in pointers)
        {
            var gfx = p.Groups[1].Value;
            var infoSym = p.Groups[2].Value;
            if (!infos.TryGetValue(infoSym, out var body)) continue;
            string F(string field) => Regex.Match(body, @"\." + field + @"\s*=\s*([^,\n]+)") is { Success: true } m ? m.Groups[1].Value.Trim() : "";
            int.TryParse(F("width"), out var w);
            int.TryParse(F("height"), out var h);
            var tag = F("paletteTag");
            string? pic = null;
            if (picTables.TryGetValue(F("images"), out var table) && Regex.Match(table, @"(gObjectEventPic_\w+)") is { Success: true } pm)
                pic = pm.Groups[1].Value;
            string? png = pic != null && incbins.TryGetValue(pic, out var path) ? Regex.Replace(path, @"\.4bpp(\.\w+)?$", ".png") : null;
            string? pal = palByTag.TryGetValue(tag, out var palSym) && incbins.TryGetValue(palSym, out var pp) ? Regex.Replace(pp, @"\.gbapal(\.\w+)?$", ".pal") : null;
            result.Add(new OverworldSprite(gfx, infoSym, w, h, tag, pic, png, pal));
        }
        return result.GroupBy(s => s.GfxId).Select(g => g.First()).ToList();
    }

    /// <summary>All palettes referenced by sprites (tag → project-relative .pal).</summary>
    public static Dictionary<string, string> Palettes(IEnumerable<OverworldSprite> sprites) =>
        sprites.Where(s => s.PalettePath != null).GroupBy(s => s.PaletteTag).ToDictionary(g => g.Key, g => g.First().PalettePath!);

    /// <summary>Checks a new sprite sheet: frame size, frame count, colors, palette fit.</summary>
    public static SpriteCheckResult Check(PngImage png, int frameW, int frameH, IReadOnlyDictionary<string, Rgba32[]> palettes)
    {
        var issues = new List<SpriteIssue>();
        int frames = 0;
        if (png.Width % frameW != 0 || png.Height % frameH != 0)
            issues.Add(new(true, $"The image is {png.Width}×{png.Height} pixels – not a multiple of the frame size {frameW}×{frameH}."));
        else
        {
            frames = png.Width / frameW * (png.Height / frameH);
            if (png.Height != frameH) issues.Add(new(false, "The frames span multiple rows; pokeemerald expects them side by side in a single row."));
            if (frames != StandardFrames && frames != 1)
                issues.Add(new(false, $"{frames} frames found. A walking character (sAnimTable_Standard) needs {StandardFrames}: facing down/up/left plus two walking steps each."));
        }

        // Transparent color: palette index 0 for indexed images, otherwise alpha 0 or the top-left pixel.
        Rgba32? transparent = png.IsIndexed && png.Palette is { Length: > 0 } ? png.Palette[0] : null;
        var colors = new HashSet<Rgba32>();
        for (int i = 0; i < png.Rgba.Pixels.Length; i++)
        {
            var c = png.Rgba.Pixels[i];
            if (c.A == 0) continue;
            if (png.IsIndexed && png.Indices![i] == 0) continue;
            if (!png.IsIndexed && transparent == null) transparent = png.Rgba.Pixels[0];
            if (!png.IsIndexed && c.SameRgb(transparent!.Value)) continue;
            colors.Add(c.Opaque);
        }
        if (colors.Count > 15)
            issues.Add(new(true, $"{colors.Count} colors – an overworld sprite has at most 15 plus transparency (one 16-color palette)."));
        if (!png.IsIndexed)
            issues.Add(new(false, "The image is not indexed. It will be converted to a palette on import; transparency = alpha 0 or the top-left color."));

        var gbaColors = colors.Select(GbaColor.ToBgr555).ToHashSet();
        var matching = palettes.Where(kv => gbaColors.IsSubsetOf(kv.Value.Skip(1).Select(GbaColor.ToBgr555))).Select(kv => kv.Key).OrderBy(k => k).ToList();
        if (matching.Count == 0 && colors.Count > 0)
            issues.Add(new(false, "No existing palette contains all colors – the sprite needs its own palette (its own palette tag)."));
        return new SpriteCheckResult
        {
            FrameWidth = frameW, FrameHeight = frameH, Frames = frames, Colors = colors.ToList(), Issues = issues, MatchingPalettes = matching,
        };
    }

    /// <summary>Renders a sheet with a game palette (as the GBA shows it), index 0 transparent.</summary>
    public static RgbaImage Render(PngImage png, Rgba32[]? palette)
    {
        var img = new RgbaImage(png.Width, png.Height);
        for (int i = 0; i < img.Pixels.Length; i++)
        {
            if (png.IsIndexed && png.Indices != null)
            {
                int idx = png.Indices[i] & 0xF;
                if (idx == 0) continue;
                var c = palette != null && idx < palette.Length ? palette[idx] : png.Palette![Math.Min(png.Indices[i], png.Palette.Length - 1)];
                img.Pixels[i] = GbaColor.ToDisplayed(c with { A = 255 });
            }
            else img.Pixels[i] = png.Rgba.Pixels[i];
        }
        return img;
    }
}
