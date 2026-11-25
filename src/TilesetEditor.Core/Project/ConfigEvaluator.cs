using System.Text;
using System.Text.RegularExpressions;

namespace TilesetEditor.Core.Project;

/// <summary>
/// Decides config-dependent values the way the compiler would for the project's current settings: "I_PRICE &gt;= GEN_7 ? 800 : 1200"
/// and the active branch of "#if B_UPDATED_MOVE_DATA &gt;= GEN_5 … #else … #endif". It reads include/config/*.h, the GEN_ and
/// TRUE/FALSE constants. When a condition names something unknown the answer is null – never a guess.
/// </summary>
public sealed partial class ConfigEvaluator
{
    private readonly CDefines _defines;

    public ConfigEvaluator(CDefines defines) => _defines = defines;

    public static readonly string[] Headers = ["include/gba/defines.h", "include/constants/global.h"];

    public static ConfigEvaluator Load(string root)
    {
        var d = new CDefines();
        foreach (var rel in Headers)
        {
            var p = ProjectPaths.Combine(root, rel);
            if (File.Exists(p)) d.AddFromText(File.ReadAllText(p));
        }
        var config = ProjectPaths.Combine(root, "include/config");
        if (Directory.Exists(config))
            foreach (var f in Directory.EnumerateFiles(config, "*.h").OrderBy(f => f, StringComparer.Ordinal))
                d.AddFromText(File.ReadAllText(f));
        // TRUE/FALSE live in gba/defines.h in some versions and nowhere obvious in others.
        d.AddFromText("#define TRUE 1\n#define FALSE 0\n");
        return new ConfigEvaluator(d);
    }

    /// <summary>This evaluator plus the #defines of a data file (e.g. TREASURE_FACTOR in items.h, PIKACHU_DEFENSE in a species file).</summary>
    public ConfigEvaluator ForFile(string fileText)
    {
        var d = _defines.Clone();
        d.AddFromText(fileText);
        return new ConfigEvaluator(d);
    }

    /// <summary>The number an integer expression stands for with the current settings, or null.</summary>
    public long? Number(string expr) => _defines.EvaluateExpression(StripComments(expr));

    /// <summary>The value of a preprocessor condition, or null if it cannot be decided.</summary>
    public bool? Condition(string expr) => _defines.EvaluateCondition(StripComments(expr));

    /// <summary>
    /// "cond ? a : b" (also nested and in parentheses) → the text of the branch that applies, itself resolved; null if
    /// <paramref name="raw"/> is not a conditional expression or the condition cannot be decided.
    /// </summary>
    public string? ResolveTernary(string raw)
    {
        var t = StripOuterParens(raw.Trim());
        int q = TopLevel(t, '?');
        if (q < 0) return null;
        int colon = MatchingColon(t, q);
        if (colon < 0) return null;
        var cond = Condition(t[..q]);
        if (cond == null) return null;
        var chosen = StripOuterParens((cond.Value ? t[(q + 1)..colon] : t[(colon + 1)..]).Trim());
        return ResolveTernary(chosen) ?? chosen;
    }

    /// <summary>
    /// The text of <paramref name="block"/> as the compiler sees it: lines of inactive #if/#elif/#else branches and all
    /// directive lines removed. Null if a condition on the way cannot be decided.
    /// </summary>
    public string? Preprocess(string block)
    {
        var sb = new StringBuilder();
        // Per nesting level: is the enclosing code active, has a branch of this group been taken, is the current branch active.
        var stack = new Stack<(bool Outer, bool Taken, bool Active)>();
        bool active = true;
        foreach (var line in block.Split('\n'))
        {
            var m = Directive().Match(line);
            if (!m.Success)
            {
                if (active) sb.Append(line).Append('\n');
                continue;
            }
            string kw = m.Groups[1].Value, arg = m.Groups[2].Value;
            switch (kw)
            {
                case "if" or "ifdef" or "ifndef":
                {
                    bool? c = !active ? false
                        : kw == "if" ? Condition(arg)
                        : kw == "ifdef" ? _defines.Contains(arg.Trim())
                        : !_defines.Contains(arg.Trim());
                    if (c == null) return null;
                    stack.Push((active, c.Value, active && c.Value));
                    active = active && c.Value;
                    break;
                }
                case "elif":
                {
                    if (stack.Count == 0) return null;
                    var (outer, taken, _) = stack.Pop();
                    bool? c = !outer || taken ? false : Condition(arg);
                    if (c == null) return null;
                    stack.Push((outer, taken || c.Value, outer && !taken && c.Value));
                    active = outer && !taken && c.Value;
                    break;
                }
                case "else":
                {
                    if (stack.Count == 0) return null;
                    var (outer, taken, _) = stack.Pop();
                    stack.Push((outer, true, outer && !taken));
                    active = outer && !taken;
                    break;
                }
                case "endif":
                {
                    if (stack.Count == 0) return null;
                    active = stack.Pop().Outer;
                    break;
                }
                default:
                    // #define/#undef inside a value block: not supported.
                    return null;
            }
        }
        if (stack.Count != 0) return null;
        if (!block.EndsWith('\n') && sb.Length > 0) sb.Length--;
        return sb.ToString();
    }

    /// <summary>The config names a condition depends on (for notes like "depends on I_PRICE").</summary>
    public static IEnumerable<string> Names(string condition) =>
        Regex.Matches(condition, @"\b[A-Z][A-Z0-9_]*\b").Select(m => m.Value)
            .Where(n => n is not ("TRUE" or "FALSE") && !n.StartsWith("GEN_", StringComparison.Ordinal)).Distinct();

    [GeneratedRegex(@"^[ \t]*#[ \t]*(if|ifdef|ifndef|elif|else|endif|define|undef)\b(.*?)\r?$")]
    private static partial Regex Directive();

    private static string StripComments(string s) => Regex.Replace(Regex.Replace(s, @"/\*.*?\*/", " ", RegexOptions.Singleline), @"//[^\n]*", "");

    private static string StripOuterParens(string t)
    {
        while (t.Length >= 2 && t[0] == '(' && Closing(t, 0) == t.Length - 1) t = t[1..^1].Trim();
        return t;
    }

    private static int Closing(string t, int open)
    {
        int depth = 0;
        for (int i = open; i < t.Length; i++)
        {
            if (t[i] == '(') depth++;
            else if (t[i] == ')' && --depth == 0) return i;
        }
        return -1;
    }

    /// <summary>First <paramref name="c"/> outside parentheses and string literals.</summary>
    private static int TopLevel(string t, char c)
    {
        int depth = 0;
        for (int i = 0; i < t.Length; i++)
        {
            char x = t[i];
            if (x == '"') { for (i++; i < t.Length && t[i] != '"'; i++) if (t[i] == '\\') i++; continue; }
            if (x is '(' or '{' or '[') depth++;
            else if (x is ')' or '}' or ']') depth--;
            else if (x == c && depth == 0) return i;
        }
        return -1;
    }

    /// <summary>The ':' that belongs to the '?' at <paramref name="q"/> (nested ?: pairs are skipped).</summary>
    private static int MatchingColon(string t, int q)
    {
        int depth = 0, pending = 0;
        for (int i = q + 1; i < t.Length; i++)
        {
            char x = t[i];
            if (x == '"') { for (i++; i < t.Length && t[i] != '"'; i++) if (t[i] == '\\') i++; continue; }
            if (x is '(' or '{' or '[') depth++;
            else if (x is ')' or '}' or ']') depth--;
            else if (depth == 0 && x == '?') pending++;
            else if (depth == 0 && x == ':')
            {
                if (pending == 0) return i;
                pending--;
            }
        }
        return -1;
    }
}
