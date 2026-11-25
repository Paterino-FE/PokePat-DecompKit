using System.Collections.Concurrent;
using TilesetEditor.Core.Gba;
using TilesetEditor.Core.Imaging;
using TilesetEditor.Core.Project;

namespace TilesetEditor.Core.Maps;

/// <summary>
/// Renders a map layout like the game shows it (without sprites): every block's metatile with all its layers drawn
/// bottom to top, 16×16 pixels per block. Metatile ids below NUM_METATILES_IN_PRIMARY come from the primary tileset,
/// the rest from the secondary; tile ids and palettes are split the same way. Uncovered pixels show the backdrop
/// (color 0 of palette 0). The metatile sheets of a tileset pair are cached and re-rendered when their files change.
/// </summary>
public static class MapRenderer
{
    public const int BlockSize = 16;

    private sealed record Atlas(string Stamp, RgbaImage Image, int PrimaryCount, int SecondaryCount);

    private static readonly ConcurrentDictionary<string, Atlas> Cache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Renders the whole layout. Missing tilesets render as grey blocks.</summary>
    public static RgbaImage Render(ProjectInfo project, LayoutInfo layout, MapGrid grid)
    {
        var img = new RgbaImage(grid.Width * BlockSize, grid.Height * BlockSize);
        var atlas = GetAtlas(project, layout.PrimaryTileset, layout.SecondaryTileset);
        int perPrimary = project.Constants.NumMetatilesInPrimary;
        var grey = new Rgba32(96, 96, 96);
        for (int y = 0; y < grid.Height; y++)
            for (int x = 0; x < grid.Width; x++)
            {
                int id = grid.MetatileId(x, y);
                // Atlas slots: primary metatiles at their id, secondary metatiles at NUM_METATILES_IN_PRIMARY + local id = their id.
                int slot = atlas == null ? -1
                    : id < perPrimary ? (id < atlas.PrimaryCount ? id : -1)
                    : (id - perPrimary < atlas.SecondaryCount ? id : -1);
                if (atlas == null || slot < 0)
                {
                    for (int py = 0; py < BlockSize; py++)
                        for (int px = 0; px < BlockSize; px++)
                            img[x * BlockSize + px, y * BlockSize + py] = ((px ^ py) & 4) == 0 ? grey : new Rgba32(128, 128, 128);
                    continue;
                }
                img.Blit(atlas.Image, (slot % 32) * BlockSize, (slot / 32) * BlockSize, x * BlockSize, y * BlockSize, BlockSize, BlockSize);
            }
        return img;
    }

    /// <summary>Loads the map's layout + blockdata and renders it.</summary>
    public static RgbaImage? Render(ProjectInfo project, MapScene scene) =>
        scene.Layout != null && scene.Grid != null ? Render(project, scene.Layout, scene.Grid) : null;

    public static void ClearCache() => Cache.Clear();

    private static string Stamp(ProjectInfo project, TilesetInfo? t)
    {
        if (t == null || !t.IsFullyResolved) return "-";
        long ticks = 0;
        foreach (var rel in new[] { t.TilesImagePath!, t.MetatilesPath! }.Concat(t.PalettePaths))
        {
            var f = project.FullPath(rel);
            if (File.Exists(f)) ticks = Math.Max(ticks, File.GetLastWriteTimeUtc(f).Ticks);
        }
        return $"{t.Name}@{ticks}";
    }

    private static Atlas? GetAtlas(ProjectInfo project, string? primaryName, string? secondaryName)
    {
        var p = primaryName != null ? project.FindTileset(primaryName) : null;
        var s = secondaryName != null ? project.FindTileset(secondaryName) : null;
        if (p is not { IsFullyResolved: true }) return null;
        string key = $"{project.Root}|{primaryName}|{secondaryName}";
        string stamp = Stamp(project, p) + "|" + Stamp(project, s);
        if (Cache.TryGetValue(key, out var cached) && cached.Stamp == stamp) return cached;
        TilesetData primary;
        TilesetData? secondary = null;
        try
        {
            primary = project.LoadTilesetData(p);
            if (s is { IsFullyResolved: true }) secondary = project.LoadTilesetData(s);
        }
        catch (Exception ex) when (ex is IOException or FormatException or InvalidOperationException or UnauthorizedAccessException)
        {
            return null;
        }
        var atlas = BuildAtlas(project.Constants, primary, secondary, stamp);
        Cache[key] = atlas;
        return atlas;
    }

    /// <summary>All metatiles of the pair in one sheet, 32 per row: primary at 0…, secondary at NUM_METATILES_IN_PRIMARY….</summary>
    private static Atlas BuildAtlas(FieldmapConstants c, TilesetData primary, TilesetData? secondary, string stamp)
    {
        int pCount = Math.Min(primary.MetatileCount, c.NumMetatilesInPrimary);
        int sCount = secondary == null ? 0 : Math.Min(secondary.MetatileCount, c.MaxSecondaryMetatiles);
        int total = c.NumMetatilesInPrimary + Math.Max(sCount, 1);
        var img = new RgbaImage(32 * BlockSize, (total + 31) / 32 * BlockSize);
        var backdrop = Color(c, primary, secondary, 0, 0) with { A = 255 };
        for (int m = 0; m < pCount; m++) Draw(img, m, c, primary, secondary, primary, m, backdrop);
        for (int m = 0; m < sCount; m++) Draw(img, c.NumMetatilesInPrimary + m, c, primary, secondary, secondary!, m, backdrop);
        return new Atlas(stamp, img, pCount, sCount);
    }

    private static Rgba32 Color(FieldmapConstants c, TilesetData primary, TilesetData? secondary, int palette, int index)
    {
        var src = palette < c.NumPalsInPrimary ? primary : secondary ?? primary;
        var pal = palette < src.Palettes.Count ? src.Palettes[palette] : null;
        var col = pal != null && index < pal.Length ? pal[index] : new Rgba32(0, 0, 0);
        return GbaColor.ToDisplayed(col with { A = 255 });
    }

    private static void Draw(RgbaImage target, int slot, FieldmapConstants c, TilesetData primary, TilesetData? secondary,
        TilesetData owner, int metatile, Rgba32 backdrop)
    {
        int ox = (slot % 32) * BlockSize, oy = (slot / 32) * BlockSize;
        for (int y = 0; y < BlockSize; y++)
            for (int x = 0; x < BlockSize; x++)
                target[ox + x, oy + y] = backdrop;
        int layers = owner.TilesPerMetatile / 4;
        for (int layer = 0; layer < layers; layer++)
            for (int q = 0; q < 4; q++)
            {
                int index = metatile * owner.TilesPerMetatile + layer * 4 + q;
                if (index >= owner.MetatileEntries.Count) continue;
                var e = owner.MetatileEntries[index];
                byte[]? tile = e.TileId < c.NumTilesInPrimary
                    ? (e.TileId < primary.Tiles.Count ? primary.Tiles[e.TileId] : null)
                    : secondary != null && e.TileId - c.NumTilesInPrimary < secondary.Tiles.Count ? secondary.Tiles[e.TileId - c.NumTilesInPrimary] : null;
                if (tile == null) continue;
                int qx = ox + (q % 2) * 8, qy = oy + (q / 2) * 8;
                for (int y = 0; y < 8; y++)
                    for (int x = 0; x < 8; x++)
                    {
                        int sx = e.HFlip ? 7 - x : x, sy = e.VFlip ? 7 - y : y;
                        int ci = tile[sy * 8 + sx];
                        if (ci == 0) continue; // color 0 is transparent on every BG layer
                        target[qx + x, qy + y] = Color(c, primary, secondary, e.Palette, ci);
                    }
            }
    }
}
