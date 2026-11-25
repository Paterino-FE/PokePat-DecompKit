using System.Text.RegularExpressions;
using TilesetEditor.Core.Imaging;

namespace TilesetEditor.Core.Trainers;

/// <summary>Constants for the trainer editor's pickers, read from the branch's headers.</summary>
public sealed partial class TrainerSymbols
{
    public IReadOnlyList<string> Species { get; init; } = [];
    public IReadOnlyList<string> Items { get; init; } = [];
    public IReadOnlyList<string> Moves { get; init; } = [];
    public IReadOnlyList<string> Abilities { get; init; } = [];
    public IReadOnlyList<string> Natures { get; init; } = [];
    public IReadOnlyList<string> Classes { get; init; } = [];
    public IReadOnlyList<string> Pics { get; init; } = [];
    public IReadOnlyList<string> Music { get; init; } = [];
    public IReadOnlyList<string> AiFlags { get; init; } = [];

    private static readonly string[] Markers = ["_COUNT", "_START", "_END", "NUM_", "_FIRST", "_LAST"];

    private static List<string> Defines(string text, string prefix, params string[] exclude) =>
        Regex.Matches(text, @"^[ \t]*#define[ \t]+(" + prefix + @"\w+)\b(?!\()", RegexOptions.Multiline)
            .Select(m => m.Groups[1].Value)
            .Where(n => !Markers.Any(x => n.Contains(x, StringComparison.Ordinal)) && !exclude.Contains(n))
            .Distinct().ToList();

    /// <summary>Enum members "PREFIX_X," / "PREFIX_X = …," (newer headers use enums instead of #define).</summary>
    private static List<string> EnumMembers(string text, string prefix) =>
        Regex.Matches(text, @"^[ \t]*(" + prefix + @"\w+)[ \t]*(?:=[^,\r\n]*)?,", RegexOptions.Multiline)
            .Select(m => m.Groups[1].Value).Where(n => !Markers.Any(x => n.Contains(x, StringComparison.Ordinal))).Distinct().ToList();

    private static List<string> Both(string text, string prefix, params string[] exclude) =>
        Defines(text, prefix, exclude).Concat(EnumMembers(text, prefix)).Where(n => !exclude.Contains(n)).Distinct().ToList();

    public static TrainerSymbols Load(string root)
    {
        string Read(params string[] parts)
        {
            var p = Path.Combine([root, .. parts]);
            return File.Exists(p) ? File.ReadAllText(p) : "";
        }
        var trainers = Read("include", "constants", "trainers.h");
        var battleAi = Read("include", "constants", "battle_ai.h");
        return new TrainerSymbols
        {
            Species = Both(Read("include", "constants", "species.h"), "SPECIES_", "SPECIES_EGG"),
            Items = Both(Read("include", "constants", "items.h"), "ITEM_", "ITEM_NONE"),
            Moves = Both(Read("include", "constants", "moves.h"), "MOVE_", "MOVE_NONE", "MOVE_UNAVAILABLE"),
            Abilities = Both(Read("include", "constants", "abilities.h"), "ABILITY_", "ABILITY_NONE"),
            Natures = Both(Read("include", "constants", "pokemon.h"), "NATURE_"),
            Classes = Both(trainers, "TRAINER_CLASS_"),
            Pics = Both(trainers, "TRAINER_PIC_"),
            Music = Both(trainers, "TRAINER_ENCOUNTER_MUSIC_"),
            AiFlags = Defines(battleAi, "AI_FLAG_"),
        };
    }
}

/// <summary>Finds front pictures of trainers and Pokémon for the editor's previews (best effort, null if not found).</summary>
public static partial class TrainerGraphics
{
    /// <summary>TRAINER_PIC_X → graphics/…/x.png, via src/data/graphics/trainers.h (gTrainerSprites or gTrainerPicInfo).</summary>
    public static Dictionary<string, string> TrainerPics(string root)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var p in Sprites.CharacterGraphics.TrainerPics(root))
            if (p.PngRel != null) result.TryAdd(p.Constant, p.PngRel);
        return result;
    }

    /// <summary>graphics/pokemon/&lt;name&gt;[/&lt;form&gt;]/front.png or anim_front.png for SPECIES_X (guessing the folder from the name).</summary>
    public static string? SpeciesFront(string root, string species)
    {
        if (!species.StartsWith("SPECIES_", StringComparison.Ordinal)) return null;
        var parts = species[8..].ToLowerInvariant().Split('_');
        var candidates = new List<string>();
        for (int split = parts.Length; split >= 1; split--)
        {
            var head = string.Join("_", parts[..split]);
            var tail = string.Join("_", parts[split..]);
            candidates.Add(tail.Length > 0 ? Path.Combine(head, tail) : head);
        }
        candidates.Add(string.Join("_", parts));
        foreach (var c in candidates.Distinct())
            foreach (var f in new[] { "anim_front.png", "front.png" })
            {
                var p = Path.Combine(root, "graphics", "pokemon", c, f);
                if (File.Exists(p)) return p;
            }
        return null;
    }

    /// <summary>Loads an indexed sprite with index 0 transparent; for anim_front sheets only the first 64×64 frame.</summary>
    public static RgbaImage? LoadSprite(string path, int maxHeight = 64)
    {
        try
        {
            var png = Png.Load(path);
            var img = png.Rgba;
            int h = Math.Min(img.Height, Math.Max(maxHeight, img.Width));
            var result = new RgbaImage(img.Width, h);
            for (int y = 0; y < h; y++)
                for (int x = 0; x < img.Width; x++)
                {
                    bool transparent = png.Indices != null ? png.Indices[y * png.Width + x] == 0 : img[x, y].A == 0;
                    result[x, y] = transparent ? default : img[x, y];
                }
            return result;
        }
        catch (Exception ex) when (ex is IOException or PngFormatException)
        {
            return null;
        }
    }
}
