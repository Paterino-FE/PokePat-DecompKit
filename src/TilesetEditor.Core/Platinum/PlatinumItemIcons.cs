using System.Text;
using System.Text.Json;
using TilesetEditor.Core.Gba;
using TilesetEditor.Core.Imaging;
using TilesetEditor.Core.Project;

namespace TilesetEditor.Core.Platinum;

/// <summary>The icon of an item: picture (…_NCGR → icons/x.png) and palette (…_NCLR → icons/y.pal) from its JSON file.</summary>
public sealed record ItemIcon(string ItemFile, string Sprite, string Palette, string? PngRel, string? PalRel);

/// <summary>
/// Item icons of pokeplatinum (res/items/icons): 32×32 pictures with 16 colours and a palette file. Many items share a
/// picture (all TMs: one picture with a palette per type), so an icon is either changed for every item that uses it or the
/// item gets its own picture and palette (added to the build lists and item_icon.order).
/// </summary>
public static class PlatinumItemIcons
{
    public const string IconDir = "res/items/icons";
    public const string IconMeson = IconDir + "/meson.build";
    public const string OrderFile = "res/items/item_icon.order";
    public const int Size = 32;

    private static string Stem(string symbol, string suffix) => symbol.EndsWith(suffix, StringComparison.Ordinal) ? symbol[..^suffix.Length] : symbol;

    public static ItemIcon? Of(string root, string itemFile)
    {
        var path = ProjectPaths.Combine(root, itemFile);
        if (!File.Exists(path)) return null;
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        if (!doc.RootElement.TryGetProperty("icon", out var icon) || icon.ValueKind != JsonValueKind.Object) return null;
        var sprite = icon.TryGetProperty("sprite", out var s) ? s.GetString() ?? "" : "";
        var palette = icon.TryGetProperty("palette", out var p) ? p.GetString() ?? "" : "";
        string? Existing(string rel) => File.Exists(ProjectPaths.Combine(root, rel)) ? rel : null;
        return new ItemIcon(itemFile, sprite, palette, Existing($"{IconDir}/{Stem(sprite, "_NCGR")}.png"), Existing($"{IconDir}/{Stem(palette, "_NCLR")}.pal"));
    }

    public static RgbaImage? Render(string root, ItemIcon icon)
    {
        if (icon.PngRel == null) return null;
        var png = Png.Load(ProjectPaths.Combine(root, icon.PngRel));
        var pal = icon.PalRel != null ? JascPalette.Load(ProjectPaths.Combine(root, icon.PalRel)) : png.Palette;
        var img = new RgbaImage(png.Width, png.Height);
        for (int i = 0; i < img.Pixels.Length; i++)
        {
            int idx = png.Indices?[i] ?? 0;
            img.Pixels[i] = idx == 0 || pal == null || idx >= pal.Length ? Rgba32.Transparent : pal[idx] with { A = 255 };
        }
        return img;
    }

    /// <summary>Item files whose icon uses the same picture.</summary>
    public static List<(string File, string Palette)> Users(string root, string sprite)
    {
        var list = new List<(string, string)>();
        foreach (var f in Directory.EnumerateFiles(ProjectPaths.Combine(root, PlatinumItems.DataDir), "*.json"))
        {
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(f));
                if (doc.RootElement.TryGetProperty("icon", out var icon) && icon.TryGetProperty("sprite", out var s) && s.GetString() == sprite)
                    list.Add(($"{PlatinumItems.DataDir}/{Path.GetFileName(f)}", icon.TryGetProperty("palette", out var p) ? p.GetString() ?? "" : ""));
            }
            catch (JsonException) { }
        }
        return list;
    }

    /// <summary>16 colours (index 0 = transparent, the top-left colour counts as background) → picture indices and palette.</summary>
    private static (byte[] Indices, Rgba32[] Palette) Quantize(RgbaImage picture)
    {
        if (picture.Width != Size || picture.Height != Size) throw new InvalidDataException($"The picture is {picture.Width}×{picture.Height}; item icons are {Size}×{Size}.");
        var bg = picture[0, 0];
        var img = new RgbaImage(Size, Size);
        for (int i = 0; i < img.Pixels.Length; i++)
        {
            var c = picture.Pixels[i];
            img.Pixels[i] = c.A < 128 || (bg.A == 255 && c.SameRgb(bg)) ? Rgba32.Transparent : c with { A = 255 };
        }
        var reduced = ColorQuantizer.Reduce(img, 15);
        var colors = reduced.Pixels.Where(p => p.A != 0).Select(p => p with { A = 255 }).Distinct().Take(15).ToList();
        var palette = new Rgba32[16];
        palette[0] = new Rgba32(180, 180, 180);
        for (int i = 0; i < 15; i++) palette[i + 1] = i < colors.Count ? colors[i] : new Rgba32(0, 0, 0);
        var idx = new byte[Size * Size];
        for (int i = 0; i < idx.Length; i++)
        {
            var c = reduced.Pixels[i];
            if (c.A == 0) continue;
            int k = colors.FindIndex(x => x.SameRgb(c));
            idx[i] = (byte)(k < 0 ? 1 : k + 1);
        }
        return (idx, palette);
    }

    /// <summary>Changes the shared picture and its palette – every item that uses both changes.</summary>
    public static List<PlatinumFileChange> PlanReplaceShared(string root, ItemIcon icon, RgbaImage picture)
    {
        if (icon.PngRel == null || icon.PalRel == null) throw new InvalidDataException("The icon's picture or palette file was not found.");
        var (idx, pal) = Quantize(picture);
        return
        [
            new(icon.PngRel, Png.EncodeIndexed(Size, Size, idx, pal, 4), "new icon picture"),
            new(icon.PalRel, Encoding.ASCII.GetBytes(JascPalette.Serialize(pal)), "new icon palette (16 colours)"),
        ];
    }

    /// <summary>Gives the item its own picture and palette named after it, in the build lists, and points its JSON at them.</summary>
    public static List<PlatinumFileChange> PlanOwnIcon(string root, ItemIcon icon, RgbaImage picture)
    {
        var name = Path.GetFileNameWithoutExtension(icon.ItemFile);
        string pngRel = $"{IconDir}/{name}.png", palRel = $"{IconDir}/{name}.pal";
        for (int n = 2; File.Exists(ProjectPaths.Combine(root, pngRel)) || File.Exists(ProjectPaths.Combine(root, palRel)); n++)
            (pngRel, palRel) = ($"{IconDir}/{name}_{n}.png", $"{IconDir}/{name}_{n}.pal");
        var stem = Path.GetFileNameWithoutExtension(pngRel);
        var (idx, pal) = Quantize(picture);
        var utf8 = new UTF8Encoding(false);

        var meson = File.ReadAllText(ProjectPaths.Combine(root, IconMeson));
        meson = InsertIntoList(meson, "item_icon_pngs = files(", $"    '{stem}.png',\n");
        meson = InsertIntoList(meson, "item_icon_pals = files(", $"    '{stem}.pal',\n");
        var order = File.ReadAllText(ProjectPaths.Combine(root, OrderFile));
        if (!order.EndsWith('\n')) order += "\n";
        order += $"{stem}.NCGR\n{stem}.NCLR\n";

        var itemText = File.ReadAllText(ProjectPaths.Combine(root, icon.ItemFile), Encoding.UTF8);
        var spans = JsonText.Locate(itemText);
        if (!spans.TryGetValue("icon.sprite", out var s) || !spans.TryGetValue("icon.palette", out var p))
            throw new InvalidDataException($"{icon.ItemFile} has no icon.sprite / icon.palette.");
        itemText = JsonText.Replace(itemText, [(s, JsonText.Quote(stem + "_NCGR")), (p, JsonText.Quote(stem + "_NCLR"))]);
        return
        [
            new(pngRel, Png.EncodeIndexed(Size, Size, idx, pal, 4), "own icon picture"),
            new(palRel, Encoding.ASCII.GetBytes(JascPalette.Serialize(pal)), "own icon palette"),
            new(IconMeson, utf8.GetBytes(meson), $"{stem}.png / {stem}.pal in the build"),
            new(OrderFile, utf8.GetBytes(order), $"{stem} in item_icon.narc"),
            new(icon.ItemFile, utf8.GetBytes(itemText), $"icon → {stem}"),
        ];
    }

    private static string InsertIntoList(string text, string listStart, string line)
    {
        int start = text.IndexOf(listStart, StringComparison.Ordinal);
        int close = start < 0 ? -1 : text.IndexOf("\n)", start, StringComparison.Ordinal);
        if (close < 0) throw new InvalidDataException($"\"{listStart}\" was not found in {IconMeson}.");
        return text.Insert(close + 1, line);
    }
}
