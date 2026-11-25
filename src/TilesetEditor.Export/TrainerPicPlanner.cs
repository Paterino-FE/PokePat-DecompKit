using System.Text;
using System.Text.RegularExpressions;
using TilesetEditor.Core.Gba;
using TilesetEditor.Core.Imaging;
using TilesetEditor.Core.Project;
using TilesetEditor.Core.Sprites;

namespace TilesetEditor.Export;

public sealed record TrainerPicResult(ExportPlan Plan, string Constant, Rgba32[] Palette);

/// <summary>
/// Registers a new trainer battle picture (TRAINER_PIC_X): 64×64 PNG with its own palette, the constant (raising
/// TRAINER_PIC_COUNT), the graphics declarations and the table entry – the same pattern as the branch's own pictures:
/// <list type="bullet">
/// <item>up to expansion 1.12: #define + INCBIN + TRAINER_SPRITE(…) in gTrainerSprites</item>
/// <item>expansion 1.13+: enumerator in enum TrainerPic + INCGFX + [TRAINER_PIC_X] = { .frontPic = TRAINER_FRONT_PIC(…) } in gTrainerPicInfo</item>
/// </list>
/// </summary>
public static partial class TrainerPicPlanner
{
    public const int Size = 64;
    public const string FrontDir = "graphics/trainers/front_pics";
    public const string PaletteDir = "graphics/trainers/palettes";

    [GeneratedRegex("^[A-Z][A-Za-z0-9]*$")]
    private static partial Regex NameRegex();

    public static string ToConstant(string name) => "TRAINER_PIC_" + Regex.Replace(name, "([a-z0-9])([A-Z])", "$1_$2").ToUpperInvariant();

    /// <summary>Checks a candidate image: 64×64, at most 15 colors plus transparency (first pixel or alpha 0).</summary>
    public static List<string> Check(PngImage png)
    {
        var errors = new List<string>();
        if (png.Width != Size || png.Height != Size) errors.Add($"A trainer picture must be {Size}×{Size} pixels (this one is {png.Width}×{png.Height}).");
        try { SpriteImportPlanner.PaletteFromSheet(png); }
        catch (ExportValidationException ex) { errors.AddRange(ex.Errors); }
        return errors;
    }

    public static TrainerPicResult Plan(string root, string name, string pngPath)
    {
        var errors = new List<string>();
        name = name.Trim();
        if (!NameRegex().IsMatch(name)) errors.Add("Name: start with an uppercase letter, letters and digits only (e.g. Guard or SeaCaptain).");
        string constant = ToConstant(name), file = SpriteImportPlanner.ToFileName(name);
        string frontSym = $"gTrainerFrontPic_{name}", palSym = $"gTrainerPalette_{name}";
        string pngRel = $"{FrontDir}/{file}.png", palRel = $"{PaletteDir}/{file}.pal";
        string Read(string rel) => File.ReadAllText(ProjectPaths.Combine(root, rel));
        var consts = Read(CharacterGraphics.TrainerConstantsFile);
        var gfx = Read(CharacterGraphics.TrainerGraphicsFile);
        if (Regex.IsMatch(consts, @"\b" + constant + @"\b")) errors.Add($"{constant} already exists.");
        if (Regex.IsMatch(gfx, @"\b(" + frontSym + "|" + palSym + @")\b")) errors.Add($"{frontSym} or {palSym} is already taken.");
        if (File.Exists(ProjectPaths.Combine(root, pngRel))) errors.Add($"{pngRel} already exists.");
        if (File.Exists(ProjectPaths.Combine(root, palRel))) errors.Add($"{palRel} already exists.");
        var png = Png.Load(pngPath);
        errors.AddRange(Check(png));
        var count = Regex.Match(consts, @"^([ \t]*#define[ \t]+TRAINER_PIC_COUNT[ \t]+)(\d+)", RegexOptions.Multiline);
        var countEnum = Regex.Match(consts, @"^([ \t]*)TRAINER_PIC_COUNT[ \t]*,", RegexOptions.Multiline);
        bool picInfo = CharacterGraphics.UsesPicInfo(gfx);
        bool incGfx = GraphicsIncludes.UsesIncGfx(gfx);
        if (!count.Success && !countEnum.Success) errors.Add($"TRAINER_PIC_COUNT (#define or enum) was not found in {CharacterGraphics.TrainerConstantsFile}.");
        if (!picInfo && !gfx.Contains("gTrainerSprites[]", StringComparison.Ordinal))
            errors.Add($"Neither gTrainerSprites nor gTrainerPicInfo was found in {CharacterGraphics.TrainerGraphicsFile}.");
        if (errors.Count > 0) throw new ExportValidationException(errors);

        // Image: own palette, index 0 transparent
        var pal = SpriteImportPlanner.PaletteFromSheet(png);
        var px = png.Rgba.Pixels;
        var indices = new byte[px.Length];
        for (int i = 0; i < px.Length; i++)
        {
            bool transparent = png.IsIndexed ? png.Indices![i] == 0 || px[i].A == 0 : px[i].A == 0 || px[i].SameRgb(px[0]);
            indices[i] = transparent ? (byte)0 : (byte)Array.IndexOf(pal, GbaColor.Snap(px[i].Opaque), 1);
        }
        var files = new List<PlannedFile>
        {
            new(pngRel, PlannedAction.Create, Png.EncodeIndexed(Size, Size, indices, pal, 4), "Trainer picture 64×64, 4-bit indexed"),
            new(palRel, PlannedAction.Create, Encoding.ASCII.GetBytes(JascPalette.Serialize(pal)), "Palette of the picture"),
        };
        PlannedFile Modify(string rel, string text, string desc) =>
            new(rel, PlannedAction.Modify, new UTF8Encoding(false).GetBytes(text), desc, Hashing.Sha256(File.ReadAllBytes(ProjectPaths.Combine(root, rel))));

        int id;
        if (count.Success)
        {
            // Constant before TRAINER_PIC_COUNT, aligned like it; raise the count.
            id = int.Parse(count.Groups[2].Value);
            int column = count.Groups[1].Value.TrimStart().Length;
            string head = $"#define {constant}";
            string line = (head.Length < column ? head.PadRight(column) : head + " ") + id + "\n";
            int lineStart = consts.LastIndexOf('\n', count.Index) + 1;
            consts = consts[..lineStart] + line + consts[lineStart..];
            consts = Regex.Replace(consts, @"^([ \t]*#define[ \t]+TRAINER_PIC_COUNT[ \t]+)\d+", m => m.Groups[1].Value + (id + 1), RegexOptions.Multiline);
        }
        else
        {
            // Enumerator directly before TRAINER_PIC_COUNT (which then grows by one by itself).
            var defines = new CDefines();
            defines.AddFromText(consts);
            id = (int)(defines.TryEvaluate("TRAINER_PIC_COUNT") ?? throw new ExportValidationException(["The value of TRAINER_PIC_COUNT could not be determined."]));
            consts = consts[..countEnum.Index] + countEnum.Groups[1].Value + constant + ",\n" + consts[countEnum.Index..];
        }
        files.Add(Modify(CharacterGraphics.TrainerConstantsFile, consts, $"{constant} = {id}, TRAINER_PIC_COUNT {id} → {id + 1}"));

        // Declarations after the last front picture/palette pair, in the file's style.
        var decls = GraphicsIncludes.Declarations(gfx).ToList();
        var lastFrontDecl = decls.LastOrDefault(d => d.Symbol.StartsWith("gTrainerFrontPic_", StringComparison.Ordinal))
            ?? throw new ExportValidationException(["No gTrainerFrontPic_ declaration found."]);
        var lines = gfx.Split('\n').ToList();
        int lastFront = gfx[..lastFrontDecl.Index].Count(c => c == '\n');
        int after = lastFront + 1 < lines.Count && Regex.IsMatch(lines[lastFront + 1], @"^const u(16|32) gTrainerPalette_") ? lastFront + 1 : lastFront;
        string frontExt = lastFrontDecl.Include.OutputExtension;
        lines.InsertRange(after + 1, incGfx
            ?
            [
                $"const u32 {frontSym}[] = {GraphicsInclude.Format(true, 32, pngRel, frontExt.Length > 0 ? frontExt : ".4bpp.smol")};",
                $"const u16 {palSym}[] = {GraphicsInclude.Format(true, 16, palRel, ".gbapal")};",
            ]
            :
            [
                $"const u32 {frontSym}[] = INCBIN_U32(\"{FrontDir}/{file}.4bpp.lz\");",
                $"const u32 {palSym}[] = INCBIN_U32(\"{PaletteDir}/{file}.gbapal.lz\");",
            ]);
        gfx = string.Join('\n', lines);
        int arr = gfx.IndexOf(picInfo ? "gTrainerPicInfo[" : "gTrainerSprites[]", StringComparison.Ordinal);
        int open = gfx.IndexOf('{', arr);
        int close = gfx.IndexOf("\n};", open, StringComparison.Ordinal);
        if (picInfo)
        {
            gfx = gfx[..(close + 1)] + $"    [{constant}] =\n    {{\n        .frontPic = TRAINER_FRONT_PIC({frontSym}, {palSym}),\n    }},\n" + gfx[(close + 1)..];
            files.Add(Modify(CharacterGraphics.TrainerGraphicsFile, gfx, $"{frontSym}, {palSym} and gTrainerPicInfo[{constant}]"));
        }
        else
        {
            var segment = gfx[open..close];
            segment = SpriteImportPlanner.InsertAfterLastTopLevel(segment, @"^\s*TRAINER_SPRITE\(", $"    TRAINER_SPRITE({constant}, {frontSym}, {palSym}),\n");
            gfx = gfx[..open] + segment + gfx[close..];
            files.Add(Modify(CharacterGraphics.TrainerGraphicsFile, gfx, $"INCBIN {frontSym}, {palSym} and TRAINER_SPRITE({constant})"));
        }

        var plan = ExportPlan.ForFiles(root, $"New trainer picture {constant}", FrontDir, files);
        plan.Notes.Add($"Afterwards {constant} can be chosen as \"Picture\" in the Trainers module.");
        plan.Notes.Add("The picture gets its own palette made from its colors (index 0 = transparent).");
        return new TrainerPicResult(plan, constant, pal);
    }
}
