using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using TilesetEditor.Core.Project;

namespace TilesetEditor.Core.GameData;

/// <summary>Adds a trainer class: "#define TRAINER_CLASS_X n" before TRAINER_CLASS_COUNT (count + 1) and an entry in gTrainerClasses.</summary>
public static partial class TrainerClasses
{
    public const string ConstantsFile = "include/constants/trainers.h";
    public const string TableFile = "src/battle_main.c";
    public const string TableName = "gTrainerClasses";
    /// <summary>struct Trainer stores the class in a u8.</summary>
    public const int MaxClasses = 256;

    public sealed record AddResult(string Constants, string Table, int Value);

    [GeneratedRegex(@"^([ \t]*#define[ \t]+TRAINER_CLASS_COUNT)([ \t]+)([^\r\n]+?)[ \t]*(?://[^\r\n]*)?\r?$", RegexOptions.Multiline)]
    private static partial Regex CountDefine();

    [GeneratedRegex(@"^[ \t]*#define[ \t]+(TRAINER_CLASS_\w+)[ \t]+([^\r\n/]+?)[ \t]*(?://[^\r\n]*)?\r?$", RegexOptions.Multiline)]
    private static partial Regex ClassDefine();

    [GeneratedRegex(@"^TRAINER_CLASS_[A-Z0-9_]+$")]
    private static partial Regex ConstantName();

    /// <summary>Problem with a proposed constant name, or null.</summary>
    public static string? CheckConstant(string constants, string constant)
    {
        if (!ConstantName().IsMatch(constant)) return "The constant must look like TRAINER_CLASS_NAME (capital letters, digits and _).";
        if (constant == "TRAINER_CLASS_COUNT") return "TRAINER_CLASS_COUNT is reserved.";
        if (Regex.IsMatch(constants, @"\b" + Regex.Escape(constant) + @"\b")) return constant + " already exists.";
        return null;
    }

    private static bool TryNumber(string s, out long value)
    {
        s = s.Trim();
        if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) return long.TryParse(s[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out value);
        return long.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out value);
    }

    /// <summary>A number in the notation of <paramref name="like"/> (decimal or 0x with the same letter case).</summary>
    private static string FormatLike(long value, string like, bool upperDefault)
    {
        like = like.Trim();
        if (!like.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) return value.ToString(CultureInfo.InvariantCulture);
        bool upper = like.Skip(2).Any(char.IsLetter) ? like.Skip(2).Any(char.IsUpper) : upperDefault;
        return like[..2] + value.ToString(upper ? "X" : "x", CultureInfo.InvariantCulture);
    }

    public static AddResult Add(string constants, string table, string constant, string name, int? money, string? ball)
    {
        if (CheckConstant(constants, constant) is { } problem) throw new InvalidOperationException(problem);
        var count = CountDefine().Match(constants);
        if (!count.Success) throw new InvalidOperationException($"TRAINER_CLASS_COUNT was not found in {ConstantsFile}.");
        var defines = ClassDefine().Matches(constants).Where(m => m.Groups[1].Value != "TRAINER_CLASS_COUNT").ToList();
        bool upperHex = defines.Any(d => d.Groups[2].Value.StartsWith("0x", StringComparison.OrdinalIgnoreCase) && d.Groups[2].Value.Skip(2).Any(char.IsUpper));
        string countValue = count.Groups[3].Value;
        long value;
        string valueText, newCount;
        if (TryNumber(countValue, out var n))
        {
            value = n;
            valueText = FormatLike(n, countValue, upperHex);
            newCount = FormatLike(n + 1, countValue, upperHex);
        }
        else if (Regex.Match(countValue, @"^\(?\s*(TRAINER_CLASS_\w+)\s*\+\s*1\s*\)?$") is { Success: true } expr)
        {
            var last = expr.Groups[1].Value;
            var cd = new CDefines();
            cd.AddFromText(constants);
            value = (cd.TryEvaluate(last) ?? throw new InvalidOperationException($"The value of {last} could not be determined.")) + 1;
            var lastDefine = defines.LastOrDefault(d => d.Groups[1].Value == last);
            valueText = lastDefine != null ? FormatLike(value, lastDefine.Groups[2].Value, upperHex) : value.ToString(CultureInfo.InvariantCulture);
            newCount = countValue[..expr.Groups[1].Index] + constant + countValue[(expr.Groups[1].Index + last.Length)..];
        }
        else throw new InvalidOperationException($"TRAINER_CLASS_COUNT is defined as \"{countValue}\"; the toolkit can only extend a number or \"TRAINER_CLASS_X + 1\".");
        if (value >= MaxClasses) throw new InvalidOperationException($"There are already {value} trainer classes; the trainer data stores the class in one byte (at most {MaxClasses}).");

        // Constant: after the last class define before the count, aligned like the count line.
        string nl = constants.Contains("\r\n") ? "\r\n" : "\n";
        int column = count.Groups[1].Length + count.Groups[2].Length - (count.Groups[1].Value.Length - count.Groups[1].Value.TrimStart().Length);
        var head = "#define " + constant;
        var line = (head.Length < column ? head.PadRight(column) : head + " ") + valueText;
        var anchor = defines.Where(d => d.Index < count.Index).LastOrDefault();
        int insertAt;
        if (anchor != null)
        {
            int eol = constants.IndexOf('\n', anchor.Index);
            insertAt = eol < 0 ? constants.Length : eol + 1;
        }
        else insertAt = count.Index;
        // Count first (it comes after the insertion point), then the new line.
        var cv = count.Groups[3];
        var updated = constants[..cv.Index] + newCount + constants[(cv.Index + cv.Length)..];
        updated = updated[..insertAt] + line + nl + updated[insertAt..];

        return new AddResult(updated, AddEntry(table, constant, name, money, ball), (int)value);
    }

    /// <summary>Appends "[TRAINER_CLASS_X] = { _("NAME"), money, ball }," after the last entry of gTrainerClasses, in its layout.</summary>
    public static string AddEntry(string table, string constant, string name, int? money, string? ball)
    {
        var parsed = CTable.Parse(table, TableName, "TRAINER_CLASS_");
        if (parsed.Entries.Count == 0) throw new InvalidOperationException($"{TableName} has no entries in {TableFile}.");
        if (parsed.Entries.Any(e => e.Key == constant)) throw new InvalidOperationException($"{constant} already has an entry in {TableFile}.");
        var last = parsed.Entries[^1];
        string nl = table.Contains("\r\n") ? "\r\n" : "\n";
        string indent = CTable.LineIndent(table, last.Start);
        bool spaced = last.Open + 1 < table.Length && table[last.Open + 1] == ' ';
        string str = "_(\"" + CString.Encode(name) + "\")";
        var values = new List<string>();
        string entry;
        if (last.Positional)
        {
            values.Add(str);
            if (money != null || ball != null) values.Add((money ?? 0).ToString(CultureInfo.InvariantCulture));
            if (ball != null) values.Add(ball);
            entry = spaced ? $"[{constant}] = {{ {string.Join(", ", values)} }}," : $"[{constant}] = {{{string.Join(", ", values)}}},";
        }
        else
        {
            values.Add(".name = " + str);
            if (money != null) values.Add(".money = " + money.Value.ToString(CultureInfo.InvariantCulture));
            if (ball != null) values.Add(".ball = " + ball);
            entry = $"[{constant}] = {{ {string.Join(", ", values)} }},";
        }
        int eol = table.IndexOf('\n', last.Close);
        int insertAt = eol < 0 ? table.Length : eol + 1;
        var sb = new StringBuilder(table.Length + entry.Length + 8);
        sb.Append(table, 0, insertAt);
        if (eol < 0) sb.Append(nl);
        sb.Append(indent).Append(entry).Append(nl);
        sb.Append(table, insertAt, table.Length - insertAt);
        return sb.ToString();
    }

    /// <summary>Other files with tables indexed by trainer class (they would need an entry for a new class too).</summary>
    public static List<string> OtherClassTables(string root)
    {
        var result = new List<string>();
        var rx = new Regex(@"\[TRAINER_CLASS_COUNT\]|^[ \t]*\[TRAINER_CLASS_\w+\][ \t]*=", RegexOptions.Multiline);
        foreach (var dir in new[] { "src", "include" })
        {
            var full = ProjectPaths.Combine(root, dir);
            if (!Directory.Exists(full)) continue;
            foreach (var f in Directory.EnumerateFiles(full, "*.*", SearchOption.AllDirectories))
            {
                if (!f.EndsWith(".c", StringComparison.Ordinal) && !f.EndsWith(".h", StringComparison.Ordinal)) continue;
                var rel = Path.GetRelativePath(root, f).Replace('\\', '/');
                if (rel is TableFile or "include/data.h") continue;
                string text;
                try { text = File.ReadAllText(f); } catch (IOException) { continue; }
                if (!text.Contains("TRAINER_CLASS_", StringComparison.Ordinal)) continue;
                if (rx.IsMatch(text)) result.Add(rel);
            }
        }
        return result;
    }
}
