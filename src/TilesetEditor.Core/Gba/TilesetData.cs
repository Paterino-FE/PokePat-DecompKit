using System.Buffers.Binary;
using TilesetEditor.Core.Imaging;

namespace TilesetEditor.Core.Gba;

/// <summary>
/// The Porymap-format data of one tileset: 8x8 tiles (4bpp indices), 16 palettes, metatile entries and attributes.
/// Used for the primary context as well as for the compiled secondary.
/// </summary>
public sealed class TilesetData
{
    public const int TileSize = 8;

    public required string Name { get; init; }
    /// <summary>Tile pixel indices (0..15), 64 per tile.</summary>
    public required IReadOnlyList<byte[]> Tiles { get; init; }
    /// <summary>Palettes as stored in the .pal files (8-bit values).</summary>
    public required IReadOnlyList<Rgba32[]> Palettes { get; init; }
    public required IReadOnlyList<MetatileEntry> MetatileEntries { get; init; }
    public required IReadOnlyList<uint> Attributes { get; init; }
    public required int TilesPerMetatile { get; init; }

    public int MetatileCount => MetatileEntries.Count / TilesPerMetatile;

    public MetatileEntry GetEntry(int metatile, int slot) => MetatileEntries[metatile * TilesPerMetatile + slot];

    /// <summary>Loads tiles.png, palettes/NN.pal, metatiles.bin and metatile_attributes.bin.</summary>
    public static TilesetData Load(string name, string tilesPng, IReadOnlyList<string> palettePaths, string metatilesBin,
        string attributesBin, int tilesPerMetatile, int attributeSize) =>
        FromBytes(name, File.ReadAllBytes(tilesPng), palettePaths.Select(p => File.Exists(p) ? JascPalette.Load(p) : new Rgba32[16]).ToList(),
            File.ReadAllBytes(metatilesBin), File.ReadAllBytes(attributesBin), tilesPerMetatile, attributeSize, tilesPng, metatilesBin, attributesBin);

    /// <summary>The same from file contents (e.g. a planned import that is not written yet).</summary>
    public static TilesetData FromBytes(string name, byte[] tilesPngBytes, IReadOnlyList<Rgba32[]> palettesIn, byte[] mt, byte[] at,
        int tilesPerMetatile, int attributeSize, string tilesPng = "tiles.png", string metatilesBin = "metatiles.bin", string attributesBin = "metatile_attributes.bin")
    {
        var png = Png.Decode(tilesPngBytes);
        if (!png.IsIndexed || png.Indices == null)
            throw new FormatException($"'{tilesPng}' is not an indexed PNG.");
        if (png.Width % TileSize != 0 || png.Height % TileSize != 0)
            throw new FormatException($"'{tilesPng}': dimensions {png.Width}x{png.Height} are not a multiple of 8.");
        var tiles = SliceTiles(png.Width, png.Height, png.Indices);

        var palettes = new List<Rgba32[]>();
        foreach (var p in palettesIn)
        {
            var pal = p;
            if (pal.Length < 16) Array.Resize(ref pal, 16);
            palettes.Add(pal);
        }

        if (mt.Length % (2 * tilesPerMetatile) != 0)
            throw new FormatException($"'{metatilesBin}': size {mt.Length} is not a multiple of {2 * tilesPerMetatile} bytes.");
        var entries = new MetatileEntry[mt.Length / 2];
        for (int i = 0; i < entries.Length; i++) entries[i] = new MetatileEntry(BinaryPrimitives.ReadUInt16LittleEndian(mt.AsSpan(i * 2)));

        if (at.Length % attributeSize != 0)
            throw new FormatException($"'{attributesBin}': size {at.Length} is not a multiple of {attributeSize}.");
        var attrs = new uint[at.Length / attributeSize];
        for (int i = 0; i < attrs.Length; i++)
        {
            attrs[i] = attributeSize switch
            {
                1 => at[i],
                2 => BinaryPrimitives.ReadUInt16LittleEndian(at.AsSpan(i * 2)),
                4 => BinaryPrimitives.ReadUInt32LittleEndian(at.AsSpan(i * 4)),
                _ => throw new ArgumentOutOfRangeException(nameof(attributeSize)),
            };
        }

        return new TilesetData
        {
            Name = name,
            Tiles = tiles,
            Palettes = palettes,
            MetatileEntries = entries,
            Attributes = attrs,
            TilesPerMetatile = tilesPerMetatile,
        };
    }

    /// <summary>Splits an indexed image into 8x8 tiles in row-major order. Only the low nibble is kept (like gbagfx).</summary>
    public static List<byte[]> SliceTiles(int width, int height, byte[] indices)
    {
        var tiles = new List<byte[]>();
        for (int ty = 0; ty < height / TileSize; ty++)
            for (int tx = 0; tx < width / TileSize; tx++)
            {
                var t = new byte[64];
                for (int y = 0; y < 8; y++)
                    for (int x = 0; x < 8; x++)
                        t[y * 8 + x] = (byte)(indices[(ty * 8 + y) * width + tx * 8 + x] & 0xF);
                tiles.Add(t);
            }
        return tiles;
    }
}
