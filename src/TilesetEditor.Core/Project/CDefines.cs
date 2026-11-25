using System.Globalization;
using System.Text.RegularExpressions;

namespace TilesetEditor.Core.Project;

/// <summary>Reads simple #define constants from C headers and evaluates integer expressions over them.</summary>
public sealed partial class CDefines
{
    private readonly Dictionary<string, string> _raw = new(StringComparer.Ordinal);

    [GeneratedRegex(@"^[ \t]*#[ \t]*define[ \t]+(\w+)[ \t]+(.+?)[ \t]*(?://.*)?$", RegexOptions.Multiline)]
    private static partial Regex DefineRegex();

    public void AddFromText(string text)
    {
        text = Regex.Replace(text, @"/\*.*?\*/", "", RegexOptions.Singleline);
        foreach (Match m in DefineRegex().Matches(text))
        {
            // First definition wins, which matches how the expansion headers list the default layout first.
            _raw.TryAdd(m.Groups[1].Value, m.Groups[2].Value.Trim());
        }
    }

    public bool Contains(string name) => _raw.ContainsKey(name);

    public long? TryEvaluate(string name) => TryEvaluate(name, 0);

    private long? TryEvaluate(string name, int depth)
    {
        if (depth > 32 || !_raw.TryGetValue(name, out var expr)) return null;
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

    /// <summary>Tiny recursive-descent evaluator for + - * / &lt;&lt; &gt;&gt; | &amp; and parentheses.</summary>
    private sealed class Parser(string text, Func<string, long?> resolve)
    {
        private int _pos;

        public void ExpectEnd()
        {
            SkipWs();
            if (_pos != text.Length) throw new FormatException();
        }

        public long ParseExpression() => ParseOr();

        private long ParseOr()
        {
            long v = ParseAnd();
            while (Accept("|")) v |= ParseAnd();
            return v;
        }

        private long ParseAnd()
        {
            long v = ParseShift();
            while (Accept("&")) v &= ParseShift();
            return v;
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
            return resolve(tok) ?? throw new FormatException();
        }

        private bool Accept(string s)
        {
            SkipWs();
            if (string.CompareOrdinal(text, _pos, s, 0, s.Length) != 0) return false;
            // do not treat '<<' as '<' etc. (only the operators above exist, so a prefix check is enough)
            _pos += s.Length;
            return true;
        }

        private void SkipWs()
        {
            while (_pos < text.Length && char.IsWhiteSpace(text[_pos])) _pos++;
        }
    }
}
