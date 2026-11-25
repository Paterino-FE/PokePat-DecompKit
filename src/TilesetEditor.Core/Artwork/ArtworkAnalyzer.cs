using TilesetEditor.Core.Gba;
using TilesetEditor.Core.Imaging;
using TilesetEditor.Core.Project;

namespace TilesetEditor.Core.Artwork;

/// <summary>
/// Fast local checks before running Porytiles. They mirror the hard limits Porytiles and the engine enforce
/// and give exact grid positions. Porytiles remains the authority; its diagnostics are added after compiling.
/// </summary>
public static class ArtworkAnalyzer
{
    public const int MaxColorsPerTile = 15;

    public static List<Issue> Analyze(Artwork art, FieldmapConstants constants, IReadOnlyList<Rgba32[]>? primaryPalettes)
    {
        var issues = new List<Issue>();
        if (!art.PresentLayers().Any())
        {
            issues.Add(new Issue(IssueSeverity.Error, "NO_LAYERS", "No layer has been imported yet."));
            return issues;
        }

        // Metatile capacity
        if (art.MetatileCount > constants.MaxSecondaryMetatiles)
            issues.Add(new Issue(IssueSeverity.Error, "METATILE_CAPACITY",
                $"The image contains {art.MetatileCount} metatiles; a secondary tileset holds at most {constants.MaxSecondaryMetatiles} " +
                $"(NUM_METATILES_TOTAL − NUM_METATILES_IN_PRIMARY). Shrink or split the image."));

        // Per-tile color counts, semi-transparency
        var tileColorSets = new List<(IssueLocation Loc, HashSet<Rgba32> Colors)>();
        foreach (var (layer, img) in art.PresentLayers())
        {
            for (int ty = 0; ty < art.TileRows; ty++)
                for (int tx = 0; tx < art.TileColumns; tx++)
                {
                    var colors = new HashSet<Rgba32>();
                    bool semi = false;
                    for (int y = 0; y < 8; y++)
                        for (int x = 0; x < 8; x++)
                        {
                            var c = img[tx * 8 + x, ty * 8 + y];
                            if (art.IsTransparent(c)) continue;
                            if (c.A < 255) semi = true;
                            colors.Add(c.Opaque);
                        }
                    if (colors.Count == 0) continue;
                    int metatile = art.MetatileIndexAt(tx * 8, ty * 8);
                    var loc = new IssueLocation(metatile, layer, tx, ty);
                    tileColorSets.Add((loc, colors));
                    if (colors.Count > MaxColorsPerTile)
                        issues.Add(new Issue(IssueSeverity.Error, "TILE_COLORS",
                            $"Too many colors in 8×8 tile: {colors.Count} instead of at most {MaxColorsPerTile} (plus transparency).", loc));
                    if (semi)
                        issues.Add(new Issue(IssueSeverity.Warning, "SEMI_TRANSPARENT",
                            "Semi-transparent pixels found. The GBA only supports fully opaque or transparent; they will be treated as opaque.", loc));
                }
        }

        // Layer mode
        if (constants.LayerMode == LayerMode.Dual)
        {
            for (int m = 0; m < art.MetatileCount; m++)
            {
                if (art.MetatileHasContent(Layer.Bottom, m) && art.MetatileHasContent(Layer.Middle, m) && art.MetatileHasContent(Layer.Top, m))
                {
                    var (x, y) = art.MetatileOrigin(m);
                    issues.Add(new Issue(IssueSeverity.Error, "LAYER_MODE",
                        "This metatile has content on all three layers. The project uses dual-layer (8 tiles per metatile): " +
                        "only two layers per metatile are possible (Bottom+Middle, Middle+Top or Bottom+Top).",
                        new IssueLocation(m, null, x / 8, y / 8)));
                }
            }
        }

        // Unique tile estimate (exact RGBA content, flips considered). Upper bound: Porytiles may reuse primary tiles.
        var unique = new HashSet<string>();
        foreach (var (layer, img) in art.PresentLayers())
            for (int ty = 0; ty < art.TileRows; ty++)
                for (int tx = 0; tx < art.TileColumns; tx++)
                {
                    var key = CanonicalTileKey(art, img, tx, ty);
                    if (key != null) unique.Add(key);
                }
        if (unique.Count > constants.MaxSecondaryTiles)
            issues.Add(new Issue(IssueSeverity.Warning, "TILE_CAPACITY",
                $"Estimated {unique.Count} distinct 8×8 tiles; a secondary tileset holds {constants.MaxSecondaryTiles}. " +
                "Porytiles can reuse tiles from the primary; if compilation fails, reuse repeated tiles or split the image."));

        // Palette capacity estimate
        AnalyzePaletteCapacity(issues, tileColorSets, constants, primaryPalettes);

        // GBA color collisions
        var byGba = art.OpaqueColors().GroupBy(GbaColor.ToBgr555).Where(g => g.Count() > 1).ToList();
        if (byGba.Count > 0)
        {
            var examples = string.Join("; ", byGba.Take(5).Select(g => string.Join(" = ", g.Select(c => c.ToHex()))));
            issues.Add(new Issue(IssueSeverity.Warning, "GBA_COLLISION",
                $"{byGba.Count} color group(s) are indistinguishable on the GBA (15-bit colors), e.g. {examples}. " +
                "They still take up separate palette slots. Use \"Round to GBA colors\" to merge them."));
        }

        return issues;
    }

    private static void AnalyzePaletteCapacity(List<Issue> issues, List<(IssueLocation Loc, HashSet<Rgba32> Colors)> tiles,
        FieldmapConstants constants, IReadOnlyList<Rgba32[]>? primaryPalettes)
    {
        var primarySets = (primaryPalettes ?? [])
            .Take(constants.NumPalsInPrimary)
            .Select(p => new HashSet<Rgba32>(p.Skip(1).Select(c => c.Opaque)))
            .ToList();
        // Tiles whose colors all fit in one primary palette can use it; all others need secondary palettes.
        var needSecondary = tiles.Where(t => t.Colors.Count <= MaxColorsPerTile && !primarySets.Any(p => t.Colors.IsSubsetOf(p))).ToList();
        var colors = new HashSet<Rgba32>(needSecondary.SelectMany(t => t.Colors));
        int capacity = constants.NumPalsInSecondary * MaxColorsPerTile;
        if (colors.Count <= capacity) return;

        issues.Add(new Issue(IssueSeverity.Error, "PALETTE_CAPACITY",
            $"Not enough palette capacity: the graphics need at least {colors.Count} colors of their own, the secondary tileset has " +
            $"{constants.NumPalsInSecondary} palettes × {MaxColorsPerTile} = {capacity} slots (palettes {constants.NumPalsInPrimary}–{constants.NumPalsTotal - 1}). " +
            "The marked tiles have the most colors that appear nowhere else – reducing colors there helps most."));

        // Highlight the tiles contributing the most colors that no other tile uses.
        var usage = new Dictionary<Rgba32, int>();
        foreach (var t in needSecondary)
            foreach (var c in t.Colors)
                usage[c] = usage.GetValueOrDefault(c) + 1;
        foreach (var t in needSecondary
                     .Select(t => (t.Loc, Own: t.Colors.Count(c => usage[c] == 1)))
                     .Where(x => x.Own > 0)
                     .OrderByDescending(x => x.Own)
                     .Take(40))
        {
            issues.Add(new Issue(IssueSeverity.Warning, "PALETTE_PRESSURE",
                $"This tile brings {t.Own} color(s) that no other tile uses.", t.Loc));
        }
    }

    /// <summary>Content key of a tile, identical for flipped copies. Null for empty tiles.</summary>
    private static string? CanonicalTileKey(Artwork art, RgbaImage img, int tx, int ty)
    {
        Span<uint> px = stackalloc uint[64];
        bool any = false;
        for (int y = 0; y < 8; y++)
            for (int x = 0; x < 8; x++)
            {
                var c = img[tx * 8 + x, ty * 8 + y];
                if (art.IsTransparent(c)) { px[y * 8 + x] = 0; continue; }
                any = true;
                px[y * 8 + x] = 0xFF000000u | ((uint)c.R << 16) | ((uint)c.G << 8) | c.B;
            }
        if (!any) return null;
        string best = "";
        for (int f = 0; f < 4; f++)
        {
            var sb = new System.Text.StringBuilder(64 * 8);
            for (int y = 0; y < 8; y++)
                for (int x = 0; x < 8; x++)
                {
                    int sx = (f & 1) != 0 ? 7 - x : x, sy = (f & 2) != 0 ? 7 - y : y;
                    sb.Append(px[sy * 8 + sx].ToString("X8"));
                }
            var s = sb.ToString();
            if (best.Length == 0 || string.CompareOrdinal(s, best) < 0) best = s;
        }
        return best;
    }

    /// <summary>Distinct opaque colors of one 8x8 tile with pixel counts, for the selection panel.</summary>
    public static List<(Rgba32 Color, int Pixels)> TileColors(Artwork art, Layer layer, int tx, int ty)
    {
        var img = art.GetLayer(layer);
        var counts = new Dictionary<Rgba32, int>();
        if (img == null || tx < 0 || ty < 0 || tx >= art.TileColumns || ty >= art.TileRows) return [];
        for (int y = 0; y < 8; y++)
            for (int x = 0; x < 8; x++)
            {
                var c = img[tx * 8 + x, ty * 8 + y];
                if (art.IsTransparent(c)) continue;
                counts[c.Opaque] = counts.GetValueOrDefault(c.Opaque) + 1;
            }
        return counts.OrderByDescending(kv => kv.Value).Select(kv => (kv.Key, kv.Value)).ToList();
    }
}
