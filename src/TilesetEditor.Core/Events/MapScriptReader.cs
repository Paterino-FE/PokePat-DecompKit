using System.Text.Json;
using System.Text.RegularExpressions;

namespace TilesetEditor.Core.Events;

public enum ScriptLabelKind
{
    Script,
    Movement,
    Text,
    MapScripts,
    Data,
}

/// <summary>One readable line of an existing script.</summary>
public sealed record ScriptLine(int Line, string Raw, string Text, string Icon, string? Jump);

public sealed record ScriptLabel(string Name, ScriptLabelKind Kind, int Line, IReadOnlyList<ScriptLine> Lines, IReadOnlyList<string> UsedBy)
{
    public string KindText => Kind switch
    {
        ScriptLabelKind.Script => "Script",
        ScriptLabelKind.Movement => "Movement",
        ScriptLabelKind.Text => "Text",
        ScriptLabelKind.MapScripts => "Map scripts",
        _ => "Data",
    };
    public string Display => UsedBy.Count > 0 ? $"{Name}   ← {string.Join(", ", UsedBy)}" : Name;
    public override string ToString() => Display;
}

/// <summary>
/// Reads the hand-written scripts of a map (data/maps/&lt;Map&gt;/scripts.inc) and describes every command in plain
/// English – movements as step lists, messages with their text – for a read-only view. Nothing is written.
/// </summary>
public static partial class MapScriptReader
{
    [GeneratedRegex(@"^([A-Za-z_]\w*):{1,2}\s*(?:@.*)?$")]
    private static partial Regex LabelLine();

    [GeneratedRegex(@"^\s*\.string\s+""(.*)""\s*$")]
    private static partial Regex StringLine();

    public sealed record Result(string File, IReadOnlyList<ScriptLabel> Labels, string? Note);

    public static Result Read(string root, string mapFolder)
    {
        var dir = Path.Combine(root, "data", "maps", mapFolder);
        var inc = Path.Combine(dir, "scripts.inc");
        var pory = Path.Combine(dir, "scripts.pory");
        var usedBy = UsedBy(Path.Combine(dir, "map.json"));
        if (!File.Exists(inc))
            return new Result($"data/maps/{mapFolder}/scripts.inc", [], File.Exists(pory) ? "This map is written in Poryscript (scripts.pory)." : "This map has no scripts.inc.");
        var text = File.ReadAllText(inc);
        // Movement and text labels from shared files, so "Common_Movement_…" can be shown too.
        var shared = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var f in new[] { Path.Combine(root, "data", "scripts", "movement.inc") }.Where(File.Exists))
            foreach (var (name, body) in Blocks(File.ReadAllText(f))) shared[name] = body.Select(b => b.Text).ToList();
        var labels = Parse(text, usedBy, shared);
        return new Result($"data/maps/{mapFolder}/scripts.inc", labels,
            File.Exists(pory) ? "scripts.inc is generated from scripts.pory – edit the .pory file." : null);
    }

    /// <summary>map.json: which person / sign / trigger runs which script label.</summary>
    public static Dictionary<string, List<string>> UsedBy(string mapJson)
    {
        var result = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        if (!File.Exists(mapJson)) return result;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(mapJson));
            void Add(string script, string who)
            {
                if (script.Length == 0 || script == "0x0" || script == "NULL") return;
                if (!result.TryGetValue(script, out var l)) result[script] = l = [];
                l.Add(who);
            }
            string S(JsonElement e, string k) => e.TryGetProperty(k, out var v) ? v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : v.ToString() : "";
            void Each(string array, Action<JsonElement, int> f)
            {
                if (doc.RootElement.TryGetProperty(array, out var arr) && arr.ValueKind == JsonValueKind.Array)
                {
                    int i = 0;
                    foreach (var e in arr.EnumerateArray()) f(e, i++);
                }
            }
            Each("object_events", (e, i) => Add(S(e, "script"), $"Person #{i + 1}" + (S(e, "local_id") is { Length: > 0 } id ? $" {id}" : "") +
                                                                 $" ({S(e, "graphics_id").Replace("OBJ_EVENT_GFX_", "")})"));
            Each("coord_events", (e, i) => Add(S(e, "script"), $"Trigger #{i + 1} ({S(e, "x")},{S(e, "y")})"));
            Each("bg_events", (e, i) => Add(S(e, "script"), $"Sign #{i + 1} ({S(e, "x")},{S(e, "y")})"));
        }
        catch (JsonException)
        {
        }
        return result;
    }

    private static List<(string Name, List<(int Line, string Text)> Body)> Blocks(string text)
    {
        var result = new List<(string, List<(int, string)>)>();
        var lines = text.Replace("\r\n", "\n").Split('\n');
        (string Name, List<(int, string)> Body)? current = null;
        for (int i = 0; i < lines.Length; i++)
        {
            var raw = lines[i];
            var m = LabelLine().Match(raw);
            if (m.Success && !char.IsWhiteSpace(raw.FirstOrDefault()))
            {
                current = (m.Groups[1].Value, new List<(int, string)>());
                result.Add(current.Value);
                continue;
            }
            var t = raw.Trim();
            int at = t.IndexOf('@');
            if (at >= 0 && !t.StartsWith(".string", StringComparison.Ordinal)) t = t[..at].Trim();
            if (t.Length == 0 || current == null) continue;
            current.Value.Body.Add((i + 1, t));
        }
        return result;
    }

    public static List<ScriptLabel> Parse(string text, IReadOnlyDictionary<string, List<string>> usedBy, IReadOnlyDictionary<string, List<string>>? shared = null)
    {
        var blocks = Blocks(text);
        var texts = new Dictionary<string, string>(StringComparer.Ordinal);
        var movements = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        if (shared != null) foreach (var (k, v) in shared) movements[k] = v;
        foreach (var (name, body) in blocks)
        {
            if (body.Count > 0 && body.All(b => b.Text.StartsWith(".string", StringComparison.Ordinal)))
                texts[name] = string.Concat(body.Select(b => StringLine().Match(b.Text) is { Success: true } sm ? sm.Groups[1].Value : ""));
            else if (body.Count > 0 && body.Last().Text == "step_end")
                movements[name] = body.Select(b => b.Text).ToList();
        }
        var result = new List<ScriptLabel>();
        foreach (var (name, body) in blocks)
        {
            var kind = texts.ContainsKey(name) ? ScriptLabelKind.Text
                : movements.ContainsKey(name) && body.Count > 0 && body.Last().Text == "step_end" ? ScriptLabelKind.Movement
                : body.Any(b => b.Text.StartsWith("map_script", StringComparison.Ordinal)) ? ScriptLabelKind.MapScripts
                : body.Any(b => b.Text.StartsWith('.')) ? ScriptLabelKind.Data
                : ScriptLabelKind.Script;
            var lines = kind switch
            {
                ScriptLabelKind.Text => [new ScriptLine(body[0].Line, "", "“" + Readable(texts[name]) + "”", "💬", null)],
                ScriptLabelKind.Movement => [new ScriptLine(body[0].Line, "", Steps(movements[name]), "🚶", null)],
                _ => body.Select(b => Describe(b.Line, b.Text, texts, movements)).ToList(),
            };
            result.Add(new ScriptLabel(name, kind, body.Count > 0 ? body[0].Line - 1 : 0, lines, usedBy.TryGetValue(name, out var u) ? u : []));
        }
        return result;
    }

    /// <summary>"walk_down ×3, walk_right ×7, face_left" from a movement label body.</summary>
    public static string Steps(IEnumerable<string> body)
    {
        var steps = body.Where(s => s != "step_end").ToList();
        var parts = new List<string>();
        for (int i = 0; i < steps.Count;)
        {
            int j = i;
            while (j < steps.Count && steps[j] == steps[i]) j++;
            parts.Add(j - i > 1 ? $"{steps[i]} ×{j - i}" : steps[i]);
            i = j;
        }
        return parts.Count == 0 ? "(no steps)" : string.Join(", ", parts);
    }

    private static string Readable(string gbaText) =>
        gbaText.Replace("\\n", " ").Replace("\\l", " ").Replace("\\p", " ¶ ").Replace("$", "").Replace("{PLAYER}", "[Player]").Trim();

    private static string Who(string arg) => arg.Trim() switch
    {
        "OBJ_EVENT_ID_PLAYER" => "Player",
        "OBJ_EVENT_ID_CAMERA" => "Camera",
        "VAR_LAST_TALKED" => "the person talked to",
        var s when s.StartsWith("LOCALID_", StringComparison.Ordinal) => s[8..],
        var s => "person " + s,
    };

    private static ScriptLine Describe(int line, string raw, Dictionary<string, string> texts, Dictionary<string, List<string>> movements)
    {
        var sp = raw.IndexOfAny([' ', '\t']);
        var cmd = sp < 0 ? raw : raw[..sp];
        var args = sp < 0 ? [] : raw[(sp + 1)..].Split(',').Select(a => a.Trim()).ToArray();
        string A(int i) => i < args.Length ? args[i] : "";
        string Txt(string label) => texts.TryGetValue(label, out var t) ? "“" + Readable(t) + "”" : label;
        ScriptLine L(string icon, string text, string? jump = null) => new(line, raw, text, icon, jump);
        return cmd switch
        {
            "msgbox" => L("💬", Txt(A(0)) + (A(1) switch { "MSGBOX_YESNO" => "  (Yes/No)", "MSGBOX_SIGN" => "  (sign)", _ => "" })),
            "message" => L("💬", Txt(A(0))),
            "waitmessage" => L("⏳", "Wait until the text is shown"),
            "closemessage" => L("✖", "Close the text box"),
            "applymovement" => L("🚶", $"{Who(A(0))}: " + (movements.TryGetValue(A(1), out var mv) ? Steps(mv) : A(1)), movements.ContainsKey(A(1)) ? A(1) : null),
            "waitmovement" => L("⏳", "Wait until the movements are finished"),
            "lock" => L("🔒", "Stop the person talked to"),
            "lockall" => L("🔒", "Stop everyone"),
            "faceplayer" => L("↻", "Person turns to the player"),
            "release" or "releaseall" => L("🔓", "Let everyone move again"),
            "setflag" => L("🚩", $"Set {A(0)}"),
            "clearflag" => L("🏳", $"Clear {A(0)}"),
            "setvar" => L("🔢", $"{A(0)} = {A(1)}"),
            "addvar" => L("🔢", $"{A(0)} += {A(1)}"),
            "subvar" => L("🔢", $"{A(0)} -= {A(1)}"),
            "goto_if_set" => L("🔀", $"If {A(0)} is set → go to {A(1)}", A(1)),
            "goto_if_unset" => L("🔀", $"If {A(0)} is not set → go to {A(1)}", A(1)),
            "call_if_set" => L("🔀", $"If {A(0)} is set → run {A(1)}", A(1)),
            "call_if_unset" => L("🔀", $"If {A(0)} is not set → run {A(1)}", A(1)),
            var c when c.StartsWith("goto_if_", StringComparison.Ordinal) => L("🔀", $"If {A(0)} {Op(c[8..])} {A(1)} → go to {A(2)}", A(2)),
            var c when c.StartsWith("call_if_", StringComparison.Ordinal) => L("🔀", $"If {A(0)} {Op(c[8..])} {A(1)} → run {A(2)}", A(2)),
            "goto" => L("➡", $"Go to {A(0)}", A(0)),
            "call" => L("↪", $"Run {A(0)}", A(0)),
            "end" => L("■", "End"),
            "return" => L("↩", "Return"),
            var c when c.StartsWith("trainerbattle", StringComparison.Ordinal) => L("⚔", $"Trainer battle {A(0)}" + (args.Length > 1 ? $" ({string.Join(", ", args.Skip(1).Where(a => texts.ContainsKey(a)).Select(Txt).Take(1))})" : "")),
            "giveitem" or "additem" => L("🎁", $"Give {A(0)}" + (A(1).Length > 0 && A(1) != "1" ? $" ×{A(1)}" : "")),
            "removeitem" => L("🎁", $"Take {A(0)}" + (A(1).Length > 0 && A(1) != "1" ? $" ×{A(1)}" : "")),
            "givemon" => L("🐾", $"Give Pokémon {A(0)} (level {A(1)})"),
            "removeobject" => L("👻", $"Hide {Who(A(0))}"),
            "addobject" => L("👤", $"Show {Who(A(0))}"),
            "turnobject" => L("↻", $"{Who(A(0))} turns {A(1).Replace("DIR_", "").ToLowerInvariant()}"),
            "setobjectxy" or "setobjectxyperm" => L("📍", $"Place {Who(A(0))} at {A(1)},{A(2)}"),
            "delay" => L("⏱", $"Wait {A(0)} frames"),
            "playse" => L("🔊", $"Sound {A(0)}"),
            "playfanfare" => L("🎺", $"Fanfare {A(0)}"),
            "playbgm" => L("🎵", $"Music {A(0)}"),
            "fadedefaultbgm" or "fadeoutbgm" => L("🎵", "Back to the map music"),
            "special" or "specialvar" => L("⚙", $"Special {string.Join(", ", args)}"),
            "warp" or "warpsilent" or "warpdoor" or "warpteleport" => L("🚪", $"Warp to {A(0)} ({string.Join(",", args.Skip(1))})"),
            "fadescreen" => L("🌗", $"Screen {A(0)}"),
            "map_script" => L("🗺", $"{A(0).Replace("MAP_SCRIPT_", "")} → {A(1)}", A(1)),
            _ => L("•", raw),
        };
    }

    private static string Op(string suffix) => suffix switch
    {
        "eq" => "==",
        "ne" => "!=",
        "lt" => "<",
        "le" => "<=",
        "gt" => ">",
        "ge" => ">=",
        _ => suffix,
    };
}
