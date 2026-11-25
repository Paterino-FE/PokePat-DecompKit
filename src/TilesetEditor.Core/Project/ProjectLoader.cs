using System.Text.Json;
using System.Text.RegularExpressions;
using TilesetEditor.Core.Gba;

namespace TilesetEditor.Core.Project;

public sealed class ProjectLoadException(string message) : Exception(message);

/// <summary>
/// Reads the parts of a pokeemerald(-expansion) project that matter for tilesets. The parsing follows
/// what Porymap does: tileset structs from headers.h, INCBIN/INCGFX symbols resolved in the tileset data headers
/// (and src/graphics.c, where the expansion keeps the primary tilesets), .4bpp(.lz) mapped back to .png
/// and .gbapal to .pal (INCGFX names the .png/.pal directly).
/// </summary>
public static partial class ProjectLoader
{
    public const string HeadersFile = "src/data/tilesets/headers.h";
    public const string GraphicsFile = "src/data/tilesets/graphics.h";
    public const string MetatilesFile = "src/data/tilesets/metatiles.h";
    public const string FieldmapFile = "include/fieldmap.h";
    public const string GlobalFieldmapFile = "include/global.fieldmap.h";
    public const string LayoutsFile = "data/layouts/layouts.json";

    /// <summary>Files scanned for INCBIN symbol definitions, in priority order.</summary>
    public static readonly string[] SymbolFiles = [GraphicsFile, MetatilesFile, "src/graphics.c"];

    [GeneratedRegex(@"const\s+struct\s+Tileset\s+(\w+)\s*=\s*\{(.*?)\}\s*;", RegexOptions.Singleline)]
    private static partial Regex TilesetStructRegex();

    [GeneratedRegex(@"\.(\w+)\s*=\s*([^,}]+)")]
    private static partial Regex FieldRegex();

    // const u16 gTilesetPalettes_X[][16] = { ... };   (expansion 1.13+: const u16 ALIGNED(4) gTilesetPalettes_X[][16])
    [GeneratedRegex(@"(?:const\s+)?u16\s+(?:ALIGNED\s*\(\s*\d+\s*\)\s+)?(\w+)\s*\[\s*\]\s*\[\s*16\s*\]\s*=\s*\{(.*?)\}\s*;", RegexOptions.Singleline)]
    private static partial Regex PaletteArrayRegex();

    [GeneratedRegex(@"metatileAttributes\s*;")]
    private static partial Regex AttrMemberRegex();

    [GeneratedRegex(@"const\s+u(8|16|32)\s*\*\s*metatileAttributes\s*;")]
    private static partial Regex AttrMemberTypeRegex();

    public static ProjectInfo Load(string root) => Load(root, new DiskFileSource(root));

    public static ProjectInfo Load(string root, IFileSource fs)
    {
        if (!fs.FileExists(HeadersFile) || !fs.FileExists(FieldmapFile))
            throw new ProjectLoadException(
                $"'{root}' does not look like a pokeemerald project: '{FieldmapFile}' or '{HeadersFile}' is missing.");

        var warnings = new List<string>();

        // --- Engine constants
        var defines = new CDefines();
        defines.AddFromText(fs.ReadAllText(FieldmapFile));
        string globalFieldmap = fs.FileExists(GlobalFieldmapFile) ? fs.ReadAllText(GlobalFieldmapFile) : "";
        defines.AddFromText(globalFieldmap);

        int Need(string name, int fallback)
        {
            var v = defines.TryEvaluate(name);
            if (v == null)
            {
                warnings.Add($"{name} not found, using default value {fallback}.");
                return fallback;
            }
            return (int)v.Value;
        }

        var d = FieldmapConstants.EmeraldDefaults;
        var constants = new FieldmapConstants(
            Need("NUM_TILES_IN_PRIMARY", d.NumTilesInPrimary),
            Need("NUM_TILES_TOTAL", d.NumTilesTotal),
            Need("NUM_METATILES_IN_PRIMARY", d.NumMetatilesInPrimary),
            Need("NUM_METATILES_TOTAL", d.NumMetatilesTotal),
            Need("NUM_PALS_IN_PRIMARY", d.NumPalsInPrimary),
            Need("NUM_PALS_TOTAL", d.NumPalsTotal),
            Need("NUM_TILES_PER_METATILE", d.NumTilesPerMetatile),
            Need("MAX_MAP_DATA_SIZE", d.MaxMapDataSize));
        if (constants.NumTilesPerMetatile is not (8 or 12))
            throw new ProjectLoadException($"NUM_TILES_PER_METATILE = {constants.NumTilesPerMetatile} is not supported (expected 8 or 12).");

        // --- Attribute layout
        int attrBits = 16;
        var typeMatch = AttrMemberTypeRegex().Match(globalFieldmap);
        if (typeMatch.Success) attrBits = int.Parse(typeMatch.Groups[1].Value);
        else if (AttrMemberRegex().IsMatch(globalFieldmap)) warnings.Add("Type of struct Tileset.metatileAttributes not recognized, assuming u16.");
        var attrLayout = new AttributeLayout(
            attrBits / 8,
            (uint)(defines.TryEvaluate("METATILE_ATTR_BEHAVIOR_MASK") ?? 0x00FF),
            (uint)(defines.TryEvaluate("METATILE_ATTR_LAYER_MASK") ?? 0xF000),
            (int)(defines.TryEvaluate("METATILE_ATTR_LAYER_SHIFT") ?? 12));

        // --- Tileset structs
        string headers = StripComments(fs.ReadAllText(HeadersFile));
        var tilesets = new List<TilesetInfo>();
        bool anyCompressedField = false, anyCallbackField = false;
        foreach (Match m in TilesetStructRegex().Matches(headers))
        {
            var fields = FieldRegex().Matches(m.Groups[2].Value)
                .ToDictionary(f => f.Groups[1].Value, f => f.Groups[2].Value.Trim(), StringComparer.Ordinal);
            anyCompressedField |= fields.ContainsKey("isCompressed");
            anyCallbackField |= fields.ContainsKey("callback");
            tilesets.Add(new TilesetInfo
            {
                Name = m.Groups[1].Value,
                IsSecondary = fields.TryGetValue("isSecondary", out var sec) && IsTrue(sec),
                IsCompressed = fields.TryGetValue("isCompressed", out var comp) ? IsTrue(comp) : null,
                Callback = fields.GetValueOrDefault("callback"),
                TilesSymbol = fields.GetValueOrDefault("tiles"),
                PalettesSymbol = fields.GetValueOrDefault("palettes"),
                MetatilesSymbol = fields.GetValueOrDefault("metatiles"),
                AttributesSymbol = fields.GetValueOrDefault("metatileAttributes"),
            });
        }
        if (tilesets.Count == 0) throw new ProjectLoadException($"No tilesets found in '{HeadersFile}'.");

        // --- Symbols
        var singles = new Dictionary<string, (GraphicsInclude Include, string File, int Bits)>(StringComparer.Ordinal);
        var paletteArrays = new Dictionary<string, (List<GraphicsInclude> Includes, string File)>(StringComparer.Ordinal);
        bool incGfx = false;
        var known = new HashSet<string>(StringComparer.Ordinal);
        foreach (var t in tilesets) known.Add(t.Name);
        foreach (var file in SymbolFiles)
        {
            if (!fs.FileExists(file)) continue;
            string text = StripComments(fs.ReadAllText(file));
            foreach (var decl in GraphicsIncludes.Declarations(text))
            {
                singles.TryAdd(decl.Symbol, (decl.Include, file, decl.Bits));
                known.Add(decl.Symbol);
                incGfx |= decl.Include.IsIncGfx && file == GraphicsFile;
            }
            foreach (Match m in PaletteArrayRegex().Matches(text))
            {
                var includes = GraphicsIncludes.Includes(m.Groups[2].Value).ToList();
                paletteArrays.TryAdd(m.Groups[1].Value, (includes, file));
                known.Add(m.Groups[1].Value);
            }
        }

        var paletteCounts = new List<int>();
        var tilesExts = new List<string>();
        var palExts = new List<string>();
        var attrDeclBits = new List<int>();
        foreach (var t in tilesets)
        {
            if (t.TilesSymbol != null && singles.TryGetValue(t.TilesSymbol, out var tiles))
            {
                t.TilesImagePath = tiles.Include.SourcePath;
                t.GraphicsDeclaredIn = tiles.File;
                if (t.IsSecondary) tilesExts.Add(tiles.Include.OutputExtension);
            }
            if (t.PalettesSymbol != null && paletteArrays.TryGetValue(t.PalettesSymbol, out var pals))
            {
                t.PalettePaths.AddRange(pals.Includes.Select(i => i.SourcePath));
                if (t.IsSecondary)
                {
                    paletteCounts.Add(pals.Includes.Count);
                    if (pals.Includes.Count > 0) palExts.Add(pals.Includes[0].OutputExtension);
                }
            }
            if (t.MetatilesSymbol != null && singles.TryGetValue(t.MetatilesSymbol, out var mt)) t.MetatilesPath = mt.Include.BuiltPath;
            if (t.AttributesSymbol != null && singles.TryGetValue(t.AttributesSymbol, out var at))
            {
                t.AttributesPath = at.Include.BuiltPath;
                if (t.IsSecondary) attrDeclBits.Add(at.Bits);
            }
            if (!t.IsFullyResolved)
                warnings.Add($"{t.Name}: not all files could be resolved (tiles/palettes/metatiles/attributes).");
        }

        var style = new RegistrationStyle(
            HeadersHaveIsCompressed: anyCompressedField,
            HeadersHaveCallback: anyCallbackField,
            PaletteIncbinCount: MostCommon(paletteCounts, 16),
            TilesOutputExtension: MostCommon(tilesExts, ".4bpp.lz"),
            PalettesOutputExtension: MostCommon(palExts, ".gbapal"),
            AttributeDeclarationBits: MostCommon(attrDeclBits, attrBits),
            UsesIncGfx: incGfx);

        // --- Layouts
        var layouts = new List<LayoutInfo>();
        if (fs.FileExists(LayoutsFile))
        {
            try
            {
                using var doc = JsonDocument.Parse(fs.ReadAllText(LayoutsFile));
                if (doc.RootElement.TryGetProperty("layouts", out var arr))
                {
                    foreach (var l in arr.EnumerateArray())
                    {
                        if (l.ValueKind != JsonValueKind.Object || !l.TryGetProperty("id", out var id)) continue;
                        layouts.Add(new LayoutInfo(
                            id.GetString() ?? "",
                            l.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "",
                            l.TryGetProperty("primary_tileset", out var p) ? p.GetString() : null,
                            l.TryGetProperty("secondary_tileset", out var s) ? s.GetString() : null,
                            l.TryGetProperty("width", out var w) && w.ValueKind == JsonValueKind.Number ? w.GetInt32() : 0,
                            l.TryGetProperty("height", out var h) && h.ValueKind == JsonValueKind.Number ? h.GetInt32() : 0,
                            l.TryGetProperty("blockdata_filepath", out var bd) ? bd.GetString() : null));
                    }
                }
            }
            catch (JsonException ex)
            {
                warnings.Add($"Could not read {LayoutsFile}: {ex.Message}");
            }
        }

        // --- Expansion version (informational)
        string? expansion = null;
        const string expansionHeader = "include/constants/expansion.h";
        if (fs.FileExists(expansionHeader))
        {
            var ed = new CDefines();
            ed.AddFromText(fs.ReadAllText(expansionHeader));
            var maj = ed.TryEvaluate("EXPANSION_VERSION_MAJOR");
            var min = ed.TryEvaluate("EXPANSION_VERSION_MINOR");
            var pat = ed.TryEvaluate("EXPANSION_VERSION_PATCH");
            if (maj != null && min != null) expansion = $"{maj}.{min}.{pat ?? 0}";
        }

        var info = new ProjectInfo
        {
            Root = root,
            Constants = constants,
            Attributes = attrLayout,
            Tilesets = tilesets,
            Layouts = layouts,
            Style = style,
            HeadersPath = HeadersFile,
            GraphicsPath = GraphicsFile,
            MetatilesPath = MetatilesFile,
            PrimaryTilesetsDir = "data/tilesets/primary/",
            SecondaryTilesetsDir = "data/tilesets/secondary/",
            ExpansionVersion = expansion,
            KnownSymbols = known,
        };
        info.Warnings.AddRange(warnings);
        return info;
    }

    private static bool IsTrue(string v) => v.Trim() is "TRUE" or "1" or "true";

    public static string StripComments(string text)
    {
        text = Regex.Replace(text, @"/\*.*?\*/", " ", RegexOptions.Singleline);
        return Regex.Replace(text, @"//[^\n]*", "");
    }

    private static T MostCommon<T>(List<T> values, T fallback) where T : notnull =>
        values.Count == 0 ? fallback : values.GroupBy(v => v).OrderByDescending(g => g.Count()).First().Key;
}
