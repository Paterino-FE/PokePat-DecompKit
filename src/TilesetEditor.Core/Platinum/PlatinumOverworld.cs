using System.Text;
using System.Text.RegularExpressions;
using TilesetEditor.Core.Imaging;
using TilesetEditor.Core.Project;

namespace TilesetEditor.Core.Platinum;

/// <summary>An overworld graphic (OBJ_EVENT_GFX_…) and the picture it uses.</summary>
public sealed record OverworldSprite(string Gfx, string? Member, string? PngRel);

/// <summary>
/// Overworld sprites of newer pokeplatinum: res/graphics/field_sprites/&lt;group&gt;/&lt;name&gt;.png (32 px wide, frames of
/// 32×32 below each other) is turned into &lt;name&gt;.nsbtx by the build; src/overlay005/object_event_gfx_data.c gives every
/// OBJ_EVENT_GFX its renderer, texture, model/animation and details. A new character is a new picture plus a copy of the
/// four table entries of a similar character.
/// </summary>
public static partial class PlatinumOverworld
{
    public const string Folder = "res/graphics/field_sprites";
    public const string MesonFile = Folder + "/meson.build";
    public const string OrderFile = Folder + "/field_sprites.order";
    public const string DataFile = "src/overlay005/object_event_gfx_data.c";
    public const string ConstantsFile = "generated/object_events_gfx.txt";
    public const string ListStart = "field_sprites_textures_32_px_single_palette = [";

    public static bool IsSupported(string root) =>
        File.Exists(ProjectPaths.Combine(root, DataFile)) && File.Exists(ProjectPaths.Combine(root, MesonFile)) && File.Exists(ProjectPaths.Combine(root, OrderFile));

    [GeneratedRegex(@"OBJ_EVENT_TEXTURE\((\w+),\s*(\w+)\)")]
    private static partial Regex TextureLine();

    [GeneratedRegex(@"'file':\s*'([^']+)'")]
    private static partial Regex MesonFileEntry();

    public static List<OverworldSprite> List(string root)
    {
        if (!IsSupported(root)) return [];
        var files = MesonFileEntry().Matches(File.ReadAllText(ProjectPaths.Combine(root, MesonFile)))
            .Select(m => m.Groups[1].Value).GroupBy(f => Path.GetFileNameWithoutExtension(f)).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        var textures = TextureLine().Matches(File.ReadAllText(ProjectPaths.Combine(root, DataFile)))
            .Where(m => m.Groups[1].Value != "SENTINEL_ID")
            .GroupBy(m => m.Groups[1].Value).ToDictionary(g => g.Key, g => g.First().Groups[2].Value, StringComparer.Ordinal);
        var list = new List<OverworldSprite>();
        foreach (var line in File.ReadLines(ProjectPaths.Combine(root, ConstantsFile)))
        {
            var gfx = line.Split('=')[0].Trim();
            if (!gfx.StartsWith("OBJ_EVENT_GFX_", StringComparison.Ordinal) || gfx is "OBJ_EVENT_GFX_SENTINEL_ID") continue;
            var key = gfx[14..];
            textures.TryGetValue(key, out var member);
            string? png = null;
            if (member != null && member.EndsWith("_nsbtx", StringComparison.Ordinal) && files.TryGetValue(member[..^6], out var f)) png = $"{Folder}/{f}";
            list.Add(new OverworldSprite(gfx, member, png));
        }
        return list;
    }

    /// <summary>Frame <paramref name="frame"/> (32×32) of a sprite picture; index 0 transparent.</summary>
    public static RgbaImage? Frame(string root, string pngRel, int frame = 0)
    {
        var png = Png.Load(ProjectPaths.Combine(root, pngRel));
        int size = Math.Min(png.Width, 32), top = frame * size;
        if (top + size > png.Height) top = 0;
        var img = new RgbaImage(png.Width, Math.Min(size, png.Height));
        for (int y = 0; y < img.Height; y++)
            for (int x = 0; x < img.Width; x++)
            {
                int i = (top + y) * png.Width + x;
                img[x, y] = png.Indices != null && png.Indices[i] == 0 ? Rgba32.Transparent : png.Rgba.Pixels[i];
            }
        return img;
    }

    /// <summary>The standing frame of a person looking in direction 0 north, 1 south, 2 west, 3 east (4 frames per direction).</summary>
    public static RgbaImage? Facing(string root, string pngRel, int direction) => Frame(root, pngRel, Math.Clamp(direction, 0, 3) * 4);

    /// <summary>A picture in the format of the sprite sheets: 16 colours (index 0 = transparent), 4-bit PNG.</summary>
    public static byte[] Encode(RgbaImage picture)
    {
        var bg = picture[0, 0];
        var img = new RgbaImage(picture.Width, picture.Height);
        for (int i = 0; i < img.Pixels.Length; i++)
        {
            var c = picture.Pixels[i];
            img.Pixels[i] = c.A < 128 || (bg.A == 255 && c.SameRgb(bg)) ? Rgba32.Transparent : c with { A = 255 };
        }
        var reduced = ColorQuantizer.Reduce(img, 15);
        var colors = reduced.Pixels.Where(p => p.A != 0).Select(p => p with { A = 255 }).Distinct().Take(15).ToList();
        var palette = new Rgba32[16];
        palette[0] = new Rgba32(255, 0, 255);
        for (int i = 0; i < 15; i++) palette[i + 1] = i < colors.Count ? colors[i] : new Rgba32(0, 0, 0);
        var idx = new byte[img.Pixels.Length];
        for (int i = 0; i < idx.Length; i++)
        {
            var c = reduced.Pixels[i];
            if (c.A == 0) continue;
            int k = colors.FindIndex(x => x.SameRgb(c));
            idx[i] = (byte)(k < 0 ? 1 : k + 1);
        }
        return Png.EncodeIndexed(img.Width, img.Height, idx, palette, 4);
    }

    /// <summary>
    /// Replaces a sprite sheet with a PNG. An indexed PNG with at most 16 colours keeps its own palette exactly (order and
    /// colour 0 = transparent); other pictures are reduced to 15 colours + transparency.
    /// </summary>
    public static PlatinumFileChange PlanReplace(string root, OverworldSprite sprite, PngImage png)
    {
        png = IndexedPicture.BackgroundFirst(png, png.Width);
        if (sprite.PngRel != null && File.Exists(ProjectPaths.Combine(root, sprite.PngRel)))
        {
            var old = Png.Load(ProjectPaths.Combine(root, sprite.PngRel));
            if (png.Width == old.Width && ExpandUniqueFrames(old, png) is { } full)
            {
                var c = PlanReplace(root, sprite, full);
                return c with { Description = c.Description + $", {png.Height / png.Width} frames from the game's stored order placed as the game uses them" };
            }
        }
        var change = PlanReplace(root, sprite, IndexedPicture.WithTransparentIndex0(png));
        return IndexedPicture.KeepOwnPalette(png, 4) is { } own ? change with { Content = own, Description = "new picture (its own 16-colour palette)" } : change;
    }

    /// <summary>
    /// The order in which the game stores the different frames of a sheet: the textures are named basename.1 … basename.N
    /// (frame number + 1) and stored sorted by name as text (.1, .10, .11 … .2 …), each different picture once. Returns for
    /// every frame of <paramref name="sheet"/> the number of its picture in that order – e.g. Dawn's 32 frames use 24.
    /// </summary>
    public static int[] StoredFrameOrder(PngImage sheet)
    {
        int size = sheet.Width * sheet.Width, n = sheet.Height / Math.Max(1, sheet.Width);
        string Key(int f) => sheet.Indices != null
            ? Convert.ToBase64String(sheet.Indices, f * size, size)
            : string.Join(",", sheet.Rgba.Pixels.Skip(f * size).Take(size).Select(c => c.A == 0 ? 0 : c.R << 16 | c.G << 8 | c.B));
        var byName = Enumerable.Range(0, n).OrderBy(f => (f + 1).ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparer.Ordinal);
        var stored = new Dictionary<string, int>();
        var result = new int[n];
        foreach (var f in byName)
        {
            var k = Key(f);
            if (!stored.TryGetValue(k, out var no)) stored[k] = no = stored.Count;
            result[f] = no;
        }
        return result;
    }

    /// <summary>
    /// A sheet with only the different frames (as exported from the game's textures, e.g. 24 for Dawn's 32) spread to the
    /// full frame order of <paramref name="old"/>; null when the number of frames does not match.
    /// </summary>
    public static PngImage? ExpandUniqueFrames(PngImage old, PngImage unique)
    {
        if (unique.Width != old.Width || unique.Height % unique.Width != 0) return null;
        var order = StoredFrameOrder(old);
        int distinct = order.Length == 0 ? 0 : order.Max() + 1;
        if (unique.Height / unique.Width != distinct) return null;
        int size = old.Width * old.Width;
        if (distinct == order.Length)
        {
            // As many frames as the sheet: they may be in frame order already or in the stored order – the one whose
            // outlines fit the old poses better wins.
            if (order.Select((o, f) => o == f).All(x => x)) return null;
            bool Solid(PngImage p, int frame, int i) => p.Indices != null ? p.Indices[frame * size + i] != 0 : p.Rgba.Pixels[frame * size + i].A >= 128;
            long Fit(Func<int, int> source)
            {
                long same = 0;
                for (int f = 0; f < order.Length; f++)
                    for (int i = 0; i < size; i++)
                        if (Solid(old, f, i) && Solid(unique, source(f), i)) same++;
                return same;
            }
            if (Fit(f => order[f]) <= Fit(f => f)) return null;
        }
        if (unique.Indices != null && unique.Palette != null)
        {
            var idx = new byte[order.Length * size];
            for (int f = 0; f < order.Length; f++) Array.Copy(unique.Indices, order[f] * size, idx, f * size, size);
            return Png.Decode(Png.EncodeIndexed(old.Width, order.Length * old.Width, idx, unique.Palette, unique.Palette.Length <= 16 ? 4 : 8));
        }
        var img = new RgbaImage(old.Width, order.Length * old.Width);
        for (int f = 0; f < order.Length; f++) Array.Copy(unique.Rgba.Pixels, order[f] * size, img.Pixels, f * size, size);
        return Png.Decode(Png.EncodeRgba(img));
    }

    /// <summary>Like <see cref="PlanAdd(string, string, string, string, RgbaImage)"/>; an indexed PNG keeps its own palette.</summary>
    public static (List<PlatinumFileChange> Files, List<string> Notes) PlanAdd(string root, string constant, string file, string template, PngImage png)
    {
        var (files, notes) = PlanAdd(root, constant, file, template, IndexedPicture.WithTransparentIndex0(png));
        if (IndexedPicture.KeepOwnPalette(png, 4) is { } own)
            files = files.Select(f => f.Path.EndsWith(".png", StringComparison.Ordinal) ? f with { Content = own, Description = "picture (16 frames of 32×32, its own palette)" } : f).ToList();
        return (files, notes);
    }

    public static PlatinumFileChange PlanReplace(string root, OverworldSprite sprite, RgbaImage picture)
    {
        if (sprite.PngRel == null) throw new InvalidDataException($"{sprite.Gfx} has no picture PokePat knows (it is built in a special way).");
        var old = Png.Load(ProjectPaths.Combine(root, sprite.PngRel));
        if (picture.Width != old.Width || picture.Height != old.Height)
            throw new InvalidDataException($"The picture is {picture.Width}×{picture.Height}; {Path.GetFileName(sprite.PngRel)} is {old.Width}×{old.Height} ({old.Height / Math.Max(1, old.Width)} frames of {old.Width}×{old.Width} below each other).");
        return new PlatinumFileChange(sprite.PngRel, Encode(picture), "new picture (16 colours)");
    }

    /// <summary>
    /// A new character: <paramref name="constant"/> (OBJ_EVENT_GFX_…) after the last ordinary one, the picture as
    /// npc/&lt;file&gt;.png (32×512: 16 frames like the other people), and the table entries of <paramref name="template"/>.
    /// </summary>
    public static (List<PlatinumFileChange> Files, List<string> Notes) PlanAdd(string root, string constant, string file, string template, RgbaImage picture)
    {
        if (!IsSupported(root)) throw new InvalidDataException("This version of pokeplatinum keeps overworld sprites in a form PokePat cannot add to (use a newer pokeplatinum).");
        if (!Regex.IsMatch(constant, @"^OBJ_EVENT_GFX_[A-Z0-9_]+$")) throw new InvalidDataException("The constant must look like OBJ_EVENT_GFX_NAME.");
        file = Regex.Replace(file.Trim().ToLowerInvariant(), @"[^a-z0-9_]+", "_").Trim('_');
        if (file.Length == 0) throw new InvalidDataException("The file name is empty.");
        if (picture.Width != 32 || picture.Height != 512) throw new InvalidDataException($"The picture is {picture.Width}×{picture.Height}; people are 32×512 (16 frames of 32×32 below each other, like npc/youngster.png).");
        string Read(string rel) => File.ReadAllText(ProjectPaths.Combine(root, rel), Encoding.UTF8);
        var constants = Read(ConstantsFile);
        if (Regex.IsMatch(constants, @"^" + constant + @"\b", RegexOptions.Multiline)) throw new InvalidDataException($"{constant} already exists.");
        var pngRel = $"{Folder}/npc/{file}.png";
        if (File.Exists(ProjectPaths.Combine(root, pngRel))) throw new InvalidDataException($"{pngRel} already exists.");
        var order = Read(OrderFile);
        if (order.Split('\n').Any(l => l.Trim() == file + ".nsbtx")) throw new InvalidDataException($"{file}.nsbtx is already in {OrderFile}.");

        // Constant: after the last ordinary graphic (before the fixed ids such as OBJ_EVENT_GFX_BERRY_SPROUT = 4096).
        var lines = constants.Replace("\r\n", "\n").TrimEnd('\n').Split('\n').ToList();
        int firstFixed = lines.FindIndex(l => l.Contains('='));
        int ordinary = lines.Take(firstFixed < 0 ? lines.Count : firstFixed).Count(l => l.Trim().Length > 0);
        lines.Insert(firstFixed < 0 ? lines.Count : firstFixed, constant);
        int value = ordinary; // enumerators count from 0
        var notes = new List<string> { $"{constant} = {value} (the next free number of the ordinary graphics)." };
        // Some numbers without a constant are used directly by the field code: warn when the new one appears as hex there.
        var hex = new Regex($@"\b0x{value:X}\b", RegexOptions.IgnoreCase);
        var hits = new[] { "src/overlay005", "src/overlay009" }.Select(d => ProjectPaths.Combine(root, d)).Where(Directory.Exists)
            .SelectMany(d => Directory.EnumerateFiles(d, "*.c")).Where(f => hex.IsMatch(File.ReadAllText(f))).Select(Path.GetFileName).ToList();
        if (hits.Count > 0) notes.Add($"Check: 0x{value:X} appears in {string.Join(", ", hits)} – make sure the number is not used for another object there.");

        // Tables: copy the template's four entries.
        var data = Read(DataFile);
        var key = template.StartsWith("OBJ_EVENT_GFX_", StringComparison.Ordinal) ? template[14..] : template;
        var newKey = constant[14..];
        string CopyLine(string macro, string? replaceSecond = null)
        {
            var m = Regex.Match(data, @"^([ \t]*)" + macro + @"\(" + key + @",([^\n]*)\)\s*,[ \t]*$", RegexOptions.Multiline);
            if (!m.Success) throw new InvalidDataException($"{template} has no {macro} entry to copy.");
            var rest = m.Groups[2].Value;
            // Keep the column of the second value: the template's name plus its padding.
            int width = key.Length + (rest.Length - rest.TrimStart().Length);
            rest = new string(' ', Math.Max(1, width - newKey.Length)) + rest.TrimStart();
            if (replaceSecond != null)
            {
                var second = Regex.Match(rest, @"^(\s*)(\w+)(\s*)");
                rest = second.Groups[1].Value + replaceSecond.PadRight(Math.Max(replaceSecond.Length, second.Groups[2].Value.Length + second.Groups[3].Value.Length)) + rest[second.Length..];
                rest = rest.TrimEnd();
            }
            return $"{m.Groups[1].Value}{macro}({newKey},{rest}),\n";
        }
        string Insert(string text, string macro, string line)
        {
            var sentinel = Regex.Match(text, @"^[ \t]*" + macro + @"\(SENTINEL_ID,", RegexOptions.Multiline);
            if (!sentinel.Success) throw new InvalidDataException($"The end of the {macro} table was not found.");
            return text.Insert(sentinel.Index, line);
        }
        foreach (var (macro, second) in new[] { ("OBJ_EVENT_RENDERER", (string?)null), ("OBJ_EVENT_TEXTURE", file + "_nsbtx"), ("OBJ_EVENT_MODEL_ANIM", null), ("OBJ_EVENT_DETAILS", null) })
            data = Insert(data, macro, CopyLine(macro, second));

        var meson = Read(MesonFile);
        int start = meson.IndexOf(ListStart, StringComparison.Ordinal);
        int close = start < 0 ? -1 : meson.IndexOf("\n]", start, StringComparison.Ordinal);
        if (close < 0) throw new InvalidDataException($"The list {ListStart} was not found in {MesonFile}.");
        var basename = file.Length > 15 ? file[..15] : file; // texture names in BTX0 have 16 bytes
        meson = meson.Insert(close + 1, $"    {{ 'file': 'npc/{file}.png', 'basename': '{basename}' }},\n");

        if (!order.EndsWith('\n')) order += "\n";
        order += file + ".nsbtx\n";

        var utf8 = new UTF8Encoding(false);
        notes.Add($"Behaviour, animation and shadow are copied from {template}.");
        notes.Add($"Use it in Events & Dialogs as graphics {constant}, then build the ROM.");
        return (
        [
            new(pngRel, Encode(picture), "picture (16 frames of 32×32, 16 colours)"),
            new(ConstantsFile, utf8.GetBytes(string.Join("\n", lines) + "\n"), constant),
            new(DataFile, utf8.GetBytes(data), $"renderer, texture, animation and details (copy of {template})"),
            new(MesonFile, utf8.GetBytes(meson), $"npc/{file}.png in the build"),
            new(OrderFile, utf8.GetBytes(order), $"{file}.nsbtx in field_sprites.narc"),
        ], notes);
    }
}
