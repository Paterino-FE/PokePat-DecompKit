using System.Text;
using System.Text.RegularExpressions;
using TilesetEditor.Core.Gba;
using TilesetEditor.Core.Imaging;
using TilesetEditor.Core.Project;
using TilesetEditor.Core.SecretBases;
using TilesetEditor.Core.Sprites;

namespace TilesetEditor.Export;

/// <summary>A new sprite decoration (doll/cushion style): it appears as an overworld object when placed in a secret base.</summary>
/// <param name="Name">Constant part, e.g. MEW_DOLL → DECOR_MEW_DOLL.</param>
/// <param name="DisplayName">In-game name (max. 15 characters).</param>
/// <param name="Category">DECORCAT_DOLL or DECORCAT_CUSHION (only these two are placed as objects with interaction scripts).</param>
/// <param name="Shape">DECORSHAPE_… (as the existing dolls: small 1x1, big ones as in the branch).</param>
/// <param name="GfxConstant">Existing OBJ_EVENT_GFX_… (ignored when <paramref name="NewSprite"/> is set).</param>
/// <param name="IconPngPath">Optional 24×24 menu icon; without it the menu shows the object sprite (like the small dolls).</param>
/// <param name="NewSprite">Registers a new overworld sprite first (SpriteImportPlanner) and uses it.</param>
public sealed record NewDecorationRequest(string Name, string DisplayName, string Category, string Shape, int Price, string Description,
    string? GfxConstant, string? IconPngPath = null, SpriteImportRequest? NewSprite = null);

public sealed record DecorationPlanResult(ExportPlan Plan, string Constant, int Id, IReadOnlyList<(Rgba32 From, Rgba32 To)> ColorChanges);

/// <summary>
/// Plans a new sprite decoration: constant + NUM_DECORATIONS, gDecorations entry, description, DecorGfx (= the OBJ_EVENT_GFX id,
/// which InitSecretBaseDecorationSprites writes into VAR_OBJ_GFX_ID_x), gDecorIconTable entry and optionally an icon
/// (PNG + INCBIN + extern) and a new overworld sprite. expansion 1.16+: description and icon go into the gDecorations entry.
/// </summary>
public static partial class DecorationPlanner
{
    public static readonly string[] SpriteCategories = ["DECORCAT_DOLL", "DECORCAT_CUSHION"];

    [GeneratedRegex("^[A-Z][A-Z0-9_]*$")]
    private static partial Regex ConstantName();

    public static string ToPascal(string constantPart) =>
        string.Concat(constantPart.Split('_', StringSplitOptions.RemoveEmptyEntries).Select(p => p[0] + p[1..].ToLowerInvariant()));

    public static DecorationPlanResult Plan(string root, NewDecorationRequest req, DecorationData data,
        IReadOnlyList<OverworldSprite> sprites, IReadOnlyDictionary<string, Rgba32[]> palettes)
    {
        var errors = new List<string>();
        var name = req.Name.Trim().ToUpperInvariant();
        if (name.StartsWith("DECOR_", StringComparison.Ordinal)) name = name[6..];
        if (!ConstantName().IsMatch(name)) errors.Add("Constant: uppercase letters, digits and _ only, starting with a letter (e.g. MEW_DOLL).");
        string constant = "DECOR_" + name, pascal = ToPascal(name);
        string desc = $"DecorDesc_{name}", gfxSym = $"DecorGfx_{name}";
        if (data.Items.Any(d => d.Constant == constant)) errors.Add($"{constant} already exists.");
        if (Decorations.CheckName(req.DisplayName) is { } nameProblem) errors.Add("Name: " + nameProblem);
        if (Decorations.CheckDescription(req.Description) is { } descProblem) errors.Add("Description: " + descProblem);
        if (req.Price is < 0 or > 65535) errors.Add("Price: 0 … 65535.");
        if (!SpriteCategories.Contains(req.Category)) errors.Add("Category: only DECORCAT_DOLL or DECORCAT_CUSHION can be a sprite decoration.");
        if (!Regex.IsMatch(req.Shape, @"^DECORSHAPE_\d+x\d+$")) errors.Add("Shape: DECORSHAPE_WxH expected.");
        int id = data.MaxId + 1;
        if (id > Decorations.MaxId) errors.Add($"The new id would be {id}; decoration ids are stored as u8 (max. {Decorations.MaxId}).");
        string Read(string rel) => File.ReadAllText(ProjectPaths.Combine(root, rel));
        bool consolidated = data.Consolidated;
        foreach (var (rel, sym) in new[] { (Decorations.DescriptionFile, desc), (Decorations.TilesFile, gfxSym), (Decorations.ConstantsFile, constant) })
            if ((!consolidated || rel != Decorations.DescriptionFile) && Regex.IsMatch(Read(rel), @"\b" + sym + @"\b")) errors.Add($"{sym} is already taken in {rel}.");
        if (req.NewSprite == null && (req.GfxConstant == null || sprites.All(s => s.GfxId != req.GfxConstant)))
            errors.Add($"Overworld sprite {req.GfxConstant ?? "(none)"} was not found.");
        string? iconRel = req.IconPngPath != null ? $"{Decorations.IconDir}/{name.ToLowerInvariant()}.png" : null;
        if (iconRel != null && File.Exists(ProjectPaths.Combine(root, iconRel))) errors.Add($"{iconRel} already exists.");
        if (errors.Count > 0) throw new ExportValidationException(errors);

        var files = new List<PlannedFile>();
        var notes = new List<string>();
        var colorChanges = new List<(Rgba32, Rgba32)>();
        string gfx = req.GfxConstant!;
        if (req.NewSprite != null)
        {
            var sprite = SpriteImportPlanner.Plan(root, req.NewSprite, sprites, palettes);
            files.AddRange(sprite.Plan.Files);
            notes.AddRange(sprite.Plan.Notes);
            colorChanges.AddRange(sprite.ColorChanges);
            gfx = sprite.Constant;
        }

        PlannedFile Modify(string rel, string newText, string description)
        {
            var original = File.ReadAllBytes(ProjectPaths.Combine(root, rel));
            return new PlannedFile(rel, PlannedAction.Modify, new UTF8Encoding(false).GetBytes(newText), description, Hashing.Sha256(original));
        }

        // 1. Constant + NUM_DECORATIONS
        string consts = Read(Decorations.ConstantsFile);
        files.Add(Modify(Decorations.ConstantsFile, AddConstant(consts, constant, id), $"{constant} = {id}, NUM_DECORATIONS → {constant}"));

        // 2. Header entry (fields in the order of the existing entries)
        string header = Read(Decorations.HeaderFile);
        string nl = header.Contains("\r\n") ? "\r\n" : "\n";
        string pic = "NULL", pal = "NULL";
        if (req.IconPngPath != null)
        {
            pic = $"gDecorIcon_{pascal}";
            pal = $"gDecorIconPalette_{pascal}";
        }
        var lines = new List<string>
        {
            "    [" + constant + "] =",
            "    {",
            $"        .id = {constant},",
            $"        .name = _(\"{req.DisplayName}\"),",
            "        .permission = DECORPERM_SPRITE,",
        };
        lines.Add($"        .shape = {req.Shape},");
        lines.Add($"        .category = {req.Category},");
        lines.Add($"        .price = {req.Price},");
        if (consolidated)
            lines.Add("        .description = COMPOUND_STRING(" + nl + Decorations.FormatLiterals(req.Description, "            ", "            ", nl) + "),");
        else
            lines.Add($"        .description = {desc},");
        lines.Add($"        .tiles = {gfxSym},");
        if (consolidated) lines.Add($"        .icon = {{{pic}, {pal}}},");
        lines.Add("    },");
        files.Add(Modify(Decorations.HeaderFile, Decorations.AppendToArray(header, "gDecorations", string.Join(nl, lines), true),
            $"gDecorations[{constant}]: \"{req.DisplayName}\", {req.Category.Replace("DECORCAT_", "")}, {req.Shape.Replace("DECORSHAPE_", "")}, price {req.Price}"));

        // 3. Description (own file up to expansion 1.15)
        if (!consolidated)
        {
            string descText = Read(Decorations.DescriptionFile);
            string dnl = descText.Contains("\r\n") ? "\r\n" : "\n";
            var descBlock = $"const u8 {desc}[] = _({dnl}" + Decorations.FormatLiterals(req.Description, "    ", "        ", dnl) + ");" + dnl;
            files.Add(Modify(Decorations.DescriptionFile, AppendBlock(descText, descBlock), $"{desc}"));
        }

        // 4. Graphics: the object event graphics id
        string tiles = Read(Decorations.TilesFile);
        string tnl = tiles.Contains("\r\n") ? "\r\n" : "\n";
        files.Add(Modify(Decorations.TilesFile, AppendBlock(tiles, $"const u16 {gfxSym}[] = {{{tnl}    {gfx}{tnl}}};{tnl}"), $"{gfxSym} = {{{gfx}}}"));

        // 5. Optional icon (+ icon table up to expansion 1.15)
        if (req.IconPngPath != null)
        {
            var (pngBytes, iconNote) = ConvertIcon(req.IconPngPath);
            files.Add(new PlannedFile(iconRel!, PlannedAction.Create, pngBytes, "Menu icon 24×24 (" + iconNote + ")"));
            string gfxText = Read(Decorations.IconGraphicsFile);
            string gnl = gfxText.Contains("\r\n") ? "\r\n" : "\n";
            bool incGfx = GraphicsIncludes.UsesIncGfx(gfxText);
            var (picExt, palExt) = IconExtensions(gfxText);
            string baseRel = iconRel![..^4];
            // expansion 1.13+: INCGFX with the palette taken from the PNG itself; the palette symbol is u16 there.
            string palType = incGfx ? "u16" : "u32";
            string decls = incGfx
                ? $"const u32 {pic}[] = {GraphicsInclude.Format(true, 32, iconRel, picExt)};{gnl}const u16 {pal}[] = {GraphicsInclude.Format(true, 16, iconRel, ".gbapal")};{gnl}"
                : $"const u32 {pic}[] = INCBIN_U32(\"{baseRel}{picExt}\");{gnl}const u32 {pal}[] = INCBIN_U32(\"{baseRel}{palExt}\");{gnl}";
            files.Add(Modify(Decorations.IconGraphicsFile, AppendBlock(gfxText, decls), $"{(incGfx ? "INCGFX" : "INCBIN")} {pic}, {pal}"));
            string graphicsH = Read(Decorations.GraphicsHeaderFile);
            graphicsH = SpriteImportPlanner.InsertAfterLastTopLevel(graphicsH, @"^extern const u(?:16|32) gDecorIconPalette_\w+\[\];",
                $"extern const u32 {pic}[];\nextern const {palType} {pal}[];\n");
            if (Read(Decorations.GraphicsHeaderFile).Contains("\r\n")) graphicsH = Regex.Replace(graphicsH, "(?<!\r)\n", "\r\n");
            files.Add(Modify(Decorations.GraphicsHeaderFile, graphicsH, $"extern {pic}, {pal}"));
            notes.Add("The icon is converted by the Makefile's generic rules (.png → .4bpp/.gbapal → compressed).");
        }
        else notes.Add("Without an own icon the decoration menu shows the overworld sprite (like the small dolls and cushions).");
        if (!consolidated)
        {
            string icons = Read(Decorations.IconTableFile);
            // Decorations without an entry before the new one get {NULL, NULL} implicitly; add them explicitly for clarity.
            icons = Decorations.AddMissingIconEntries(icons, data.Items.Where(d => !d.HasIconEntry));
            icons = Decorations.AppendToArray(icons, "gDecorIconTable", Decorations.IconEntryLine(icons, constant, pic, pal), false);
            var missing = data.Items.Where(d => !d.HasIconEntry).Select(d => d.Constant).ToList();
            files.Add(Modify(Decorations.IconTableFile, icons, $"gDecorIconTable[{constant}] = {{{pic}, {pal}}}" +
                (missing.Count > 0 ? $"; missing entries added: {string.Join(", ", missing)}" : "")));
        }

        var plan = ExportPlan.ForFiles(root, $"New decoration {constant}", "src/data/decoration", files);
        plan.Notes.Add($"{constant} gets id {id}. When it is placed in a secret base, the game shows {gfx} as an object (one of the OBJ_EVENT_GFX_VAR_ persons of the base map).");
        plan.Notes.AddRange(notes);
        plan.Notes.Add($"To obtain it in-game, give it in a script (givedecoration {constant}) or add it to a decoration shop list (pokemartdecoration).");
        plan.Notes.Add("Existing decoration ids do not change; save files stay compatible.");
        return new DecorationPlanResult(plan, constant, id, colorChanges);
    }

    /// <summary>Adds "#define DECOR_X  id" after the last numbered DECOR_ constant and points NUM_DECORATIONS at it.</summary>
    public static string AddConstant(string consts, string constant, int id)
    {
        string nl = consts.Contains("\r\n") ? "\r\n" : "\n";
        var last = Regex.Matches(consts, @"^#define[ \t]+DECOR_\w+[ \t]+(\d+)[^\n]*\n", RegexOptions.Multiline).LastOrDefault()
                   ?? throw new ExportValidationException(["No DECOR_ constants found."]);
        int column = last.Groups[1].Index - last.Index;
        string head = "#define " + constant;
        consts = consts.Insert(last.Index + last.Length, (head.Length < column ? head.PadRight(column) : head + " ") + id + nl);
        var num = Regex.Match(consts, @"^(#define[ \t]+NUM_DECORATIONS[ \t]+)(\w+)", RegexOptions.Multiline);
        if (!num.Success) throw new ExportValidationException(["NUM_DECORATIONS not found."]);
        return consts[..num.Groups[2].Index] + constant + consts[(num.Groups[2].Index + num.Groups[2].Length)..];
    }

    /// <summary>Appends a block at the end of the file, separated by one blank line.</summary>
    private static string AppendBlock(string text, string block)
    {
        string nl = text.Contains("\r\n") ? "\r\n" : "\n";
        var trimmed = text.TrimEnd('\r', '\n');
        return trimmed + nl + nl + block;
    }

    private static (string Pic, string Pal) IconExtensions(string gfxText)
    {
        var decls = GraphicsIncludes.Declarations(gfxText).ToList();
        var pic = decls.LastOrDefault(d => d.Symbol.StartsWith("gDecorIcon_", StringComparison.Ordinal))?.Include.OutputExtension;
        var pal = decls.LastOrDefault(d => d.Symbol.StartsWith("gDecorIconPalette_", StringComparison.Ordinal))?.Include.OutputExtension;
        return (string.IsNullOrEmpty(pic) ? ".4bpp.lz" : pic, string.IsNullOrEmpty(pal) ? ".gbapal.lz" : pal);
    }

    /// <summary>Checks a 24×24 icon and converts it to a 4-bit indexed PNG (index 0 = transparent, max. 15 colors).</summary>
    public static (byte[] Png, string Note) ConvertIcon(string path)
    {
        var png = Png.Load(path);
        if (png.Width != 24 || png.Height != 24)
            throw new ExportValidationException([$"The icon is {png.Width}×{png.Height}; decoration icons are 24×24 pixels."]);
        var pal = SpriteImportPlanner.PaletteFromSheet(png);
        var indices = new byte[png.Width * png.Height];
        int used = 0;
        for (int i = 0; i < indices.Length; i++)
        {
            var px = png.Rgba.Pixels[i];
            bool transparent = png.IsIndexed ? png.Indices![i] == 0 || px.A == 0 : px.A == 0 || px.SameRgb(png.Rgba.Pixels[0]);
            if (transparent) continue;
            var c = GbaColor.Snap(px.Opaque);
            int idx = Array.IndexOf(pal, c, 1);
            indices[i] = (byte)Math.Max(1, idx);
            used = Math.Max(used, idx);
        }
        return (Png.EncodeIndexed(24, 24, indices, pal, 4), $"{used} colors + transparency");
    }
}
