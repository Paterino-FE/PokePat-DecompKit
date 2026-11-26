using System.Text.RegularExpressions;

namespace TilesetEditor.Core.Platinum;

/// <summary>What the conversion of a hand-written script made: the blocks and how much of it stayed code.</summary>
public sealed record ConvertedScript(List<ScriptBlock> Blocks, int Understood, int CodeLines, List<string> CodeExamples);

/// <summary>
/// Turns a hand-written pokeplatinum script into building blocks, following it like the game: jumps to the script's helper
/// labels become branches ("If flag", "Ask Yes/No", "If variable", "If player has item"), item/Pokémon/battle/movement
/// commands their blocks. Lines no block knows stay as "Script code" blocks, unchanged – so nothing gets lost. The helper
/// labels stay in the file when PokéPat writes the new script, so code that jumps to them still works.
/// </summary>
public static partial class PlatinumScriptConverter
{
    [GeneratedRegex(@"^(\w+):\s*$")]
    private static partial Regex LabelRe();

    /// <param name="messageText">The raw text of a message id of the map's bank, or null when the word is no message.</param>
    public static ConvertedScript Convert(string script, string label, Func<string, string?> messageText, int maxDepth = 6)
    {
        var lines = script.Replace("\r\n", "\n").Split('\n');
        var labels = new Dictionary<string, int>(StringComparer.Ordinal);
        for (int i = 0; i < lines.Length; i++)
            if (LabelRe().Match(lines[i]) is { Success: true } m) labels.TryAdd(m.Groups[1].Value, i);
        var c = new Converter(lines, labels, messageText, maxDepth);
        var blocks = new List<ScriptBlock>();
        if (labels.TryGetValue(label, out var start)) c.Run(start + 1, blocks, 0, [label]);
        // A trailing "End event here" is the normal end of an event anyway.
        while (blocks.Count > 0 && blocks[^1].Kind == ScriptBlockKind.EndEvent) blocks.RemoveAt(blocks.Count - 1);
        Merge(blocks);
        return new ConvertedScript(blocks, c.Understood, c.CodeLines, c.Examples);
    }

    /// <summary>Neighbouring code blocks become one.</summary>
    private static void Merge(List<ScriptBlock> blocks)
    {
        for (int i = blocks.Count - 1; i > 0; i--)
            if (blocks[i].Kind == ScriptBlockKind.Code && blocks[i - 1].Kind == ScriptBlockKind.Code)
            {
                blocks[i - 1].Text += "\n" + blocks[i].Text;
                blocks.RemoveAt(i);
            }
    }

    private sealed class Converter(string[] lines, Dictionary<string, int> labels, Func<string, string?> messageText, int maxDepth)
    {
        public int Understood, CodeLines;
        public readonly List<string> Examples = [];

        /// <summary>Commands the blocks write by themselves (start, end, waiting for the player, closing the box).</summary>
        private static readonly HashSet<string> Implicit = new(StringComparer.Ordinal)
        {
            "LockAll", "ReleaseAll", "FacePlayer", "CloseMessage", "Lock", "Release",
        };

        private static (string Cmd, string[] Args) Parse(string raw)
        {
            var line = raw.Trim();
            int comment = line.IndexOf("/*", StringComparison.Ordinal);
            if (comment >= 0) line = line[..comment].Trim();
            if (line.StartsWith("//", StringComparison.Ordinal)) line = "";
            int sp = line.IndexOf(' ');
            return sp < 0 ? (line, []) : (line[..sp], line[(sp + 1)..].Split(',', StringSplitOptions.TrimEntries));
        }

        private void Code(List<ScriptBlock> into, string line)
        {
            CodeLines++;
            if (Examples.Count < 6 && !Examples.Contains(line.Trim())) Examples.Add(line.Trim());
            into.Add(new ScriptBlock { Kind = ScriptBlockKind.Code, Text = line.Trim() });
        }

        private ScriptBlock Add(List<ScriptBlock> into, ScriptBlock b) { Understood++; into.Add(b); return b; }

        private static bool Named(string value, string prefix) => Regex.IsMatch(value, "^" + prefix + @"[A-Za-z0-9_]+$");

        /// <summary>The next command line from <paramref name="i"/> on (skipping empty lines and labels a script runs into).</summary>
        private (string Cmd, string[] Args, int Index) Peek(int i)
        {
            for (; i < lines.Length; i++)
            {
                if (LabelRe().IsMatch(lines[i]) || lines[i].Trim().Length == 0) continue;
                var (cmd, args) = Parse(lines[i]);
                if (cmd.Length == 0) continue;
                return (cmd, args, i);
            }
            return ("", [], lines.Length);
        }

        /// <summary>The steps of a movement label, or null when it holds something else.</summary>
        private string? Movement(string label)
        {
            if (!labels.TryGetValue(label, out var at)) return null;
            var steps = new List<string>();
            for (int i = at + 1; i < lines.Length; i++)
            {
                var (cmd, args) = Parse(lines[i]);
                if (cmd.Length == 0) continue;
                if (cmd == "EndMovement") return steps.Count > 0 ? string.Join(";", steps) : null;
                if (cmd.StartsWith('.') || LabelRe().IsMatch(lines[i])) return null;
                if (args.Length > 1 || (args.Length == 1 && !int.TryParse(args[0], out _))) return null;
                steps.Add(args.Length == 1 ? $"{cmd} {args[0]}" : cmd);
            }
            return null;
        }

        /// <summary>
        /// A branch: the blocks at <paramref name="target"/> when the condition holds. When they end the event, the rest
        /// simply follows after the branch; otherwise it becomes the "Otherwise" part.
        /// </summary>
        private bool Branch(List<ScriptBlock> into, ScriptBlock condition, string target, string originalLine, int next, int depth, HashSet<string> seen, ScriptBlock? elseHead = null)
        {
            if (depth >= maxDepth || !labels.TryGetValue(target, out var at) || seen.Contains(target)) { Code(into, originalLine); return false; }
            var inner = new List<ScriptBlock>();
            bool ends = Run(at + 1, inner, depth + 1, [.. seen, target]);
            Add(into, condition);
            into.AddRange(inner);
            if (ends)
            {
                into.Add(new ScriptBlock { Kind = ScriptBlockKind.EndEvent });
                into.Add(new ScriptBlock { Kind = ScriptBlockKind.EndIf });
                return false;   // the caller goes on with the rest at the same level
            }
            into.Add(new ScriptBlock { Kind = ScriptBlockKind.Else });
            if (elseHead != null) into.Add(elseHead);
            bool restEnds = Run(next, into, depth + 1, seen);
            into.Add(new ScriptBlock { Kind = ScriptBlockKind.EndIf });
            return true;   // the rest was taken into the branch: nothing follows at this level
        }

        /// <summary>Converts from line <paramref name="i"/> to the end of the flow; true when it ends the event (End).</summary>
        public bool Run(int i, List<ScriptBlock> into, int depth, HashSet<string> seen)
        {
            bool first = true;
            while (i < lines.Length)
            {
                if (LabelRe().IsMatch(lines[i])) { i++; continue; }   // a script that runs into the next label goes on there
                var raw = lines[i];
                var (cmd, a) = Parse(raw);
                i++;
                if (cmd.Length == 0) continue;
                if (cmd.StartsWith('.') || cmd == "EndMovement") break;   // data, not commands
                bool atStart = first;
                first = false;
                switch (cmd)
                {
                    case "End":
                    case "Return" when depth == 0:
                        return true;
                    case "PlaySE" when atStart:
                    case "BufferPlayerName" when a.FirstOrDefault() == "0":
                    case "BufferRivalName" when a.FirstOrDefault() == "1":
                        continue;   // the blocks do these by themselves
                    case var _ when Implicit.Contains(cmd):
                        continue;
                    // Waiting for the player belongs to a message block; after other lines it stays code.
                    case "WaitButton" or "WaitABXPadPress" or "WaitABPress" or "WaitABPadPress" when into.Count > 0 && into[^1].Kind is ScriptBlockKind.Message or ScriptBlockKind.GiveItem:
                        continue;
                    case "Message" or "MessageNoSkip" or "NPCMessage" or "MessageInstant" when a.Length > 0 && messageText(a[0]) is { } text:
                    {
                        var natural = PlatinumDialogText.ToNatural(text);
                        // Message + ShowYesNoMenu + jump on the answer: a question with two branches.
                        var (n1, a1, i1) = Peek(i);
                        if (n1 == "ShowYesNoMenu" && a1.FirstOrDefault() == "VAR_RESULT")
                        {
                            var (n2, a2, i2) = Peek(i1 + 1);
                            if (n2 is "GoToIfEq" or "GoToIfNe" && a2.Length == 3 && a2[0] == "VAR_RESULT" && a2[1] is "MENU_NO" or "MENU_YES")
                            {
                                bool jumpOnNo = (n2 == "GoToIfEq") == (a2[1] == "MENU_NO");
                                var ask = new ScriptBlock { Kind = ScriptBlockKind.Ask, Text = natural };
                                if (!jumpOnNo)
                                {
                                    // Jump on YES: the target is the YES part, the rest the NO part.
                                    if (Branch(into, ask, a2[2], lines[i2], i2 + 1, depth, seen)) return true;
                                    i = i2 + 1;
                                    continue;
                                }
                                // Jump on NO: the rest is the YES part, the target the NO part.
                                if (depth >= maxDepth || !labels.TryGetValue(a2[2], out var noAt) || seen.Contains(a2[2])) { Add(into, new ScriptBlock { Kind = ScriptBlockKind.Message, Text = natural }); Code(into, lines[i1]); Code(into, lines[i2]); i = i2 + 1; continue; }
                                Add(into, ask);
                                bool yesEnds = Run(i2 + 1, into, depth + 1, seen);
                                into.Add(new ScriptBlock { Kind = ScriptBlockKind.Else });
                                bool noEnds = Run(noAt + 1, into, depth + 1, [.. seen, a2[2]]);
                                into.Add(new ScriptBlock { Kind = ScriptBlockKind.EndIf });
                                return true;
                            }
                        }
                        Add(into, new ScriptBlock { Kind = ScriptBlockKind.Message, Text = natural });
                        continue;
                    }
                    case "GoToIfSet" or "GoToIfUnset" when a.Length == 2 && Named(a[0], "FLAG_"):
                        if (Branch(into, new ScriptBlock { Kind = ScriptBlockKind.IfFlag, Constant = a[0], X = cmd == "GoToIfUnset" ? 1 : 0 }, a[1], raw, i, depth, seen)) return true;
                        continue;
                    case "GoToIfEq" or "GoToIfNe" when a.Length == 3 && a[0].StartsWith("VAR_", StringComparison.Ordinal) && a[0] != "VAR_RESULT" && int.TryParse(a[1], out var value):
                        if (Branch(into, new ScriptBlock { Kind = ScriptBlockKind.IfVar, Constant = a[0], Number = value, X = cmd == "GoToIfNe" ? 1 : 0 }, a[2], raw, i, depth, seen)) return true;
                        continue;
                    case "CheckItem" when a.Length == 3 && a[2] == "VAR_RESULT" && int.TryParse(a[1], out var count):
                    {
                        var (n1, a1, i1) = Peek(i);
                        if (n1 is "GoToIfEq" or "GoToIfNe" && a1.Length == 3 && a1[0] == "VAR_RESULT" && a1[1] is "FALSE" or "TRUE" or "0" or "1")
                        {
                            bool jumpWhenMissing = (n1 == "GoToIfEq") == (a1[1] is "FALSE" or "0");
                            if (Branch(into, new ScriptBlock { Kind = ScriptBlockKind.IfItem, Constant = a[0], Number = count, X = jumpWhenMissing ? 1 : 0 }, a1[2], lines[i1], i1 + 1, depth, seen)) return true;
                            i = i1 + 1;
                            continue;
                        }
                        Code(into, raw);
                        continue;
                    }
                    case "GoTo" when a.Length == 1:
                        if (depth < maxDepth && labels.TryGetValue(a[0], out var to) && !seen.Contains(a[0]))
                            return Run(to + 1, into, depth, [.. seen, a[0]]);
                        Code(into, raw);
                        return true;   // the flow goes on elsewhere
                    case "SetVar" when a.Length == 2 && a[0] == "VAR_0x8004" && Named(a[1], "ITEM_"):
                    {
                        // SetVar 0x8004 item, SetVar 0x8005 count, the common give-item script.
                        var (n1, a1, i1) = Peek(i);
                        var (n2, a2, i2) = Peek(i1 + 1);
                        int qty = 1;
                        if (n1 == "SetVar" && a1.Length == 2 && a1[0] == "VAR_0x8005" && int.TryParse(a1[1], out qty)
                            && (n2 == "Common_GiveItemQuantity" || (n2 == "CallCommonScript" && a2.FirstOrDefault() == "CommonScript_AddItemQuantity")))
                        {
                            Add(into, new ScriptBlock { Kind = ScriptBlockKind.GiveItem, Constant = a[1], Number = qty });
                            i = i2 + 1;
                            continue;
                        }
                        Code(into, raw);
                        continue;
                    }
                    case "SetVar" when a.Length == 2 && a[0].StartsWith("VAR_", StringComparison.Ordinal) && !a[0].StartsWith("VAR_0x8", StringComparison.Ordinal) && int.TryParse(a[1], out var v):
                        Add(into, new ScriptBlock { Kind = ScriptBlockKind.SetVar, Constant = a[0], Number = v });
                        continue;
                    case "SetFlag" when a.Length == 1 && Named(a[0], "FLAG_"):
                        Add(into, new ScriptBlock { Kind = ScriptBlockKind.SetFlag, Constant = a[0] });
                        continue;
                    case "ClearFlag" when a.Length == 1 && Named(a[0], "FLAG_"):
                        Add(into, new ScriptBlock { Kind = ScriptBlockKind.ClearFlag, Constant = a[0] });
                        continue;
                    case "StartTrainerBattle" or "StartWildBattle":
                    {
                        // The battle, the check whether it was won and the jump to the black-out: the block does all three.
                        var (n1, a1, i1) = Peek(i);
                        var (n2, a2, i2) = Peek(i1 + 1);
                        bool standard = n1 == "CheckWonBattle" && n2 == "GoToIfEq" && a2.Length == 3 && a2[1] == "FALSE";
                        var b = cmd == "StartTrainerBattle" && a.Length >= 1 && Named(a[0], "TRAINER_")
                            ? new ScriptBlock { Kind = ScriptBlockKind.TrainerBattle, Constant = a[0] }
                            : cmd == "StartWildBattle" && a.Length == 2 && Named(a[0], "SPECIES_") && int.TryParse(a[1], out var lvl) ? new ScriptBlock { Kind = ScriptBlockKind.WildBattle, Constant = a[0], Number = lvl } : null;
                        if (b == null || !standard || (cmd == "StartTrainerBattle" && a.Length > 1)) { Code(into, raw); continue; }
                        Add(into, b);
                        i = i2 + 1;
                        continue;
                    }
                    case "GivePokemon" when a.Length >= 2 && Named(a[0], "SPECIES_") && int.TryParse(a[1], out var level) && (a.Length < 3 || a[2] == "ITEM_NONE"):
                        if (into.Count > 0 && into[^1] is { Kind: ScriptBlockKind.Fanfare }) into.RemoveAt(into.Count - 1);
                        Add(into, new ScriptBlock { Kind = ScriptBlockKind.GivePokemon, Constant = a[0], Number = level });
                        continue;
                    case "HealParty":
                        Add(into, new ScriptBlock { Kind = ScriptBlockKind.HealParty });
                        continue;
                    case "PlayFanfare" when a.Length == 1 && Peek(i) is { Cmd: "WaitFanfare" } wf:
                        Add(into, new ScriptBlock { Kind = ScriptBlockKind.Fanfare, Constant = Regex.Replace(a[0], @"_sseq$", "") });
                        i = wf.Index + 1;
                        continue;
                    case "WaitTime" when a.Length >= 1 && int.TryParse(a[0], out var frames):
                        Add(into, new ScriptBlock { Kind = ScriptBlockKind.Wait, Number = frames });
                        continue;
                    case "ApplyMovement" when a.Length == 2 && Named(a[0], "LOCALID_") && Movement(a[1]) is { } steps:
                    {
                        var (n1, _, i1) = Peek(i);
                        Add(into, new ScriptBlock { Kind = ScriptBlockKind.Move, Constant = a[0], Text = steps, Number = n1 == "WaitMovement" ? 1 : 0 });
                        if (n1 == "WaitMovement") i = i1 + 1;
                        continue;
                    }
                    case "RemoveObject" when a.Length == 1 && Named(a[0], "LOCALID_"):
                        Add(into, new ScriptBlock { Kind = ScriptBlockKind.HidePerson, Constant = a[0] });
                        continue;
                    case "AddObject" when a.Length == 1 && Named(a[0], "LOCALID_"):
                        Add(into, new ScriptBlock { Kind = ScriptBlockKind.ShowPerson, Constant = a[0] });
                        continue;
                    case "GiveMoney" when a.Length == 1 && int.TryParse(a[0], out var money):
                        Add(into, new ScriptBlock { Kind = ScriptBlockKind.GiveMoney, Number = money });
                        continue;
                    case "FadeScreenOut" when Peek(i) is { Cmd: "WaitFadeScreen" } w && Peek(w.Index + 1) is { Cmd: "Warp" }:
                        i = w.Index + 1;
                        continue;
                    case "Warp" when a.Length >= 3 && Named(a[0], "MAP_HEADER_") && int.TryParse(a[1], out var wx) && int.TryParse(a[2], out var wz):
                        Add(into, new ScriptBlock { Kind = ScriptBlockKind.Warp, Constant = a[0], X = wx, Z = wz });
                        return true;
                    case "ShowMugshot" when a.Length >= 1:
                        Add(into, new ScriptBlock { Kind = ScriptBlockKind.Mugshot, Constant = a[0], X = a.Length > 1 && a[1] == "MUGSHOT_POS_LEFT" ? 1 : 0 });
                        continue;
                    case "ClearMugshot":
                        Add(into, new ScriptBlock { Kind = ScriptBlockKind.Mugshot, Constant = "MUGSHOT_NONE" });
                        continue;
                    case "ShowPicture" or "ShowFullscreenPicture" when a.Length == 1:
                        Add(into, new ScriptBlock { Kind = ScriptBlockKind.Picture, Constant = a[0] });
                        continue;
                    case "HidePicture":
                        Add(into, new ScriptBlock { Kind = ScriptBlockKind.Picture, Constant = "PICTURE_NONE" });
                        continue;
                    default:
                        Code(into, raw);
                        continue;
                }
            }
            return false;
        }
    }
}
