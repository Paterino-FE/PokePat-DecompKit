using System.Globalization;
using System.Text.RegularExpressions;

namespace TilesetEditor.Core.Project;

/// <summary>Reads simple #define constants and enumerators from C headers and evaluates integer expressions over them.</summary>
public sealed partial class CDefines
{
    private readonly Dictionary<string, string> _raw = new(StringComparer.Ordinal);
    /// <summary>Later definitions of the same name (e.g. the #else branch of "#if IS_FRLG"), tried when the first does not evaluate.</summary>
    private readonly Dictionary<string, List<string>> _alternatives = new(StringComparer.Ordinal);

    [GeneratedRegex(@"^[ \t]*#[ \t]*define[ \t]+(\w+)[ \t]+(.+?)[ \t]*(?://.*)?$", RegexOptions.Multiline)]
    private static partial Regex DefineRegex();

    // enum Name { … }   enum __attribute__((packed)) Name { … }   enum Name : u8 { … }
    [GeneratedRegex(@"\benum\b\s*(?:__attribute__\s*\(\([^)]*\)\)\s*)?(?:\w+\s*)?(?::\s*\w+\s*)?\{(.*?)\}", RegexOptions.Singleline)]
    private static partial Regex EnumRegex();

    /// <summary>Adds the #defines and the enumerators of the text (enumerators: 0, previous + 1, or their explicit value).</summary>
    public void AddFromText(string text)
    {
        text = Regex.Replace(text, @"/\*.*?\*/", "", RegexOptions.Singleline);
        foreach (Match m in DefineRegex().Matches(text))
        {
            // First definition wins, which matches how the expansion headers list the default layout first.
            if (!_raw.TryAdd(m.Groups[1].Value, m.Groups[2].Value.Trim()))
            {
                if (!_alternatives.TryGetValue(m.Groups[1].Value, out var list)) _alternatives[m.Groups[1].Value] = list = [];
                list.Add(m.Groups[2].Value.Trim());
            }
        }
        // Enumerators in order, so that long enums (hundreds of "previous + 1") need no deep recursion.
        var known = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var e in Enumerators(text))
        {
            long? value = null;
            var inc = Regex.Match(e.Expression, @"^\((\w+)\) \+ 1$");
            if (inc.Success && known.TryGetValue(inc.Groups[1].Value, out var prev)) value = prev + 1;
            else
            {
                try
                {
                    var p = new Parser(e.Expression, n => known.TryGetValue(n, out var k) ? k : TryEvaluate(n));
                    value = p.ParseExpression();
                    p.ExpectEnd();
                }
                catch (FormatException) { value = null; }
            }
            if (value != null) known[e.Name] = value.Value;
            _raw.TryAdd(e.Name, value?.ToString(CultureInfo.InvariantCulture) ?? e.Expression);
        }
    }

    /// <summary>An enumerator and its value as an expression over earlier enumerators.</summary>
    public sealed record Enumerator(string Name, string Expression, int Index);

    /// <summary>
    /// The enumerators of every enum in <paramref name="text"/> (comments removed by the caller or not; preprocessor lines are
    /// skipped, so members inside #if count as present). <see cref="Enumerator.Index"/> is the position of the name in the text.
    /// </summary>
    public static List<Enumerator> Enumerators(string text)
    {
        var result = new List<Enumerator>();
        // Comments become spaces of the same length, so positions stay valid and commas in comments do not split items.
        text = Regex.Replace(text, @"/\*.*?\*/|//[^\n]*", c => new string(' ', c.Length), RegexOptions.Singleline);
        foreach (Match m in EnumRegex().Matches(text))
        {
            var body = m.Groups[1];
            string? previous = null;
            int pos = body.Index, end = body.Index + body.Length;
            while (pos < end)
            {
                int comma = text.IndexOf(',', pos, end - pos);
                int stop = comma < 0 ? end : comma;
                var item = text[pos..stop];
                // Drop preprocessor lines inside the item.
                var clean = Regex.Replace(item, @"^[ \t]*#[^\n]*$", "", RegexOptions.Multiline).Trim();
                var em = Regex.Match(clean, @"^([A-Za-z_]\w*)\s*(?:=\s*(.+))?$", RegexOptions.Singleline);
                if (em.Success)
                {
                    string name = em.Groups[1].Value;
                    string expr = em.Groups[2].Success ? em.Groups[2].Value.Trim() : previous == null ? "0" : $"({previous}) + 1";
                    int nameAt = pos + item.IndexOf(name, StringComparison.Ordinal);
                    result.Add(new Enumerator(name, expr, nameAt));
                    previous = name;
                }
                pos = stop + 1;
            }
        }
        return result;
    }

    public bool Contains(string name) => _raw.ContainsKey(name);

    /// <summary>A copy that can take more definitions without changing this one.</summary>
    public CDefines Clone()
    {
        var c = new CDefines();
        foreach (var kv in _raw) c._raw[kv.Key] = kv.Value;
        foreach (var kv in _alternatives) c._alternatives[kv.Key] = [.. kv.Value];
        return c;
    }

    public long? TryEvaluate(string name) => TryEvaluate(name, 0);

    private long? TryEvaluate(string name, int depth)
    {
        if (depth > 32 || !_raw.TryGetValue(name, out var expr)) return null;
        if (Evaluate(expr, depth) is { } v) return v;
        // The first definition may name something only defined for another configuration (MAX_TRAINERS_COUNT_FRLG):
        // fall back to the other definitions of the name in order.
        if (_alternatives.TryGetValue(name, out var others))
            foreach (var alt in others)
                if (Evaluate(alt, depth) is { } a) return a;
        return null;
    }

    private long? Evaluate(string expr, int depth)
    {
        try
        {
            var p = new Parser(expr, n => TryEvaluate(n, depth + 1));
            var v = p.ParseExpression();
            p.ExpectEnd();
            return v;
        }
        catch (FormatException)
        {
            return null;
        }
    }

    /// <summary>Evaluates an integer expression over the known names; null if it cannot be evaluated.</summary>
    public long? EvaluateExpression(string expr) => Evaluate(expr, 0);

    /// <summary>
    /// Evaluates a preprocessor condition (#if): defined(X) is supported; a name without value makes the result unknown
    /// (null) instead of 0, so a missing config header never silently picks a branch.
    /// </summary>
    public bool? EvaluateCondition(string expr)
    {
        try
        {
            var p = new Parser(expr.Trim(), n => TryEvaluate(n, 1), Contains);
            var v = p.ParseExpression();
            p.ExpectEnd();
            return v != 0;
        }
        catch (FormatException)
        {
            return null;
        }
    }

    /// <summary>Recursive-descent evaluator with C precedence: ?: || &amp;&amp; | ^ &amp; == != &lt; &gt; &lt;= &gt;= &lt;&lt; &gt;&gt; + - * / % and unary - ! ~.</summary>
    private sealed class Parser(string text, Func<string, long?> resolve, Func<string, bool>? isDefined = null)
    {
        private int _pos;

        public void ExpectEnd()
        {
            SkipWs();
            if (_pos != text.Length) throw new FormatException();
        }

        public long ParseExpression()
        {
            long c = ParseLogicalOr();
            if (!Accept("?")) return c;
            long a = ParseExpression();
            if (!Accept(":")) throw new FormatException();
            long b = ParseExpression();
            return c != 0 ? a : b;
        }

        private long ParseLogicalOr()
        {
            long v = ParseLogicalAnd();
            while (Accept("||")) { long r = ParseLogicalAnd(); v = v != 0 || r != 0 ? 1 : 0; }
            return v;
        }

        private long ParseLogicalAnd()
        {
            long v = ParseOr();
            while (Accept("&&")) { long r = ParseOr(); v = v != 0 && r != 0 ? 1 : 0; }
            return v;
        }

        private long ParseOr()
        {
            long v = ParseXor();
            while (AcceptNotFollowedBy("|", '|')) v |= ParseXor();
            return v;
        }

        private long ParseXor()
        {
            long v = ParseAnd();
            while (Accept("^")) v ^= ParseAnd();
            return v;
        }

        private long ParseAnd()
        {
            long v = ParseEquality();
            while (AcceptNotFollowedBy("&", '&')) v &= ParseEquality();
            return v;
        }

        private long ParseEquality()
        {
            long v = ParseRelational();
            while (true)
            {
                if (Accept("==")) v = v == ParseRelational() ? 1 : 0;
                else if (Accept("!=")) v = v != ParseRelational() ? 1 : 0;
                else return v;
            }
        }

        private long ParseRelational()
        {
            long v = ParseShift();
            while (true)
            {
                if (Accept("<=")) v = v <= ParseShift() ? 1 : 0;
                else if (Accept(">=")) v = v >= ParseShift() ? 1 : 0;
                else if (AcceptNotFollowedBy("<", '<')) v = v < ParseShift() ? 1 : 0;
                else if (AcceptNotFollowedBy(">", '>')) v = v > ParseShift() ? 1 : 0;
                else return v;
            }
        }

        private long ParseShift()
        {
            long v = ParseAdd();
            while (true)
            {
                if (Accept("<<")) v <<= (int)ParseAdd();
                else if (Accept(">>")) v >>= (int)ParseAdd();
                else return v;
            }
        }

        private long ParseAdd()
        {
            long v = ParseMul();
            while (true)
            {
                if (Accept("+")) v += ParseMul();
                else if (Accept("-")) v -= ParseMul();
                else return v;
            }
        }

        private long ParseMul()
        {
            long v = ParseUnary();
            while (true)
            {
                if (Accept("*")) v *= ParseUnary();
                else if (Accept("/")) { var d = ParseUnary(); if (d == 0) throw new FormatException(); v /= d; }
                else if (Accept("%")) { var d = ParseUnary(); if (d == 0) throw new FormatException(); v %= d; }
                else return v;
            }
        }

        private long ParseUnary()
        {
            if (Accept("-")) return -ParseUnary();
            if (Accept("+")) return ParseUnary();
            if (AcceptNotFollowedBy("!", '=')) return ParseUnary() == 0 ? 1 : 0;
            if (Accept("~")) return ~ParseUnary();
            if (Accept("(")) { var v = ParseExpression(); if (!Accept(")")) throw new FormatException(); return v; }
            SkipWs();
            int start = _pos;
            while (_pos < text.Length && (char.IsLetterOrDigit(text[_pos]) || text[_pos] == '_')) _pos++;
            if (start == _pos) throw new FormatException();
            var tok = text[start.._pos];
            if (char.IsDigit(tok[0]))
            {
                tok = tok.TrimEnd('u', 'U', 'l', 'L');
                if (tok.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                    return long.Parse(tok[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                return long.Parse(tok, CultureInfo.InvariantCulture);
            }
            if (tok == "defined" && isDefined != null)
            {
                bool paren = Accept("(");
                SkipWs();
                int ns = _pos;
                while (_pos < text.Length && (char.IsLetterOrDigit(text[_pos]) || text[_pos] == '_')) _pos++;
                if (ns == _pos || (paren && !Accept(")"))) throw new FormatException();
                return isDefined(text[ns.._pos]) ? 1 : 0;
            }
            return resolve(tok) ?? throw new FormatException();
        }

        private bool Accept(string s)
        {
            SkipWs();
            if (string.CompareOrdinal(text, _pos, s, 0, s.Length) != 0) return false;
            _pos += s.Length;
            return true;
        }

        /// <summary>Accepts a one-character operator unless the next character makes it another one (| vs ||, &lt; vs &lt;&lt;/&lt;=).</summary>
        private bool AcceptNotFollowedBy(string s, char next)
        {
            SkipWs();
            if (string.CompareOrdinal(text, _pos, s, 0, s.Length) != 0) return false;
            int after = _pos + s.Length;
            if (after < text.Length && (text[after] == next || (s is "<" or ">" && text[after] == '='))) return false;
            _pos = after;
            return true;
        }

        private void SkipWs()
        {
            while (_pos < text.Length && char.IsWhiteSpace(text[_pos])) _pos++;
        }
    }
}
