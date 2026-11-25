using System.Buffers.Binary;
using System.Text.RegularExpressions;
using TilesetEditor.Core.Gba;
using TilesetEditor.Core.Imaging;
using TilesetEditor.Core.Project;

namespace TilesetEditor.Core.Tilesets;

public enum TilesetKind { Primary, Secondary }

/// <summary>
/// A finished tileset in Porymap's format as people share it (asset repos, other hacks): a tile image, some or all of the
/// 16 palettes (NN.pal), metatiles.bin and metatile_attributes.bin – often incomplete ("use these palettes with General").
/// <see cref="Read"/> finds the files in a folder and works out what it is: primary or secondary, dual or triple layer,
/// which existing tileset it is based on.
/// </summary>
public sealed partial class TilesetPackage
{
    public required string Folder { get; init; }
    /// <summary>The indexed tile image (null if the folder has none).</summary>
    public string? TilesImage { get; init; }
    public int TileCount { get; init; }
    /// <summary>Palette slot → .pal file found in the folder.</summary>
    public required IReadOnlyDictionary<int, string> Palettes { get; init; }
    public string? MetatilesFile { get; init; }
    public string? AttributesFile { get; init; }
    /// <summary>8 (dual layer) or 12 (triple layer) tiles per metatile, as found in metatiles.bin; null if unknown.</summary>
    public int? TilesPerMetatile { get; init; }
    public int MetatileCount { get; init; }
    public TilesetKind SuggestedKind { get; init; }
    /// <summary>The existing tileset this one is a variant of (e.g. gTileset_General for a snow recolour), or null.</summary>
    public string? SuggestedBase { get; init; }
    public required string SuggestedName { get; init; }
    /// <summary>The folder holds RGBA artwork (layer PNGs) instead of a finished tileset: it goes through Porytiles.</summary>
    public bool IsArtwork { get; init; }
    public string? ReadmeText { get; init; }
    public List<string> Notes { get; } = [];
    public List<string> Problems { get; } = [];

    public bool HasTiles => TilesImage != null;
    public bool HasMetatiles => MetatilesFile != null;
    public bool IsComplete => HasTiles && HasMetatiles && Palettes.Count >= 13;

    [GeneratedRegex(@"^(\d{1,2})\.pal$", RegexOptions.IgnoreCase)]
    private static partial Regex PaletteFile();

    [GeneratedRegex(@"demo|preview|screenshot|example|showcase|sample", RegexOptions.IgnoreCase)]
    private static partial Regex PreviewName();

    public static TilesetPackage Read(string folder, ProjectInfo project)
    {
        if (!Directory.Exists(folder)) throw new DirectoryNotFoundException($"Folder not found: {folder}");
        var files = Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories)
            .Where(f => Path.GetRelativePath(folder, f).Count(c => c is '/' or '\\') <= 1) // the folder and one level below (palettes/)
            .ToList();
        string? Find(string name) => files.FirstOrDefault(f => Path.GetFileName(f).Equals(name, StringComparison.OrdinalIgnoreCase));

        // Palettes: NN.pal next to the image or in palettes/.
        var palettes = new SortedDictionary<int, string>();
        foreach (var f in files)
            if (PaletteFile().Match(Path.GetFileName(f)) is { Success: true } m && int.Parse(m.Groups[1].Value) is var slot and < 16)
                palettes.TryAdd(slot, f);

        // Tile image: tiles.png, else an indexed 128-px wide image that is not a preview screenshot.
        var pngs = files.Where(f => f.EndsWith(".png", StringComparison.OrdinalIgnoreCase)).ToList();
        string? tiles = Find("tiles.png");
        PngImage? tilesPng = null;
        var rgbaLayers = new List<string>();
        IEnumerable<string> tileCandidates = tiles != null ? [tiles] : pngs.OrderBy(p => PreviewName().IsMatch(Path.GetFileName(p)) ? 1 : 0).ToList();
        foreach (var f in tileCandidates)
        {
            PngImage png;
            try { png = Png.Load(f); } catch (Exception ex) when (ex is IOException or FormatException or InvalidDataException) { continue; }
            if (png.IsIndexed && png.Width % 8 == 0 && png.Height % 8 == 0 && png.Width <= 256 && !PreviewName().IsMatch(Path.GetFileName(f)))
            {
                tiles = f;
                tilesPng = png;
                break;
            }
            if (!png.IsIndexed && !PreviewName().IsMatch(Path.GetFileName(f))) rgbaLayers.Add(f);
        }
        if (tiles != null && tilesPng == null)
        {
            try { tilesPng = Png.Load(tiles); } catch (Exception ex) when (ex is IOException or FormatException or InvalidDataException) { tiles = null; }
            if (tilesPng is { IsIndexed: false }) { rgbaLayers.Add(tiles!); tiles = null; tilesPng = null; }
        }
        int tileCount = tilesPng == null ? 0 : tilesPng.Width / 8 * (tilesPng.Height / 8);

        string? metatiles = Find("metatiles.bin");
        string? attributes = Find("metatile_attributes.bin");
        string? readme = files.FirstOrDefault(f => Path.GetFileName(f).StartsWith("readme", StringComparison.OrdinalIgnoreCase));
        var readmeText = readme != null ? File.ReadAllText(readme) : null;

        // Base tileset: the longest existing tileset name mentioned by the file names, the folder or the readme.
        var hints = string.Join(" ", files.Select(Path.GetFileNameWithoutExtension).Append(Path.GetFileName(folder)).Append(readmeText ?? ""));
        var hintKey = Regex.Replace(hints, "[^A-Za-z0-9]", "").ToLowerInvariant();
        var baseTileset = project.Tilesets
            .Select(t => (t, key: Regex.Replace(TilesetNaming.StripPrefix(t.Name), "[^A-Za-z0-9]", "").ToLowerInvariant()))
            .Where(x => x.key.Length >= 4 && hintKey.Contains(x.key))
            .OrderByDescending(x => x.key.Length).ThenBy(x => x.t.IsSecondary)
            .Select(x => x.t).FirstOrDefault();

        // Layers: size of metatiles.bin against the attribute count, the base tileset or the limits.
        int? tpm = null;
        int metatileCount = 0;
        long mtBytes = metatiles != null ? new FileInfo(metatiles).Length : 0;
        if (metatiles != null)
        {
            var candidates = new[] { 8, 12 }.Where(n => mtBytes % (2 * n) == 0).ToList();
            if (attributes != null && candidates.Count > 1)
            {
                long ab = new FileInfo(attributes).Length;
                foreach (var size in new[] { project.Attributes.SizeBytes, 2, 4 }.Distinct())
                    if (ab % size == 0 && candidates.FirstOrDefault(n => mtBytes / (2 * n) == ab / size) is var hit and > 0) { candidates = [hit]; break; }
            }
            if (candidates.Count > 1 && baseTileset?.MetatilesPath is { } bm && File.Exists(project.FullPath(bm)))
            {
                long baseCount = new FileInfo(project.FullPath(bm)).Length / (2 * project.Constants.NumTilesPerMetatile);
                if (candidates.FirstOrDefault(n => mtBytes / (2 * n) == baseCount) is var hit and > 0) candidates = [hit];
            }
            if (candidates.Count > 1)
            {
                // Both fit: prefer the one that keeps within the limits, then the project's own layout.
                var within = candidates.Where(n => mtBytes / (2 * n) <= project.Constants.NumMetatilesTotal - project.Constants.NumMetatilesInPrimary
                                                   || mtBytes / (2 * n) <= project.Constants.NumMetatilesInPrimary).ToList();
                candidates = within.Contains(project.Constants.NumTilesPerMetatile) ? [project.Constants.NumTilesPerMetatile] : within.Take(1).ToList();
            }
            if (candidates.Count == 1)
            {
                tpm = candidates[0];
                metatileCount = (int)(mtBytes / (2 * tpm.Value));
            }
        }

        // Kind: as the base, else from the palette slots, else from the size.
        var c = project.Constants;
        TilesetKind kind = baseTileset != null ? (baseTileset.IsSecondary ? TilesetKind.Secondary : TilesetKind.Primary)
            : palettes.Count > 0 && palettes.Keys.All(k => k < c.NumPalsInPrimary) ? TilesetKind.Primary
            : palettes.Count > 0 && palettes.Keys.Any(k => k >= c.NumPalsInPrimary && k < c.NumPalsTotal) && !palettes.Keys.Any(k => k < c.NumPalsInPrimary) ? TilesetKind.Secondary
            : tileCount > c.NumTilesTotal - c.NumTilesInPrimary ? TilesetKind.Primary
            : TilesetKind.Secondary;

        var suggested = TilesetNaming.Suggest(Path.GetFileName(folder.TrimEnd('/', '\\')));
        if (baseTileset != null && Regex.Match(hints, TilesetNaming.Prefix + @"(\w+?)_?Tiles", RegexOptions.IgnoreCase) is { Success: true } named)
            suggested = TilesetNaming.Prefix + named.Groups[1].Value;

        var package = new TilesetPackage
        {
            Folder = folder,
            TilesImage = tiles,
            TileCount = tileCount,
            Palettes = palettes,
            MetatilesFile = metatiles,
            AttributesFile = attributes,
            TilesPerMetatile = tpm,
            MetatileCount = metatileCount,
            SuggestedKind = kind,
            SuggestedBase = baseTileset?.Name,
            SuggestedName = suggested,
            IsArtwork = tiles == null && metatiles == null && rgbaLayers.Count > 0,
            ReadmeText = readmeText,
        };
        if (package.IsArtwork)
            package.Notes.Add($"No finished tileset here, but {rgbaLayers.Count} RGBA image(s): build a new tileset from them with Porytiles.");
        if (metatiles != null && tpm == null)
            package.Problems.Add($"metatiles.bin has {mtBytes} bytes – that is neither a whole number of dual-layer (16 bytes) nor triple-layer (24 bytes) metatiles.");
        if (tpm != null && tpm != c.NumTilesPerMetatile)
            package.Notes.Add($"metatiles.bin is {(tpm == 12 ? "triple" : "dual")}-layer, the project is {(c.NumTilesPerMetatile == 12 ? "triple" : "dual")}-layer: the metatiles are converted.");
        if (tiles != null && tilesPng != null && tilesPng.BitDepth > 4 && tilesPng.Indices!.Any(i => (i & 0x0F) != i && (i >> 4) != 0))
            package.Notes.Add("The tile image is 8-bit indexed; only the low 4 bits of each pixel are used, as in the game.");
        if (baseTileset == null && !package.IsComplete && !package.IsArtwork)
            package.Notes.Add("The folder does not contain a complete tileset: choose the existing tileset it is based on, its files fill the gaps.");
        return package;
    }

    // ------------------------------------------------------------------ metatile layer conversion

    /// <summary>Layer types of the 2-layer metatile format (METATILE_LAYER_TYPE_*).</summary>
    public enum LayerType { Normal = 0, Covered = 1, Split = 2 }

    public sealed record LayerConversion(ushort[] Entries, LayerType[] LayerTypes, int Lossy);

    /// <summary>
    /// Triple-layer (12 entries: bottom, middle, top) → dual-layer (8 entries + layer type): two used layers are kept with the
    /// matching layer type (middle+top = normal, bottom+middle = covered, bottom+top = split). Metatiles that use all three
    /// layers keep middle and top and are counted in <see cref="LayerConversion.Lossy"/>.
    /// </summary>
    public static LayerConversion TripleToDual(ReadOnlySpan<ushort> triple)
    {
        int count = triple.Length / 12;
        var result = new ushort[count * 8];
        var types = new LayerType[count];
        int lossy = 0;
        for (int m = 0; m < count; m++)
        {
            var mt = triple.Slice(m * 12, 12);
            bool b = Used(mt, 0), mid = Used(mt, 1), top = Used(mt, 2);
            (int first, int second, LayerType type) = (b, mid, top) switch
            {
                (false, _, _) => (1, 2, LayerType.Normal),
                (true, _, false) => (0, 1, LayerType.Covered),
                (true, false, true) => (0, 2, LayerType.Split),
                _ => (1, 2, LayerType.Normal),
            };
            if (b && mid && top) lossy++;
            for (int i = 0; i < 4; i++)
            {
                result[m * 8 + i] = mt[first * 4 + i];
                result[m * 8 + 4 + i] = mt[second * 4 + i];
            }
            types[m] = type;
        }
        return new LayerConversion(result, types, lossy);
    }

    /// <summary>A layer of a metatile is used if one of its four entries names a tile other than 0 (the empty tile).</summary>
    private static bool Used(ReadOnlySpan<ushort> metatile, int layer)
    {
        for (int i = 0; i < 4; i++) if ((metatile[layer * 4 + i] & 0x3FF) != 0) return true;
        return false;
    }

    /// <summary>Dual-layer + layer types → triple-layer: the two layers go where their layer type puts them, the third stays empty.</summary>
    public static ushort[] DualToTriple(ReadOnlySpan<ushort> dual, IReadOnlyList<LayerType> types)
    {
        int count = dual.Length / 8;
        var result = new ushort[count * 12];
        for (int m = 0; m < count; m++)
        {
            var (first, second) = (m < types.Count ? types[m] : LayerType.Normal) switch
            {
                LayerType.Covered => (0, 1),
                LayerType.Split => (0, 2),
                _ => (1, 2),
            };
            for (int i = 0; i < 4; i++)
            {
                result[m * 12 + first * 4 + i] = dual[m * 8 + i];
                result[m * 12 + second * 4 + i] = dual[m * 8 + 4 + i];
            }
        }
        return result;
    }

    public static ushort[] ReadEntries(byte[] bytes)
    {
        var e = new ushort[bytes.Length / 2];
        for (int i = 0; i < e.Length; i++) e[i] = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(i * 2));
        return e;
    }

    public static byte[] WriteEntries(ReadOnlySpan<ushort> entries)
    {
        var b = new byte[entries.Length * 2];
        for (int i = 0; i < entries.Length; i++) BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(i * 2), entries[i]);
        return b;
    }

    public static uint[] ReadAttributes(byte[] bytes, int size)
    {
        var a = new uint[bytes.Length / size];
        for (int i = 0; i < a.Length; i++)
            a[i] = size == 4 ? BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(i * 4)) : size == 2 ? BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(i * 2)) : bytes[i];
        return a;
    }

    public static byte[] WriteAttributes(IReadOnlyList<uint> attrs, int size)
    {
        var b = new byte[attrs.Count * size];
        for (int i = 0; i < attrs.Count; i++)
        {
            if (size == 4) BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(i * 4), attrs[i]);
            else if (size == 2) BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(i * 2), (ushort)attrs[i]);
            else b[i] = (byte)attrs[i];
        }
        return b;
    }
}
