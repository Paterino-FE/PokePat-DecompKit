using System.Text.Json;
using TilesetEditor.Core.Imaging;

namespace TilesetEditor.Core.Text;

/// <summary>
/// Glyph widths (tools/poryscript/font_config.json, the same data Poryscript wraps text with) and the glyph
/// sheet (graphics/fonts/latin_normal.png, 16×16 cells, 16 per row, cell index = charmap byte).
/// </summary>
public sealed class GbaFont
{
    public const int CellSize = 16;
    public const int DefaultWidth = 6;

    private readonly Dictionary<string, int> _widths;

    public string FontId { get; }
    public int MaxLineLength { get; }
    public int NumLines { get; }
    public int CursorOverlapWidth { get; }
    /// <summary>Indexed glyph sheet; null when only measuring.</summary>
    public PngImage? Glyphs { get; init; }

    private GbaFont(string id, Dictionary<string, int> widths, int maxLine, int numLines, int cursorOverlap)
    {
        FontId = id;
        _widths = widths;
        MaxLineLength = maxLine;
        NumLines = numLines;
        CursorOverlapWidth = cursorOverlap;
    }

    /// <summary>Emerald defaults when the project has no font_config.json.</summary>
    public static GbaFont Fallback() => new("1_latin_rse", new Dictionary<string, int> { [" "] = 3 }, 208, 2, 0);

    public static GbaFont LoadFromProject(string projectRoot)
    {
        var cfg = Path.Combine(projectRoot, "tools", "poryscript", "font_config.json");
        GbaFont font = File.Exists(cfg) ? ParseConfig(File.ReadAllText(cfg)) : Fallback();
        var sheet = Path.Combine(projectRoot, "graphics", "fonts", "latin_normal.png");
        return File.Exists(sheet) ? font.WithGlyphs(Png.Load(sheet)) : font;
    }

    public GbaFont WithGlyphs(PngImage glyphs) =>
        new(FontId, _widths, MaxLineLength, NumLines, CursorOverlapWidth) { Glyphs = glyphs };

    public static GbaFont ParseConfig(string json, string? fontId = null)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        fontId ??= root.TryGetProperty("defaultFontId", out var d) ? d.GetString() : null;
        var fonts = root.GetProperty("fonts");
        var font = fontId != null && fonts.TryGetProperty(fontId, out var f) ? f : fonts.EnumerateObject().First().Value;
        var widths = new Dictionary<string, int>(StringComparer.Ordinal);
        if (font.TryGetProperty("widths", out var w))
            foreach (var p in w.EnumerateObject()) widths[p.Name] = p.Value.GetInt32();
        int Int(string k, int def) => font.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : def;
        return new GbaFont(fontId ?? "?", widths, Int("maxLineLength", 208), Int("numLines", 2), Int("cursorOverlapWidth", 0));
    }

    /// <summary>Width of one text unit: a character or a {PLACEHOLDER}.</summary>
    public int Width(string unit) => _widths.TryGetValue(unit, out var w) ? w : unit.StartsWith('{') ? 60 : DefaultWidth;

    public int Width(IEnumerable<string> units) => units.Sum(Width);
}
