using TilesetEditor.Core.Gba;

namespace TilesetEditor.Core.Project;

/// <summary>Engine limits from include/fieldmap.h.</summary>
public sealed record FieldmapConstants(
    int NumTilesInPrimary,
    int NumTilesTotal,
    int NumMetatilesInPrimary,
    int NumMetatilesTotal,
    int NumPalsInPrimary,
    int NumPalsTotal,
    int NumTilesPerMetatile,
    int MaxMapDataSize)
{
    public static readonly FieldmapConstants EmeraldDefaults = new(512, 1024, 512, 1024, 6, 13, 8, 10240);

    public int MaxSecondaryTiles => NumTilesTotal - NumTilesInPrimary;
    public int MaxSecondaryMetatiles => NumMetatilesTotal - NumMetatilesInPrimary;
    public int NumPalsInSecondary => NumPalsTotal - NumPalsInPrimary;
    public LayerMode LayerMode => NumTilesPerMetatile == 12 ? LayerMode.Triple : LayerMode.Dual;
}

public enum LayerMode
{
    /// <summary>8 tiles per metatile: two of the three layers (bottom/middle/top) per metatile.</summary>
    Dual,
    /// <summary>12 tiles per metatile: all three layers.</summary>
    Triple,
}

/// <summary>A tileset declared in src/data/tilesets/headers.h with its resolved asset paths.</summary>
public sealed class TilesetInfo
{
    public required string Name { get; init; }
    public required bool IsSecondary { get; init; }
    public bool? IsCompressed { get; init; }
    public string? Callback { get; init; }
    public string? TilesSymbol { get; init; }
    public string? PalettesSymbol { get; init; }
    public string? MetatilesSymbol { get; init; }
    public string? AttributesSymbol { get; init; }

    /// <summary>Porymap-editable source files (project-relative). Null when the symbol could not be resolved.</summary>
    public string? TilesImagePath { get; set; }
    public List<string> PalettePaths { get; } = [];
    public string? MetatilesPath { get; set; }
    public string? AttributesPath { get; set; }

    /// <summary>Where the symbol definitions were found (for diagnostics).</summary>
    public string? GraphicsDeclaredIn { get; set; }

    public bool IsFullyResolved =>
        TilesImagePath != null && PalettePaths.Count > 0 && MetatilesPath != null && AttributesPath != null;

    public string FriendlyName => TilesetNaming.StripPrefix(Name);

    public override string ToString() => Name;
}

public sealed record LayoutInfo(string Id, string Name, string? PrimaryTileset, string? SecondaryTileset,
    int Width = 0, int Height = 0, string? BlockdataPath = null);

/// <summary>Formatting conventions detected from the existing registration files.</summary>
public sealed record RegistrationStyle(
    bool HeadersHaveIsCompressed,
    bool HeadersHaveCallback,
    int PaletteIncbinCount,
    string TilesOutputExtension,
    string PalettesOutputExtension,
    int AttributeDeclarationBits,
    /// <summary>graphics.h declares with INCGFX_U32("tiles.png", ".4bpp.smol") (expansion 1.13+) instead of INCBIN.</summary>
    bool UsesIncGfx = false);

/// <summary>Everything the app needs to know about the target decomp project.</summary>
public sealed class ProjectInfo
{
    public required string Root { get; init; }
    public required FieldmapConstants Constants { get; init; }
    public required AttributeLayout Attributes { get; init; }
    public required IReadOnlyList<TilesetInfo> Tilesets { get; init; }
    public required IReadOnlyList<LayoutInfo> Layouts { get; init; }
    public required RegistrationStyle Style { get; init; }
    public required string HeadersPath { get; init; }
    public required string GraphicsPath { get; init; }
    public required string MetatilesPath { get; init; }
    public required string PrimaryTilesetsDir { get; init; }
    public required string SecondaryTilesetsDir { get; init; }
    /// <summary>Set if the project is pokeemerald-expansion (version string from CHANGELOG/README if found).</summary>
    public string? ExpansionVersion { get; init; }
    public List<string> Warnings { get; } = [];

    /// <summary>Every C symbol defined in the scanned tileset declaration files (for collision checks).</summary>
    public required IReadOnlySet<string> KnownSymbols { get; init; }

    public IEnumerable<TilesetInfo> PrimaryTilesets => Tilesets.Where(t => !t.IsSecondary);
    public IEnumerable<TilesetInfo> SecondaryTilesets => Tilesets.Where(t => t.IsSecondary);

    public TilesetInfo? FindTileset(string name) => Tilesets.FirstOrDefault(t => t.Name == name);

    public string FullPath(string rel) => ProjectPaths.Combine(Root, rel);

    /// <summary>Loads the Porymap-format data for a tileset of this project.</summary>
    public TilesetData LoadTilesetData(TilesetInfo t)
    {
        if (!t.IsFullyResolved)
            throw new InvalidOperationException($"The files of '{t.Name}' could not be fully resolved.");
        return TilesetData.Load(t.Name, FullPath(t.TilesImagePath!), t.PalettePaths.Select(FullPath).ToList(),
            FullPath(t.MetatilesPath!), FullPath(t.AttributesPath!), Constants.NumTilesPerMetatile, Attributes.SizeBytes);
    }
}
