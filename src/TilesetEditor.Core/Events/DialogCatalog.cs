using System.Text.Json;
using System.Text.RegularExpressions;

namespace TilesetEditor.Core.Events;

public enum ExistingEventKind { Person, Sign, Trigger, MapScript, Shared }

/// <summary>A text of the game: label, where it is defined and the scripts that show it.</summary>
public sealed record ExistingDialog(string Map, string File, int Line, string Label, string GameText)
{
    /// <summary>The text as the event editor writes it: lines, an empty line between boxes, no control codes.</summary>
    public string EditorText => DialogCatalog.ToEditorText(GameText);
    public List<string> UsedByScripts { get; } = [];
    public string Preview => EditorText.Replace("\n\n", " ¶ ").Replace('\n', ' ');
}

/// <summary>Something in a map that runs a script: a person, a sign, a trigger tile or the map's own scripts.</summary>
public sealed record ExistingEvent(string Map, ExistingEventKind Kind, string Who, string Script, int X, int Y)
{
    public List<ExistingDialog> Dialogs { get; } = [];
    public string KindText => Kind switch
    {
        ExistingEventKind.Person => "Person",
        ExistingEventKind.Sign => "Sign / hidden",
        ExistingEventKind.Trigger => "Trigger tile",
        ExistingEventKind.MapScript => "Map script",
        _ => "Shared",
    };
}

/// <summary>
/// Every dialog and event the game already has: texts of data/maps/*/scripts.inc and the shared script/text files, and the
/// persons, signs and triggers of each map.json with the texts their scripts show (followed through goto/call within the file).
/// </summary>
public static partial class DialogCatalog
{
    public sealed record Result(IReadOnlyList<ExistingEvent> Events, IReadOnlyList<ExistingDialog> Dialogs, IReadOnlyList<string> Maps);

    [GeneratedRegex(@"^([A-Za-z_]\w*):{1,2}\s*(?:@.*)?$")]
    private static partial Regex LabelLine();

    [GeneratedRegex(@"^\s*\.string\s+""(.*)""\s*$")]
    private static partial Regex StringLine();

    [GeneratedRegex(@"\b[A-Za-z_]\w*\b")]
    private static partial Regex Identifier();

    /// <summary>Game text (\n, \l, \p, trailing $) → editor text (line breaks, empty line between boxes).</summary>
    public static string ToEditorText(string game)
    {
        var t = game.EndsWith('$') ? game[..^1] : game;
        return t.Replace("\\p", "\n\n").Replace("\\n", "\n").Replace("\\l", "\n").Trim('\n');
    }

    public static Result Load(string root)
    {
        var events = new List<ExistingEvent>();
        var dialogs = new List<ExistingDialog>();
        var maps = new List<string>();
        var mapsDir = Path.Combine(root, "data", "maps");
        if (Directory.Exists(mapsDir))
            foreach (var dir in Directory.EnumerateDirectories(mapsDir).OrderBy(d => d, StringComparer.Ordinal))
            {
                var map = Path.GetFileName(dir);
                var inc = Path.Combine(dir, "scripts.inc");
                if (!File.Exists(inc) && !File.Exists(Path.Combine(dir, "map.json"))) continue;
                maps.Add(map);
                var file = $"data/maps/{map}/scripts.inc";
                var (labels, texts) = File.Exists(inc) ? ParseFile(File.ReadAllText(inc), map, file) : (new Dictionary<string, List<string>>(), new Dictionary<string, ExistingDialog>());
                dialogs.AddRange(texts.Values);
                foreach (var e in MapEvents(Path.Combine(dir, "map.json"), map))
                {
                    foreach (var d in Reachable(e.Script, labels, texts)) e.Dialogs.Add(d);
                    events.Add(e);
                }
                // The map's own scripts (on entering, on frame …) and scripts only reached from elsewhere.
                foreach (var l in labels.Keys.Where(l => l.Contains("_MapScripts", StringComparison.Ordinal)))
                {
                    var e = new ExistingEvent(map, ExistingEventKind.MapScript, "When the map loads", l, -1, -1);
                    foreach (var d in Reachable(l, labels, texts)) e.Dialogs.Add(d);
                    if (e.Dialogs.Count > 0) events.Add(e);
                }
                foreach (var (label, body) in labels)
                    foreach (var token in body.SelectMany(b => Identifier().Matches(b).Select(m => m.Value)))
                        if (texts.TryGetValue(token, out var d) && !d.UsedByScripts.Contains(label)) d.UsedByScripts.Add(label);
            }

        // Shared texts (signs of every Pokémon Center, the PC, common messages …).
        foreach (var rel in SharedFiles(root))
        {
            var (labels, texts) = ParseFile(File.ReadAllText(Path.Combine(root, rel)), "(shared)", rel);
            dialogs.AddRange(texts.Values);
            foreach (var (label, body) in labels)
                foreach (var token in body.SelectMany(b => Identifier().Matches(b).Select(m => m.Value)))
                    if (texts.TryGetValue(token, out var d) && !d.UsedByScripts.Contains(label)) d.UsedByScripts.Add(label);
        }
        return new Result(events, dialogs, maps);
    }

    private static IEnumerable<string> SharedFiles(string root)
    {
        foreach (var dir in new[] { "data/text", "data/scripts" })
        {
            var full = Path.Combine(root, dir);
            if (!Directory.Exists(full)) continue;
            foreach (var f in Directory.EnumerateFiles(full, "*.inc").OrderBy(f => f, StringComparer.Ordinal))
                yield return dir + "/" + Path.GetFileName(f);
        }
    }

    /// <summary>Script labels with their lines, and the text labels of one .inc file.</summary>
    private static (Dictionary<string, List<string>> Scripts, Dictionary<string, ExistingDialog> Texts) ParseFile(string text, string map, string file)
    {
        var scripts = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var texts = new Dictionary<string, ExistingDialog>(StringComparer.Ordinal);
        var lines = text.Replace("\r\n", "\n").Split('\n');
        string? label = null;
        int labelLine = 0;
        var body = new List<string>();
        void Flush()
        {
            if (label == null || body.Count == 0) return;
            if (body.All(b => b.StartsWith(".string", StringComparison.Ordinal)))
                texts.TryAdd(label, new ExistingDialog(map, file, labelLine, label,
                    string.Concat(body.Select(b => StringLine().Match(b) is { Success: true } m ? m.Groups[1].Value : ""))));
            else scripts.TryAdd(label, [.. body]);
        }
        for (int i = 0; i < lines.Length; i++)
        {
            var raw = lines[i];
            var m = LabelLine().Match(raw);
            if (m.Success && raw.Length > 0 && !char.IsWhiteSpace(raw[0]))
            {
                Flush();
                label = m.Groups[1].Value;
                labelLine = i + 1;
                body = [];
                continue;
            }
            var t = raw.Trim();
            if (!t.StartsWith(".string", StringComparison.Ordinal) && t.IndexOf('@') is var at and >= 0) t = t[..at].Trim();
            if (t.Length > 0 && label != null) body.Add(t);
        }
        Flush();
        return (scripts, texts);
    }

    /// <summary>Texts a script shows, following goto/call/trainerbattle labels inside the same file (each label once).</summary>
    private static List<ExistingDialog> Reachable(string start, Dictionary<string, List<string>> scripts, Dictionary<string, ExistingDialog> texts)
    {
        var result = new List<ExistingDialog>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var queue = new Queue<string>();
        queue.Enqueue(start);
        while (queue.Count > 0 && seen.Count < 40)
        {
            var label = queue.Dequeue();
            if (!seen.Add(label) || !scripts.TryGetValue(label, out var body)) continue;
            foreach (var line in body)
                foreach (Match t in Identifier().Matches(line))
                {
                    if (texts.TryGetValue(t.Value, out var d)) { if (!result.Contains(d)) result.Add(d); }
                    else if (scripts.ContainsKey(t.Value)) queue.Enqueue(t.Value);
                }
        }
        return result;
    }

    private static IEnumerable<ExistingEvent> MapEvents(string mapJson, string map)
    {
        if (!File.Exists(mapJson)) yield break;
        JsonDocument doc;
        try { doc = JsonDocument.Parse(File.ReadAllText(mapJson)); }
        catch (JsonException) { yield break; }
        using (doc)
        {
            static string S(JsonElement e, string k) => e.TryGetProperty(k, out var v) ? v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : v.ToString() : "";
            static int I(JsonElement e, string k) => int.TryParse(S(e, k), out var n) ? n : -1;
            var result = new List<ExistingEvent>();
            void Each(string array, Func<JsonElement, int, ExistingEvent?> make)
            {
                if (!doc.RootElement.TryGetProperty(array, out var arr) || arr.ValueKind != JsonValueKind.Array) return;
                int i = 0;
                foreach (var e in arr.EnumerateArray())
                    if (make(e, i++) is { } ev && ev.Script.Length > 0 && ev.Script is not ("0x0" or "NULL" or "0")) result.Add(ev);
            }
            Each("object_events", (e, i) => new ExistingEvent(map, ExistingEventKind.Person,
                $"#{i + 1} {S(e, "graphics_id").Replace("OBJ_EVENT_GFX_", "")}" + (S(e, "local_id") is { Length: > 0 } id ? $" ({id})" : ""), S(e, "script"), I(e, "x"), I(e, "y")));
            Each("bg_events", (e, i) => new ExistingEvent(map, ExistingEventKind.Sign, $"#{i + 1} {S(e, "type").Replace("hidden_item", "hidden item")}", S(e, "script"), I(e, "x"), I(e, "y")));
            Each("coord_events", (e, i) => new ExistingEvent(map, ExistingEventKind.Trigger, $"#{i + 1} trigger", S(e, "script"), I(e, "x"), I(e, "y")));
            foreach (var ev in result) yield return ev;
        }
    }
}
