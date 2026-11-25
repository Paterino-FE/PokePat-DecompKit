using System.Text.RegularExpressions;

namespace TilesetEditor.Core.Maps;

/// <summary>
/// Turns the movement commands of a script label in data/maps/&lt;Map&gt;/scripts.inc into phases for the movement preview:
/// consecutive applymovement lines up to waitmovement run at the same time (one phase); turnobject and setobjectxy are
/// shown as instant turns/teleports. Only the label's own lines are read (goto/call are not followed).
/// </summary>
public static partial class ScriptMovementPhases
{
    [GeneratedRegex(@"^([A-Za-z_]\w*):{1,2}\s*(?:@.*)?$")]
    private static partial Regex LabelLine();

    public sealed record Result(IReadOnlyList<MovementPhase> Phases, IReadOnlyList<string> Warnings);

    /// <summary>label → body lines (comments stripped) of a .inc file.</summary>
    public static Dictionary<string, List<string>> Blocks(string text)
    {
        var result = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        List<string>? current = null;
        foreach (var raw in text.Replace("\r\n", "\n").Split('\n'))
        {
            var m = LabelLine().Match(raw);
            if (m.Success && raw.Length > 0 && !char.IsWhiteSpace(raw[0]))
            {
                current = [];
                result.TryAdd(m.Groups[1].Value, current);
                continue;
            }
            var t = raw.Trim();
            int at = t.IndexOf('@');
            if (at >= 0 && !t.StartsWith(".string", StringComparison.Ordinal)) t = t[..at].Trim();
            if (t.Length > 0) current?.Add(t);
        }
        return result;
    }

    /// <summary>Movement labels (bodies ending in step_end) of the map's scripts.inc and data/scripts/movement.inc.</summary>
    public static Dictionary<string, List<string>> Movements(params string[] incTexts)
    {
        var result = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var text in incTexts)
            foreach (var (name, body) in Blocks(text))
                if (body.Count > 0 && body[^1] == "step_end")
                    result.TryAdd(name, body.Where(b => b != "step_end").ToList());
        return result;
    }

    /// <summary>
    /// Phases of event-builder blocks: "Movement" and "Emote" (one phase each), "Move together" (one simultaneous phase),
    /// "Face direction" (an instant turn). Other blocks are skipped.
    /// </summary>
    public static List<MovementPhase> FromBlocks(IEnumerable<Events.CommandStep> steps)
    {
        var phases = new List<MovementPhase>();
        foreach (var step in steps)
        {
            if (step.Def is not { } def) continue;
            var a = new Events.BlockArgs(def, step.Args);
            var caption = $"{def.Title}: {def.Summary(a)}";
            switch (def.Id)
            {
                case "move": phases.Add(MovementPhase.FromMove(a["target"], a["moves"], caption)); break;
                case "emote": phases.Add(MovementPhase.FromMove(a["target"], a["emote"], caption)); break;
                case "move_together": phases.Add(MovementPhase.FromGroup(a["group"], caption)); break;
                case "face" when FacingExtensions.Parse(a["dir"]) is { } f:
                    phases.Add(MovementPhase.FromMove(a["target"], "face_" + f.ToString().ToLowerInvariant(), caption));
                    break;
            }
        }
        return phases;
    }

    public static Result Read(string root, string mapFolder, string label)
    {
        var inc = Path.Combine(root, "data", "maps", mapFolder, "scripts.inc");
        var shared = Path.Combine(root, "data", "scripts", "movement.inc");
        if (!File.Exists(inc)) return new Result([], ["This map has no scripts.inc."]);
        var text = File.ReadAllText(inc);
        return Build(text, label, Movements(text, File.Exists(shared) ? File.ReadAllText(shared) : ""));
    }

    public static Result Build(string scriptText, string label, IReadOnlyDictionary<string, List<string>> movements)
    {
        var warnings = new List<string>();
        var phases = new List<MovementPhase>();
        if (!Blocks(scriptText).TryGetValue(label, out var body))
            return new Result(phases, [$"Label {label} was not found."]);
        // A movement label on its own: show it for the person/player chosen later.
        if (movements.ContainsKey(label))
            return new Result([new MovementPhase { Caption = label }.Add("OBJ_EVENT_ID_PLAYER", string.Join(",", movements[label]))],
                ["A movement label on its own: shown for the player. Open the script that uses it to see who really walks."]);

        MovementPhase? open = null;
        void Close()
        {
            if (open != null && open.Orders.Count > 0) phases.Add(open);
            open = null;
        }
        MovementPhase Open() => open ??= new MovementPhase { Caption = $"Phase {phases.Count + 1}" };
        int line = 0;
        foreach (var raw in body)
        {
            line++;
            int sp = raw.IndexOfAny([' ', '\t']);
            var cmd = sp < 0 ? raw : raw[..sp];
            var args = sp < 0 ? [] : raw[(sp + 1)..].Split(',').Select(a => a.Trim()).ToArray();
            string A(int i) => i < args.Length ? args[i] : "";
            switch (cmd)
            {
                case "applymovement":
                case "applymovementat":
                    if (movements.TryGetValue(A(1), out var steps))
                        Open().Orders.Add(new MovementOrder(A(0), steps));
                    else warnings.Add($"Movement {A(1)} was not found (not in scripts.inc or data/scripts/movement.inc).");
                    break;
                case "waitmovement":
                case "waitmovementat":
                    Close();
                    break;
                case "turnobject" when FacingExtensions.Parse(A(1)) is { } f:
                    AddInstant(A(0), "face_" + f.ToString().ToLowerInvariant());
                    break;
                case "setobjectxy" when int.TryParse(A(1), out var x) && int.TryParse(A(2), out var y):
                    AddInstant(A(0), $"@{x},{y}");
                    break;
                case "end" or "return" or "goto" or "releaseall" or "release":
                    break;
            }
        }
        Close();
        if (phases.Count == 0) warnings.Add($"{label} has no applymovement lines of its own (goto/call are not followed).");
        return new Result(phases, warnings);

        void AddInstant(string target, string step)
        {
            // Inside an open phase the turn/teleport happens while the others walk; otherwise it is its own short phase.
            if (open != null && open.Orders.All(o => o.Target != target)) { open.Orders.Add(new MovementOrder(target, [step])); return; }
            Close();
            var p = new MovementPhase { Caption = $"Phase {phases.Count + 1}" };
            p.Orders.Add(new MovementOrder(target, [step]));
            phases.Add(p);
        }
    }
}
