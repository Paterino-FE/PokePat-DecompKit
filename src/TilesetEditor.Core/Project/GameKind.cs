namespace TilesetEditor.Core.Project;

/// <summary>The game (decomp project) a PokéPat project is based on. Chosen at the start; decides which modules exist.</summary>
public enum GameKind
{
    /// <summary>Pokémon Emerald – pokeemerald / pokeemerald-expansion (GBA).</summary>
    Emerald,
    /// <summary>Pokémon Platinum – pret/pokeplatinum (NDS).</summary>
    Platinum,
}

public static class GameKinds
{
    public static IReadOnlyList<GameKind> All { get; } = Enum.GetValues<GameKind>();

    /// <summary>Short name for menus ("Emerald", "Platinum").</summary>
    public static string Title(this GameKind g) => g switch
    {
        GameKind.Platinum => "Pokémon Platinum",
        _ => "Pokémon Emerald",
    };

    /// <summary>The decomp project the game is built from.</summary>
    public static string DecompName(this GameKind g) => g switch
    {
        GameKind.Platinum => "pokeplatinum",
        _ => "pokeemerald-expansion",
    };

    public static string Console(this GameKind g) => g switch
    {
        GameKind.Platinum => "Nintendo DS",
        _ => "Game Boy Advance",
    };

    public static string DefaultRepository(this GameKind g) => g switch
    {
        GameKind.Platinum => "https://github.com/pret/pokeplatinum",
        _ => "https://github.com/rh-hideout/pokeemerald-expansion",
    };

    /// <summary>Default Linux folder of a new project ("~" = home).</summary>
    public static string DefaultProjectPath(this GameKind g) => g switch
    {
        GameKind.Platinum => "~/pokeplatinum",
        _ => "~/pokeemerald-expansion",
    };

    /// <summary>ROM written by the build, relative to the project root, when the Makefile does not name another one.</summary>
    public static string DefaultRomPath(this GameKind g) => g switch
    {
        GameKind.Platinum => "build/pokeplatinum.us.nds",
        _ => "pokeemerald.gba",
    };

    public static GameKind Parse(string? value, GameKind fallback = GameKind.Emerald) =>
        Enum.TryParse<GameKind>(value, ignoreCase: true, out var g) ? g : fallback;
}

/// <summary>Recognizes the game of a project folder by its characteristic files.</summary>
public static class GameDetector
{
    /// <summary>Emerald: the tileset headers and fieldmap.h of pokeemerald(-expansion).</summary>
    public static readonly string[] EmeraldMarkers = ["include/fieldmap.h", "src/data/tilesets/headers.h"];

    /// <summary>Platinum: the platinum.us ROM description and the JSON species data of pret/pokeplatinum.</summary>
    public static readonly string[] PlatinumMarkers =
        ["platinum.us/rom_rev1.ini", "res/pokemon/bulbasaur/data.json", "include/data/map_headers.h", "res/field/maps/groups.json"];

    /// <summary>The game of the folder, or null if it is neither (or does not exist).</summary>
    public static GameKind? Detect(string root) => Detect(new DiskFileSource(root));

    public static GameKind? Detect(IFileSource fs)
    {
        if (EmeraldMarkers.All(fs.FileExists)) return GameKind.Emerald;
        // A hack may rename or remove single files; two of the three markers are enough.
        if (PlatinumMarkers.Count(fs.FileExists) >= 2) return GameKind.Platinum;
        return null;
    }
}
