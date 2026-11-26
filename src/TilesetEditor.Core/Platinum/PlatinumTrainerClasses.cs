using System.Text;
using System.Text.RegularExpressions;
using TilesetEditor.Core.Imaging;
using TilesetEditor.Core.Project;

namespace TilesetEditor.Core.Platinum;

/// <summary>What a new trainer class gets: name, picture source and the values the game keeps per class.</summary>
public sealed record NewTrainerClass(string Constant, string Name, string NameWithArticle, string CopyFrom, bool Female, int PrizeMultiplier, RgbaImage? Picture);

/// <summary>
/// Adds trainer classes to pokeplatinum. A class consists of: its constant at the end of generated/trainer_classes.txt
/// (the number decides which graphics and texts the game loads), the folder res/trainers/classes/&lt;name&gt; (front picture,
/// cell/animation data, scan picture and its key – copied from a class with the same layout), an entry in both trainer
/// class text banks, and its gender (battle intro/music) and prize money multiplier in include/data.
/// </summary>
public static partial class PlatinumTrainerClasses
{
    public const string ConstantsFile = "generated/trainer_classes.txt";
    public const string NamesFile = "res/text/trainer_class_names.json";
    public const string ArticlesFile = "res/text/trainer_class_names_with_articles.json";
    public const string GendersFile = "include/data/trainer_class_genders.h";
    public const string PrizeFile = "include/data/trainer_class_prize_mul.h";

    /// <summary>Files of a class folder that are copied (back sprites, mugshots and banners belong to special classes).</summary>
    public static readonly string[] FrontFiles = ["front.png", "front_anim.json", "front_cell.json", "front_scan.png", "front_scan.png.key"];

    public static string Folder(string constant) =>
        $"{PlatinumProject.TrainerClassesDir}/{(constant.StartsWith("TRAINER_CLASS_", StringComparison.Ordinal) ? constant[14..] : constant).ToLowerInvariant()}";

    [GeneratedRegex(@"^TRAINER_CLASS_[A-Z0-9_]+$")]
    private static partial Regex ConstantPattern();

    /// <summary>Gender and prize multiplier of an existing class (defaults when not found).</summary>
    public static (bool Female, int Prize) Values(string root, string constant)
    {
        bool female = false;
        int prize = 8;
        string Read(string f) { var p = ProjectPaths.Combine(root, f); return File.Exists(p) ? File.ReadAllText(p) : ""; }
        var g = Regex.Match(Read(GendersFile), @"\[" + Regex.Escape(constant) + @"\]\s*=\s*(GENDER_\w+)");
        if (g.Success) female = g.Groups[1].Value == "GENDER_FEMALE";
        var m = Regex.Match(Read(PrizeFile), @"\[" + Regex.Escape(constant) + @"\]\s*=\s*(\d+)");
        if (m.Success) prize = int.Parse(m.Groups[1].Value);
        return (female, prize);
    }

    /// <summary>The project's files for the new class (new and changed); throws with a readable message on a problem.</summary>
    public static (List<PlatinumFileChange> Files, List<string> Notes) PlanAdd(string root, NewTrainerClass c)
    {
        var notes = new List<string>();
        if (!ConstantPattern().IsMatch(c.Constant)) throw new InvalidDataException("The constant must look like TRAINER_CLASS_NAME (capital letters, digits, _).");
        if (c.Name.Trim().Length == 0) throw new InvalidDataException("The class needs a name.");
        if (c.PrizeMultiplier is < 0 or > 255) throw new InvalidDataException("The prize money multiplier must be 0–255.");
        string Full(string rel) => ProjectPaths.Combine(root, rel);
        var constants = File.ReadAllLines(Full(ConstantsFile)).Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
        if (constants.Contains(c.Constant)) throw new InvalidDataException($"{c.Constant} already exists.");
        if (!constants.Contains(c.CopyFrom)) throw new InvalidDataException($"{c.CopyFrom} is not a trainer class.");
        var folder = Folder(c.Constant);
        if (Directory.Exists(Full(folder))) throw new InvalidDataException($"{folder} already exists.");
        var source = Folder(c.CopyFrom);
        foreach (var f in FrontFiles)
            if (!File.Exists(Full($"{source}/{f}"))) throw new InvalidDataException($"{source}/{f} is missing – choose another class to copy.");
        if (c.CopyFrom == "TRAINER_CLASS_CASTLE_VALET") throw new InvalidDataException("The Castle Valet's picture is built in a special way – choose another class to copy.");
        int index = constants.Count;

        var files = new List<PlatinumFileChange>();
        foreach (var f in FrontFiles)
        {
            var bytes = File.ReadAllBytes(Full($"{source}/{f}"));
            if (f == "front.png" && c.Picture != null) bytes = FrontPicture(c.Picture, bytes, notes);
            files.Add(new PlatinumFileChange($"{folder}/{f}", bytes, f == "front.png" && c.Picture != null ? "new battle picture" : $"copied from {source}"));
        }
        if (c.Picture == null) notes.Add($"The picture is a copy of {c.CopyFrom}'s – replace it in the Trainers module (\"Replace class picture…\").");
        notes.Add("front_scan.png (a second form of the picture the game uses in some screens) is copied as it is.");

        var utf8 = new UTF8Encoding(false);
        var constText = File.ReadAllText(Full(ConstantsFile));
        files.Add(new PlatinumFileChange(ConstantsFile, utf8.GetBytes((constText.EndsWith('\n') ? constText : constText + "\n") + c.Constant + "\n"),
            $"{c.Constant} = {index} (at the end, so no other class changes its number)"));
        files.Add(new PlatinumFileChange(NamesFile, utf8.GetBytes(AppendMessage(File.ReadAllText(Full(NamesFile)), index, c.Name.Trim())), $"message {index}: \"{c.Name.Trim()}\""));
        files.Add(new PlatinumFileChange(ArticlesFile, utf8.GetBytes(AppendMessage(File.ReadAllText(Full(ArticlesFile)), index, c.NameWithArticle.Trim())),
            $"message {index}: \"{c.NameWithArticle.Trim()}\""));
        files.Add(new PlatinumFileChange(GendersFile, utf8.GetBytes(AppendTableEntry(File.ReadAllText(Full(GendersFile)), c.Constant, c.Female ? "GENDER_FEMALE" : "GENDER_MALE")),
            $"{c.Constant}: {(c.Female ? "female" : "male")} (battle intro and music)"));
        files.Add(new PlatinumFileChange(PrizeFile, utf8.GetBytes(AppendTableEntry(File.ReadAllText(Full(PrizeFile)), c.Constant, c.PrizeMultiplier.ToString())),
            $"{c.Constant}: prize money × {c.PrizeMultiplier}"));
        notes.Add($"Prize money = level of the last Pokémon × 4 × {c.PrizeMultiplier} (twice in double battles).");
        return (files, notes);
    }

    /// <summary>Replaces the battle picture of an existing class.</summary>
    public static (PlatinumFileChange File, List<string> Notes) PlanReplacePicture(string root, string constant, RgbaImage picture)
    {
        var rel = $"{Folder(constant)}/front.png";
        var full = ProjectPaths.Combine(root, rel);
        if (!File.Exists(full)) throw new InvalidDataException($"{rel} does not exist.");
        var notes = new List<string>();
        var bytes = FrontPicture(picture, File.ReadAllBytes(full), notes);
        notes.Add("The cell and animation data stay – they fit a picture with the same layout as before.");
        return (new PlatinumFileChange(rel, bytes, "new battle picture"), notes);
    }

    /// <summary>
    /// An 80×80 picture as the game's front.png: 16 colours (index 0 = transparent, shown in the old picture's background
    /// colour), stored like the original (8-bit PNG with indices 0–15).
    /// </summary>
    public static byte[] FrontPicture(RgbaImage picture, byte[] original, List<string> notes)
    {
        if (picture.Width != 80 || picture.Height != 80)
            throw new InvalidDataException($"The picture is {picture.Width}×{picture.Height}; trainer pictures are 80×80.");
        var old = Png.Decode(original);
        var bg = picture[0, 0];
        var img = new RgbaImage(80, 80);
        for (int i = 0; i < img.Pixels.Length; i++)
        {
            var c = picture.Pixels[i];
            img.Pixels[i] = c.A < 128 || (bg.A == 255 && c.SameRgb(bg)) ? Rgba32.Transparent : c with { A = 255 };
        }
        var reduced = ColorQuantizer.Reduce(img, 15);
        var colors = reduced.Pixels.Where(p => p.A != 0).Select(p => p with { A = 255 }).Distinct().ToList();
        if (img.Pixels.Where(p => p.A != 0).Distinct().Count() > 15) notes.Add("The picture had more than 15 colours; they were reduced to 15.");
        colors = colors.Take(15).ToList();
        var palette = new Rgba32[16];
        palette[0] = old.Palette is { Length: > 0 } p0 ? p0[0] with { A = 255 } : new Rgba32(255, 255, 255);
        for (int i = 0; i < 15; i++) palette[i + 1] = i < colors.Count ? colors[i] : new Rgba32(0, 0, 0);
        var idx = new byte[80 * 80];
        for (int i = 0; i < idx.Length; i++)
        {
            var c = reduced.Pixels[i];
            if (c.A == 0) continue;
            int best = 1; long bestD = long.MaxValue;
            for (int k = 1; k <= colors.Count; k++)
            {
                long d = (long)(palette[k].R - c.R) * (palette[k].R - c.R) + (long)(palette[k].G - c.G) * (palette[k].G - c.G) + (long)(palette[k].B - c.B) * (palette[k].B - c.B);
                if (d < bestD) { bestD = d; best = k; }
            }
            idx[i] = (byte)best;
        }
        return Png.EncodeIndexed(80, 80, idx, palette, old.BitDepth == 4 ? 4 : 8);
    }

    /// <summary>Adds { "id": …, "en_US": … } as message <paramref name="index"/> at the end of a text bank, in the file's format.</summary>
    public static string AppendMessage(string json, int index, string text)
    {
        var spans = JsonText.Locate(json);
        if (!spans.TryGetValue("messages", out var array)) throw new InvalidDataException("The text bank has no \"messages\" list.");
        int count = 0;
        while (spans.ContainsKey($"messages[{count}]")) count++;
        if (count != index)
            throw new InvalidDataException($"The text bank has {count} messages, but the new class gets number {index}. Make both lists match first (one message per trainer class).");
        string id = $"msg_{index}";
        if (count > 0 && JsonText.Display(spans, $"messages[{count - 1}].id") is { } lastId)
        {
            var m = Regex.Match(lastId, @"^(.*?)(\d+)$");
            if (m.Success) id = m.Groups[1].Value + (int.Parse(m.Groups[2].Value) + 1).ToString().PadLeft(m.Groups[2].Value.Length, '0');
        }
        var last = spans[$"messages[{count - 1}]"];
        var indent = JsonText.IndentAt(json, last.Start);
        var inner = indent + "  ";
        var entry = $",\n{indent}{{\n{inner}\"id\": {JsonText.Quote(id)},\n{inner}\"en_US\": {JsonText.Quote(text)}\n{indent}}}";
        int insertAt = last.Start + last.Length;
        return json[..insertAt] + entry + json[insertAt..];
    }

    /// <summary>Adds "[CONSTANT] = value," as the last entry of a designated-initializer table, aligned like the others.</summary>
    public static string AppendTableEntry(string text, string constant, string value)
    {
        var close = Regex.Match(text, @"\n(\s*)\};", RegexOptions.RightToLeft);
        if (!close.Success) throw new InvalidDataException("The end of the table was not found.");
        var lastEntry = Regex.Match(text[..close.Index], @"\n([ \t]*)\[(\w+)\](\s*)=", RegexOptions.RightToLeft);
        var indent = lastEntry.Success ? lastEntry.Groups[1].Value : "    ";
        int width = lastEntry.Success ? lastEntry.Groups[2].Value.Length + 2 + lastEntry.Groups[3].Value.Length : constant.Length + 3;
        var key = $"[{constant}]";
        var line = $"{indent}{key.PadRight(Math.Max(width, key.Length + 1))}= {value},";
        return text[..close.Index] + "\n" + line + text[close.Index..];
    }
}
