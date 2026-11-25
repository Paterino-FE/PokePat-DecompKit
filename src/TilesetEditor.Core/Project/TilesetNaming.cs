using System.Text.RegularExpressions;

namespace TilesetEditor.Core.Project;

/// <summary>Naming rules mirrored from Porymap (Tileset::getExpectedDir / stripPrefix).</summary>
public static partial class TilesetNaming
{
    public const string Prefix = "gTileset_";

    [GeneratedRegex("^gTileset_[A-Z][A-Za-z0-9]*$")]
    private static partial Regex ValidName();

    [GeneratedRegex("([a-z])([A-Z0-9])")]
    private static partial Regex CamelBoundary();

    public static string StripPrefix(string name) => name.StartsWith(Prefix, StringComparison.Ordinal) ? name[Prefix.Length..] : name;

    /// <summary>Porymap: stripPrefix(name).replace(/([a-z])([A-Z0-9])/, "\1_\2").toLower()</summary>
    public static string ToFolderName(string name) =>
        CamelBoundary().Replace(StripPrefix(name), "$1_$2").ToLowerInvariant();

    public static string? Validate(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "Enter a name for the new tileset.";
        if (!name.StartsWith(Prefix, StringComparison.Ordinal)) return $"The name must start with '{Prefix}' (Porymap convention).";
        if (!ValidName().IsMatch(name))
            return $"Invalid name '{name}'. Allowed: '{Prefix}' followed by an uppercase letter and letters/digits, e.g. gTileset_MyTown.";
        return null;
    }

    /// <summary>Normalizes user input such as "my town" or "MyTown" to "gTileset_MyTown".</summary>
    public static string Suggest(string input)
    {
        var s = StripPrefix(input.Trim());
        var parts = Regex.Split(s, "[^A-Za-z0-9]+").Where(p => p.Length > 0);
        var pascal = string.Concat(parts.Select(p => char.ToUpperInvariant(p[0]) + p[1..]));
        return Prefix + pascal;
    }

    public static string TilesSymbol(string name) => $"gTilesetTiles_{StripPrefix(name)}";
    public static string PalettesSymbol(string name) => $"gTilesetPalettes_{StripPrefix(name)}";
    public static string MetatilesSymbol(string name) => $"gMetatiles_{StripPrefix(name)}";
    public static string AttributesSymbol(string name) => $"gMetatileAttributes_{StripPrefix(name)}";
}
