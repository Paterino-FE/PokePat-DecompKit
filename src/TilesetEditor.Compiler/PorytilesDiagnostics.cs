using System.Text;
using System.Text.RegularExpressions;
using TilesetEditor.Core.Artwork;

namespace TilesetEditor.Compiler;

public enum DiagnosticKind
{
    Error,
    Warning,
    Remark,
    Fatal,
}

public sealed record DiagnosticLocation(int Metatile, Layer? Layer, int Quadrant, int? Row, int? Col);

public sealed record PorytilesDiagnostic(DiagnosticKind Kind, string? Tag, string Text, string Notes, DiagnosticLocation? Location)
{
    /// <summary>First meaningful line, used as the short message.</summary>
    public string Headline
    {
        get
        {
            var lines = Text.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0 && !l.All(c => c is '↓' or ' ' or 'X'));
            return lines.FirstOrDefault() ?? Tag ?? Kind.ToString();
        }
    }
}

/// <summary>Parses Porytiles 2.x diagnostic output (the "error [tag]: │ ..." block format).</summary>
public static partial class PorytilesDiagnostics
{
    [GeneratedRegex(@"\x1B\[[0-9;]*[A-Za-z]")]
    private static partial Regex Ansi();

    [GeneratedRegex(@"^(error|warning|remark|note|fatal)(?:\s+\[([^\]]+)\])?:\s*$")]
    private static partial Regex Header();

    [GeneratedRegex(@"metatile\s+0x[0-9A-Fa-f]+\((\d+)\)(?:\|(bottom|middle|top))?\|(northwest|northeast|southwest|southeast)\((\d)\)(?:\|(\d+),(\d+))?")]
    private static partial Regex LocationRegex();

    public static List<PorytilesDiagnostic> Parse(string output)
    {
        var result = new List<PorytilesDiagnostic>();
        string? kind = null, tag = null;
        var body = new StringBuilder();

        void Flush()
        {
            if (kind == null) return;
            var text = body.ToString().Trim('\n', ' ');
            if (kind == "note")
            {
                if (result.Count > 0)
                {
                    var last = result[^1];
                    result[^1] = last with { Notes = (last.Notes + "\n" + text).Trim() };
                }
            }
            else
            {
                var k = kind switch
                {
                    "error" => DiagnosticKind.Error,
                    "warning" => DiagnosticKind.Warning,
                    "fatal" => DiagnosticKind.Fatal,
                    _ => DiagnosticKind.Remark,
                };
                result.Add(new PorytilesDiagnostic(k, tag, text, "", ParseLocation(text)));
            }
            kind = null;
            tag = null;
            body.Clear();
        }

        foreach (var rawLine in Ansi().Replace(output, "").Replace("\r", "").Split('\n'))
        {
            var h = Header().Match(rawLine.TrimEnd());
            if (h.Success)
            {
                Flush();
                kind = h.Groups[1].Value;
                tag = h.Groups[2].Success ? h.Groups[2].Value : null;
                continue;
            }
            if (kind == null) continue;
            var line = rawLine;
            if (line.StartsWith('│')) line = line[1..];
            else if (line.StartsWith('├'))
            {
                var section = line[1..].Trim().TrimEnd(':');
                line = section switch
                {
                    "caused by" => "→ caused by:",
                    "root cause" => "→ root cause:",
                    _ => section,
                };
            }
            else if (line.Length > 0 && !char.IsWhiteSpace(line[0]))
            {
                // Unformatted output (e.g. a crash trace) ends the block.
                Flush();
                continue;
            }
            body.Append(line.StartsWith(' ') ? line[1..] : line).Append('\n');
        }
        Flush();
        return result;
    }

    public static DiagnosticLocation? ParseLocation(string text)
    {
        var m = LocationRegex().Match(text);
        if (!m.Success) return null;
        return new DiagnosticLocation(
            int.Parse(m.Groups[1].Value),
            LayerNames.FromPorytilesName(m.Groups[2].Success ? m.Groups[2].Value : null),
            int.Parse(m.Groups[4].Value),
            m.Groups[5].Success ? int.Parse(m.Groups[5].Value) : null,
            m.Groups[6].Success ? int.Parse(m.Groups[6].Value) : null);
    }

    /// <summary>Maps a Porytiles location (metatile index + quadrant + row,col) to source coordinates of the artwork.</summary>
    public static IssueLocation? ToIssueLocation(DiagnosticLocation? loc, Artwork art)
    {
        if (loc == null || loc.Metatile < 0 || loc.Metatile >= art.MetatileCount) return loc == null ? null : new IssueLocation(loc.Metatile, loc.Layer, null, null);
        var (x, y) = art.MetatileOrigin(loc.Metatile);
        int tx = x / 8 + loc.Quadrant % 2, ty = y / 8 + loc.Quadrant / 2;
        return new IssueLocation(loc.Metatile, loc.Layer, tx, ty,
            loc.Col != null ? tx * 8 + loc.Col : null,
            loc.Row != null ? ty * 8 + loc.Row : null);
    }

    public static Issue ToIssue(PorytilesDiagnostic d, Artwork? art)
    {
        var sev = d.Kind switch
        {
            DiagnosticKind.Error or DiagnosticKind.Fatal => IssueSeverity.Error,
            DiagnosticKind.Warning => IssueSeverity.Warning,
            _ => IssueSeverity.Info,
        };
        var hint = d.Tag switch
        {
            "tile-color-count-violation" => "Too many colors in an 8×8 tile (at most 15 plus transparency). ",
            "global-color-count-violation" => "Too many colors overall for the available palettes. ",
            "layer-mode-violation" => "Content on all three layers; the project allows only two per metatile. ",
            _ => "",
        };
        string message = d.Kind == DiagnosticKind.Fatal ? FatalSummary(d.Text) : hint + d.Headline;
        var loc = art != null ? ToIssueLocation(d.Location, art) : null;
        return new Issue(sev, d.Tag ?? d.Kind.ToString().ToLowerInvariant(), message, loc, "Porytiles");
    }

    /// <summary>For fatal blocks the root cause is the useful part.</summary>
    private static string FatalSummary(string text)
    {
        var idx = text.LastIndexOf("→ root cause:", StringComparison.Ordinal);
        var tail = idx >= 0 ? text[(idx + "→ root cause:".Length)..] : text;
        var lines = tail.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).Take(4);
        return "Porytiles: " + string.Join(" ", lines);
    }

    public static bool LooksLikeCrash(string output) =>
        output.Contains("Stack trace (most recent call first)", StringComparison.Ordinal) || output.Contains("panic", StringComparison.OrdinalIgnoreCase);
}
