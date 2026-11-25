using System.Text.RegularExpressions;

namespace TilesetEditor.Core.Project;

public sealed record ConfigChange(string Name, int Line, string OldValue, string NewValue);

public sealed class ConfigConflictException(string message) : Exception(message);

/// <summary>
/// Changes values of simple #defines in place. Only the value token on the expected line is replaced, so comments,
/// alignment and line endings stay as they are. If the line no longer contains "#define NAME OLDVALUE" (the file
/// was changed meanwhile), nothing is changed and a conflict is reported.
/// </summary>
public static class ConfigEditor
{
    public static readonly IReadOnlyList<string> Generations = ["GEN_3", "GEN_4", "GEN_5", "GEN_6", "GEN_7", "GEN_8", "GEN_9", "GEN_LATEST"];

    public static string Apply(string text, IEnumerable<ConfigChange> changes)
    {
        var lines = text.Split('\n');
        foreach (var c in changes)
        {
            if (c.Line < 1 || c.Line > lines.Length)
                throw new ConfigConflictException($"{c.Name}: line {c.Line} no longer exists.");
            var line = lines[c.Line - 1];
            var m = Regex.Match(line, @"^(\s*#\s*define\s+" + Regex.Escape(c.Name) + @"\s+)(" + Regex.Escape(c.OldValue) + @")(?=\s|//|$|\r)");
            if (!m.Success)
                throw new ConfigConflictException($"{c.Name}: line {c.Line} no longer contains the value {c.OldValue} – the file has changed in the meantime. Please reload.");
            if (!IsSimpleValue(c.NewValue))
                throw new ConfigConflictException($"{c.Name}: \"{c.NewValue}\" is not a simple value (allowed: TRUE/FALSE, number, GEN_…).");
            // Keep the column of a trailing comment where possible.
            var rest = line[(m.Index + m.Length)..];
            int diff = c.OldValue.Length - c.NewValue.Length;
            if (diff > 0 && rest.StartsWith(' ')) rest = new string(' ', diff) + rest;
            else if (diff < 0 && rest.StartsWith(new string(' ', -diff + 1))) rest = rest[(-diff)..];
            lines[c.Line - 1] = m.Groups[1].Value + c.NewValue + rest;
        }
        return string.Join('\n', lines);
    }

    public static bool IsSimpleValue(string v) =>
        Regex.IsMatch(v, @"^(TRUE|FALSE|-?\d+|0x[0-9A-Fa-f]+|GEN_\w+|[A-Z][A-Z0-9_]*)$");
}
