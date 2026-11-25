using System.Text.RegularExpressions;

namespace TilesetEditor.Core.Sprites;

/// <summary>A battle picture of a trainer class (TRAINER_PIC_…).</summary>
public sealed record TrainerPic(string Constant, int Id, string FrontSymbol, string PaletteSymbol, string? PngRel, string? PalRel)
{
    public string Display => $"{Constant}  ({Id})";
    public override string ToString() => Display;
}

/// <summary>One image of the player character (overworld state or battle picture) and where its colors come from.</summary>
public sealed record PlayerGraphic(string Character, string Title, string Rel, string? PalRel, bool PaletteShared, string Note)
{
    public string Display => $"{Character}: {Title}";
    public override string ToString() => Display;
}

/// <summary>Reads trainer battle pictures and the player's graphics of a pokeemerald-expansion project.</summary>
public static partial class CharacterGraphics
{
    public const string TrainerConstantsFile = "include/constants/trainers.h";
    public const string TrainerGraphicsFile = "src/data/graphics/trainers.h";

    [GeneratedRegex(@"^\s*#define\s+(TRAINER_PIC_\w+)\s+(\d+)", RegexOptions.Multiline)]
    private static partial Regex PicDefine();

    [GeneratedRegex(@"TRAINER_SPRITE\(\s*(TRAINER_PIC_\w+)\s*,\s*(\w+)\s*,\s*(\w+)")]
    private static partial Regex SpriteEntry();

    [GeneratedRegex(@"(\w+)\[\]\s*=\s*INCBIN_U(?:8|16|32)\(""([^""]+)""")]
    private static partial Regex Incbin();

    private static string? PngOf(string bin) => bin.Contains(".4bpp") ? Regex.Replace(bin, @"\.4bpp(\.lz|\.smol)?$", ".png") : null;
    private static string? PalOf(string bin) => Regex.Replace(bin, @"\.gbapal(\.lz)?$", ".pal");

    public static List<TrainerPic> TrainerPics(string root)
    {
        string Read(string rel)
        {
            var p = Path.Combine(root, rel.Replace('/', Path.DirectorySeparatorChar));
            return File.Exists(p) ? File.ReadAllText(p) : "";
        }
        var gfx = Read(TrainerGraphicsFile);
        var bins = Incbin().Matches(gfx).GroupBy(m => m.Groups[1].Value).ToDictionary(g => g.Key, g => g.First().Groups[2].Value);
        var entries = SpriteEntry().Matches(gfx).GroupBy(m => m.Groups[1].Value).ToDictionary(g => g.Key, g => (Front: g.First().Groups[2].Value, Pal: g.First().Groups[3].Value));
        var result = new List<TrainerPic>();
        foreach (Match m in PicDefine().Matches(Read(TrainerConstantsFile)))
        {
            var name = m.Groups[1].Value;
            if (name.EndsWith("_COUNT", StringComparison.Ordinal)) continue;
            entries.TryGetValue(name, out var e);
            string? png = e.Front != null && bins.TryGetValue(e.Front, out var fb) ? PngOf(fb) : null;
            string? pal = e.Pal != null && bins.TryGetValue(e.Pal, out var pb) ? PalOf(pb) : null;
            if (pal != null && !File.Exists(Path.Combine(root, pal.Replace('/', Path.DirectorySeparatorChar)))) pal = null; // built from the PNG
            result.Add(new TrainerPic(name, int.Parse(m.Groups[2].Value), e.Front ?? "", e.Pal ?? "", png, pal));
        }
        return result;
    }

    private static readonly (string File, string Title)[] States =
    [
        ("walking", "Walking"), ("running", "Running"), ("mach_bike", "Mach Bike"), ("acro_bike", "Acro Bike"), ("surfing", "Surfing"),
        ("underwater", "Underwater (Dive)"), ("field_move", "Field move / using an item (Cut, Rock Smash …)"), ("fishing", "Fishing"),
        ("watering", "Watering berries"), ("decorating", "Decorating (secret base)"),
    ];

    /// <summary>
    /// The player characters' images: overworld states from graphics/object_events/pics/people/&lt;who&gt;/ and the battle
    /// front/back pictures. Palettes come from the overworld sprites registered with these images.
    /// </summary>
    public static List<PlayerGraphic> Player(string root, IReadOnlyList<OverworldSprite> sprites)
    {
        var result = new List<PlayerGraphic>();
        foreach (var who in new[] { "brendan", "may" })
        {
            string character = who == "brendan" ? "Brendan" : "May";
            var dir = $"graphics/object_events/pics/people/{who}";
            var full = Path.Combine(root, dir.Replace('/', Path.DirectorySeparatorChar));
            if (!Directory.Exists(full)) continue;
            var own = sprites.Where(s => s.PngPath != null && s.PngPath.StartsWith(dir + "/", StringComparison.Ordinal)).ToList();
            string? defaultPal = own.Where(s => !s.PngPath!.Contains("underwater")).Select(s => s.PalettePath).FirstOrDefault(p => p != null);
            var files = Directory.EnumerateFiles(full, "*.png").Select(Path.GetFileNameWithoutExtension).OfType<string>().ToList();
            foreach (var f in files.OrderBy(f => { int i = Array.FindIndex(States, s => s.File == f); return i < 0 ? 99 : i; }).ThenBy(f => f))
            {
                var rel = $"{dir}/{f}.png";
                var pal = own.FirstOrDefault(s => s.PngPath == rel)?.PalettePath ?? (f.Contains("underwater") ? null : defaultPal);
                var title = States.FirstOrDefault(s => s.File == f).Title ?? f.Replace('_', ' ');
                result.Add(new PlayerGraphic(character, title, rel, pal, true,
                    pal != null ? $"Palette {pal} is shared by all of {character}'s overworld images." : "Palette not found – colors are mapped onto the image's own palette."));
            }
            var front = $"graphics/trainers/front_pics/{who}.png";
            if (File.Exists(Path.Combine(root, front.Replace('/', Path.DirectorySeparatorChar))))
            {
                var frontPal = $"graphics/trainers/front_pics/{who}.pal";
                bool hasPal = File.Exists(Path.Combine(root, frontPal.Replace('/', Path.DirectorySeparatorChar)));
                result.Add(new PlayerGraphic(character, "Battle: front picture", front, hasPal ? frontPal : null, false,
                    "Shown in battle intros, the trainer card and link battles."));
            }
            var back = $"graphics/trainers/back_pics/{who}.png";
            if (File.Exists(Path.Combine(root, back.Replace('/', Path.DirectorySeparatorChar))))
                result.Add(new PlayerGraphic(character, "Battle: back (throwing animation)", back, null, true,
                    "The back picture uses the front picture's palette; colors are mapped onto it."));
        }
        return result;
    }
}
