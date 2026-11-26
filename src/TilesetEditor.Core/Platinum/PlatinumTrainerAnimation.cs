using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using TilesetEditor.Core.Imaging;
using TilesetEditor.Core.Project;

namespace TilesetEditor.Core.Platinum;

/// <summary>
/// One step of a trainer's battle animation: show cell <paramref name="Cell"/> for <paramref name="Delay"/> frames (1/60 s).
/// <paramref name="Attribute"/> is the step's signal to the battle: 1 = the white screen flash of the ball throw.
/// </summary>
public sealed record TrainerAnimStep(int Cell, int Delay, int Attribute = 0)
{
    public bool Flash => Attribute == 1;
}

/// <summary>
/// The battle picture of a trainer class with its animation (Barry, gym leaders, Elite Four …): front.png holds the frames
/// (80×80 each, one below the other with a line of 1 pixel between them), front_cell.json one cell per frame (cell k shows
/// frame k, sometimes moved a few pixels – the little hop), front_anim.json the sequences: which cell for how long.
/// Sequence 0 is the still picture, the next one(s) the animation when the battle starts.
/// </summary>
public sealed class TrainerAnimation
{
    public required string Folder { get; init; }
    public required List<RgbaImage> Frames { get; init; }
    /// <summary>Per cell: how far its picture is moved from the normal place (pixels), or null for an empty cell.</summary>
    public required List<(int Dx, int Dy)?> Cells { get; init; }
    public required List<List<TrainerAnimStep>> Sequences { get; init; }
    public bool IsAnimated => Frames.Count > 1;

    /// <summary>What cell <paramref name="cell"/> shows: its frame at its place on an 80×80 picture.</summary>
    public RgbaImage Render(int cell)
    {
        var img = new RgbaImage(PlatinumTrainerAnimations.Size, PlatinumTrainerAnimations.Size);
        if (cell < 0 || cell >= Cells.Count || Cells[cell] is not { } at || cell >= Frames.Count) return img;
        var f = Frames[cell];
        for (int y = 0; y < img.Height; y++)
            for (int x = 0; x < img.Width; x++)
            {
                int sx = x - at.Dx, sy = y - at.Dy;
                if (sx >= 0 && sy >= 0 && sx < f.Width && sy < f.Height) img[x, y] = f[sx, sy];
            }
        return img;
    }

    /// <summary>The longest sequence (the animation when the battle starts), or 0.</summary>
    public int MainSequence => Sequences.Count == 0 ? 0 : Sequences.Select((s, i) => (s, i)).OrderByDescending(x => x.s.Count).First().i;
}

public static class PlatinumTrainerAnimations
{
    public const int Size = 80;
    private const int Pitch = Size + 1;

    public static int FrameCount(PngImage sheet) => (sheet.Height + 1) / Pitch;

    public static TrainerAnimation Load(string root, string trainerClass)
    {
        var folder = PlatinumTrainerClasses.Folder(trainerClass);
        var png = Png.Load(ProjectPaths.Combine(root, folder + "/front.png"));
        var frames = new List<RgbaImage>();
        for (int k = 0; k < FrameCount(png); k++)
        {
            var f = new RgbaImage(Size, Size);
            for (int y = 0; y < Size; y++)
                for (int x = 0; x < Size; x++)
                {
                    int i = (k * Pitch + y) * png.Width + x;
                    if (i >= png.Rgba.Pixels.Length || x >= png.Width) continue;
                    f[x, y] = png.Indices != null && png.Indices[i] == 0 ? Rgba32.Transparent : png.Rgba.Pixels[i] with { A = 255 };
                }
            frames.Add(f);
        }
        var cells = new List<(int, int)?>();
        var cellPath = ProjectPaths.Combine(root, folder + "/front_cell.json");
        if (File.Exists(cellPath) && JsonNode.Parse(File.ReadAllText(cellPath))?["cells"] is JsonArray cellArr)
            foreach (var c in cellArr)
            {
                var oams = c?["OAM"] as JsonArray;
                if (oams == null || oams.Count == 0) { cells.Add(null); continue; }
                int minX = oams.Min(o => (int)o!["Attr1"]!["XCoordinate"]!), minY = oams.Min(o => (int)o!["Attr0"]!["YCoordinate"]!);
                cells.Add((minX + Size / 2, minY + Size / 2));
            }
        else cells.AddRange(frames.Select(_ => ((int, int)?)(0, 0)));
        return new TrainerAnimation { Folder = folder, Frames = frames, Cells = cells, Sequences = ReadSequences(root, folder) };
    }

    private static List<List<TrainerAnimStep>> ReadSequences(string root, string folder)
    {
        var path = ProjectPaths.Combine(root, folder + "/front_anim.json");
        var result = new List<List<TrainerAnimStep>>();
        if (!File.Exists(path) || JsonNode.Parse(File.ReadAllText(path)) is not JsonObject anim) return result;
        var results = (anim["animationResults"] as JsonArray)?.Select(r => (int)r!["index"]!).ToList() ?? [];
        var attrs = (anim["uaatData"]?["frameAttributes"] as JsonArray)?.Select(a => (int)a!).ToList() ?? [];
        int at = 0;
        foreach (var s in anim["sequences"] as JsonArray ?? [])
            result.Add((s!["frameData"] as JsonArray ?? []).Select(f =>
            {
                int id = (int)f!["resultId"]!;
                int attr = at < attrs.Count ? attrs[at] : 0;
                at++;
                return new TrainerAnimStep(id < results.Count ? results[id] : 0, (int)f["frameDelay"]!, attr);
            }).ToList());
        return result;
    }

    // ------------------------------------------------------------------ replacing pictures

    /// <summary>
    /// A new sheet: as tall as front.png (frames with the 1-pixel line between them) or only the frames one below the other
    /// (N × 80). An indexed PNG with up to 16 colours keeps its palette (colour 0 = transparent); other pictures are reduced
    /// to 15 colours for all frames together (transparent or the top-left colour = background).
    /// </summary>
    public static PlatinumFileChange PlanReplaceSheet(string root, string trainerClass, PngImage picture)
    {
        var rel = PlatinumTrainerClasses.Folder(trainerClass) + "/front.png";
        var old = Png.Load(ProjectPaths.Combine(root, rel));
        int n = FrameCount(old);
        if (picture.Width != Size) throw new InvalidDataException($"The picture is {picture.Width} pixels wide; trainer pictures are {Size} wide.");
        bool withLines = picture.Height == old.Height, plain = picture.Height == n * Size;
        if (!withLines && !plain)
            throw new InvalidDataException($"The picture is {picture.Width}×{picture.Height}; this class has {n} frame(s): {Size}×{old.Height} (with a 1-pixel line between the frames) or {Size}×{n * Size} (frames directly below each other).");
        int Src(int frame, int y) => (withLines ? frame * Pitch : frame * Size) + y;

        byte[] idx;
        Rgba32[] palette;
        if (picture.Indices != null && picture.Palette is { Length: > 0 } own && picture.Indices.All(i => i < 16))
        {
            idx = picture.Indices;
            palette = Enumerable.Range(0, 16).Select(i => i < own.Length ? own[i] with { A = 255 } : new Rgba32(0, 0, 0)).ToArray();
        }
        else
        {
            var img = picture.Rgba.Clone();
            var bg = img[0, 0];
            for (int i = 0; i < img.Pixels.Length; i++)
            {
                var c = img.Pixels[i];
                img.Pixels[i] = c.A < 128 || (bg.A == 255 && c.SameRgb(bg)) ? Rgba32.Transparent : Gba.GbaColor.Snap(c with { A = 255 });
            }
            var reduced = ColorQuantizer.Reduce(img, 15);
            var colors = reduced.Pixels.Where(c => c.A != 0).Select(c => c with { A = 255 }).Distinct().Take(15).ToList();
            palette = new Rgba32[16];
            palette[0] = old.Palette is { Length: > 0 } p0 ? p0[0] with { A = 255 } : new Rgba32(255, 255, 255);
            for (int i = 0; i < 15; i++) palette[i + 1] = i < colors.Count ? colors[i] : new Rgba32(0, 0, 0);
            var number = new Dictionary<Rgba32, int>();
            for (int i = 0; i < colors.Count; i++) number[colors[i]] = i + 1;
            idx = reduced.Pixels.Select(c => (byte)(c.A == 0 ? 0 : number.GetValueOrDefault(c with { A = 255 }, 1))).ToArray();
        }
        // The lines between the frames stay as they were.
        var oldIdx = old.Indices ?? new byte[old.Width * old.Height];
        var outIdx = (byte[])oldIdx.Clone();
        for (int k = 0; k < n; k++)
            for (int y = 0; y < Size; y++)
                Array.Copy(idx, Src(k, y) * Size, outIdx, (k * Pitch + y) * Size, Size);
        return new PlatinumFileChange(rel, Png.EncodeIndexed(Size, old.Height, outIdx, palette, old.BitDepth == 4 ? 4 : 8),
            $"new battle picture with {n} frame(s)");
    }

    /// <summary>One frame anew (80×80), drawn with the colours the sheet already has (all frames share one palette).</summary>
    public static PlatinumFileChange PlanReplaceFrame(string root, string trainerClass, int frame, PngImage picture)
    {
        var rel = PlatinumTrainerClasses.Folder(trainerClass) + "/front.png";
        var old = Png.Load(ProjectPaths.Combine(root, rel));
        if (frame < 0 || frame >= FrameCount(old)) throw new InvalidDataException($"Frame {frame + 1} does not exist.");
        if (picture.Width != Size || picture.Height != Size) throw new InvalidDataException($"A frame is {Size}×{Size}; the picture is {picture.Width}×{picture.Height}.");
        var pal = old.Palette ?? throw new InvalidDataException($"{rel} has no palette.");
        var img = IndexedPicture.WithTransparentIndex0(picture);
        var bg = img[0, 0];
        bool bgIsColour = picture.Indices == null && bg.A == 255;
        var outIdx = (byte[])(old.Indices ?? new byte[old.Width * old.Height]).Clone();
        int colors = Math.Min(16, pal.Length);
        for (int y = 0; y < Size; y++)
            for (int x = 0; x < Size; x++)
            {
                var c = img[x, y];
                byte best = 0;
                if (c.A >= 128 && !(bgIsColour && c.SameRgb(bg)))
                {
                    long bestD = long.MaxValue;
                    for (int k = 1; k < colors; k++)
                    {
                        long d = (long)(pal[k].R - c.R) * (pal[k].R - c.R) + (long)(pal[k].G - c.G) * (pal[k].G - c.G) + (long)(pal[k].B - c.B) * (pal[k].B - c.B);
                        if (d < bestD) { bestD = d; best = (byte)k; }
                    }
                }
                outIdx[(frame * Pitch + y) * Size + x] = best;
            }
        return new PlatinumFileChange(rel, Png.EncodeIndexed(Size, old.Height, outIdx, pal.Take(Math.Max(16, pal.Length)).ToArray(), old.BitDepth == 4 ? 4 : 8),
            $"frame {frame + 1} of the battle picture (in the sheet's colours)");
    }

    // ------------------------------------------------------------------ timing

    /// <summary>
    /// Sequence <paramref name="sequence"/> of front_anim.json anew: which cell for how many frames. Results for new cells are
    /// added; the counts, the per-frame attributes and the file's own layout (tabs, like the build tools write it) stay right.
    /// </summary>
    public static PlatinumFileChange PlanTiming(string root, string trainerClass, int sequence, IReadOnlyList<TrainerAnimStep> steps)
    {
        var rel = PlatinumTrainerClasses.Folder(trainerClass) + "/front_anim.json";
        var full = ProjectPaths.Combine(root, rel);
        var text = File.ReadAllText(full);
        var anim = JsonNode.Parse(text) as JsonObject ?? throw new InvalidDataException($"{rel} is not a JSON object.");
        var seqs = anim["sequences"] as JsonArray ?? throw new InvalidDataException($"{rel} has no sequences.");
        if (sequence < 0 || sequence >= seqs.Count) throw new InvalidDataException($"{rel} has no sequence {sequence}.");
        if (steps.Count == 0) throw new InvalidDataException("An animation needs at least one step.");
        if (steps.Any(s => s.Delay is < 1 or > 255)) throw new InvalidDataException("Each step lasts 1–255 frames (60 frames = 1 second).");
        if (steps.Any(s => s.Attribute != 0) && anim["uaatData"]?["frameAttributes"] is null)
            throw new InvalidDataException($"{rel} has no place for the flash signal (uaatData).");
        int cellCount = (JsonNode.Parse(File.ReadAllText(ProjectPaths.Combine(root, PlatinumTrainerClasses.Folder(trainerClass) + "/front_cell.json")))?["cellCount"])?.GetValue<int>() ?? int.MaxValue;
        if (steps.Any(s => s.Cell < 0 || s.Cell >= cellCount)) throw new InvalidDataException($"This class has {cellCount} cells (0–{cellCount - 1}).");

        var results = anim["animationResults"] as JsonArray ?? [];
        int ResultFor(int cell)
        {
            for (int i = 0; i < results.Count; i++)
                if ((int)results[i]!["index"]! == cell && (int?)results[i]!["resultType"] is null or 0) return i;
            results.Add(new JsonObject { ["resultType"] = 0, ["index"] = cell });
            return results.Count - 1;
        }
        // Frame attributes are stored for all sequences one after the other: keep the others, fit this one's.
        var attrs = anim["uaatData"]?["frameAttributes"] as JsonArray;
        List<int>? attrList = attrs?.Select(a => (int)a!).ToList();
        int start = seqs.Take(sequence).Sum(s => (s!["frameData"] as JsonArray)?.Count ?? 0);
        int oldCount = (seqs[sequence]!["frameData"] as JsonArray)?.Count ?? 0;
        var frameData = new JsonArray();
        foreach (var s in steps) frameData.Add(new JsonObject { ["frameDelay"] = s.Delay, ["resultId"] = ResultFor(s.Cell) });
        var seq = seqs[sequence]!.AsObject();
        seq["frameCount"] = steps.Count;
        if (seq.ContainsKey("loopStartFrame") && (int)seq["loopStartFrame"]! >= steps.Count) seq["loopStartFrame"] = 0;
        seq["frameData"] = frameData;
        if (attrList != null && attrs != null)
        {
            // Each step carries its own signal (e.g. the flash of the ball throw), so it moves with the step.
            var all = attrList.Take(start).Concat(steps.Select(s => s.Attribute)).Concat(attrList.Skip(start + oldCount)).ToList();
            anim["uaatData"]!["frameAttributes"] = new JsonArray(all.Select(a => (JsonNode?)JsonValue.Create(a)).ToArray());
        }
        anim["frameCount"] = seqs.Sum(s => (s!["frameData"] as JsonArray)?.Count ?? 0);
        anim["resultCount"] = results.Count;
        var nl = text.Contains("\r\n") ? "\r\n" : "\n";
        var output = CJson(anim, 0).Replace("\n", nl) + (text.EndsWith('\n') ? nl : "");
        return new PlatinumFileChange(rel, new UTF8Encoding(false).GetBytes(output), $"animation: {steps.Count} step(s), {steps.Sum(s => s.Delay)} frames");
    }

    /// <summary>JSON in the layout of cJSON (the pokeplatinum tools): tabs, "key":&lt;tab&gt;value, arrays on one line.</summary>
    public static string CJson(JsonNode? node, int depth)
    {
        switch (node)
        {
            case JsonObject o:
            {
                if (o.Count == 0) return "{\n" + new string('\t', depth) + "}";
                var sb = new StringBuilder("{\n");
                int i = 0;
                foreach (var (k, v) in o)
                {
                    sb.Append('\t', depth + 1).Append(System.Text.Json.JsonSerializer.Serialize(k)).Append(":\t").Append(CJson(v, depth + 1));
                    sb.Append(++i < o.Count ? ",\n" : "\n");
                }
                return sb.Append('\t', depth).Append('}').ToString();
            }
            case JsonArray a:
                return "[" + string.Join(", ", a.Select(x => CJson(x, depth + 1))) + "]";
            case null:
                return "null";
            default:
                var v2 = node.AsValue();
                if (v2.TryGetValue<bool>(out var b)) return b ? "true" : "false";
                if (v2.TryGetValue<string>(out var s)) return System.Text.Json.JsonSerializer.Serialize(s);
                if (v2.TryGetValue<double>(out var d)) return d.ToString(CultureInfo.InvariantCulture);
                return node.ToJsonString();
        }
    }
}
