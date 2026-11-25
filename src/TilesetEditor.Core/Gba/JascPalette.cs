using System.Globalization;
using System.Text;
using TilesetEditor.Core.Imaging;

namespace TilesetEditor.Core.Gba;

/// <summary>Paint Shop Pro (JASC-PAL) palette as used by pokeemerald and Porymap.</summary>
public static class JascPalette
{
    public static Rgba32[] Load(string path) => Parse(File.ReadAllText(path), path);

    public static Rgba32[] Parse(string text, string sourceName = "<palette>")
    {
        var lines = text.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToArray();
        if (lines.Length < 3 || lines[0] != "JASC-PAL")
            throw new FormatException($"{sourceName}: not a JASC-PAL file.");
        if (!int.TryParse(lines[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out int count) || count < 0)
            throw new FormatException($"{sourceName}: invalid color count.");
        if (lines.Length < 3 + count)
            throw new FormatException($"{sourceName}: expected {count} colors, found {lines.Length - 3}.");
        var result = new Rgba32[count];
        for (int i = 0; i < count; i++)
        {
            var parts = lines[3 + i].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 3 ||
                !byte.TryParse(parts[0], CultureInfo.InvariantCulture, out var r) ||
                !byte.TryParse(parts[1], CultureInfo.InvariantCulture, out var g) ||
                !byte.TryParse(parts[2], CultureInfo.InvariantCulture, out var b))
                throw new FormatException($"{sourceName}: color line {i + 1} is invalid: '{lines[3 + i]}'.");
            result[i] = new Rgba32(r, g, b);
        }
        return result;
    }

    /// <summary>Serializes with CRLF line endings, which is what the project's .gitattributes enforces for *.pal.</summary>
    public static string Serialize(IReadOnlyList<Rgba32> colors)
    {
        var sb = new StringBuilder();
        sb.Append("JASC-PAL\r\n0100\r\n");
        sb.Append(colors.Count.ToString(CultureInfo.InvariantCulture)).Append("\r\n");
        foreach (var c in colors)
            sb.Append(CultureInfo.InvariantCulture, $"{c.R} {c.G} {c.B}\r\n");
        return sb.ToString();
    }
}
