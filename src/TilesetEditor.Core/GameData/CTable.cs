using System.Text;
using System.Text.RegularExpressions;
using TilesetEditor.Core.Trainers;

namespace TilesetEditor.Core.GameData;

/// <summary>
/// One initializer of a table entry: ".name = value" (designated) or the n-th value of "{ a, b, c }" (positional).
/// <see cref="ValueStart"/>..<see cref="ValueEnd"/> is the value text without surrounding blanks, so an edit that replaces
/// exactly this range keeps alignment, commas and comments byte-identical.
/// </summary>
public sealed record CTableField(string Name, int Index, int ValueStart, int ValueEnd, string Raw, bool Conditional)
{
    /// <summary>The designated name, or "#n" for the n-th positional value.</summary>
    public string Id => Name.Length > 0 ? Name : "#" + Index;
}

/// <summary>One "[KEY] = { … }," of a C table with absolute positions.</summary>
public sealed record CTableEntry(string Key, int Start, int Open, int Close, int Line, IReadOnlyList<CTableField> Fields, bool Positional)
{
    /// <summary>Why the whole entry cannot be edited safely (e.g. defined twice in #if/#else), or null.</summary>
    public string? ReadOnlyReason { get; init; }

    public CTableField? Field(string id) => Fields.FirstOrDefault(f => f.Id == id);
}

/// <summary>"static const u8 sName[] = _("…");" – a text that table fields can refer to by name.</summary>
public sealed record CStaticString(string Name, int ValueStart, int ValueEnd, string Raw, bool IsStatic, bool Conditional);

/// <summary>A parsed C table (or all keyed entries of a file) plus the named strings of the file.</summary>
public sealed class CTableFile
{
    public required string Text { get; init; }
    public required IReadOnlyList<CTableEntry> Entries { get; init; }
    public required IReadOnlyDictionary<string, CStaticString> Strings { get; init; }

    private Dictionary<string, CTableEntry>? _byKey;

    /// <summary>The entry for a key if it is defined exactly once.</summary>
    public CTableEntry? Entry(string key)
    {
        _byKey ??= Entries.GroupBy(e => e.Key).Where(g => g.Count() == 1).ToDictionary(g => g.Key, g => g.First());
        return _byKey.GetValueOrDefault(key);
    }

    /// <summary>Number of references to an identifier in the file (whole words, not counting its own definition).</summary>
    public int References(string identifier) =>
        Regex.Matches(Text, @"\b" + Regex.Escape(identifier) + @"\b").Count - (Strings.ContainsKey(identifier) ? 1 : 0);
}

/// <summary>
/// Generic reader for C tables of the form "[CONSTANT] = { .field = value, … }" or "[CONSTANT] = { value, value }".
/// It only records positions; writers replace value ranges and leave every other byte as it is.
/// Preprocessor lines (#if, #define with continuations) are skipped; fields between an #if and its #endif inside
/// an entry are marked conditional, and keys defined more than once get a read-only reason.
/// </summary>
public static class CTable
{
    private static readonly Regex StaticString = new(@"^[ \t]*(static[ \t]+)?const[ \t]+u8[ \t]+(\w+)[ \t]*\[[ \t]*\][ \t]*=[ \t]*", RegexOptions.Multiline | RegexOptions.Compiled);

    /// <summary>Parses the entries whose key starts with <paramref name="keyPrefix"/>.</summary>
    /// <param name="marker">Name of the table variable, e.g. "gItemsInfo"; null = all keyed entries of the file.</param>
    public static CTableFile Parse(string text, string? marker, string keyPrefix)
    {
        var pp = Preprocessor(text);
        int regionStart = 0, regionEnd = text.Length;
        if (marker != null)
        {
            (regionStart, regionEnd) = FindTable(text, marker, pp)
                ?? throw new FormatException($"The table {marker} was not found.");
        }
        var lines = LineStarts(text);
        var entryStart = new Regex(@"^[ \t]*\[(" + Regex.Escape(keyPrefix) + @"\w*)\][ \t]*=[ \t]*(?://[^\r\n]*)?\r?\n?[ \t]*\{", RegexOptions.Multiline);
        var entries = new List<CTableEntry>();
        int lastClose = -1;
        foreach (Match m in entryStart.Matches(text, regionStart))
        {
            if (m.Index >= regionEnd) break;
            if (m.Index <= lastClose || InRanges(pp, m.Index)) continue;
            int open = m.Index + m.Length - 1;
            int close = Match(text, open, pp);
            if (close < 0 || close > regionEnd) continue;
            var (fields, positional, anomaly) = ScanFields(text, open, close, pp);
            entries.Add(new CTableEntry(m.Groups[1].Value, m.Index, open, close, LineOf(lines, m.Index), fields, positional)
            {
                ReadOnlyReason = anomaly ? "The entry is split by #if/#else in an unusual way." : null,
            });
            lastClose = close;
        }
        // A key defined more than once (alternatives in #if/#else) cannot be edited safely.
        var dup = entries.GroupBy(e => e.Key).Where(g => g.Count() > 1).Select(g => g.Key).ToHashSet();
        for (int i = 0; i < entries.Count; i++)
            if (dup.Contains(entries[i].Key))
                entries[i] = entries[i] with { ReadOnlyReason = "Defined more than once (alternatives in #if/#else) – edit it in the source file." };

        var strings = new Dictionary<string, CStaticString>();
        var seen = new HashSet<string>();
        foreach (Match m in StaticString.Matches(text))
        {
            int vs = m.Index + m.Length;
            int paren = text.IndexOf('(', vs);
            if (paren < 0) continue;
            var head = text[vs..paren].Trim();
            if (head is not ("_" or "COMPOUND_STRING")) continue;
            int end = Match(text, paren, pp);
            if (end < 0) continue;
            var name = m.Groups[2].Value;
            var s = new CStaticString(name, vs, end + 1, text[vs..(end + 1)], m.Groups[1].Success, ConditionalAt(pp, m.Index));
            if (!seen.Add(name)) { strings.Remove(name); continue; }
            strings[name] = s;
        }
        foreach (var name in seen.Where(n => !strings.ContainsKey(n)).ToList()) strings.Remove(name);
        return new CTableFile { Text = text, Entries = entries, Strings = strings };
    }

    /// <summary>Range "{ … }" of the initializer of "marker[…] = {".</summary>
    public static (int Open, int Close)? FindTable(string text, string marker) => FindTable(text, marker, Preprocessor(text));

    private static (int Open, int Close)? FindTable(string text, string marker, List<Directive> pp)
    {
        int from = 0;
        while (true)
        {
            int i = text.IndexOf(marker, from, StringComparison.Ordinal);
            if (i < 0) return null;
            from = i + marker.Length;
            if (i > 0 && (char.IsLetterOrDigit(text[i - 1]) || text[i - 1] == '_')) continue;
            int j = from;
            while (j < text.Length && char.IsWhiteSpace(text[j])) j++;
            if (j >= text.Length || text[j] != '[') continue;
            j = Match(text, j, pp);
            if (j < 0) continue;
            j++;
            while (j < text.Length && char.IsWhiteSpace(text[j])) j++;
            if (j >= text.Length || text[j] != '=') continue;
            int open = text.IndexOf('{', j);
            if (open < 0) return null;
            int close = Match(text, open, pp);
            if (close < 0) return null;
            return (open, close);
        }
    }

    // ---------------------------------------------------------------- preprocessor

    /// <summary>A preprocessor line (with backslash continuations) and its keyword (if, ifdef, else, endif, define …).</summary>
    public sealed record Directive(int Start, int End, string Keyword);

    public static List<Directive> Preprocessor(string text)
    {
        var result = new List<Directive>();
        int i = 0;
        bool lineStart = true;
        while (i < text.Length)
        {
            char c = text[i];
            if (c == '\n') { lineStart = true; i++; continue; }
            if (c is ' ' or '\t' or '\r') { i++; continue; }
            if (c == '/' && i + 1 < text.Length && text[i + 1] == '*')
            {
                int e = text.IndexOf("*/", i + 2, StringComparison.Ordinal);
                i = e < 0 ? text.Length : e + 2;
                continue;
            }
            if (lineStart && c == '#')
            {
                int start = i, k = i + 1;
                while (k < text.Length && text[k] is ' ' or '\t') k++;
                int ks = k;
                while (k < text.Length && char.IsLetter(text[k])) k++;
                var kw = text[ks..k];
                int end = k;
                while (true)
                {
                    int nl = text.IndexOf('\n', end);
                    if (nl < 0) { end = text.Length; break; }
                    int b = nl - 1;
                    if (b >= 0 && text[b] == '\r') b--;
                    if (b >= 0 && text[b] == '\\') { end = nl + 1; continue; }
                    end = nl;
                    break;
                }
                result.Add(new Directive(start, end, kw));
                i = end;
                continue;
            }
            lineStart = false;
            if (c == '/' && i + 1 < text.Length && text[i + 1] == '/') { while (i < text.Length && text[i] != '\n') i++; continue; }
            if (c is '"' or '\'')
            {
                for (i++; i < text.Length && text[i] != c && text[i] != '\n'; i++) if (text[i] == '\\') i++;
                i++;
                continue;
            }
            i++;
        }
        return result;
    }

    private static bool InRanges(List<Directive> pp, int pos)
    {
        int lo = 0, hi = pp.Count - 1;
        while (lo <= hi)
        {
            int mid = (lo + hi) / 2;
            if (pos < pp[mid].Start) hi = mid - 1;
            else if (pos >= pp[mid].End) lo = mid + 1;
            else return true;
        }
        return false;
    }

    /// <summary>True if <paramref name="pos"/> is inside an #if block (any depth).</summary>
    private static bool ConditionalAt(List<Directive> pp, int pos)
    {
        int depth = 0;
        foreach (var d in pp)
        {
            if (d.Start >= pos) break;
            if (d.Keyword is "if" or "ifdef" or "ifndef") depth++;
            else if (d.Keyword == "endif") depth--;
        }
        return depth > 0;
    }

    private static Directive? DirectiveAt(List<Directive> pp, int pos) => DirectiveIndex(pp, pos) is int i and >= 0 ? pp[i] : null;

    private static int DirectiveIndex(List<Directive> pp, int pos)
    {
        int lo = 0, hi = pp.Count - 1;
        while (lo <= hi)
        {
            int mid = (lo + hi) / 2;
            if (pos < pp[mid].Start) hi = mid - 1;
            else if (pos > pp[mid].Start) lo = mid + 1;
            else return mid;
        }
        return -1;
    }

    /// <summary>End of the #endif that closes the #else/#elif at <paramref name="pos"/> (the other branches are skipped).</summary>
    private static int SkipToEndif(string text, List<Directive> pp, int pos)
    {
        int level = 0;
        for (int j = DirectiveIndex(pp, pos) + 1; j > 0 && j < pp.Count; j++)
        {
            var kw = pp[j].Keyword;
            if (kw is "if" or "ifdef" or "ifndef") level++;
            else if (kw == "endif") { if (level == 0) return pp[j].End; level--; }
        }
        return text.Length;
    }

    /// <summary>
    /// Like <see cref="TrainerFile.Match"/>, but preprocessor lines are skipped and only the first branch of an
    /// #if/#else counts, so "COMPOUND_STRING(\n#if X\n "a"),\n#else\n "b"),\n#endif" still balances.
    /// </summary>
    public static int Match(string t, int open, List<Directive> pp)
    {
        int depth = 0;
        for (int i = open; i < t.Length; i++)
        {
            char c = t[i];
            if (c == '"' || c == '\'')
            {
                for (i++; i < t.Length && t[i] != c && t[i] != '\n'; i++) if (t[i] == '\\') i++;
                continue;
            }
            if (c == '/' && i + 1 < t.Length && t[i + 1] == '/') { while (i < t.Length && t[i] != '\n') i++; continue; }
            if (c == '/' && i + 1 < t.Length && t[i + 1] == '*') { i = t.IndexOf("*/", i + 2, StringComparison.Ordinal); if (i < 0) return -1; i++; continue; }
            if (c == '#' && DirectiveAt(pp, i) is { } d)
            {
                i = (d.Keyword is "else" or "elif" ? SkipToEndif(t, pp, i) : d.End) - 1;
                continue;
            }
            if (c is '{' or '(' or '[') depth++;
            else if (c is '}' or ')' or ']')
            {
                depth--;
                if (depth == 0) return i;
            }
        }
        return -1;
    }

    // ---------------------------------------------------------------- fields

    private static bool HasDirective(List<Directive> pp, int start, int end)
    {
        int lo = 0, hi = pp.Count;
        while (lo < hi) { int mid = (lo + hi) / 2; if (pp[mid].Start < start) lo = mid + 1; else hi = mid; }
        return lo < pp.Count && pp[lo].Start < end;
    }

    private static (List<CTableField> Fields, bool Positional, bool Anomaly) ScanFields(string t, int open, int close, List<Directive> pp)
    {
        var designated = new List<CTableField>();
        var positional = new List<CTableField>();
        int depth = 0, index = 0;
        bool anomaly = false, sawDesignated = false;
        int i = open + 1;
        while (i < close)
        {
            char c = t[i];
            if (char.IsWhiteSpace(c) || c == ',') { i++; continue; }
            if (c == '/' && t[i + 1] == '/') { while (i < close && t[i] != '\n') i++; continue; }
            if (c == '/' && t[i + 1] == '*') { int e = t.IndexOf("*/", i + 2, StringComparison.Ordinal); i = e < 0 ? close : e + 2; continue; }
            if (c == '#' && DirectiveAt(pp, i) is { } d)
            {
                if (d.Keyword is "else" or "elif" && depth <= 0)
                {
                    // The #if was opened inside the previous value (e.g. a text with two variants): skip the other
                    // branches and keep that value read-only.
                    var list = sawDesignated ? designated : positional;
                    if (list.Count > 0) list[^1] = list[^1] with { Conditional = true };
                    else anomaly = true;
                    i = SkipToEndif(t, pp, i);
                    continue;
                }
                if (d.Keyword is "if" or "ifdef" or "ifndef") depth++;
                else if (d.Keyword == "endif") { depth--; if (depth < 0) anomaly = true; }
                i = d.End;
                continue;
            }
            bool cond = depth != 0 || anomaly;
            if (c == '.')
            {
                int ns = i + 1, j = ns;
                while (j < close && (char.IsLetterOrDigit(t[j]) || t[j] == '_')) j++;
                var name = t[ns..j];
                while (j < close && char.IsWhiteSpace(t[j])) j++;
                if (name.Length == 0 || j >= close || t[j] != '=') { i = j + 1; continue; }
                sawDesignated = true;
                var (end, vs, ve) = ScanValue(t, j + 1, close, pp);
                designated.Add(new CTableField(name, -1, vs, ve, t[vs..ve], cond || HasDirective(pp, vs, ve)));
                i = end;
                continue;
            }
            if (sawDesignated)
            {
                // Macro lines between designated fields (SHADOW(…), FOOTPRINT(…)): skip the identifier and its arguments.
                if (char.IsLetter(c) || c == '_')
                {
                    int j = i;
                    while (j < close && (char.IsLetterOrDigit(t[j]) || t[j] == '_')) j++;
                    int k = j;
                    while (k < close && t[k] is ' ' or '\t') k++;
                    if (k < close && t[k] == '(') { int m = Match(t, k, pp); j = m < 0 ? close : m + 1; }
                    i = j;
                }
                else i++;
                continue;
            }
            {
                var (end, vs, ve) = ScanValue(t, i, close, pp);
                if (ve > vs) positional.Add(new CTableField("", index++, vs, ve, t[vs..ve], cond || HasDirective(pp, vs, ve)));
                i = end;
            }
        }
        bool isPositional = designated.Count == 0;
        var fields = isPositional ? positional : designated;
        // The same field twice (#if/#else alternatives) → both read-only.
        var twice = fields.GroupBy(f => f.Id).Where(g => g.Count() > 1).Select(g => g.Key).ToHashSet();
        if (twice.Count > 0) fields = fields.Select(f => twice.Contains(f.Id) ? f with { Conditional = true } : f).ToList();
        return (fields, isPositional, anomaly);
    }

    /// <summary>Scans a value up to the next top-level comma (or the end); returns the end and the trimmed value range.</summary>
    private static (int End, int ValueStart, int ValueEnd) ScanValue(string t, int v, int close, List<Directive> pp)
    {
        int k = v, first = -1, last = -1;
        while (k < close)
        {
            char d = t[k];
            if (d == '/' && k + 1 < close && t[k + 1] == '/') { while (k < close && t[k] != '\n') k++; continue; }
            if (d == '/' && k + 1 < close && t[k + 1] == '*') { int e = t.IndexOf("*/", k + 2, StringComparison.Ordinal); k = e < 0 ? close : e + 2; continue; }
            if (d is '"' or '\'')
            {
                if (first < 0) first = k;
                for (k++; k < close && t[k] != d; k++) if (t[k] == '\\') k++;
                last = k;
                k++;
                continue;
            }
            if (d is '{' or '(' or '[')
            {
                if (first < 0) first = k;
                int m = Match(t, k, pp);
                if (m < 0 || m > close) { last = close - 1; k = close; break; }
                last = m;
                k = m + 1;
                continue;
            }
            if (d == ',') break;
            if (d == '\n')
            {
                // A value without trailing comma followed by a directive or the next designated field.
                int n = k + 1;
                while (n < close && t[n] is ' ' or '\t' or '\r') n++;
                if (n < close && t[n] == '#') break;
                if (n < close && t[n] == '.' && n + 1 < close && (char.IsLetter(t[n + 1]) || t[n + 1] == '_'))
                {
                    int q = n + 1;
                    while (q < close && (char.IsLetterOrDigit(t[q]) || t[q] == '_')) q++;
                    while (q < close && t[q] is ' ' or '\t') q++;
                    if (q < close && t[q] == '=' && (q + 1 >= close || t[q + 1] != '=')) break;
                }
            }
            if (!char.IsWhiteSpace(d))
            {
                if (first < 0) first = k;
                last = k;
            }
            k++;
        }
        if (first < 0) return (k < close ? k + 1 : k, v, v);
        return (k < close ? k + 1 : k, first, last + 1);
    }

    // ---------------------------------------------------------------- lines

    public static int[] LineStarts(string text)
    {
        var list = new List<int> { 0 };
        for (int i = 0; i < text.Length; i++) if (text[i] == '\n') list.Add(i + 1);
        return [.. list];
    }

    public static int LineOf(int[] lineStarts, int pos)
    {
        int i = Array.BinarySearch(lineStarts, pos);
        return (i >= 0 ? i : ~i - 1) + 1;
    }

    /// <summary>Whitespace at the start of the line containing <paramref name="pos"/>.</summary>
    public static string LineIndent(string text, int pos)
    {
        int ls = text.LastIndexOf('\n', Math.Max(0, pos - 1)) + 1;
        if (pos == 0) ls = 0;
        int e = ls;
        while (e < text.Length && text[e] is ' ' or '\t') e++;
        return text[ls..e];
    }

    /// <summary>Applies (start, end, replacement) edits; they must not overlap.</summary>
    public static string ApplyEdits(string text, IEnumerable<(int Start, int End, string Text)> edits)
    {
        var sorted = edits.OrderByDescending(e => e.Start).ThenByDescending(e => e.End).ToList();
        for (int i = 1; i < sorted.Count; i++)
            if (sorted[i].End > sorted[i - 1].Start) throw new InvalidOperationException("Overlapping edits.");
        var sb = new StringBuilder(text);
        foreach (var (start, end, t) in sorted)
        {
            sb.Remove(start, end - start);
            sb.Insert(start, t);
        }
        return sb.ToString();
    }
}

/// <summary>Game text in C: _("…"), COMPOUND_STRING("…" "…") and name macros like ITEM_NAME("…") (expansion 1.13+).</summary>
public static partial class CString
{
    [GeneratedRegex(@"^(_|COMPOUND_STRING|[A-Z][A-Z0-9]*_NAME)[ \t]*\(\s*((?:""(?:[^""\\\r\n]|\\.)*""\s*)+)\)$", RegexOptions.Singleline)]
    private static partial Regex StringValue();

    [GeneratedRegex(@"""((?:[^""\\\r\n]|\\.)*)""")]
    private static partial Regex Literal();

    /// <summary>The text of a string value (literals joined, \" decoded; \n, \l, \p stay as written), or null if not a plain string.</summary>
    public static string? TryRead(string raw)
    {
        var m = StringValue().Match(raw.Trim());
        if (!m.Success) return null;
        var sb = new StringBuilder();
        foreach (Match l in Literal().Matches(m.Groups[2].Value)) sb.Append(Decode(l.Groups[1].Value));
        return sb.ToString();
    }

    public static string? Macro(string raw)
    {
        var m = StringValue().Match(raw.Trim());
        return m.Success ? m.Groups[1].Value : null;
    }

    private static string Decode(string s)
    {
        if (!s.Contains('\\')) return s;
        var sb = new StringBuilder();
        for (int i = 0; i < s.Length; i++)
        {
            if (s[i] == '\\' && i + 1 < s.Length && s[i + 1] is '"' or '\\') { sb.Append(s[i + 1]); i++; continue; }
            sb.Append(s[i]);
        }
        return sb.ToString();
    }

    /// <summary>Escapes a game text for a C literal; the control codes \n, \l, \p stay as they are.</summary>
    public static string Encode(string s)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            if (c == '\\' && i + 1 < s.Length && s[i + 1] is 'n' or 'l' or 'p') { sb.Append(c).Append(s[i + 1]); i++; continue; }
            if (c is '"' or '\\') sb.Append('\\');
            sb.Append(c);
        }
        return sb.ToString();
    }

    /// <summary>Splits after each \n, \l and \p (one C literal per text line, as the source files do).</summary>
    public static List<string> Pieces(string text)
    {
        var result = new List<string>();
        int start = 0;
        for (int i = 0; i + 1 < text.Length; i++)
        {
            if (text[i] == '\\' && text[i + 1] is 'n' or 'l' or 'p')
            {
                result.Add(text[start..(i + 2)]);
                start = i + 2;
                i++;
            }
            else if (text[i] == '\\') i++;
        }
        if (start < text.Length || result.Count == 0) result.Add(text[start..]);
        return result;
    }

    /// <summary>
    /// A new string value in the layout of <paramref name="originalRaw"/> (which starts at <paramref name="rawStart"/> in
    /// <paramref name="fileText"/>): literals on their own lines stay on their own lines with the same indentation,
    /// continuation literals aligned under the first stay aligned, single-line strings stay single-line if possible.
    /// </summary>
    public static string Format(string newText, string originalRaw, string fileText, int rawStart) =>
        TryRead(originalRaw) == newText ? originalRaw : Layout(newText, originalRaw, fileText, rawStart);

    /// <summary>Number of C literals in a string value.</summary>
    public static int LiteralCount(string raw) => Literal().Matches(raw).Count;

    /// <summary><see cref="Format"/> without the shortcut for unchanged text (used by tests to check the layout rules).</summary>
    internal static string Layout(string newText, string originalRaw, string fileText, int rawStart)
    {
        var macro = Macro(originalRaw) ?? "_";
        string nl = fileText.Contains("\r\n") ? "\r\n" : "\n";
        var pieces = Pieces(newText).Select(p => "\"" + Encode(p) + "\"").ToList();
        int open = originalRaw.IndexOf('(');
        int q = originalRaw.IndexOf('"', open);
        string head = originalRaw[..(open + 1)];
        string between = originalRaw[(open + 1)..q];
        int literals = Literal().Matches(originalRaw).Count;
        if (between.Contains('\n'))
        {
            string indent = between[(between.LastIndexOf('\n') + 1)..];
            return head + string.Concat(pieces.Select(p => nl + indent + p)) + ")";
        }
        if (pieces.Count == 1) return head + between + pieces[0] + ")";
        if (literals > 1)
        {
            int abs = rawStart + q;
            int ls = fileText.LastIndexOf('\n', abs - 1) + 1;
            var pad = new string(fileText[ls..abs].Select(ch => ch == '\t' ? '\t' : ' ').ToArray());
            return head + between + pieces[0] + string.Concat(pieces.Skip(1).Select(p => nl + pad + p)) + ")";
        }
        string fieldIndent = CTable.LineIndent(fileText, rawStart) + "    ";
        return macro + "(" + string.Concat(pieces.Select(p => nl + fieldIndent + p)) + ")";
    }
}
