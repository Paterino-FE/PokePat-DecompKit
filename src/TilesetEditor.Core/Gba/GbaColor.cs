using TilesetEditor.Core.Imaging;

namespace TilesetEditor.Core.Gba;

/// <summary>Conversion between 24-bit RGB and the GBA's 15-bit BGR555 format.</summary>
public static class GbaColor
{
    /// <summary>Same truncation gbagfx uses when converting .pal to .gbapal: the top 5 bits of each channel.</summary>
    public static ushort ToBgr555(Rgba32 c) => (ushort)((c.R >> 3) | ((c.G >> 3) << 5) | ((c.B >> 3) << 10));

    /// <summary>Expands BGR555 to 8-bit channels the way the hardware/most emulators display it.</summary>
    public static Rgba32 FromBgr555(ushort v)
    {
        static byte Expand(int c5) => (byte)((c5 << 3) | (c5 >> 2));
        return new Rgba32(Expand(v & 0x1F), Expand((v >> 5) & 0x1F), Expand((v >> 10) & 0x1F));
    }

    /// <summary>The color as it actually appears on the GBA.</summary>
    public static Rgba32 ToDisplayed(Rgba32 c) => c.A == 0 ? c : FromBgr555(ToBgr555(c)) with { A = c.A };

    /// <summary>Snaps a color to the 5-bit grid while keeping it in the 8-bit space (multiples of 8).</summary>
    public static Rgba32 Snap(Rgba32 c) => new((byte)(c.R & 0xF8), (byte)(c.G & 0xF8), (byte)(c.B & 0xF8), c.A);
}
