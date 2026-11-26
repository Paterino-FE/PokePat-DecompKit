using System.Text.RegularExpressions;

namespace TilesetEditor.Core.Project;

/// <summary>A problem the build reported: where (project-relative file and line, if known) and what.</summary>
public sealed record BuildProblem(bool IsError, string? File, int? Line, string Message, string Raw)
{
    public string Where => File == null ? "" : Line is { } l ? $"{File}:{l}" : File;
}

/// <summary>
/// Reads the problems out of a build log: gcc/clang/assembler and the pokeplatinum data tools ("file:line:col: error: …"),
/// the Metrowerks compiler ("# File: …" blocks), linker errors (undefined symbols – the script label they come from is
/// searched in the project), and the text encoder ("unrecognized character in …json: line 105").
/// </summary>
public static partial class BuildProblems
{
    [GeneratedRegex(@"^(?<file>[^\s:'""][^:'""]*?):(?<line>\d+)(?::(?<col>\d+))?:\s*(?<sev>fatal error|error|warning|Error|Warning|Fatal error):\s*(?<msg>.+)$")]
    private static partial Regex GccLine();

    [GeneratedRegex(@"unrecognized character in (?<file>\S+?): line (?<line>\d+) pos (?<pos>\d+)(?<rest>.*)$")]
    private static partial Regex MsgEncLine();

    [GeneratedRegex(@"undefined reference to [`'](?<sym>[^'`]+)'")]
    private static partial Regex LdUndefined();

    [GeneratedRegex(@"in function [`'](?<fn>[^'`]+)'")]
    private static partial Regex LdFunction();

    [GeneratedRegex(@"Undefined\s*:\s*""(?<sym>[^""]+)""")]
    private static partial Regex MwUndefined();

    [GeneratedRegex(@"Referenced from ""(?<fn>[^""]+)"" in (?<obj>\S+)")]
    private static partial Regex MwReferenced();

    public static List<BuildProblem> Parse(string log, string? projectRoot = null)
    {
        var problems = new List<BuildProblem>();
        var lines = log.Replace("\r\n", "\n").Split('\n');
        string? mwFile = null;
        int? mwLine = null;
        string? ldFunction = null;
        for (int i = 0; i < lines.Length; i++)
        {
            var raw = lines[i].TrimEnd();
            var line = raw.Trim();
            if (line.Length == 0) continue;

            var mw = MwUndefined().Match(line);
            if (mw.Success)
            {
                var refd = i + 1 < lines.Length ? MwReferenced().Match(lines[i + 1]) : Match.Empty;
                problems.Add(new BuildProblem(true, null, null,
                    $"\"{mw.Groups["sym"].Value}\" is missing when linking" + (refd.Success ? $" (used by {refd.Groups["fn"].Value} in {refd.Groups["obj"].Value})" : "") + " – a C file is not built, or a function is not defined.", raw));
                continue;
            }
            // Metrowerks: "#    File: src/x.c", "#      12: code", "#   Error: ^", "#   message"
            if (line.StartsWith('#'))
            {
                var body = line.TrimStart('#').Trim();
                if (body.StartsWith("File:", StringComparison.Ordinal)) { mwFile = body[5..].Trim(); continue; }
                var num = Regex.Match(body, @"^(\d+):");
                if (num.Success) { mwLine = int.Parse(num.Groups[1].Value); continue; }
                if (body.StartsWith("Error:", StringComparison.Ordinal) || body.StartsWith("Warning:", StringComparison.Ordinal))
                {
                    bool err = body.StartsWith("Error:", StringComparison.Ordinal);
                    var msg = i + 1 < lines.Length && lines[i + 1].TrimStart().StartsWith('#') ? lines[i + 1].Trim().TrimStart('#').Trim() : body;
                    problems.Add(new BuildProblem(err, Rel(mwFile, projectRoot), mwLine, msg, raw));
                }
                continue;
            }
            var g = GccLine().Match(line);
            if (g.Success && !g.Groups["file"].Value.Contains(' '))
            {
                var sev = g.Groups["sev"].Value.ToLowerInvariant();
                problems.Add(new BuildProblem(!sev.Contains("warning"), Rel(g.Groups["file"].Value, projectRoot), int.Parse(g.Groups["line"].Value), g.Groups["msg"].Value.Trim(), raw));
                continue;
            }
            var enc = MsgEncLine().Match(line);
            if (enc.Success)
            {
                problems.Add(new BuildProblem(true, Rel(enc.Groups["file"].Value, projectRoot), int.Parse(enc.Groups["line"].Value),
                    $"The game's font cannot show a character here (column {enc.Groups["pos"].Value}{enc.Groups["rest"].Value.Replace("value", ":").TrimEnd()}).", raw));
                continue;
            }
            var fn = LdFunction().Match(line);
            if (fn.Success) ldFunction = fn.Groups["fn"].Value;
            var und = LdUndefined().Match(line);
            if (und.Success)
            {
                var (file, at) = FindLabel(projectRoot, ldFunction);
                problems.Add(new BuildProblem(true, file, at, $"\"{und.Groups["sym"].Value}\" is not defined" + (ldFunction != null ? $" (used in {ldFunction})" : "") + ".", raw));
                continue;
            }
        }
        // The same problem is often printed several times (e.g. by make and ninja).
        return problems.GroupBy(p => (p.File, p.Line, p.Message)).Select(x => x.First()).ToList();
    }

    /// <summary>Project-relative path: absolute paths inside the project, "../" from the build folder, Windows separators.</summary>
    public static string? Rel(string? file, string? projectRoot)
    {
        if (string.IsNullOrWhiteSpace(file)) return null;
        var f = file.Replace('\\', '/').Trim('"', '\'');
        if (projectRoot != null)
        {
            var root = projectRoot.Replace('\\', '/').TrimEnd('/');
            foreach (var candidate in new[] { root, ToWslPath(root) }.OfType<string>())
                if (f.StartsWith(candidate + "/", StringComparison.OrdinalIgnoreCase)) return f[(candidate.Length + 1)..];
            // Absolute paths of another checkout of the same project (e.g. /tmp/…/upstream2/res/…): keep from a known top folder.
            var known = Regex.Match(f, @"(?:^|/)((?:src|include|res|asm|platinum\.us|generated|tools|data)/.+)$");
            if (f.StartsWith('/') && known.Success) return known.Groups[1].Value;
        }
        while (f.StartsWith("../", StringComparison.Ordinal)) f = f[3..];
        if (f.StartsWith("./", StringComparison.Ordinal)) f = f[2..];
        return f;
    }

    /// <summary>\\wsl.localhost\Ubuntu\home\x → /home/x (the path the build inside WSL prints).</summary>
    private static string? ToWslPath(string root)
    {
        var m = Regex.Match(root, @"^//wsl(?:\.localhost|\$)/[^/]+(/.*)$", RegexOptions.IgnoreCase);
        return m.Success ? m.Groups[1].Value : null;
    }

    /// <summary>Where a script label is defined (res/field/**/*.s), for linker errors that name the script.</summary>
    private static (string? File, int? Line) FindLabel(string? root, string? label)
    {
        if (root == null || label == null || !Directory.Exists(System.IO.Path.Combine(root, "res", "field"))) return (null, null);
        foreach (var f in Directory.EnumerateFiles(System.IO.Path.Combine(root, "res", "field"), "*.s", SearchOption.AllDirectories))
        {
            int n = 0;
            foreach (var l in File.ReadLines(f))
            {
                n++;
                if (l.StartsWith(label + ":", StringComparison.Ordinal)) return (System.IO.Path.GetRelativePath(root, f).Replace('\\', '/'), n);
            }
        }
        return (null, null);
    }
}
