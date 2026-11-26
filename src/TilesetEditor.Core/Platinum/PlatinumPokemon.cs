using System.Text.Json;
using TilesetEditor.Core.Gba;
using TilesetEditor.Core.Imaging;
using TilesetEditor.Core.Project;

namespace TilesetEditor.Core.Platinum;

public enum PlatinumSpritePart { MaleFront, MaleBack, FemaleFront, FemaleBack }

/// <summary>A file the toolkit wants to write (project-relative path, new content).</summary>
public sealed record PlatinumFileChange(string Path, byte[] Content, string Description);

/// <summary>
/// The battle sprites of a species (res/pokemon/&lt;name&gt;/): four 160×80 4bpp images (two 80×80 frames each) sharing
/// normal.pal, plus shiny.pal with the same indices. Index 0 is transparent.
/// </summary>
public sealed class PlatinumSpriteSet
{
    public const int FrameSize = 80;
    public const int Width = 160, Height = 80;

    public required string Folder { get; init; }
    public required Rgba32[] Normal { get; init; }
    public required Rgba32[] Shiny { get; init; }
    /// <summary>Indices per part (null if the file is missing or not an indexed image).</summary>
    public required Dictionary<PlatinumSpritePart, byte[]?> Indices { get; init; }
    public required Dictionary<PlatinumSpritePart, byte[]?> FileBytes { get; init; }

    public static string FileName(PlatinumSpritePart p) => p switch
    {
        PlatinumSpritePart.MaleFront => "male_front.png",
        PlatinumSpritePart.MaleBack => "male_back.png",
        PlatinumSpritePart.FemaleFront => "female_front.png",
        _ => "female_back.png",
    };

    /// <summary>True if the species has its own female sprite (the files differ).</summary>
    public bool HasFemaleDifference(bool back) =>
        FileBytes[back ? PlatinumSpritePart.FemaleBack : PlatinumSpritePart.FemaleFront] is { } f &&
        FileBytes[back ? PlatinumSpritePart.MaleBack : PlatinumSpritePart.MaleFront] is { } m && !f.AsSpan().SequenceEqual(m);

    public static PlatinumSpriteSet Load(string root, string species)
    {
        var folder = $"{PlatinumProject.SpeciesDir}/{PlatinumNames.SpeciesFolder(species)}";
        var dir = ProjectPaths.Combine(root, folder);
        Rgba32[] Pal(string name)
        {
            var p = Path.Combine(dir, name);
            return File.Exists(p) ? JascPalette.Load(p) : new Rgba32[16];
        }
        var indices = new Dictionary<PlatinumSpritePart, byte[]?>();
        var bytes = new Dictionary<PlatinumSpritePart, byte[]?>();
        foreach (var part in Enum.GetValues<PlatinumSpritePart>())
        {
            var path = Path.Combine(dir, FileName(part));
            if (!File.Exists(path)) { indices[part] = null; bytes[part] = null; continue; }
            var data = File.ReadAllBytes(path);
            bytes[part] = data;
            try
            {
                var png = Png.Decode(data);
                indices[part] = png.IsIndexed && png.Width == Width && png.Height == Height ? png.Indices : null;
            }
            catch (PngFormatException) { indices[part] = null; }
        }
        return new PlatinumSpriteSet { Folder = folder, Normal = Pal("normal.pal"), Shiny = Pal("shiny.pal"), Indices = indices, FileBytes = bytes };
    }

    /// <summary>One 80×80 frame (0 or 1) in normal or shiny colours; index 0 transparent.</summary>
    public RgbaImage? Frame(PlatinumSpritePart part, int frame, bool shiny)
    {
        if (Indices[part] is not { } idx) return null;
        var pal = shiny ? Shiny : Normal;
        var img = new RgbaImage(FrameSize, FrameSize);
        for (int y = 0; y < FrameSize; y++)
            for (int x = 0; x < FrameSize; x++)
            {
                int i = idx[y * Width + frame * FrameSize + x];
                img[x, y] = i == 0 || i >= pal.Length ? Rgba32.Transparent : pal[i] with { A = 255 };
            }
        return img;
    }

    /// <summary>The whole 160×80 sheet in normal colours (for re-quantizing the parts that are kept).</summary>
    public RgbaImage? Sheet(PlatinumSpritePart part)
    {
        if (Indices[part] is not { } idx) return null;
        var img = new RgbaImage(Width, Height);
        for (int i = 0; i < idx.Length; i++) img.Pixels[i] = idx[i] == 0 || idx[i] >= Normal.Length ? Rgba32.Transparent : Normal[idx[i]] with { A = 255 };
        return img;
    }
}

/// <summary>Builds the file changes for replacing battle sprites of a species.</summary>
public static class PlatinumSpriteImport
{
    /// <summary>
    /// Replaces the front or back sprite with <paramref name="picture"/> (80×80: used for both frames, or 160×80: two frames).
    /// All four sprites share one 16-colour palette, so the kept sprites are re-coloured into the new palette; the shiny
    /// palette is derived by giving each new colour the shiny colour of the nearest old colour.
    /// Transparent pixels (alpha 0) and pixels in the picture's top-left colour become index 0.
    /// </summary>
    public static (List<PlatinumFileChange> Files, List<string> Notes) Replace(PlatinumSpriteSet set, bool back, RgbaImage picture, bool femaleToo)
    {
        if (!((picture.Width == 80 || picture.Width == 160) && picture.Height == 80))
            throw new InvalidDataException($"The picture is {picture.Width}×{picture.Height}; it must be 80×80 (one frame) or 160×80 (two frames side by side).");
        var notes = new List<string>();
        var sheet = new RgbaImage(PlatinumSpriteSet.Width, PlatinumSpriteSet.Height);
        var bg = picture[0, 0];
        for (int f = 0; f < 2; f++)
            for (int y = 0; y < 80; y++)
                for (int x = 0; x < 80; x++)
                {
                    var c = picture[picture.Width == 160 ? f * 80 + x : x, y];
                    sheet[f * 80 + x, y] = c.A < 128 || (bg.A == 255 && c.SameRgb(bg)) ? Rgba32.Transparent : c with { A = 255 };
                }
        if (picture.Width == 80) notes.Add("The 80×80 picture is used for both animation frames.");

        var replaced = new List<PlatinumSpritePart> { back ? PlatinumSpritePart.MaleBack : PlatinumSpritePart.MaleFront };
        bool femaleSame = !set.HasFemaleDifference(back);
        if (femaleToo || femaleSame) replaced.Add(back ? PlatinumSpritePart.FemaleBack : PlatinumSpritePart.FemaleFront);

        // Every sheet that ends up in the files: the new one and the kept ones in their current colours.
        var sheets = new Dictionary<PlatinumSpritePart, RgbaImage>();
        foreach (var part in Enum.GetValues<PlatinumSpritePart>())
        {
            if (set.FileBytes[part] == null) continue;
            if (replaced.Contains(part)) sheets[part] = sheet;
            else if (set.Sheet(part) is { } kept) sheets[part] = kept;
        }

        // One palette for all: up to 15 colours + transparent.
        var all = new RgbaImage(PlatinumSpriteSet.Width, PlatinumSpriteSet.Height * sheets.Count);
        int row = 0;
        foreach (var s in sheets.Values) { all.Blit(s, 0, 0, 0, row * PlatinumSpriteSet.Height, s.Width, s.Height); row++; }
        var reduced = ColorQuantizer.Reduce(all, 15);
        var colors = reduced.Pixels.Where(p => p.A != 0).Select(p => p with { A = 255 }).Distinct().Take(15).ToList();
        if (reduced.Pixels.Where(p => p.A != 0).Select(p => p with { A = 255 }).Distinct().Count() > 15)
            notes.Add("The colours were reduced to 15.");
        var palette = new Rgba32[16];
        palette[0] = set.Normal.Length > 0 ? set.Normal[0] with { A = 255 } : new Rgba32(255, 255, 255);
        for (int i = 0; i < colors.Count; i++) palette[i + 1] = colors[i];
        for (int i = colors.Count + 1; i < 16; i++) palette[i] = new Rgba32(0, 0, 0);

        // Shiny: the shiny colour of the nearest old normal colour.
        var shiny = new Rgba32[16];
        shiny[0] = set.Shiny.Length > 0 ? set.Shiny[0] with { A = 255 } : palette[0];
        for (int i = 1; i < 16; i++)
        {
            if (i > colors.Count) { shiny[i] = palette[i]; continue; }
            int best = -1; long bestD = long.MaxValue;
            for (int j = 1; j < Math.Min(set.Normal.Length, set.Shiny.Length); j++)
            {
                long d = Dist(palette[i], set.Normal[j]);
                if (d < bestD) { bestD = d; best = j; }
            }
            shiny[i] = best > 0 ? set.Shiny[best] with { A = 255 } : palette[i];
        }
        notes.Add("The shiny colours were taken over from the nearest old colours – check them in the shiny preview.");

        var files = new List<PlatinumFileChange>();
        row = 0;
        foreach (var (part, _) in sheets)
        {
            var idx = new byte[PlatinumSpriteSet.Width * PlatinumSpriteSet.Height];
            for (int y = 0; y < PlatinumSpriteSet.Height; y++)
                for (int x = 0; x < PlatinumSpriteSet.Width; x++)
                {
                    var c = reduced[x, row * PlatinumSpriteSet.Height + y];
                    idx[y * PlatinumSpriteSet.Width + x] = c.A == 0 ? (byte)0 : Nearest(palette, c);
                }
            row++;
            var content = Png.EncodeIndexed(PlatinumSpriteSet.Width, PlatinumSpriteSet.Height, idx, palette, 4);
            var what = replaced.Contains(part) ? "new sprite" : "same picture, colours moved to the new palette";
            files.Add(new PlatinumFileChange($"{set.Folder}/{PlatinumSpriteSet.FileName(part)}", content, what));
        }
        files.Add(new PlatinumFileChange($"{set.Folder}/normal.pal", System.Text.Encoding.ASCII.GetBytes(JascPalette.Serialize(palette)), "palette (16 colours)"));
        files.Add(new PlatinumFileChange($"{set.Folder}/shiny.pal", System.Text.Encoding.ASCII.GetBytes(JascPalette.Serialize(shiny)), "shiny palette"));
        return (files, notes);
    }

    private static long Dist(Rgba32 a, Rgba32 b) =>
        (long)(a.R - b.R) * (a.R - b.R) + (long)(a.G - b.G) * (a.G - b.G) + (long)(a.B - b.B) * (a.B - b.B);

    private static byte Nearest(Rgba32[] pal, Rgba32 c)
    {
        int best = 1; long bestD = long.MaxValue;
        for (int i = 1; i < pal.Length; i++)
        {
            long d = Dist(pal[i], c);
            if (d < bestD) { bestD = d; best = i; }
        }
        return (byte)best;
    }
}

/// <summary>Level-up learnset and evolutions of a species (learnset.by_level in data.json).</summary>
public static class PlatinumLearnsets
{
    public sealed record Entry(int Level, string Move);

    public static List<Entry> Read(string dataJson)
    {
        using var doc = JsonDocument.Parse(dataJson);
        var list = new List<Entry>();
        if (doc.RootElement.TryGetProperty("learnset", out var ls) && ls.TryGetProperty("by_level", out var by))
            foreach (var e in by.EnumerateArray())
                if (e.ValueKind == JsonValueKind.Array && e.GetArrayLength() >= 2)
                    list.Add(new Entry(e[0].GetInt32(), e[1].GetString() ?? ""));
        return list;
    }

    /// <summary>Evolutions as readable lines ("Level 16 → SPECIES_IVYSAUR").</summary>
    public static List<string> Evolutions(string dataJson)
    {
        using var doc = JsonDocument.Parse(dataJson);
        var list = new List<string>();
        if (doc.RootElement.TryGetProperty("evolutions", out var ev) && ev.ValueKind == JsonValueKind.Array)
            foreach (var e in ev.EnumerateArray())
                if (e.ValueKind == JsonValueKind.Array && e.GetArrayLength() >= 3)
                    list.Add($"{PlatinumNames.Pretty(e[0].GetString() ?? "", "EVO_")} {e[1].GetRawText()} → {PlatinumNames.Pretty(e[2].GetString() ?? "", "SPECIES_")}");
        return list;
    }

    /// <summary>Replaces learnset.by_level in the file text, one "[ level, "MOVE" ]" per line like the original files (order as given).</summary>
    public static string Write(string dataJson, IReadOnlyList<Entry> entries)
    {
        var spans = JsonText.Locate(dataJson);
        if (!spans.TryGetValue("learnset.by_level", out var span))
            throw new InvalidOperationException("data.json has no learnset.by_level.");
        var indent = JsonText.IndentAt(dataJson, span.Start);
        var inner = indent + "    ";
        var raw = entries.Count == 0
            ? "[  ]" // how the species files write an empty list
            : "[\n" + string.Join(",\n", entries.Select(e => $"{inner}[ {e.Level}, {JsonText.Quote(e.Move)} ]")) + "\n" + indent + "]";
        return JsonText.Replace(dataJson, [(span, raw)]);
    }
}
