using System.Globalization;
using System.Text.RegularExpressions;
using TilesetEditor.Core.Text;

namespace TilesetEditor.Core.GameData;

/// <summary>Result of checking a game text: an error blocks saving, a warning is only shown.</summary>
public sealed record TextCheck(string? Error, string? Warning)
{
    public static readonly TextCheck Ok = new(null, null);
}

/// <summary>
/// Checks texts the game shows (names, descriptions) against the project's charmap.txt: only known characters and
/// {PLACEHOLDERS}, the control codes \n \l \p only where line breaks are allowed, and the length in bytes as the game
/// stores it (a placeholder such as {PKMN} takes as many bytes as charmap.txt gives it).
/// </summary>
public sealed partial class GameText
{
    private readonly Charmap? _charmap;
    private readonly Dictionary<string, int> _placeholderBytes = new(StringComparer.Ordinal);
    /// <summary>Platinum (tools/msgenc/charmap.txt): the characters the font knows; null for Emerald.</summary>
    private HashSet<string>? _nitroChars;
    /// <summary>The letters allowed after a backslash: Emerald \n \l \p, Platinum \n \r (new box) \f (scroll).</summary>
    private string _controls = "nlp";

    [GeneratedRegex(@"^([A-Z_][A-Z0-9_]*)\s*=\s*((?:[0-9A-Fa-f]{2}\s*)+)(?:@.*)?$")]
    private static partial Regex NameLine();

    public GameText(string? charmapText)
    {
        if (charmapText == null) return;
        _charmap = Charmap.Parse(charmapText);
        foreach (var raw in charmapText.Split('\n'))
        {
            var m = NameLine().Match(raw.Trim());
            if (m.Success) _placeholderBytes.TryAdd(m.Groups[1].Value, m.Groups[2].Value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length);
        }
    }

    public static GameText Load(string root)
    {
        var path = Path.Combine(root, "charmap.txt");
        return new GameText(File.Exists(path) ? File.ReadAllText(path) : null);
    }

    public bool HasCharmap => _charmap != null || _nitroChars != null;

    /// <summary>
    /// Text checks for pokeplatinum: the characters of tools/msgenc/charmap.txt ("HEX=char" lines), {COMMANDS} as they are,
    /// and the control codes \n (new line), \r (new box) and \f (scroll).
    /// </summary>
    public static GameText ForPlatinum(string? charmapText)
    {
        var t = new GameText(null) { _controls = "nrf" };
        if (charmapText == null) return t;
        var chars = new HashSet<string>(StringComparer.Ordinal);
        foreach (var raw in charmapText.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            int eq = line.IndexOf('=');
            if (eq != 4 || line.StartsWith("//", StringComparison.Ordinal)) continue;
            var value = line[(eq + 1)..];
            if (value.Length == 0 || value.StartsWith('{') || value.StartsWith('\\')) continue;
            chars.Add(value);
        }
        t._nitroChars = chars;
        return t;
    }

    public static GameText LoadPlatinum(string root)
    {
        var path = Path.Combine(root, "tools", "msgenc", "charmap.txt");
        return ForPlatinum(File.Exists(path) ? File.ReadAllText(path) : null);
    }

    /// <summary>Units of a text: single characters, "\n"-style control codes and "{NAME}" placeholders.</summary>
    public static List<string> Units(string s)
    {
        var units = new List<string>();
        var e = StringInfo.GetTextElementEnumerator(s);
        var list = new List<string>();
        while (e.MoveNext()) list.Add(e.GetTextElement());
        for (int i = 0; i < list.Count; i++)
        {
            var t = list[i];
            if (t == "\\" && i + 1 < list.Count) { units.Add("\\" + list[i + 1]); i++; continue; }
            if (t == "{")
            {
                int j = list.IndexOf("}", i + 1);
                if (j > i) { units.Add(string.Concat(list.Skip(i).Take(j - i + 1))); i = j; continue; }
            }
            units.Add(t);
        }
        return units;
    }

    /// <summary>Length in bytes as stored by the game (without the end marker).</summary>
    public int ByteLength(string text) => Units(text).Sum(u =>
        u.StartsWith('{') && u.EndsWith('}') && u.Length > 2
            ? Math.Max(1, u[1..^1].Split(' ', StringSplitOptions.RemoveEmptyEntries).Sum(n => _placeholderBytes.GetValueOrDefault(n, 1)))
            : 1);

    public TextCheck Check(string text, int maxLength, bool softLimit, bool allowLineBreaks, string what = "The text")
    {
        if (text.Length == 0) return new TextCheck($"{what} must not be empty.", null);
        var unknown = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var u in Units(text))
        {
            if (u == "\"") return new TextCheck("Double quotes (\") cannot be shown by the game. Use ‘ ’ or “ ” if the charmap has them.", null);
            if (u == "$") return new TextCheck("The character $ ends the text in-game and is not allowed.", null);
            if (u.StartsWith('\\') && u.Length == 2)
            {
                if (!_controls.Contains(u[1]))
                    return new TextCheck(_controls == "nrf"
                        ? $"Unknown control code {u}. Allowed are \\n (new line), \\r (new box) and \\f (scroll)."
                        : $"Unknown control code {u}. Allowed are \\n (new line), \\l (scroll) and \\p (new box).", null);
                if (!allowLineBreaks) return new TextCheck(_controls == "nrf" ? "Line breaks (\\n, \\r, \\f) are not allowed here." : "Line breaks (\\n, \\l, \\p) are not allowed here.", null);
                continue;
            }
            if (u == "\\") return new TextCheck("A single backslash is not allowed.", null);
            if (u is "\n" or "\r") return new TextCheck("Use \\n for a line break.", null);
            if (_nitroChars != null)
            {
                if (u.Length > 2 && u.StartsWith('{') && u.EndsWith('}')) continue;
                if (!_nitroChars.Contains(u)) unknown.Add(u);
                continue;
            }
            if (_charmap == null) continue;
            if (u.Length > 2 && u.StartsWith('{') && u.EndsWith('}'))
            {
                if (!u[1..^1].Split(' ', StringSplitOptions.RemoveEmptyEntries).All(_charmap.HasName)) unknown.Add(u);
                continue;
            }
            if (!_charmap.HasChar(u)) unknown.Add(u);
        }
        if (unknown.Count > 0)
            return new TextCheck("The game font has no " + string.Join(" ", unknown.Select(u => "'" + u + "'")) + (_nitroChars != null ? " (not in tools/msgenc/charmap.txt)." : " (not in charmap.txt)."), null);
        if (maxLength > 0)
        {
            int len = ByteLength(text);
            if (len > maxLength)
            {
                var msg = $"{what} is {len} characters long; at most {maxLength} fit.";
                return softLimit ? new TextCheck(null, msg + " It may be cut off or overlap in-game.") : new TextCheck(msg, null);
            }
        }
        return TextCheck.Ok;
    }
}
