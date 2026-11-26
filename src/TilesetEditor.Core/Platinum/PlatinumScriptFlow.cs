using System.Text.RegularExpressions;

namespace TilesetEditor.Core.Platinum;

/// <summary>What a line of a script does, for the readable view of a hand-written script.</summary>
public enum ScriptStepKind { Message, Question, Branch, Battle, Give, Flag, Movement, Sound, Warp, Mugshot, Other, Technical }

/// <summary>One line of a script, explained; jumps to labels of the same file carry the steps there as children.</summary>
public sealed record ScriptStep(ScriptStepKind Kind, string Icon, string Text, string Line, int LineIndex, string? MessageId, string? Target, List<ScriptStep> Children);

/// <summary>
/// Reads the script of a label of a pokeplatinum map script file into readable steps ("💬 Hello!", "⚔ Trainer battle …",
/// "↪ If the player says NO → …"). Only for showing: hand-written scripts are never rewritten from this.
/// </summary>
public static partial class PlatinumScriptFlow
{
    [GeneratedRegex(@"^(\w+):[ \t]*\r?$", RegexOptions.Multiline)]
    private static partial Regex LabelLine();

    private static readonly HashSet<string> Technical = new(StringComparer.Ordinal)
    {
        "LockAll", "ReleaseAll", "FacePlayer", "WaitButton", "WaitABPress", "WaitABXPadPress", "CloseMessage", "End", "Return", "WaitFanfare",
        "WaitMovement", "WaitFadeScreen", "WaitSound", "WaitSE", "PlaySE", "Lock", "Release", "ScrCmd_", "EndMovement",
    };

    /// <summary>The lines of a label up to the next label (or the end of the file); index = line number in the file.</summary>
    public static List<(string Line, int Index)> Body(string script, string label)
    {
        var lines = script.Replace("\r\n", "\n").Split('\n');
        int start = Array.FindIndex(lines, l => l.TrimEnd() == label + ":");
        var body = new List<(string, int)>();
        if (start < 0) return body;
        for (int i = start + 1; i < lines.Length; i++)
        {
            var l = lines[i];
            if (Regex.IsMatch(l, @"^\w+:\s*$") || l.StartsWith(PlatinumScriptBlocks.Marker, StringComparison.Ordinal) || Regex.IsMatch(l, @"^\s*\.balign")) break;
            if (l.Trim().Length == 0) continue;
            body.Add((l, i));
        }
        return body;
    }

    public static bool HasLabel(string script, string label) => LabelLine().Matches(script).Any(m => m.Groups[1].Value == label);

    /// <param name="messageText">Text of a message id of the map's bank, or null if the word is not a message.</param>
    public static List<ScriptStep> Steps(string script, string label, Func<string, string?> messageText, int maxDepth = 3) =>
        Steps(script, label, messageText, maxDepth, new HashSet<string>(StringComparer.Ordinal) { label });

    private static List<ScriptStep> Steps(string script, string label, Func<string, string?> messageText, int depth, HashSet<string> visited)
    {
        var steps = new List<ScriptStep>();
        string? item = null, count = null;
        foreach (var (raw, index) in Body(script, label))
        {
            var line = raw.Trim();
            var comment = line.IndexOf("/*", StringComparison.Ordinal);
            if (comment >= 0) line = line[..comment].Trim();
            if (line.StartsWith("//", StringComparison.Ordinal) || line.Length == 0) continue;
            int sp = line.IndexOf(' ');
            var cmd = sp < 0 ? line : line[..sp];
            var args = sp < 0 ? [] : line[(sp + 1)..].Split(',', StringSplitOptions.TrimEntries);
            ScriptStep Step(ScriptStepKind k, string icon, string text, string? msg = null, string? target = null)
            {
                var children = new List<ScriptStep>();
                if (target != null && depth > 0 && HasLabel(script, target) && visited.Add(target))
                    children = Steps(script, target, messageText, depth - 1, visited);
                return new ScriptStep(k, icon, text, raw.Trim(), index, msg, target, children);
            }

            // Any argument that is a message of the bank makes this a message step (Message, NPCMessage, ShowMapSign …).
            var msgArg = args.FirstOrDefault(a => messageText(a) != null);
            if (msgArg != null)
            {
                var text = PlatinumDialogText.ToNatural(messageText(msgArg)!).Replace("\n\n", " ▸ ").Replace('\n', ' ');
                var how = cmd switch
                {
                    "Message" or "MessageNoSkip" or "MessageInstant" or "NPCMessage" or "MessageUnown" => "",
                    _ when cmd.Contains("Sign", StringComparison.Ordinal) => "(sign) ",
                    _ => $"({cmd}) ",
                };
                steps.Add(Step(ScriptStepKind.Message, "💬", how + text, msgArg));
                continue;
            }
            if (cmd == "SetVar" && args.Length == 2 && args[0] == "VAR_0x8004") item = args[1];
            if (cmd == "SetVar" && args.Length == 2 && args[0] == "VAR_0x8005") count = args[1];
            string Target() => args.Length > 0 ? args[^1] : "";
            switch (cmd)
            {
                case "ShowYesNoMenu":
                    steps.Add(Step(ScriptStepKind.Question, "❓", "Yes/No menu"));
                    break;
                case "GoTo":
                    steps.Add(Step(ScriptStepKind.Branch, "↪", $"Continue at {Target()}", target: Target()));
                    break;
                case "Call":
                    steps.Add(Step(ScriptStepKind.Branch, "↪", $"Run {Target()}", target: Target()));
                    break;
                case "CallCommonScript":
                    if (args.FirstOrDefault()?.Contains("AddItem", StringComparison.Ordinal) == true) steps.Add(Step(ScriptStepKind.Give, "🎁", $"Give item {Pretty(item)} ×{count ?? "1"}"));
                    else steps.Add(Step(ScriptStepKind.Other, "⚙", $"Common script {args.FirstOrDefault()}"));
                    break;
                case "Common_GiveItemQuantity":
                    steps.Add(Step(ScriptStepKind.Give, "🎁", $"Give item {Pretty(item)} ×{count ?? "1"}"));
                    break;
                case "StartTrainerBattle":
                    steps.Add(Step(ScriptStepKind.Battle, "⚔", $"Trainer battle: {Pretty(args.FirstOrDefault())}"));
                    break;
                case "StartWildBattle":
                    steps.Add(Step(ScriptStepKind.Battle, "🌿", $"Wild battle: {Pretty(args.FirstOrDefault())} Lv. {args.ElementAtOrDefault(1)}"));
                    break;
                case "GivePokemon":
                    steps.Add(Step(ScriptStepKind.Give, "🐾", $"Give Pokémon {Pretty(args.FirstOrDefault())} Lv. {args.ElementAtOrDefault(1)}"));
                    break;
                case "HealParty":
                    steps.Add(Step(ScriptStepKind.Give, "❤", "Heal the party"));
                    break;
                case "SetFlag":
                    steps.Add(Step(ScriptStepKind.Flag, "🚩", $"Set {args.FirstOrDefault()}"));
                    break;
                case "ClearFlag":
                    steps.Add(Step(ScriptStepKind.Flag, "🏳", $"Clear {args.FirstOrDefault()}"));
                    break;
                case "SetVar":
                    if (args.FirstOrDefault() is "VAR_0x8004" or "VAR_0x8005") steps.Add(Step(ScriptStepKind.Technical, "·", line));
                    else steps.Add(Step(ScriptStepKind.Flag, "🔢", $"{args.FirstOrDefault()} = {args.ElementAtOrDefault(1)}"));
                    break;
                case "ApplyMovement":
                    steps.Add(Step(ScriptStepKind.Movement, "🚶", $"{Pretty(args.FirstOrDefault())} moves ({args.ElementAtOrDefault(1)})"));
                    break;
                case "PlayFanfare":
                    steps.Add(Step(ScriptStepKind.Sound, "🎺", $"Fanfare {args.FirstOrDefault()}"));
                    break;
                case "PlayMusic" or "PlayDefaultMusic" or "StopMusic" or "FadeToDefaultMusic":
                    steps.Add(Step(ScriptStepKind.Sound, "🎵", line));
                    break;
                case "Warp":
                    steps.Add(Step(ScriptStepKind.Warp, "🚪", $"Warp to {Pretty(args.FirstOrDefault())} ({args.ElementAtOrDefault(1)}, {args.ElementAtOrDefault(2)})"));
                    break;
                case "ShowMugshot":
                    steps.Add(Step(ScriptStepKind.Mugshot, "🖼", $"Mugshot {args.FirstOrDefault()}" + (args.ElementAtOrDefault(1) == "MUGSHOT_POS_LEFT" ? " (left)" : "")));
                    break;
                case "ShowPicture":
                    steps.Add(Step(ScriptStepKind.Mugshot, "🗺", $"Picture {args.FirstOrDefault()}"));
                    break;
                case "ShowFullscreenPicture":
                    steps.Add(Step(ScriptStepKind.Mugshot, "🗺", $"Full-screen picture {args.FirstOrDefault()} (until A or B)"));
                    break;
                case "HidePicture":
                    steps.Add(Step(ScriptStepKind.Mugshot, "🗺", "Hide the picture"));
                    break;
                case "ClearMugshot":
                    steps.Add(Step(ScriptStepKind.Mugshot, "🖼", "Hide the mugshot"));
                    break;
                case "WaitTime":
                    steps.Add(Step(ScriptStepKind.Technical, "⏱", $"Wait {args.FirstOrDefault()} frames"));
                    break;
                case "RemoveObject":
                    steps.Add(Step(ScriptStepKind.Movement, "👻", $"{Pretty(args.FirstOrDefault())} disappears"));
                    break;
                case "AddObject":
                    steps.Add(Step(ScriptStepKind.Movement, "👤", $"{Pretty(args.FirstOrDefault())} appears"));
                    break;
                default:
                    if (cmd.StartsWith("GoToIf", StringComparison.Ordinal) || cmd.StartsWith("CallIf", StringComparison.Ordinal))
                        steps.Add(Step(ScriptStepKind.Branch, "↪", Condition(cmd, args), target: Target()));
                    else if (Technical.Contains(cmd) || cmd.StartsWith("Buffer", StringComparison.Ordinal) || cmd.StartsWith("Wait", StringComparison.Ordinal) || cmd.StartsWith("Fade", StringComparison.Ordinal))
                        steps.Add(Step(ScriptStepKind.Technical, "·", line));
                    else
                        steps.Add(Step(ScriptStepKind.Other, "⚙", line));
                    break;
            }
        }
        return steps;
    }

    private static string Condition(string cmd, string[] a)
    {
        var target = a.Length > 0 ? a[^1] : "";
        var go = cmd.StartsWith("Call", StringComparison.Ordinal) ? "run" : "continue at";
        var op = cmd[(cmd.StartsWith("GoToIf", StringComparison.Ordinal) ? 6 : 6)..];
        if (a.Length == 3 && a[0] == "VAR_RESULT" && a[1] is "MENU_NO" or "MENU_YES")
            return $"If the player says {(a[1] == "MENU_NO" ? "NO" : "YES")}{(op == "Ne" ? " not" : "")}, {go} {target}";
        if (a.Length == 2 && op is "Set" or "Unset") return $"If {a[0]} is {(op == "Set" ? "set" : "not set")}, {go} {target}";
        var sign = op switch { "Eq" => "=", "Ne" => "≠", "Lt" => "<", "Le" => "≤", "Gt" => ">", "Ge" => "≥", _ => op };
        return a.Length >= 3 ? $"If {a[0]} {sign} {a[1]}, {go} {target}" : $"{cmd} {string.Join(", ", a)}";
    }

    /// <summary>TRAINER_YOUNGSTER_TRISTAN → Youngster Tristan, ITEM_POTION → Potion, LOCALID_OLD_MAN → Old Man.</summary>
    public static string Pretty(string? constant)
    {
        if (string.IsNullOrEmpty(constant)) return "?";
        var c = Regex.Replace(constant, @"^(TRAINER|ITEM|SPECIES|LOCALID|MAP_HEADER|OBJ_EVENT_GFX|FLAG|VAR)_", "");
        return string.Join(' ', c.Split('_', StringSplitOptions.RemoveEmptyEntries).Select(w => w.Length > 1 ? w[0] + w[1..].ToLowerInvariant() : w));
    }
}
