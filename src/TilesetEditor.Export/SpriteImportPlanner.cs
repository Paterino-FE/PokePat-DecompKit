using System.Text;
using System.Text.RegularExpressions;
using TilesetEditor.Core.Gba;
using TilesetEditor.Core.Imaging;
using TilesetEditor.Core.Project;
using TilesetEditor.Core.Sprites;

namespace TilesetEditor.Export;

public sealed record SpriteImportRequest(string Name, string PngPath, int FrameWidth, int FrameHeight, string PaletteTag, bool AllowNearestColors,
    /// <summary>Set to create a new palette (e.g. "Guard") from the sheet's own colors instead of using <see cref="PaletteTag"/>.</summary>
    string? NewPaletteName = null);

public sealed record SpriteImportResult(ExportPlan Plan, IReadOnlyList<(Rgba32 From, Rgba32 To)> ColorChanges, string Constant);

/// <summary>
/// Registers a new overworld sprite in pokeemerald-expansion, using an existing palette tag:
/// PNG (indexed to the palette), constant + NUM_OBJ_EVENT_GFX, INCBIN, pic table, graphics info, pointer table (extern + entry)
/// and the gbagfx rule in spritesheet_rules.mk. Insertions go after the last top-level entry of each file (never inside #if).
/// expansion 1.13+: the constant is an enumerator before NUM_OBJ_EVENT_GFX, and INCGFX carries the gbagfx options
/// (-mwidth/-mheight), so no make rule is needed.
/// </summary>
public static partial class SpriteImportPlanner
{
    public const string ConstantsFile = "include/constants/event_objects.h";
    public const string GraphicsFile = "src/data/object_events/object_event_graphics.h";
    public const string PicTablesFile = "src/data/object_events/object_event_pic_tables.h";
    public const string InfoFile = "src/data/object_events/object_event_graphics_info.h";
    public const string PointersFile = "src/data/object_events/object_event_graphics_info_pointers.h";
    public const string RulesFile = "spritesheet_rules.mk";
    public const string MovementFile = "src/event_object_movement.c";
    public const string PaletteDir = "graphics/object_events/palettes";

    public static string ToPaletteTag(string name) => "OBJ_EVENT_PAL_TAG_" + Regex.Replace(name, "([a-z0-9])([A-Z])", "$1_$2").ToUpperInvariant();

    [GeneratedRegex("^[A-Z][A-Za-z0-9]*$")]
    private static partial Regex NameRegex();

    public static string ToConstant(string name) => "OBJ_EVENT_GFX_" + Regex.Replace(name, "([a-z0-9])([A-Z])", "$1_$2").ToUpperInvariant();
    public static string ToFileName(string name) => Regex.Replace(name, "([a-z0-9])([A-Z])", "$1_$2").ToLowerInvariant();

    public static SpriteImportResult Plan(string root, SpriteImportRequest req, IReadOnlyList<OverworldSprite> sprites, IReadOnlyDictionary<string, Rgba32[]> palettes)
    {
        var errors = new List<string>();
        if (!NameRegex().IsMatch(req.Name)) errors.Add("Name: start with an uppercase letter, letters and digits only (e.g. Guard or OldSailor).");
        string constant = ToConstant(req.Name), file = ToFileName(req.Name);
        string pic = $"gObjectEventPic_{req.Name}", table = $"sPicTable_{req.Name}", info = $"gObjectEventGraphicsInfo_{req.Name}";
        string pngRel = $"graphics/object_events/pics/people/{file}.png";
        if (sprites.Any(s => s.GfxId == constant)) errors.Add($"{constant} already exists.");
        string Read(string rel) => File.ReadAllText(ProjectPaths.Combine(root, rel));
        foreach (var (rel, sym) in new[] { (GraphicsFile, pic), (PicTablesFile, table), (InfoFile, info), (ConstantsFile, constant) })
            if (Regex.IsMatch(Read(rel), @"\b" + sym + @"\b")) errors.Add($"{sym} is already taken in {rel}.");
        if (File.Exists(ProjectPaths.Combine(root, pngRel))) errors.Add($"{pngRel} already exists.");
        bool newPal = !string.IsNullOrWhiteSpace(req.NewPaletteName);
        string palTag = req.PaletteTag, palSym = "", palRel = "";
        Rgba32[]? pal = null;
        if (newPal)
        {
            var pn = req.NewPaletteName!.Trim();
            if (!NameRegex().IsMatch(pn)) errors.Add("Palette name: start with an uppercase letter, letters and digits only (e.g. Guard).");
            palTag = ToPaletteTag(pn);
            palSym = $"gObjectEventPal_{pn}";
            palRel = $"{PaletteDir}/{ToFileName(pn)}.pal";
            if (Regex.IsMatch(Read(ConstantsFile), @"\b" + palTag + @"\b")) errors.Add($"{palTag} already exists – choose another palette name or use the existing palette.");
            if (Regex.IsMatch(Read(GraphicsFile), @"\b" + palSym + @"\b")) errors.Add($"{palSym} is already taken in {GraphicsFile}.");
            if (File.Exists(ProjectPaths.Combine(root, palRel))) errors.Add($"{palRel} already exists.");
            if (!Read(MovementFile).Contains("sObjectEventSpritePalettes[]", StringComparison.Ordinal)) errors.Add($"The palette table sObjectEventSpritePalettes was not found in {MovementFile}.");
        }
        else if (!palettes.TryGetValue(req.PaletteTag, out pal)) errors.Add($"Palette {req.PaletteTag} not found.");
        if (errors.Count > 0) throw new ExportValidationException(errors);

        // --- Image: check and index to the palette
        var png = Png.Load(req.PngPath);
        var check = OverworldSprites.Check(png, req.FrameWidth, req.FrameHeight, palettes);
        if (check.Issues.Any(i => i.IsError)) throw new ExportValidationException(check.Issues.Where(i => i.IsError).Select(i => i.Message).ToList());
        if (newPal) pal = PaletteFromSheet(png);
        var changes = new List<(Rgba32, Rgba32)>();
        var indices = new byte[png.Width * png.Height];
        var map = new Dictionary<Rgba32, byte>();
        var missing = new List<Rgba32>();
        for (int i = 0; i < indices.Length; i++)
        {
            bool transparent = png.IsIndexed ? png.Indices![i] == 0 || png.Rgba.Pixels[i].A == 0
                : png.Rgba.Pixels[i].A == 0 || png.Rgba.Pixels[i].SameRgb(png.Rgba.Pixels[0]);
            if (transparent) continue;
            var c = png.Rgba.Pixels[i].Opaque;
            if (!map.TryGetValue(c, out var idx))
            {
                int exact = Array.FindIndex(pal!, 1, k => GbaColor.ToBgr555(k) == GbaColor.ToBgr555(c));
                if (exact > 0) idx = (byte)exact;
                else
                {
                    int best = 1;
                    for (int k = 2; k < Math.Min(16, pal!.Length); k++)
                        if (Dist(pal[k], c) < Dist(pal[best], c)) best = k;
                    idx = (byte)best;
                    missing.Add(c);
                    changes.Add((c, pal![best]));
                }
                map[c] = idx;
            }
            indices[i] = idx;
        }
        if (missing.Count > 0 && !req.AllowNearestColors)
            throw new ExportValidationException([$"{missing.Count} color(s) are not in palette {palTag}: " +
                string.Join(", ", missing.Take(8).Select(m => m.ToHex())) + ". \"Use closest color\" replaces them."]);
        var palette16 = Enumerable.Range(0, 16).Select(k => k < pal!.Length ? pal[k] : new Rgba32(0, 0, 0)).ToList();
        var pngBytes = Png.EncodeIndexed(png.Width, png.Height, indices, palette16, 4);

        // --- Templates: palette fields from a sprite with this palette, size fields from one with this frame size
        var palSprite = newPal ? null : sprites.First(s => s.PaletteTag == req.PaletteTag);
        var sizeSprite = sprites.FirstOrDefault(s => s.Width == req.FrameWidth && s.Height == req.FrameHeight)
                         ?? throw new ExportValidationException([$"No existing {req.FrameWidth}×{req.FrameHeight} sprite found to use as a template."]);
        string infoText = Read(InfoFile);
        var palFields = palSprite != null ? StructFields(infoText, palSprite.InfoSymbol) : NewPaletteFields(infoText, sprites);
        var sizeFields = StructFields(infoText, sizeSprite.InfoSymbol);
        string F(Dictionary<string, string> d, string k, string def) => d.TryGetValue(k, out var v) ? v : def;
        int frames = check.Frames;
        var body = new StringBuilder();
        body.Append($"const struct ObjectEventGraphicsInfo {info} = {{\n");
        void Field(string k, string v) => body.Append($"    .{k} = {v},\n");
        Field("tileTag", "TAG_NONE");
        Field("paletteTag", palTag);
        Field("reflectionPaletteTag", F(palFields, "reflectionPaletteTag", "OBJ_EVENT_PAL_TAG_NONE"));
        Field("size", (req.FrameWidth * req.FrameHeight / 2).ToString());
        Field("width", req.FrameWidth.ToString());
        Field("height", req.FrameHeight.ToString());
        Field("paletteSlot", F(palFields, "paletteSlot", "PALSLOT_NPC_1"));
        Field("shadowSize", F(sizeFields, "shadowSize", "SHADOW_SIZE_M"));
        Field("inanimate", frames == 1 ? "TRUE" : "FALSE");
        Field("compressed", "FALSE");
        Field("tracks", F(sizeFields, "tracks", "TRACKS_FOOT"));
        Field("oam", F(sizeFields, "oam", "&gObjectEventBaseOam_16x32"));
        Field("subspriteTables", F(sizeFields, "subspriteTables", "sOamTables_16x32"));
        Field("anims", frames == 1 ? "sAnimTable_Inanimate" : frames == OverworldSprites.StandardFrames ? "sAnimTable_Standard" : F(sizeFields, "anims", "sAnimTable_Standard"));
        Field("images", table);
        Field("affineAnims", "gDummySpriteAffineAnimTable");
        body.Append("};\n");

        // --- Text edits
        var files = new List<PlannedFile> { new(pngRel, PlannedAction.Create, pngBytes, $"Sprite sheet {png.Width}×{png.Height}, {frames} frames, indexed to {palTag}") };
        if (newPal) files.Add(new PlannedFile(palRel, PlannedAction.Create, Encoding.ASCII.GetBytes(JascPalette.Serialize(pal!)), $"New palette ({map.Count} colors + transparency)"));
        PlannedFile Modify(string rel, string newText, string desc)
        {
            var original = File.ReadAllBytes(ProjectPaths.Combine(root, rel));
            return new PlannedFile(rel, PlannedAction.Modify, new UTF8Encoding(false).GetBytes(newText), desc, Hashing.Sha256(original));
        }

        // Constant and count
        string consts = Read(ConstantsFile);
        var num = Regex.Match(consts, @"^(#define\s+NUM_OBJ_EVENT_GFX\s+)(\d+)", RegexOptions.Multiline);
        var numEnum = Regex.Match(consts, @"^([ \t]*)NUM_OBJ_EVENT_GFX[ \t]*,", RegexOptions.Multiline);
        int id;
        if (num.Success)
        {
            id = int.Parse(num.Groups[2].Value);
            var lastNumbered = Regex.Matches(consts[..num.Index], @"^#define\s+OBJ_EVENT_GFX_\w+\s+\d+[^\n]*\n", RegexOptions.Multiline).LastOrDefault()
                               ?? throw new ExportValidationException(["No numbered OBJ_EVENT_GFX constants found."]);
            int column = Regex.Match(lastNumbered.Value, @"^#define\s+\w+\s+(\d+)").Groups[1].Index; // align like the existing constants
            string head = $"#define {constant}";
            string newConst = (head.Length < column ? head.PadRight(column) : head + " ") + id + "\n";
            consts = consts.Insert(lastNumbered.Index + lastNumbered.Length, newConst);
            consts = Regex.Replace(consts, @"^(#define\s+NUM_OBJ_EVENT_GFX\s+)\d+", m => m.Groups[1].Value + (id + 1), RegexOptions.Multiline);
        }
        else if (numEnum.Success)
        {
            // expansion 1.13+: enum ObjEventGfx { …, NUM_OBJ_EVENT_GFX, } – the new enumerator goes directly before the count.
            var defines = new CDefines();
            defines.AddFromText(consts);
            id = (int)(defines.TryEvaluate("NUM_OBJ_EVENT_GFX") ?? throw new ExportValidationException(["The value of NUM_OBJ_EVENT_GFX could not be determined."]));
            consts = consts.Insert(numEnum.Index, numEnum.Groups[1].Value + constant + ",\n");
        }
        else throw new ExportValidationException(["NUM_OBJ_EVENT_GFX not found."]);
        string tagNote = "";
        if (newPal)
        {
            // Regular object event palette tags live in 0x1100…0x11FD (0x11FF = NONE, 0x11FE = WHITE); special ones (emotes …) elsewhere.
            var tags = Regex.Matches(consts, @"^#define\s+(OBJ_EVENT_PAL_TAG_\w+)\s+0x([0-9A-Fa-f]+)", RegexOptions.Multiline)
                .Select(m => (Name: m.Groups[1].Value, Value: Convert.ToInt32(m.Groups[2].Value, 16))).ToList();
            var regular = tags.Where(x => x.Value is >= 0x1100 and <= 0x11FD).ToList();
            if (regular.Count == 0) throw new ExportValidationException(["No OBJ_EVENT_PAL_TAG_ constants in 0x1100…0x11FD found."]);
            var used = tags.Select(x => x.Value).ToHashSet();
            int tagValue = regular.Max(x => x.Value) + 1;
            if (tagValue > 0x11FD || used.Contains(tagValue))
                tagValue = Enumerable.Range(0x1100, 0xFE).FirstOrDefault(v => !used.Contains(v), -1);
            if (tagValue < 0) throw new ExportValidationException(["No free object event palette tag left in 0x1100…0x11FD."]);
            // After the highest regular tag defined outside #if, aligned like it.
            var anchor = regular.Where(x => x.Value < tagValue).OrderByDescending(x => x.Value).Select(x => x.Name)
                .FirstOrDefault(n => { try { InsertAfterLastTopLevel(consts, @"^#define\s+" + n + @"\s", ""); return true; } catch (ExportValidationException) { return false; } })
                ?? regular[0].Name;
            var anchorLine = Regex.Match(consts, @"^#define\s+" + anchor + @"\s+0x", RegexOptions.Multiline);
            int tagColumn = anchorLine.Length - 2;
            string tagHead = $"#define {palTag}";
            consts = InsertAfterLastTopLevel(consts, @"^#define\s+" + anchor + @"\s",
                (tagHead.Length < tagColumn ? tagHead.PadRight(tagColumn) : tagHead + " ") + $"0x{tagValue:X4}\n");
            tagNote = $", {palTag} = 0x{tagValue:X4}";
        }
        files.Add(Modify(ConstantsFile, consts, $"{constant} = {id}, NUM_OBJ_EVENT_GFX {id} → {id + 1}{tagNote}"));

        string gfxText = Read(GraphicsFile);
        bool incGfx = GraphicsIncludes.UsesIncGfx(gfxText);
        string mwh = $"-mwidth {req.FrameWidth / 8} -mheight {req.FrameHeight / 8}";
        gfxText = InsertAfterLastTopLevel(gfxText, @"^const u32 gObjectEventPic_\w+\[\]", incGfx
            ? $"const u32 {pic}[] = {GraphicsInclude.Format(true, 32, pngRel, ".4bpp", mwh)};\n"
            : $"const u32 {pic}[] = INCBIN_U32(\"graphics/object_events/pics/people/{file}.4bpp\");\n");
        if (newPal)
            gfxText = InsertAfterLastTopLevel(gfxText, @"^const u16 gObjectEventPal_\w+\[\]", incGfx
                ? $"const u16 {palSym}[] = {GraphicsInclude.Format(true, 16, palRel, ".gbapal")};\n"
                : $"const u16 {palSym}[] = INCBIN_U16(\"{palRel[..^4]}.gbapal\");\n");
        files.Add(Modify(GraphicsFile, gfxText, newPal ? $"{(incGfx ? "INCGFX" : "INCBIN")} {pic} and {palSym}" : $"{(incGfx ? "INCGFX" : "INCBIN")} {pic}"));
        if (newPal)
            files.Add(Modify(MovementFile, InsertIntoPaletteTable(Read(MovementFile), palSym, palTag), $"Palette table entry {{{palSym}, {palTag}}}"));
        files.Add(Modify(PicTablesFile, InsertAfterLastTopLevelBlock(Read(PicTablesFile), @"^static const struct SpriteFrameImage sPicTable_\w+\[\]",
            $"\nstatic const struct SpriteFrameImage {table}[] = {{\n    overworld_ascending_frames({pic}, {req.FrameWidth / 8}, {req.FrameHeight / 8}),\n}};\n"), $"Frame table {table}"));
        files.Add(Modify(InfoFile, InsertAfterLastTopLevelBlock(infoText, @"^const struct ObjectEventGraphicsInfo gObjectEventGraphicsInfo_\w+ =", "\n" + body),
            $"Graphics info {info} (palette {palTag}, {req.FrameWidth}×{req.FrameHeight})"));
        string pointers = Read(PointersFile);
        pointers = InsertAfterLastTopLevel(pointers, @"^extern const struct ObjectEventGraphicsInfo gObjectEventGraphicsInfo_\w+;", $"extern const struct ObjectEventGraphicsInfo {info};\n");
        var lastEntry = Regex.Matches(pointers, @"^\s*\[OBJ_EVENT_GFX_\w+\]\s*=\s*&gObjectEventGraphicsInfo_\w+,[^\n]*\n", RegexOptions.Multiline).LastOrDefault()
                        ?? throw new ExportValidationException(["Pointer table gObjectEventGraphicsInfoPointers not found."]);
        pointers = pointers.Insert(lastEntry.Index + lastEntry.Length, $"    [{constant}] = &{info},\n");
        files.Add(Modify(PointersFile, pointers, $"Pointer [{constant}] and extern declaration"));
        if (!incGfx)
        {
            var rules = Read(RulesFile);
            if (!rules.EndsWith('\n')) rules += "\n";
            rules += $"\n$(OBJEVENTGFXDIR)/people/{file}.4bpp: %.4bpp: %.png\n\t$(GFX) $< $@ {mwh}\n";
            files.Add(Modify(RulesFile, rules, $"gbagfx rule for {file}.4bpp ({mwh})"));
        }

        var plan = ExportPlan.ForFiles(root, $"New overworld sprite {constant}", "graphics/object_events/pics/people", files);
        plan.Notes.Add(newPal
            ? $"The sprite gets its own new palette {palTag} ({palRel}) made from the sheet's colors."
            : $"The sprite uses the existing palette {req.PaletteTag}; its colors were mapped to that palette's entries.");
        plan.Notes.Add($"Afterwards {constant} can be chosen as a sprite in Porymap and in the toolkit (New person) – and in-game after the next build.");
        if (changes.Count > 0) plan.Notes.Add($"{changes.Count} color(s) were replaced by the closest palette color.");
        return new SpriteImportResult(plan, changes, constant);
    }

    /// <summary>16-color palette from the sheet: slot 0 = transparent color, then the distinct colors (GBA-snapped).</summary>
    public static Rgba32[] PaletteFromSheet(PngImage png)
    {
        var px = png.Rgba.Pixels;
        Rgba32 transparent = png.IsIndexed && png.Palette is { Length: > 0 } p0 ? p0[0].Opaque : px[0].A == 0 ? Rgba32.Magenta : px[0].Opaque;
        var colors = new List<Rgba32>();
        for (int i = 0; i < px.Length; i++)
        {
            bool isTransparent = png.IsIndexed ? png.Indices![i] == 0 || px[i].A == 0 : px[i].A == 0 || px[i].SameRgb(px[0]);
            if (isTransparent) continue;
            var c = GbaColor.Snap(px[i].Opaque);
            if (!colors.Contains(c)) colors.Add(c);
        }
        if (colors.Count > 15) throw new ExportValidationException([$"The sheet has {colors.Count} colors; a palette holds 15 plus transparency."]);
        var pal = new Rgba32[16];
        pal[0] = GbaColor.Snap(transparent);
        for (int i = 1; i < 16; i++) pal[i] = i - 1 < colors.Count ? colors[i - 1] : new Rgba32(0, 0, 0);
        return pal;
    }

    /// <summary>reflectionPaletteTag / paletteSlot for a sprite with its own palette: as the branch's other sprites with their own palettes.</summary>
    private static Dictionary<string, string> NewPaletteFields(string infoText, IReadOnlyList<OverworldSprite> sprites)
    {
        string[] shared = ["OBJ_EVENT_PAL_TAG_NPC_1", "OBJ_EVENT_PAL_TAG_NPC_2", "OBJ_EVENT_PAL_TAG_NPC_3", "OBJ_EVENT_PAL_TAG_NPC_4"];
        var slot = sprites.Where(s => !shared.Contains(s.PaletteTag)).TakeLast(20)
            .Select(s => StructFields(infoText, s.InfoSymbol).GetValueOrDefault("paletteSlot")).OfType<string>()
            .GroupBy(v => v).OrderByDescending(g => g.Count()).Select(g => g.Key).FirstOrDefault() ?? "PALSLOT_NPC_2";
        return new() { ["reflectionPaletteTag"] = "OBJ_EVENT_PAL_TAG_NONE", ["paletteSlot"] = slot };
    }

    /// <summary>Adds "{gObjectEventPal_X, OBJ_EVENT_PAL_TAG_X}," to sObjectEventSpritePalettes, after its last entry outside #if.</summary>
    public static string InsertIntoPaletteTable(string text, string palSym, string palTag)
    {
        int start = text.IndexOf("sObjectEventSpritePalettes[]", StringComparison.Ordinal);
        if (start < 0) throw new ExportValidationException(["sObjectEventSpritePalettes not found."]);
        int open = text.IndexOf('{', start);
        int close = text.IndexOf("\n};", open, StringComparison.Ordinal);
        var segment = text[open..close];
        var sample = Regex.Matches(segment, @"^(\s*\{gObjectEventPal_\w+,\s*)OBJ_EVENT_PAL_TAG_", RegexOptions.Multiline).LastOrDefault()
                     ?? throw new ExportValidationException(["No entries found in sObjectEventSpritePalettes."]);
        string head = $"    {{{palSym},";
        int column = sample.Groups[1].Value.TrimStart('\n').Length;
        string line = (head.Length < column ? head.PadRight(column) : head + " ") + palTag + "},\n";
        return text[..open] + InsertAfterLastTopLevel(segment, @"^\s*\{gObjectEventPal_\w+,", line) + text[close..];
    }

    private static int Dist(Rgba32 a, Rgba32 b) => (a.R - b.R) * (a.R - b.R) + (a.G - b.G) * (a.G - b.G) + (a.B - b.B) * (a.B - b.B);

    /// <summary>Field values of a struct initializer (".name = value,").</summary>
    private static Dictionary<string, string> StructFields(string text, string symbol)
    {
        var m = Regex.Match(text, Regex.Escape(symbol) + @"\s*=\s*\{(.*?)\};", RegexOptions.Singleline);
        return m.Success
            ? Regex.Matches(m.Groups[1].Value, @"\.(\w+)\s*=\s*([^,\n]+),").ToDictionary(x => x.Groups[1].Value, x => x.Groups[2].Value.Trim())
            : [];
    }

    /// <summary>Line ranges at preprocessor depth 0 (outside #if/#ifdef … #endif).</summary>
    private static bool[] TopLevelLines(string[] lines)
    {
        // The include guard (#ifndef GUARD_…) does not count as a condition.
        var top = new bool[lines.Length];
        var stack = new Stack<bool>(); // true = include guard
        for (int i = 0; i < lines.Length; i++)
        {
            var l = lines[i].TrimStart();
            if (Regex.IsMatch(l, @"^#\s*(if|ifdef|ifndef)\b"))
                stack.Push(stack.Count == 0 && Regex.IsMatch(l, @"^#\s*ifndef\s+GUARD_\w+"));
            top[i] = stack.All(g => g);
            if (Regex.IsMatch(l, @"^#\s*endif\b") && stack.Count > 0) stack.Pop();
        }
        return top;
    }

    /// <summary>Inserts after the last line matching <paramref name="pattern"/> that is not inside an #if block.</summary>
    public static string InsertAfterLastTopLevel(string text, string pattern, string insert)
    {
        var lines = text.Split('\n');
        var top = TopLevelLines(lines);
        for (int i = lines.Length - 1; i >= 0; i--)
            if (top[i] && Regex.IsMatch(lines[i], pattern))
                return string.Join('\n', lines.Take(i + 1)) + "\n" + insert.TrimEnd('\n') + "\n" + string.Join('\n', lines.Skip(i + 1));
        throw new ExportValidationException([$"Insertion point ({pattern}) not found."]);
    }

    /// <summary>Inserts after the "};" that closes the last top-level block starting with <paramref name="pattern"/>.</summary>
    public static string InsertAfterLastTopLevelBlock(string text, string pattern, string insert)
    {
        var lines = text.Split('\n');
        var top = TopLevelLines(lines);
        for (int i = lines.Length - 1; i >= 0; i--)
        {
            if (!top[i] || !Regex.IsMatch(lines[i], pattern)) continue;
            int end = i;
            while (end < lines.Length && lines[end].TrimEnd() != "};") end++;
            if (end >= lines.Length) break;
            return string.Join('\n', lines.Take(end + 1)) + "\n" + insert.TrimEnd('\n') + "\n" + string.Join('\n', lines.Skip(end + 1));
        }
        throw new ExportValidationException([$"Insertion point ({pattern}) not found."]);
    }
}
