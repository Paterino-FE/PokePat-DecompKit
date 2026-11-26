using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using TilesetEditor.Core.Project;

namespace TilesetEditor.Core.Platinum;

/// <summary>Kinds of building blocks of an event script.</summary>
public enum ScriptBlockKind
{
    Message, Question, GiveItem, GivePokemon, TrainerBattle, WildBattle, HealParty, SetFlag, ClearFlag, OnlyOnce, Fanfare, Wait, Mugshot, Warp, SetVar, Picture, Move,
    // Branches: an opening block, the blocks of its first branch, optionally Else and the blocks of the other branch, EndIf.
    Ask, IfFlag, IfVar, IfItem, Else, EndIf,
    HidePerson, ShowPerson, GiveMoney, EndEvent,
    /// <summary>Script lines as they are (commands no block knows yet) – written unchanged.</summary>
    Code,
}

/// <summary>One building block with its values (which ones are used depends on <see cref="Kind"/>).</summary>
public sealed class ScriptBlock : System.ComponentModel.INotifyPropertyChanged
{
    private string _text = "", _yes = "", _no = "", _constant = "";
    private int _number = 1, _x, _z, _depth;

    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
    private void Changed() => PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(Summary)));

    public ScriptBlockKind Kind { get; set; }
    /// <summary>Message / question text in the writer's form (Enter = line, empty line = next box).</summary>
    public string Text { get => _text; set { _text = value; Changed(); } }
    public string YesText { get => _yes; set { _yes = value; Changed(); } }
    public string NoText { get => _no; set { _no = value; Changed(); } }
    /// <summary>Item, species, trainer, flag, fanfare, mugshot or map – the main constant of the block.</summary>
    public string Constant { get => _constant; set { _constant = value; Changed(); } }
    public int Number { get => _number; set { _number = value; Changed(); } }
    public int X { get => _x; set { _x = value; Changed(); } }
    public int Z { get => _z; set { _z = value; Changed(); } }
    /// <summary>How deep inside branches the block is (for the indentation of the list; not stored).</summary>
    public int Depth
    {
        get => _depth;
        set { if (_depth == value) return; _depth = value; PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(Depth))); }
    }

    public bool Opens => Kind is ScriptBlockKind.Ask or ScriptBlockKind.IfFlag or ScriptBlockKind.IfVar or ScriptBlockKind.IfItem;

    public static string Title(ScriptBlockKind k) => k switch
    {
        ScriptBlockKind.Message => "💬 Message",
        ScriptBlockKind.Question => "❓ Yes/No question",
        ScriptBlockKind.GiveItem => "🎁 Give item",
        ScriptBlockKind.GivePokemon => "🐾 Give Pokémon",
        ScriptBlockKind.TrainerBattle => "⚔ Trainer battle",
        ScriptBlockKind.WildBattle => "🌿 Wild battle",
        ScriptBlockKind.HealParty => "❤ Heal party",
        ScriptBlockKind.SetFlag => "🚩 Set flag",
        ScriptBlockKind.ClearFlag => "🏳 Clear flag",
        ScriptBlockKind.OnlyOnce => "1️⃣ Only once (then other text)",
        ScriptBlockKind.Fanfare => "🎺 Fanfare",
        ScriptBlockKind.Wait => "⏱ Wait",
        ScriptBlockKind.Mugshot => "🖼 Mugshot",
        ScriptBlockKind.SetVar => "🔢 Set variable",
        ScriptBlockKind.Picture => "🗺 Picture",
        ScriptBlockKind.Move => "🚶 Move",
        ScriptBlockKind.Ask => "❓ Ask Yes/No",
        ScriptBlockKind.IfFlag => "🔀 If flag",
        ScriptBlockKind.IfVar => "🔀 If variable",
        ScriptBlockKind.IfItem => "🔀 If player has item",
        ScriptBlockKind.Else => "↪ Otherwise (No)",
        ScriptBlockKind.EndIf => "⏹ End of the branch",
        ScriptBlockKind.HidePerson => "👻 Hide person",
        ScriptBlockKind.ShowPerson => "🧍 Show person",
        ScriptBlockKind.GiveMoney => "💰 Give money",
        ScriptBlockKind.EndEvent => "🛑 End event here",
        ScriptBlockKind.Code => "⚙ Script code",
        _ => "🚪 Warp",
    };

    public string Summary => Kind switch
    {
        ScriptBlockKind.Message or ScriptBlockKind.Question => Text.Replace('\n', ' '),
        ScriptBlockKind.GiveItem => $"{Constant} ×{Number}",
        ScriptBlockKind.GivePokemon or ScriptBlockKind.WildBattle => $"{Constant} Lv. {Number}",
        ScriptBlockKind.Wait => $"{Number} frames",
        ScriptBlockKind.Warp => $"{Constant} ({X}, {Z})",
        ScriptBlockKind.SetVar => $"{Constant} = {Number}",
        ScriptBlockKind.Mugshot => Constant is "" or "MUGSHOT_NONE" ? "hide" : X == 1 ? $"{Constant} (left)" : Constant,
        ScriptBlockKind.Picture => Constant is "" or "PICTURE_NONE" ? "hide" : Constant.StartsWith("FULLSCREEN_", StringComparison.Ordinal) ? $"{Constant} (full screen)" : Constant,
        ScriptBlockKind.Move => $"{(Constant == "LOCALID_PLAYER" ? "Player" : Constant)}: {PlatinumScriptBlocks.MoveSummary(Text)}" + (Number == 0 ? " (no wait)" : ""),
        ScriptBlockKind.HealParty => "",
        ScriptBlockKind.Ask => Text.Replace('\n', ' ') + "  → Yes: the blocks below · No: after \"Otherwise\"",
        ScriptBlockKind.IfFlag => X == 1 ? $"{Constant} is NOT set" : $"{Constant} is set",
        ScriptBlockKind.IfVar => X == 1 ? $"{Constant} ≠ {Number}" : $"{Constant} = {Number}",
        ScriptBlockKind.IfItem => X == 1 ? $"{Constant} ×{Math.Max(1, Number)} NOT in the bag" : $"{Constant} ×{Math.Max(1, Number)} in the bag",
        ScriptBlockKind.Code => string.Join(" · ", Text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Take(3)) + (Text.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length > 3 ? " …" : ""),
        ScriptBlockKind.Else or ScriptBlockKind.EndIf or ScriptBlockKind.EndEvent => "",
        ScriptBlockKind.HidePerson => Constant + (Text.Length > 0 ? $" (stays hidden: {Text})" : ""),
        ScriptBlockKind.ShowPerson => Constant + (Text.Length > 0 ? $" (clears {Text})" : ""),
        ScriptBlockKind.GiveMoney => $"{Number} ₽",
        _ => Constant,
    };
}

/// <summary>The structure of branches in a flat block list.</summary>
public static class ScriptBlockTree
{
    /// <summary>Sets <see cref="ScriptBlock.Depth"/>: blocks inside a branch one deeper; Else and End at the level of their If.</summary>
    public static void UpdateDepths(IList<ScriptBlock> blocks)
    {
        int depth = 0;
        foreach (var b in blocks)
        {
            if (b.Kind is ScriptBlockKind.Else) { b.Depth = Math.Max(0, depth - 1); continue; }
            if (b.Kind is ScriptBlockKind.EndIf) { depth = Math.Max(0, depth - 1); b.Depth = depth; continue; }
            b.Depth = depth;
            if (b.Opens) depth++;
        }
    }

    /// <summary>The Else and EndIf that belong to the opening block at <paramref name="open"/> (-1 when missing).</summary>
    public static (int Else, int End) PartsOf(IList<ScriptBlock> blocks, int open)
    {
        int level = 0, els = -1;
        for (int i = open + 1; i < blocks.Count; i++)
        {
            var k = blocks[i].Kind;
            if (blocks[i].Opens) level++;
            else if (k == ScriptBlockKind.Else && level == 0 && els < 0) els = i;
            else if (k == ScriptBlockKind.EndIf) { if (level == 0) return (els, i); level--; }
        }
        return (els, -1);
    }

    /// <summary>The opening block an Else or EndIf at <paramref name="index"/> belongs to (-1 when none).</summary>
    public static int OpenerOf(IList<ScriptBlock> blocks, int index)
    {
        int level = 0;
        for (int i = index - 1; i >= 0; i--)
        {
            if (blocks[i].Kind == ScriptBlockKind.EndIf) level++;
            else if (blocks[i].Opens) { if (level == 0) return i; level--; }
        }
        return -1;
    }
}

/// <summary>What a generated script needs: its text, new messages and the label of the script.</summary>
/// <summary>What starts the script: talking to a person, checking a sign, stepping on a trigger tile.</summary>
public enum ScriptStart { Person, Sign, Step,
    /// <summary>Scripts the map runs while it is set up (entering, loading): no locking, no message box – they just end.</summary>
    Silent,
    /// <summary>A part other scripts run with Call: no locking, it ends with Return (back to the caller).</summary>
    Subroutine }

public sealed record BuiltScript(string Body, List<(string Id, string Text)> Messages, List<string> Includes);

/// <summary>
/// Turns building blocks into a pokeplatinum script (assembly macros), like the event builder of the Emerald part does with
/// Poryscript. Texts become messages of the map's text bank; the commands follow the project's version (e.g. how items are
/// given: Common_GiveItemQuantity in older, CallCommonScript CommonScript_AddItemQuantity in newer pokeplatinum).
/// </summary>
public static class PlatinumScriptBlocks
{
    /// <summary>Version differences: sound names (newer pokeplatinum adds "_sseq"), how items are given.</summary>
    public sealed record Dialect(string ConfirmSound, bool OldItemMacro)
    {
        /// <summary>Movement steps this pokeplatinum knows (asm/macros/movement.inc); empty = not checked.</summary>
        public IReadOnlySet<string> MovementSteps { get; init; } = new HashSet<string>();
        public string SoundSuffix => ConfirmSound.Contains("_sseq", StringComparison.Ordinal) ? "_sseq" : "";
        public string Fanfare(string name) => name.EndsWith("_sseq", StringComparison.Ordinal) || SoundSuffix.Length == 0 ? name : name + SoundSuffix;
    }

    /// <summary>Fanfares for the block (without the version suffix).</summary>
    public static readonly string[] Fanfares = ["SEQ_FANFA4", "SEQ_FANFA1", "SEQ_FANFA3", "SEQ_FANFA5", "SEQ_ASA", "SEQ_KINOMI"];

    public static Dialect DialectOf(string root)
    {
        var inc = ProjectPaths.Combine(root, "asm/macros/scrcmd.inc");
        var text = File.Exists(inc) ? File.ReadAllText(inc) : "";
        var movement = ProjectPaths.Combine(root, "asm/macros/movement.inc");
        var steps = File.Exists(movement)
            ? Regex.Matches(File.ReadAllText(movement), @"\.macro\s+(\w+)").Select(m => m.Groups[1].Value).Where(n => n != "EndMovement").ToHashSet()
            : [];
        return new Dialect(PlatinumMapScripts.ConfirmSound(root), text.Contains(".macro Common_GiveItemQuantity", StringComparison.Ordinal)) { MovementSteps = steps };
    }

    public static BuiltScript Build(string label, string textPrefix, IReadOnlyList<ScriptBlock> blocks, Dialect d, bool talkToPlayer = true) =>
        Build(label, textPrefix, blocks, d, talkToPlayer ? ScriptStart.Person : ScriptStart.Step);

    /// <summary>How the script begins: a person faces the player, a sign only locks, a trigger tile starts silently.</summary>
    public static BuiltScript Build(string label, string textPrefix, IReadOnlyList<ScriptBlock> blocks, Dialect d, ScriptStart start)
    {
        var sb = new StringBuilder();
        var messages = new List<(string, string)>();
        var includes = new List<string>();
        var tail = new StringBuilder();
        var movements = new StringBuilder();
        int moveCount = 0;
        int n = 0;
        bool boxOpen = false, needsLost = false;
        string Msg(string natural)
        {
            var id = $"{textPrefix}{++n}";
            messages.Add((id, PlatinumDialogText.FromNatural(natural, false)));
            // Names go into slot 0 (player) and 1 (rival) – unless the script fills that slot itself (some of the game's scripts
            // swap them, e.g. "BufferRivalName 0"); then its own choice stays.
            bool Filled(int slot) => Regex.IsMatch(sb.ToString(), @"^\s*Buffer\w+\s+" + slot + @"\b", RegexOptions.Multiline);
            if (natural.Contains(PlatinumDialogText.PlayerName) && !Filled(0)) sb.Append("    BufferPlayerName 0\n");
            if (natural.Contains(PlatinumDialogText.RivalName) && !Filled(1)) sb.Append("    BufferRivalName 1\n");
            return id;
        }
        void Close() { if (boxOpen) { sb.Append("    CloseMessage\n"); boxOpen = false; } }

        sb.Append(start switch
        {
            ScriptStart.Person => $"    PlaySE {d.ConfirmSound}\n    LockAll\n    FacePlayer\n",
            ScriptStart.Sign => $"    PlaySE {d.ConfirmSound}\n    LockAll\n",
            ScriptStart.Silent or ScriptStart.Subroutine => "",
            _ => "    LockAll\n",
        });
        // "Only once" goes first: afterwards the other text is shown and the script ends.
        foreach (var once in blocks.Where(b => b.Kind == ScriptBlockKind.OnlyOnce))
        {
            if (once.Constant.Length == 0) throw new InvalidDataException("\"Only once\" needs a flag (FLAG_…) that remembers it.");
            sb.Append($"    GoToIfSet {once.Constant}, {label}_AlreadyDone\n");
            tail.Append($"{label}_AlreadyDone:\n");
            if (once.Text.Trim().Length > 0) { var onceId = Msg(once.Text); tail.Append($"    Message {onceId}\n    WaitButton\n    CloseMessage\n"); }
            tail.Append("    ReleaseAll\n    End\n\n");
        }
        int q = 0, ifs = 0;
        var open = new Stack<(int Id, bool HasElse, bool BoxAtStart, string What)>();
        void EndHere()
        {
            foreach (var once in blocks.Where(x => x.Kind == ScriptBlockKind.OnlyOnce)) sb.Append($"    SetFlag {once.Constant}\n");
            Close();
            sb.Append(start switch { ScriptStart.Silent => "    End\n", ScriptStart.Subroutine => "    Return\n", _ => "    ReleaseAll\n    End\n" });
        }
        foreach (var b in blocks)
        {
            switch (b.Kind)
            {
                case ScriptBlockKind.Ask:
                {
                    int id = ++ifs;
                    var askId = Msg(b.Text.Trim().Length > 0 ? b.Text : "?");
                    sb.Append($"    Message {askId}\n    ShowYesNoMenu VAR_RESULT\n    GoToIfEq VAR_RESULT, MENU_NO, {label}_Else{id}\n");
                    boxOpen = true;
                    open.Push((id, false, true, "Ask Yes/No"));
                    break;
                }
                case ScriptBlockKind.IfFlag:
                {
                    Need(b.Constant, "FLAG_", "a flag");
                    int id = ++ifs;
                    // Jump to the other branch when the condition is not true.
                    sb.Append(b.X == 1 ? $"    GoToIfSet {b.Constant}, {label}_Else{id}\n" : $"    GoToIfUnset {b.Constant}, {label}_Else{id}\n");
                    open.Push((id, false, boxOpen, "If flag"));
                    break;
                }
                case ScriptBlockKind.IfVar:
                {
                    Need(b.Constant, "VAR_", "a variable");
                    int id = ++ifs;
                    sb.Append(b.X == 1 ? $"    GoToIfEq {b.Constant}, {b.Number}, {label}_Else{id}\n" : $"    GoToIfNe {b.Constant}, {b.Number}, {label}_Else{id}\n");
                    open.Push((id, false, boxOpen, "If variable"));
                    break;
                }
                case ScriptBlockKind.IfItem:
                {
                    Need(b.Constant, "ITEM_", "an item");
                    int id = ++ifs;
                    sb.Append($"    CheckItem {b.Constant}, {Math.Max(1, b.Number)}, VAR_RESULT\n    GoToIfEq VAR_RESULT, {(b.X == 1 ? "TRUE" : "FALSE")}, {label}_Else{id}\n");
                    open.Push((id, false, boxOpen, "If player has item"));
                    break;
                }
                case ScriptBlockKind.Else:
                {
                    if (open.Count == 0 || open.Peek().HasElse) throw new InvalidDataException("\"Otherwise\" needs an \"If …\" or \"Ask Yes/No\" above it (and only one per branch).");
                    var top = open.Pop();
                    Close();
                    sb.Append($"    GoTo {label}_EndIf{top.Id}\n\n{label}_Else{top.Id}:\n");
                    boxOpen = top.BoxAtStart;
                    open.Push(top with { HasElse = true });
                    break;
                }
                case ScriptBlockKind.EndIf:
                {
                    if (open.Count == 0) throw new InvalidDataException("\"End of the branch\" without an \"If …\" or \"Ask Yes/No\" above it.");
                    var top = open.Pop();
                    // Both ways arrive here with the message box closed.
                    Close();
                    if (!top.HasElse)
                    {
                        sb.Append($"    GoTo {label}_EndIf{top.Id}\n\n{label}_Else{top.Id}:\n");
                        if (top.BoxAtStart) sb.Append("    CloseMessage\n");
                    }
                    sb.Append($"\n{label}_EndIf{top.Id}:\n");
                    boxOpen = false;
                    break;
                }
                case ScriptBlockKind.EndEvent:
                    EndHere();
                    break;
                case ScriptBlockKind.HidePerson:
                    Need(b.Constant, "LOCALID_", "a person");
                    if (b.Text.Length > 0) { Need(b.Text, "FLAG_", "the flag that keeps the person hidden"); sb.Append($"    SetFlag {b.Text}\n"); }
                    sb.Append($"    RemoveObject {b.Constant}\n");
                    break;
                case ScriptBlockKind.ShowPerson:
                    Need(b.Constant, "LOCALID_", "a person");
                    if (b.Text.Length > 0) { Need(b.Text, "FLAG_", "the person's hide flag"); sb.Append($"    ClearFlag {b.Text}\n"); }
                    sb.Append($"    AddObject {b.Constant}\n");
                    break;
                case ScriptBlockKind.Code:
                    foreach (var raw in b.Text.Replace("\r\n", "\n").Split('\n'))
                    {
                        var line = raw.Trim();
                        if (line.Length == 0) continue;
                        sb.Append("    ").Append(line).Append('\n');
                        // Keep track of the message box, so the blocks around it close it right.
                        var cmd = line.Split(' ', 2)[0];
                        if (cmd is "CloseMessage" or "CloseMessageWithoutErasing") boxOpen = false;
                        else if (cmd.Contains("Message", StringComparison.Ordinal) || cmd.StartsWith("ShowYesNoMenu", StringComparison.Ordinal)) boxOpen = true;
                    }
                    break;
                case ScriptBlockKind.GiveMoney:
                    sb.Append($"    GiveMoney {Math.Clamp(b.Number, 1, 999999)}\n");
                    break;
                case ScriptBlockKind.Message:
                    if (b.Text.Trim().Length == 0) break;
                    var msgId = Msg(b.Text);
                    sb.Append($"    Message {msgId}\n    WaitButton\n");
                    boxOpen = true;
                    break;
                case ScriptBlockKind.Question:
                    q++;
                    var questionId = Msg(b.Text);
                    sb.Append($"    Message {questionId}\n    ShowYesNoMenu VAR_RESULT\n    GoToIfEq VAR_RESULT, MENU_NO, {label}_No{q}\n");
                    if (b.YesText.Trim().Length > 0) { var yesId = Msg(b.YesText); sb.Append($"    Message {yesId}\n    WaitButton\n"); }
                    sb.Append($"    GoTo {label}_Answered{q}\n\n{label}_No{q}:\n");
                    if (b.NoText.Trim().Length > 0) { var noId = Msg(b.NoText); sb.Append($"    Message {noId}\n    WaitButton\n"); }
                    sb.Append($"\n{label}_Answered{q}:\n");
                    boxOpen = true;
                    break;
                case ScriptBlockKind.GiveItem:
                    Need(b.Constant, "ITEM_", "an item");
                    sb.Append($"    SetVar VAR_0x8004, {b.Constant}\n    SetVar VAR_0x8005, {Math.Max(1, b.Number)}\n");
                    sb.Append(d.OldItemMacro ? "    Common_GiveItemQuantity\n" : "    CallCommonScript CommonScript_AddItemQuantity\n");
                    boxOpen = true;
                    break;
                case ScriptBlockKind.GivePokemon:
                    Need(b.Constant, "SPECIES_", "a Pokémon");
                    sb.Append($"    PlayFanfare {d.Fanfare("SEQ_FANFA4")}\n    WaitFanfare\n    GivePokemon {b.Constant}, {Math.Clamp(b.Number, 1, 100)}, ITEM_NONE, VAR_RESULT\n");
                    break;
                case ScriptBlockKind.TrainerBattle:
                    Need(b.Constant, "TRAINER_", "a trainer");
                    Close();
                    sb.Append($"    StartTrainerBattle {b.Constant}\n    CheckWonBattle VAR_RESULT\n    GoToIfEq VAR_RESULT, FALSE, {label}_LostBattle\n");
                    needsLost = true;
                    break;
                case ScriptBlockKind.WildBattle:
                    Need(b.Constant, "SPECIES_", "a Pokémon");
                    Close();
                    sb.Append($"    StartWildBattle {b.Constant}, {Math.Clamp(b.Number, 1, 100)}\n    CheckWonBattle VAR_RESULT\n    GoToIfEq VAR_RESULT, FALSE, {label}_LostBattle\n");
                    needsLost = true;
                    break;
                case ScriptBlockKind.HealParty:
                    sb.Append("    HealParty\n");
                    break;
                case ScriptBlockKind.SetFlag:
                    Need(b.Constant, "FLAG_", "a flag");
                    sb.Append($"    SetFlag {b.Constant}\n");
                    break;
                case ScriptBlockKind.ClearFlag:
                    Need(b.Constant, "FLAG_", "a flag");
                    sb.Append($"    ClearFlag {b.Constant}\n");
                    break;
                case ScriptBlockKind.SetVar:
                    Need(b.Constant, "VAR_", "a variable");
                    sb.Append($"    SetVar {b.Constant}, {b.Number}\n");
                    break;
                case ScriptBlockKind.Fanfare:
                    if (b.Constant.Length == 0) break;
                    sb.Append($"    PlayFanfare {d.Fanfare(b.Constant)}\n    WaitFanfare\n");
                    break;
                case ScriptBlockKind.Wait:
                    sb.Append($"    WaitTime {Math.Clamp(b.Number, 1, 600)}, VAR_RESULT\n");
                    break;
                case ScriptBlockKind.Mugshot:
                    // X = 1: left of the message box (mirrored); 0: right.
                    if (b.Constant.Length == 0 || b.Constant == "MUGSHOT_NONE") sb.Append("    ClearMugshot\n");
                    else sb.Append(b.X == 1 ? $"    ShowMugshot {b.Constant}, MUGSHOT_POS_LEFT\n" : $"    ShowMugshot {b.Constant}\n");
                    if (!includes.Contains("constants/mugshots.h")) includes.Add("constants/mugshots.h");
                    break;
                case ScriptBlockKind.Move:
                {
                    if (b.Constant != "LOCALID_PLAYER") Need(b.Constant, "LOCALID_", "who moves");
                    var steps = MoveSteps(b.Text);
                    if (steps.Count == 0) throw new InvalidDataException("A \"Move\" block needs at least one step.");
                    foreach (var (step, count) in steps)
                    {
                        if (d.MovementSteps.Count > 0 && !d.MovementSteps.Contains(step)) throw new InvalidDataException($"\"{step}\" is not a movement step of this pokeplatinum.");
                        if (count is < 1 or > 99) throw new InvalidDataException($"{step}: 1–99 times.");
                    }
                    var moveLabel = $"{label}_Movement{++moveCount}";
                    sb.Append($"    ApplyMovement {b.Constant}, {moveLabel}\n");
                    if (b.Number != 0) sb.Append("    WaitMovement\n");
                    // Movement data is aligned to 4 bytes, like the game's own.
                    movements.Append($"\n    .balign 4, 0\n{moveLabel}:\n");
                    foreach (var (step, count) in steps) movements.Append(count == 1 ? $"    {step}\n" : $"    {step} {count}\n");
                    movements.Append("    EndMovement\n");
                    break;
                }
                case ScriptBlockKind.Picture:
                    if (b.Constant.Length == 0 || b.Constant == "PICTURE_NONE") sb.Append("    HidePicture\n");
                    else if (b.Constant.StartsWith("FULLSCREEN_", StringComparison.Ordinal))
                    {
                        // A screen of its own (mod 1.2): the message box closes first; the field fades out and back in.
                        Close();
                        Need(b.Constant, "FULLSCREEN_", "a full-screen picture");
                        sb.Append($"    ShowFullscreenPicture {b.Constant}\n");
                    }
                    else { Need(b.Constant, "PICTURE_", "a picture"); sb.Append($"    ShowPicture {b.Constant}\n"); }
                    if (!includes.Contains("constants/mugshots.h")) includes.Add("constants/mugshots.h");
                    break;
                case ScriptBlockKind.Warp:
                    Need(b.Constant, "MAP_HEADER_", "a map");
                    Close();
                    sb.Append("    FadeScreenOut\n    WaitFadeScreen\n");
                    sb.Append($"    Warp {b.Constant}, {b.X}, {b.Z}, DIR_SOUTH\n");
                    break;
            }
        }
        if (open.Count > 0) throw new InvalidDataException($"\"{open.Peek().What}\" needs an \"End of the branch\" block below its blocks.");
        EndHere();
        sb.Append('\n');
        if (needsLost) tail.Append($"{label}_LostBattle:\n    BlackOutFromBattle\n    ReleaseAll\n    End\n\n");
        sb.Append(tail);
        sb.Append(movements.ToString().TrimStart('\n'));
        return new BuiltScript(sb.ToString().TrimEnd('\n') + "\n", messages, includes);
    }

    // ------------------------------------------------------------------ scripts kept as blocks

    /// <summary>
    /// A script made from blocks carries them in a comment line right above its label, so PokéPat can open it as blocks
    /// again. The comment is plain C (the script files go through the preprocessor) and does not change the ROM.
    /// </summary>
    public const string Marker = "/* PokePat blocks: ";

    private sealed record BlockDto(ScriptBlockKind Kind, string? Text, string? Yes, string? No, string? Constant, int Number, int X, int Z);
    private sealed record MarkerDto(int V, string Texts, List<BlockDto> Blocks);

    private static readonly System.Text.Json.JsonSerializerOptions JsonOpts = new()
    {
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    public static string MarkerLine(string textPrefix, IEnumerable<ScriptBlock> blocks)
    {
        var dto = new MarkerDto(1, textPrefix, blocks.Select(b => new BlockDto(b.Kind, Empty(b.Text), Empty(b.YesText), Empty(b.NoText), Empty(b.Constant), b.Number, b.X, b.Z)).ToList());
        return Marker + Convert.ToBase64String(System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(dto, JsonOpts)) + " */";
        static string? Empty(string s) => s.Length == 0 ? null : s;
    }

    /// <summary>The blocks and the text prefix of a script made from blocks, or null for a hand-written script.</summary>
    public static (List<ScriptBlock> Blocks, string TextPrefix)? ReadBlocks(string script, string label)
    {
        var m = Regex.Match(script, @"^/\* PokePat blocks: ([A-Za-z0-9+/=]+) \*/[ \t]*\r?\n" + Regex.Escape(label) + @":[ \t]*\r?$", RegexOptions.Multiline);
        if (!m.Success) return null;
        try
        {
            var dto = System.Text.Json.JsonSerializer.Deserialize<MarkerDto>(Convert.FromBase64String(m.Groups[1].Value), JsonOpts);
            if (dto == null) return null;
            var blocks = dto.Blocks.Select(b => new ScriptBlock
            {
                Kind = b.Kind, Text = b.Text ?? "", YesText = b.Yes ?? "", NoText = b.No ?? "", Constant = b.Constant ?? "", Number = b.Number, X = b.X, Z = b.Z,
            }).ToList();
            return (blocks, dto.Texts);
        }
        catch (Exception ex) when (ex is FormatException or System.Text.Json.JsonException) { return null; }
    }

    /// <summary>
    /// Writes the generated script for <paramref name="label"/>: replaces a script made from blocks (label, its own helper
    /// labels label_… and the comment), or only the label's own lines of a hand-written script (its helper labels may be used
    /// by other scripts and stay). A label that does not exist yet is added as the next ScriptEntry.
    /// Returns the new text and the ScriptEntry number when the label was added (else 0).
    /// </summary>
    public static (string Script, int AddedNumber) WriteScript(string script, string label, string textPrefix, IReadOnlyList<ScriptBlock> blocks, BuiltScript built)
    {
        var marker = MarkerLine(textPrefix, blocks);
        var labelLine = Regex.Match(script, @"^" + Regex.Escape(label) + @":[ \t]*\r?$", RegexOptions.Multiline);
        int added = 0;
        if (!labelLine.Success)
        {
            (script, added) = PlatinumMapScripts.AddScript(script, label, built.Body, built.Includes);
            labelLine = Regex.Match(script, @"^" + Regex.Escape(label) + @":[ \t]*\r?$", RegexOptions.Multiline);
            return (script.Insert(labelLine.Index, marker + "\n"), added);
        }
        bool managed = ReadBlocks(script, label) != null;
        int start = labelLine.Index;
        if (managed)
        {
            int lineStart = script.LastIndexOf('\n', Math.Max(0, start - 2)) + 1;
            start = lineStart;
        }
        // The region ends at the next label that is not one of ours, a block comment of another script, or .balign.
        int end = script.Length;
        foreach (Match next in Regex.Matches(script, @"^(?:(\w+):|/\* PokePat blocks:|[ \t]*\.balign)", RegexOptions.Multiline))
        {
            if (next.Index <= labelLine.Index) continue;
            var name = next.Groups[1].Success ? next.Groups[1].Value : null;
            if (managed && name != null && name.StartsWith(label + "_", StringComparison.Ordinal)) continue;
            // ".balign" right before our own movement data belongs to us.
            if (managed && name == null && !next.Value.Contains("PokePat") && Regex.Match(script[(next.Index + next.Length)..], @"^(\w+):", RegexOptions.Multiline) is { Success: true } after
                && after.Groups[1].Value.StartsWith(label + "_", StringComparison.Ordinal)
                && script[(next.Index + next.Length)..][..after.Index].Trim().Trim(',', ' ', '0', '4').Length == 0) continue;
            end = next.Index;
            break;
        }
        var block = $"{marker}\n{label}:\n{built.Body.TrimEnd('\n')}\n\n";
        script = script[..start] + block + script[end..];
        foreach (var inc in built.Includes)
        {
            var line = $"#include \"{inc}\"";
            if (script.Contains(line, StringComparison.Ordinal)) continue;
            var lastInclude = Regex.Matches(script, @"^#include[^\n]*\n", RegexOptions.Multiline).LastOrDefault();
            script = lastInclude != null ? script.Insert(lastInclude.Index + lastInclude.Length, line + "\n") : line + "\n" + script;
        }
        return (script, added);
    }

    /// <summary>
    /// Puts the generated messages into the text bank: existing ids are updated, new ones added; earlier messages of the same
    /// script that are no longer needed become empty (removing them would shift the numbers of the messages after them).
    /// </summary>
    public static List<JsonObject> WriteMessages(PlatinumJsonFile bank, string textPrefix, IReadOnlyList<(string Id, string Text)> messages)
    {
        var all = PlatinumMapScripts.Messages(bank).OfType<JsonObject>().ToList();
        var touched = new List<JsonObject>();
        foreach (var (id, text) in messages)
        {
            var m = all.FirstOrDefault(x => x["id"]?.GetValue<string>() == id);
            if (m == null) { m = PlatinumMapScripts.AddMessage(bank, id, text); all.Add(m); }
            else if (PlatinumMapScripts.MessageText(m) != text) PlatinumMapScripts.SetMessageText(m, text);
            touched.Add(m);
        }
        var ids = messages.Select(x => x.Id).ToHashSet();
        var own = new Regex("^" + Regex.Escape(textPrefix) + @"\d+$");
        foreach (var m in all)
            if (m["id"]?.GetValue<string>() is { } id && own.IsMatch(id) && !ids.Contains(id) && PlatinumMapScripts.MessageText(m).Length > 0)
                PlatinumMapScripts.SetMessageText(m, "");
        return touched;
    }

    /// <summary>A text prefix whose numbered ids (prefix1, prefix2 …) are not used yet in the bank.</summary>
    public static string FreeTextPrefix(PlatinumJsonFile bank, string wanted)
    {
        var used = PlatinumMapScripts.Messages(bank).OfType<JsonObject>().Select(m => m["id"]?.GetValue<string>() ?? "").ToHashSet();
        bool Free(string p) => !used.Any(u => u.StartsWith(p, StringComparison.Ordinal) && u.Length > p.Length && u[p.Length..].All(char.IsDigit));
        if (Free(wanted)) return wanted;
        for (int i = 2; ; i++) if (Free($"{wanted}V{i}_")) return $"{wanted}V{i}_";
    }

    /// <summary>"WalkNormalSouth 3;EmoteExclamationMark" → [(WalkNormalSouth, 3), (EmoteExclamationMark, 1)].</summary>
    public static List<(string Step, int Count)> MoveSteps(string text) =>
        text.Split([';', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(x => x.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            .Select(p => (p[0], p.Length > 1 && int.TryParse(p[1], out var n) ? n : 1)).ToList();

    /// <summary>A short summary: "↓3 ! ⏱".</summary>
    public static string MoveSummary(string text) => string.Join(" ", MoveSteps(text).Select(s =>
    {
        var arrow = s.Step.EndsWith("North", StringComparison.Ordinal) ? "↑" : s.Step.EndsWith("South", StringComparison.Ordinal) ? "↓"
            : s.Step.EndsWith("West", StringComparison.Ordinal) ? "←" : s.Step.EndsWith("East", StringComparison.Ordinal) ? "→" : "";
        var kind = s.Step.StartsWith("Face", StringComparison.Ordinal) ? "face " : s.Step.StartsWith("Jump", StringComparison.Ordinal) ? "jump " : s.Step.StartsWith("Run", StringComparison.Ordinal) ? "run " : "";
        if (arrow.Length > 0) return kind + arrow + (s.Count > 1 ? s.Count.ToString() : "");
        return s.Step switch { "EmoteExclamationMark" => "!", "EmoteDoubleExclamationMark" => "!!", _ when s.Step.StartsWith("Delay", StringComparison.Ordinal) => "⏱", _ => s.Step };
    }));

    private static void Need(string value, string prefix, string what)
    {
        if (!Regex.IsMatch(value, "^" + prefix + @"[A-Za-z0-9_]+$")) throw new InvalidDataException($"Choose {what} ({prefix}…) for the block.");
    }
}
