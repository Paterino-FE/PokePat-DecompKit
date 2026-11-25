using System.Globalization;
using System.Text;

namespace TilesetEditor.Core.Text;

public sealed record TextProblem(bool IsError, string Message);

/// <summary>Result of laying out user text for the message box.</summary>
public sealed class FormattedText
{
    /// <summary>Game text with control codes (\n second line, \l scroll, \p new box), without the terminating $.</summary>
    public required string Encoded { get; init; }
    /// <summary>Boxes as the player sees them; each box is a list of lines (already scrolled lines are included in order).</summary>
    public required IReadOnlyList<IReadOnlyList<string>> Boxes { get; init; }
    public required IReadOnlyList<TextProblem> Problems { get; init; }
    public bool HasErrors => Problems.Any(p => p.IsError);
}

/// <summary>
/// Turns plain text into Gen-3 message box text. Rules for authors (no codes needed):
/// a blank line starts a new box, a single line break forces a new line, everything else is wrapped by width
/// (font_config.json). The first wrap in a box becomes \n, further wraps \l (the box scrolls), boxes are joined by \p.
/// </summary>
public static class TextLayout
{
    /// <summary>Splits text into units: single characters (by text element) and {PLACEHOLDER} tokens.</summary>
    public static List<string> Units(string s)
    {
        var units = new List<string>();
        var e = StringInfo.GetTextElementEnumerator(s);
        var sb = new StringBuilder();
        bool inBrace = false;
        while (e.MoveNext())
        {
            var t = e.GetTextElement();
            if (inBrace)
            {
                sb.Append(t);
                if (t == "}") { units.Add(sb.ToString()); sb.Clear(); inBrace = false; }
                continue;
            }
            if (t == "{") { inBrace = true; sb.Append(t); continue; }
            units.Add(t);
        }
        if (sb.Length > 0) units.AddRange(Units(sb.ToString()[1..]).Prepend("{")); // unclosed brace: plain chars
        return units;
    }

    public static FormattedText Format(string text, GbaFont font, Charmap? charmap)
    {
        var problems = new List<TextProblem>();
        text = (text ?? "").Replace("\r\n", "\n").Replace('\r', '\n').Trim('\n', ' ');
        text = NormalizeQuotes(text, charmap);

        // Character checks
        var unknown = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var u in Units(text))
        {
            if (u is "\n") continue;
            if (u is "$") { problems.Add(new TextProblem(true, "The character $ ends the text in-game and is not allowed.")); continue; }
            if (u is "\\") { problems.Add(new TextProblem(true, "Backslash \\ is not allowed (the toolkit inserts control codes itself).")); continue; }
            if (u.StartsWith('{'))
            {
                var name = u.Trim('{', '}');
                if (charmap != null && !charmap.HasName(name)) problems.Add(new TextProblem(true, $"Placeholder {u} is unknown in the project (charmap.txt)."));
                continue;
            }
            if (charmap != null && !charmap.HasChar(u)) unknown.Add(u);
        }
        if (unknown.Count > 0)
            problems.Add(new TextProblem(true, $"These characters do not exist in the game font: {string.Join(" ", unknown)}"));

        var boxes = new List<List<string>>();
        var encoded = new StringBuilder();
        var paragraphs = SplitParagraphs(text);
        for (int p = 0; p < paragraphs.Count; p++)
        {
            if (p > 0) encoded.Append("\\p");
            var box = new List<string>();
            foreach (var forcedLine in paragraphs[p].Split('\n'))
                foreach (var line in Wrap(forcedLine.Trim(), font, problems))
                    box.Add(line);
            if (box.Count == 0) box.Add("");
            for (int l = 0; l < box.Count; l++)
            {
                if (l == 1) encoded.Append("\\n");
                else if (l > 1) encoded.Append("\\l");
                encoded.Append(box[l]);
            }
            boxes.Add(box);
        }
        if (text.Length == 0) problems.Add(new TextProblem(false, "The text is empty."));
        return new FormattedText { Encoded = encoded.ToString(), Boxes = boxes, Problems = problems };
    }

    private static List<string> SplitParagraphs(string text)
    {
        var result = new List<string>();
        var current = new List<string>();
        foreach (var line in text.Split('\n'))
        {
            if (line.Trim().Length == 0)
            {
                if (current.Count > 0) { result.Add(string.Join("\n", current)); current.Clear(); }
                continue;
            }
            current.Add(line);
        }
        if (current.Count > 0) result.Add(string.Join("\n", current));
        if (result.Count == 0) result.Add("");
        return result;
    }

    private static IEnumerable<string> Wrap(string line, GbaFont font, List<TextProblem> problems)
    {
        if (line.Length == 0) { yield return ""; yield break; }
        int max = font.MaxLineLength - font.CursorOverlapWidth;
        int space = font.Width(" ");
        var current = new StringBuilder();
        int width = 0;
        foreach (var word in line.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            int w = font.Width(Units(word));
            if (w > max) problems.Add(new TextProblem(false, $"The word \"{word}\" is wider than a line ({w} > {max} pixels)."));
            if (current.Length > 0 && width + space + w > max)
            {
                yield return current.ToString();
                current.Clear();
                width = 0;
            }
            if (current.Length > 0) { current.Append(' '); width += space; }
            current.Append(word);
            width += w;
        }
        if (current.Length > 0) yield return current.ToString();
    }

    /// <summary>Straight double quotes cannot be escaped in Poryscript strings; use the typographic ones from the charmap.</summary>
    private static string NormalizeQuotes(string text, Charmap? charmap)
    {
        if (!text.Contains('"') || charmap == null || !charmap.HasChar("“") || !charmap.HasChar("”")) return text;
        var sb = new StringBuilder(text.Length);
        bool open = true;
        foreach (var c in text)
        {
            if (c == '"') { sb.Append(open ? '“' : '”'); open = !open; }
            else sb.Append(c);
        }
        return sb.ToString();
    }
}
