namespace TilesetEditor.Core.Imaging;

/// <summary>An 8-bit-per-channel RGBA color.</summary>
public readonly record struct Rgba32(byte R, byte G, byte B, byte A = 255)
{
    public static readonly Rgba32 Transparent = new(0, 0, 0, 0);
    public static readonly Rgba32 Magenta = new(255, 0, 255);

    public bool IsFullyTransparent => A == 0;

    /// <summary>The color with alpha forced to 255, used as key when alpha does not matter.</summary>
    public Rgba32 Opaque => new(R, G, B, 255);

    public bool SameRgb(Rgba32 other) => R == other.R && G == other.G && B == other.B;

    public string ToHex() => $"#{R:X2}{G:X2}{B:X2}";

    public override string ToString() => A == 255 ? ToHex() : $"{ToHex()} (A={A})";

    /// <summary>Parses "#RRGGBB", "RRGGBB" or "R,G,B".</summary>
    public static bool TryParse(string? text, out Rgba32 color)
    {
        color = default;
        if (string.IsNullOrWhiteSpace(text)) return false;
        text = text.Trim();
        if (text.Contains(','))
        {
            var parts = text.Split(',', StringSplitOptions.TrimEntries);
            if (parts.Length != 3) return false;
            if (!byte.TryParse(parts[0], out var r) || !byte.TryParse(parts[1], out var g) || !byte.TryParse(parts[2], out var b))
                return false;
            color = new Rgba32(r, g, b);
            return true;
        }
        if (text.StartsWith('#')) text = text[1..];
        if (text.Length != 6) return false;
        try
        {
            color = new Rgba32(Convert.ToByte(text[0..2], 16), Convert.ToByte(text[2..4], 16), Convert.ToByte(text[4..6], 16));
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }
}
