using System.Globalization;
using System.Text.RegularExpressions;

namespace TilesetEditor.Core.Text;

/// <summary>The project's charmap.txt: which characters and {PLACEHOLDERS} the game's text engine knows.</summary>
public sealed partial class Charmap
{
    private readonly Dictionary<string, byte[]> _chars = new(StringComparer.Ordinal);
    private readonly Dictionary<string, byte[]> _names = new(StringComparer.Ordinal);

    [GeneratedRegex(@"^'(.+)'\s*=\s*([0-9A-Fa-f]{2}(?:\s+[0-9A-Fa-f]{2})*)\s*(?:@.*)?$")]
    private static partial Regex CharLine();

    [GeneratedRegex(@"^([A-Z_][A-Z0-9_]*)\s*=\s*([0-9A-Fa-f]{2}(?:\s+[0-9A-Fa-f]{2})*)\s*(?:@.*)?$")]
    private static partial Regex NameLine();

    public static Charmap Load(string path) => Parse(File.ReadAllText(path));

    public static Charmap Parse(string text)
    {
        var map = new Charmap();
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.TrimEnd('\r').Trim();
            if (line.Length == 0 || line.StartsWith('@')) continue;
            var m = CharLine().Match(line);
            if (m.Success)
            {
                // charmap.txt escapes the apostrophe as '\'' (and may escape " and \ the same way).
                var key = m.Groups[1].Value switch { "\\'" => "'", "\\\"" => "\"", "\\\\" => "\\", var k => k };
                map._chars.TryAdd(key, Bytes(m.Groups[2].Value));
                continue;
            }
            m = NameLine().Match(line);
            if (m.Success) map._names.TryAdd(m.Groups[1].Value, Bytes(m.Groups[2].Value));
        }
        return map;
    }

    private static byte[] Bytes(string hex) =>
        hex.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(h => byte.Parse(h, NumberStyles.HexNumber, CultureInfo.InvariantCulture)).ToArray();

    public bool HasChar(string c) => _chars.ContainsKey(c);
    public bool HasName(string name) => _names.ContainsKey(name);

    /// <summary>Glyph index for a printable character (its first byte), or null if unknown.</summary>
    public int? GlyphOf(string c) => _chars.TryGetValue(c, out var b) && b.Length == 1 ? b[0] : null;

    public int CharCount => _chars.Count;
}
