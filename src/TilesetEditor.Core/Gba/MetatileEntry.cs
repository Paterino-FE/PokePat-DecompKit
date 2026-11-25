namespace TilesetEditor.Core.Gba;

/// <summary>One 16-bit tilemap entry inside metatiles.bin.</summary>
public readonly record struct MetatileEntry(ushort Raw)
{
    public int TileId => Raw & 0x3FF;
    public bool HFlip => (Raw & 0x400) != 0;
    public bool VFlip => (Raw & 0x800) != 0;
    public int Palette => (Raw >> 12) & 0xF;

    public static MetatileEntry Create(int tileId, int palette, bool hflip = false, bool vflip = false) =>
        new((ushort)((tileId & 0x3FF) | (hflip ? 0x400 : 0) | (vflip ? 0x800 : 0) | ((palette & 0xF) << 12)));

    public override string ToString() => $"tile {TileId} pal {Palette}{(HFlip ? " H" : "")}{(VFlip ? " V" : "")}";
}

/// <summary>Metatile attribute layout derived from the project (include/global.fieldmap.h).</summary>
public sealed record AttributeLayout(int SizeBytes, uint BehaviorMask, uint LayerTypeMask, int LayerTypeShift)
{
    public static readonly AttributeLayout EmeraldDefault = new(2, 0x00FF, 0xF000, 12);

    public int Behavior(uint raw) => (int)(raw & BehaviorMask);
    public int LayerType(uint raw) => (int)((raw & LayerTypeMask) >> LayerTypeShift);
}

public static class LayerTypes
{
    public static string Describe(int layerType) => layerType switch
    {
        0 => "Normal (Middle/Top)",
        1 => "Covered (Bottom/Middle)",
        2 => "Split (Bottom/Top)",
        _ => $"Type {layerType}",
    };
}
