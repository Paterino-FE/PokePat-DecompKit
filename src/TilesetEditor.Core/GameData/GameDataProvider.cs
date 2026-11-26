using TilesetEditor.Core.Project;

namespace TilesetEditor.Core.GameData;

/// <summary>
/// The game data tables of one game: which tables exist, how they are read and how changed values are written back.
/// The Game data module works the same for every game; only the provider differs.
/// </summary>
public interface IGameDataProvider
{
    GameKind Game { get; }
    IReadOnlyList<GameDataTableDef> Tables { get; }
    /// <summary>Text checks for the project (charmap).</summary>
    GameText LoadText(string root);
    GameDataTable Load(string root, GameDataTableDef def, GameText text);
    /// <summary>Project-relative files that must be rewritten for the table's changes.</summary>
    IEnumerable<string> ChangedFiles(GameDataTable table);
    /// <summary>Applies the table's changes that belong to <paramref name="file"/> to its current text.</summary>
    string Write(GameDataTable table, string file, string text);
    List<QuickFilter> QuickFilters(GameDataTable table, string root);
    /// <summary>Notes shown in the file preview when saving.</summary>
    IReadOnlyList<string> SaveNotes(IReadOnlyList<GameDataTable> changed);
    /// <summary>Folder the preview groups the files under.</summary>
    string SourceFolder { get; }
}

/// <summary>pokeemerald(-expansion): the C tables of <see cref="GameDataCatalog"/>.</summary>
public sealed class EmeraldGameData : IGameDataProvider
{
    public static readonly EmeraldGameData Instance = new();

    public GameKind Game => GameKind.Emerald;
    public IReadOnlyList<GameDataTableDef> Tables => GameDataCatalog.Tables;
    public string SourceFolder => "src";
    public GameText LoadText(string root) => GameText.Load(root);
    public GameDataTable Load(string root, GameDataTableDef def, GameText text) => GameDataCatalog.Load(root, def, text);
    public IEnumerable<string> ChangedFiles(GameDataTable table) => GameDataCatalog.ChangedFiles(table);
    public string Write(GameDataTable table, string file, string text) => GameDataCatalog.Write(table, file, text);
    public List<QuickFilter> QuickFilters(GameDataTable table, string root) => GameData.QuickFilters.For(table, root);

    public IReadOnlyList<string> SaveNotes(IReadOnlyList<GameDataTable> changed)
    {
        var notes = new List<string>
        {
            "Only the changed values are replaced; every other character of these files stays as it is.",
            "Build the ROM afterwards to see the changes in-game.",
            "Names were checked against charmap.txt and the game's length limits. How long texts (descriptions) fit in the menus is not checked – look at them in-game.",
        };
        if (changed.Any(t => t.Def.Source == GameDataSource.MapSectionsJson))
            notes.Add("region_map_sections.json is turned into C code by the build (src/data/region_map/region_map_entries.h is generated).");
        return notes;
    }
}
