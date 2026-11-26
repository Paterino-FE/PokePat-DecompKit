using System.Globalization;
using System.Text.Json;
using TilesetEditor.Core.Imaging;
using TilesetEditor.Core.Project;

namespace TilesetEditor.Core.Platinum;

/// <summary>
/// The message font of pokeplatinum (res/fonts/font_message.png: 16×16 glyphs, 16 per row, in charmap order; widths in
/// font_message.json) and a preview of the field message box: two lines of 16 pixels, \n = next line, \r = next box
/// (after a key press), \f = scroll up one line.
/// </summary>
public sealed class PlatinumFont
{
    private readonly PngImage _glyphs;
    private readonly int[] _widths;
    private readonly Dictionary<string, int> _codes;

    public const int BoxWidth = 216, LineHeight = 16, Lines = 2;

    private PlatinumFont(PngImage glyphs, int[] widths, Dictionary<string, int> codes)
    {
        _glyphs = glyphs;
        _widths = widths;
        _codes = codes;
    }

    public static PlatinumFont? Load(string root)
    {
        string P(string rel) => ProjectPaths.Combine(root, rel);
        if (!File.Exists(P("res/fonts/font_message.png")) || !File.Exists(P("res/fonts/font_message.json")) || !File.Exists(P("tools/msgenc/charmap.txt"))) return null;
        var png = Png.Load(P("res/fonts/font_message.png"));
        using var doc = JsonDocument.Parse(File.ReadAllText(P("res/fonts/font_message.json")));
        var widths = doc.RootElement.GetProperty("glyphWidths").EnumerateArray().Select(e => e.GetInt32()).ToArray();
        var codes = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var raw in File.ReadLines(P("tools/msgenc/charmap.txt")))
        {
            int eq = raw.IndexOf('=');
            if (eq != 4 || raw.StartsWith("//", StringComparison.Ordinal)) continue;
            if (!int.TryParse(raw[..4], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var code)) continue;
            var ch = raw[(eq + 1)..];
            if (ch.Length == 0 || ch.StartsWith('{') || ch.StartsWith('\\')) continue;
            codes.TryAdd(ch, code);
        }
        return new PlatinumFont(png, widths, codes);
    }

    /// <summary>Width of a glyph in pixels (unknown characters count as a space).</summary>
    private int GlyphIndex(string ch) => _codes.TryGetValue(ch, out var code) ? code - 1 : -1;

    public int Width(string ch)
    {
        int g = GlyphIndex(ch);
        return g >= 0 && g < _widths.Length ? _widths[g] : 5;
    }

    /// <summary>Characters of the text the game cannot show (not in the charmap); {COMMANDS} and line codes are fine.</summary>
    public List<string> UnknownCharacters(string text) =>
        Units(text).Where(u => !u.StartsWith('{') && u is not ("\n" or "\r" or "\f") && !_codes.ContainsKey(u)).Distinct().ToList();

    /// <summary>Text width in pixels; {COMMANDS} are counted as a placeholder of 6 characters.</summary>
    public int Measure(string line) => Units(line).Sum(u => u.StartsWith('{') ? 6 * Width("M") : Width(u));

    private static List<string> Units(string s)
    {
        var list = new List<string>();
        var e = StringInfo.GetTextElementEnumerator(s);
        var chars = new List<string>();
        while (e.MoveNext()) chars.Add(e.GetTextElement());
        for (int i = 0; i < chars.Count; i++)
        {
            if (chars[i] == "{")
            {
                int j = chars.IndexOf("}", i);
                if (j > i) { list.Add(string.Concat(chars.Skip(i).Take(j - i + 1))); i = j; continue; }
            }
            list.Add(chars[i]);
        }
        return list;
    }

    /// <summary>One message box per page; returns the pages and a warning for lines that are too wide.</summary>
    public (List<RgbaImage> Pages, List<string> Warnings) RenderMessage(string text, Func<string, string>? placeholder = null)
    {
        var pages = new List<RgbaImage>();
        var warnings = new List<string>();
        var lines = new List<string>();
        var current = new System.Text.StringBuilder();
        void Flush(bool newPage)
        {
            lines.Add(current.ToString());
            current.Clear();
            if (newPage) { pages.Add(DrawBox(lines.TakeLast(Lines).ToList(), placeholder, warnings)); lines.Clear(); }
        }
        foreach (var ch in text)
        {
            switch (ch)
            {
                case '\n': Flush(false); if (lines.Count >= Lines) { pages.Add(DrawBox(lines.TakeLast(Lines).ToList(), placeholder, warnings)); lines.RemoveAt(0); } break;
                case '\r': Flush(true); break;
                case '\f': Flush(false); pages.Add(DrawBox(lines.TakeLast(Lines).ToList(), placeholder, warnings)); lines.RemoveAt(0); break;
                default: current.Append(ch); break;
            }
        }
        if (current.Length > 0 || lines.Count > 0) { lines.Add(current.ToString()); pages.Add(DrawBox(lines.TakeLast(Lines).ToList(), placeholder, warnings)); }
        if (lines.Count > Lines && pages.Count > 0) warnings.Add("More than two lines in one box – use \\r (new box) or \\f (scroll) after the second line.");
        return (pages, warnings.Distinct().ToList());
    }

    /// <summary>
    /// A message page as on the DS top screen (256×192): the box at the bottom and, with the mod "Dialog mugshots", the
    /// 64×64 picture where the game puts it (tile 23,10).
    /// </summary>
    public static RgbaImage Screen(RgbaImage page, RgbaImage? mugshot) => Screen(page, mugshot, false, null);

    /// <summary>
    /// With the mod's 1.1 features: the mugshot on the left (tile 1,10, mirrored like in the game) and a picture (ShowPicture)
    /// centred above the message box.
    /// </summary>
    public static RgbaImage Screen(RgbaImage? page, RgbaImage? mugshot, bool mugshotLeft, RgbaImage? picture)
    {
        var img = new RgbaImage(256, 192);
        for (int y = 0; y < 192; y++)
            for (int x = 0; x < 256; x++)
                img[x, y] = ((x / 16 + y / 16) & 1) == 0 ? new Rgba32(124, 178, 112) : new Rgba32(116, 170, 104);
        void Draw(RgbaImage src, int ox, int oy)
        {
            for (int y = 0; y < src.Height; y++)
                for (int x = 0; x < src.Width; x++)
                    if (src[x, y].A > 0 && ox + x is >= 0 and < 256 && oy + y is >= 0 and < 192) img[ox + x, oy + y] = src[x, y];
        }
        if (picture != null) Draw(picture, (32 - (picture.Width + 7) / 8) / 2 * 8, (19 - (picture.Height + 7) / 8) / 2 * 8);
        if (mugshot != null) Draw(mugshotLeft ? PlatinumMugshots.Mirror(mugshot) : mugshot, mugshotLeft ? 8 : 184, 80);
        if (page == null) return img;
        img.Blit(page, 0, 0, (256 - page.Width) / 2, 192 - page.Height - 4, page.Width, page.Height);
        return img;
    }

    private RgbaImage DrawBox(List<string> lines, Func<string, string>? placeholder, List<string> warnings)
    {
        const int pad = 8;
        var img = new RgbaImage(BoxWidth + pad * 2, Lines * LineHeight + pad * 2);
        var frame = new Rgba32(72, 72, 88);
        var fill = new Rgba32(248, 248, 248);
        for (int y = 0; y < img.Height; y++)
            for (int x = 0; x < img.Width; x++)
                img[x, y] = x < 2 || y < 2 || x >= img.Width - 2 || y >= img.Height - 2 ? frame : fill;
        var fg = new Rgba32(80, 80, 88);
        var shadow = new Rgba32(184, 184, 192);
        for (int l = 0; l < lines.Count && l < Lines; l++)
        {
            int x = pad;
            foreach (var u0 in Units(lines[l]))
            {
                var u = u0.StartsWith('{') && placeholder != null ? placeholder(u0) : u0;
                var shown = !u.StartsWith('{') ? u
                    : u == PlatinumDialogText.PlayerName ? "LUCAS" : u == PlatinumDialogText.RivalName ? "BARRY"
                    : u.StartsWith("{STRVAR", StringComparison.Ordinal) ? "XXXXXX" : "";
                foreach (var ch in Units(shown))
                {
                    int g = GlyphIndex(ch);
                    int w = Width(ch);
                    if (g >= 0) DrawGlyph(img, g, x, pad + l * LineHeight, fg, shadow);
                    x += w;
                }
            }
            if (x - pad > BoxWidth) warnings.Add($"\"{lines[l]}\" is {x - pad} pixels wide; {BoxWidth} fit in the message box.");
        }
        return img;
    }

    private void DrawGlyph(RgbaImage img, int glyph, int ox, int oy, Rgba32 fg, Rgba32 shadow)
    {
        int gx = glyph % 16 * 16, gy = glyph / 16 * 16;
        if (gy + 16 > _glyphs.Height || _glyphs.Indices == null) return;
        for (int y = 0; y < 16; y++)
            for (int x = 0; x < 16; x++)
            {
                int idx = _glyphs.Indices[(gy + y) * _glyphs.Width + gx + x];
                int px = ox + x, py = oy + y;
                if (px >= img.Width || py >= img.Height) continue;
                if (idx == 1) img[px, py] = fg;
                else if (idx == 2) img[px, py] = shadow;
            }
    }
}

/// <summary>
/// Dialog text the way a writer thinks of it: Enter = next line, an empty line = next message box. Converted to and from
/// the game's codes: \n (next line), \f (scroll one line, for the 3rd and later lines of a box) and \r (next box).
/// </summary>
public static class PlatinumDialogText
{
    public const string PlayerName = "{STRVAR_1 3, 0, 0}";
    public const string RivalName = "{STRVAR_1 3, 1, 0}";

    public static string ToNatural(string real)
    {
        var trimmed = real.EndsWith('\r') ? real[..^1] : real;
        return trimmed.Replace("\r", "\n\n").Replace("\f", "\n");
    }

    /// <summary>Natural text → game text. <paramref name="endWithBox"/>: end with \r like most texts of the original game.</summary>
    public static string FromNatural(string natural, bool endWithBox)
    {
        natural = Typographic(natural);
        var boxes = natural.Replace("\r\n", "\n").Replace('\r', '\n').Split("\n\n")
            .Select(b => b.Split('\n').Select(l => l.TrimEnd()).Where(l => l.Length > 0).ToList())
            .Where(b => b.Count > 0).ToList();
        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < boxes.Count; i++)
        {
            var lines = boxes[i];
            for (int l = 0; l < lines.Count; l++)
            {
                sb.Append(lines[l]);
                if (l < lines.Count - 1) sb.Append(l == 0 ? '\n' : '\f');
            }
            if (i < boxes.Count - 1 || endWithBox) sb.Append('\r');
        }
        return sb.ToString();
    }

    /// <summary>
    /// The game's font has no ' and " – only ‘ ’ “ ”: typed quotes become the typographic ones (an opening one at the start of
    /// a word, else a closing one / apostrophe). {COMMANDS} stay as they are.
    /// </summary>
    public static string Typographic(string s)
    {
        if (s.IndexOf('\'') < 0 && s.IndexOf('"') < 0) return s;
        var sb = new System.Text.StringBuilder(s.Length);
        int depth = 0;
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            if (c == '{') depth++;
            else if (c == '}' && depth > 0) depth--;
            bool opening = i == 0 || char.IsWhiteSpace(s[i - 1]) || s[i - 1] is '(' or '[' or '“' or '‘';
            if (depth == 0 && c == '\'') sb.Append(opening ? '‘' : '’');
            else if (depth == 0 && c == '"') sb.Append(opening ? '“' : '”');
            else sb.Append(c);
        }
        return sb.ToString();
    }

    /// <summary>Breaks every box into lines that fit the message box (keeps the writer's empty lines between boxes).</summary>
    public static string Wrap(PlatinumFont font, string natural)
    {
        var boxes = natural.Replace("\r\n", "\n").Split("\n\n");
        var result = new List<string>();
        foreach (var box in boxes)
        {
            var words = box.Replace('\n', ' ').Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var lines = new List<string>();
            var line = "";
            foreach (var w in words)
            {
                var candidate = line.Length == 0 ? w : line + " " + w;
                if (line.Length > 0 && font.Measure(candidate) > PlatinumFont.BoxWidth) { lines.Add(line); line = w; }
                else line = candidate;
            }
            if (line.Length > 0) lines.Add(line);
            if (lines.Count > 0) result.Add(string.Join("\n", lines));
        }
        return string.Join("\n\n", result);
    }
}
