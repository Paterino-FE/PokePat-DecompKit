namespace TilesetEditor.Core.Platinum;

public enum PlayKind
{
    /// <summary>A message box: press A.</summary>
    Message,
    /// <summary>The Yes/No menu of the game: answer Yes or No.</summary>
    Question,
    /// <summary>Something the preview cannot know (a battle, the bag): the person playing decides.</summary>
    Decision,
    /// <summary>Something happens without a text (a flag, a fanfare, a movement …): shown in the log.</summary>
    Log,
    /// <summary>The event is over.</summary>
    End,
}

/// <summary>One step of playing an event: what is on screen and what is asked.</summary>
public sealed record PlayStep(PlayKind Kind, string Text, string? Mugshot = null, bool MugshotLeft = false, string? Picture = null, string Yes = "Yes", string No = "No");

/// <summary>
/// Plays building blocks like the game would, one step at a time, for the preview: message boxes (one per box of a text),
/// the Yes/No menu, and branches by the flags and variables the preview remembers (they start as chosen and change with the
/// blocks). What the preview cannot know – the result of a battle, the bag – is asked.
/// </summary>
public sealed class PlatinumEventRunner
{
    private readonly List<ScriptBlock> _blocks;
    private readonly Queue<PlayStep> _pending = new();
    private int _pc;
    private bool _ended, _started;
    private Func<bool, int>? _answer;   // what to do with the answer of the current question: returns the next index
    private string? _mugshot, _picture;
    private bool _mugLeft;

    public Dictionary<string, bool> Flags { get; }
    public Dictionary<string, int> Vars { get; } = new(StringComparer.Ordinal);
    /// <summary>Everything that happened, in order (for the log of the preview).</summary>
    public List<string> History { get; } = [];

    public PlatinumEventRunner(IEnumerable<ScriptBlock> blocks, IDictionary<string, bool>? flags = null)
    {
        _blocks = blocks.ToList();
        Flags = new Dictionary<string, bool>(flags ?? new Dictionary<string, bool>(), StringComparer.Ordinal);
    }

    private bool Flag(string f) => Flags.TryGetValue(f, out var v) && v;

    /// <summary>The first step, or the next one after A was pressed / the answer was given (true = Yes / won / has it).</summary>
    public PlayStep Next(bool answer = true)
    {
        if (_answer is { } a)
        {
            _answer = null;
            _pc = a(answer);
        }
        if (!_started)
        {
            _started = true;
            // "Only once": when its flag is set already, only the other text is shown.
            foreach (var once in _blocks.Where(b => b.Kind == ScriptBlockKind.OnlyOnce))
                if (once.Constant.Length > 0 && Flag(once.Constant))
                {
                    History.Add($"1️⃣ {once.Constant} is set – the event was done before");
                    Boxes(once.Text);
                    _ended = true;
                    break;
                }
        }
        while (_pending.Count == 0)
        {
            if (_ended || _pc >= _blocks.Count)
            {
                if (!_ended) Finish();
                return new PlayStep(PlayKind.End, "The event is over.");
            }
            Run(_blocks[_pc++]);
        }
        return _pending.Dequeue();
    }

    private void Finish()
    {
        foreach (var once in _blocks.Where(b => b.Kind == ScriptBlockKind.OnlyOnce && b.Constant.Length > 0))
        {
            Flags[once.Constant] = true;
            History.Add($"🚩 {once.Constant} set (only once)");
        }
        _ended = true;
    }

    private void Boxes(string natural)
    {
        foreach (var box in natural.Replace("\r\n", "\n").Split("\n\n", StringSplitOptions.RemoveEmptyEntries))
            if (box.Trim().Length > 0) _pending.Enqueue(new PlayStep(PlayKind.Message, box.Trim('\n'), _mugshot, _mugLeft, _picture));
    }

    private void Log(string text)
    {
        History.Add(text);
        _pending.Enqueue(new PlayStep(PlayKind.Log, text, _mugshot, _mugLeft, _picture));
    }

    /// <summary>Where a branch that is not taken continues: after its Else, or after its End.</summary>
    private int OtherBranch(int open)
    {
        var (els, end) = ScriptBlockTree.PartsOf(_blocks, open);
        return els >= 0 ? els + 1 : end >= 0 ? end + 1 : _blocks.Count;
    }

    private void Branch(int open, bool take, string why)
    {
        History.Add(why + (take ? " → yes" : " → no"));
        if (!take) _pc = OtherBranch(open);
    }

    private void Run(ScriptBlock b)
    {
        int at = _pc - 1;
        switch (b.Kind)
        {
            case ScriptBlockKind.Message:
                Boxes(b.Text);
                break;
            case ScriptBlockKind.Question:
                Boxes(b.Text);
                _pending.Enqueue(new PlayStep(PlayKind.Question, "Yes or no?", _mugshot, _mugLeft, _picture));
                _answer = yes =>
                {
                    History.Add(yes ? "❓ answered Yes" : "❓ answered No");
                    Boxes(yes ? b.YesText : b.NoText);
                    return _pc;
                };
                break;
            case ScriptBlockKind.Ask:
            {
                var boxes = b.Text.Replace("\r\n", "\n").Split("\n\n", StringSplitOptions.RemoveEmptyEntries).Where(x => x.Trim().Length > 0).ToList();
                foreach (var box in boxes.Take(boxes.Count - 1)) _pending.Enqueue(new PlayStep(PlayKind.Message, box.Trim('\n'), _mugshot, _mugLeft, _picture));
                _pending.Enqueue(new PlayStep(PlayKind.Question, boxes.LastOrDefault()?.Trim('\n') ?? "", _mugshot, _mugLeft, _picture));
                _answer = yes => { History.Add(yes ? "❓ answered Yes" : "❓ answered No"); return yes ? at + 1 : OtherBranch(at); };
                break;
            }
            case ScriptBlockKind.IfFlag:
            {
                bool set = Flag(b.Constant);
                Branch(at, b.X == 1 ? !set : set, $"🔀 {b.Constant} is {(set ? "set" : "not set")}");
                break;
            }
            case ScriptBlockKind.IfVar:
            {
                int v = Vars.GetValueOrDefault(b.Constant);
                Branch(at, b.X == 1 ? v != b.Number : v == b.Number, $"🔀 {b.Constant} is {v} ({(b.X == 1 ? "not " : "")}{b.Number} wanted)");
                break;
            }
            case ScriptBlockKind.IfItem:
                _pending.Enqueue(new PlayStep(PlayKind.Decision, $"Does the player have {Pretty(b.Constant, "ITEM_")} ×{Math.Max(1, b.Number)}?", _mugshot, _mugLeft, _picture, "Has it", "Not in the bag"));
                _answer = has =>
                {
                    History.Add($"🎒 {b.Constant}: {(has ? "in the bag" : "not in the bag")}");
                    return (b.X == 1 ? !has : has) ? at + 1 : OtherBranch(at);
                };
                break;
            case ScriptBlockKind.Code:
                Log("⚙ " + b.Summary);
                break;
            case ScriptBlockKind.Else:
            {
                // Reached at the end of the first branch: skip the other one.
                int open = ScriptBlockTree.OpenerOf(_blocks, at);
                var (_, end) = open >= 0 ? ScriptBlockTree.PartsOf(_blocks, open) : (-1, -1);
                _pc = end >= 0 ? end + 1 : _blocks.Count;
                break;
            }
            case ScriptBlockKind.EndIf:
                break;
            case ScriptBlockKind.EndEvent:
                Log("🛑 The event ends here");
                Finish();
                break;
            case ScriptBlockKind.GiveItem:
                _pending.Enqueue(new PlayStep(PlayKind.Message, $"{PlatinumDialogText.PlayerName} obtained\n{Pretty(b.Constant, "ITEM_")}{(b.Number > 1 ? " ×" + b.Number : "")}!", _mugshot, _mugLeft, _picture));
                History.Add($"🎁 {b.Constant} ×{Math.Max(1, b.Number)}");
                break;
            case ScriptBlockKind.GivePokemon:
                _pending.Enqueue(new PlayStep(PlayKind.Message, $"{PlatinumDialogText.PlayerName} received\n{Pretty(b.Constant, "SPECIES_")}!", _mugshot, _mugLeft, _picture));
                History.Add($"🐾 {b.Constant} Lv. {b.Number}");
                break;
            case ScriptBlockKind.GiveMoney:
                Log($"💰 +{b.Number} ₽");
                break;
            case ScriptBlockKind.TrainerBattle:
            case ScriptBlockKind.WildBattle:
            {
                string who = b.Kind == ScriptBlockKind.TrainerBattle ? Pretty(b.Constant, "TRAINER_") : $"wild {Pretty(b.Constant, "SPECIES_")} Lv. {b.Number}";
                _mugshot = null; _picture = null;
                _pending.Enqueue(new PlayStep(PlayKind.Decision, $"⚔ Battle against {who}", null, false, null, "Won", "Lost"));
                _answer = won =>
                {
                    History.Add($"⚔ {who}: {(won ? "won" : "lost – the player blacks out, the event ends")}");
                    if (!won) { _ended = true; return _blocks.Count; }
                    return _pc;
                };
                break;
            }
            case ScriptBlockKind.HealParty: Log("❤ The party is healed"); break;
            case ScriptBlockKind.SetFlag: Flags[b.Constant] = true; Log($"🚩 {b.Constant} set"); break;
            case ScriptBlockKind.ClearFlag: Flags[b.Constant] = false; Log($"🏳 {b.Constant} cleared"); break;
            case ScriptBlockKind.SetVar: Vars[b.Constant] = b.Number; Log($"🔢 {b.Constant} = {b.Number}"); break;
            case ScriptBlockKind.OnlyOnce: break;
            case ScriptBlockKind.Fanfare: Log($"🎺 {b.Constant}"); break;
            case ScriptBlockKind.Wait: Log($"⏱ wait {b.Number} frames"); break;
            case ScriptBlockKind.Move: Log($"🚶 {(b.Constant == "LOCALID_PLAYER" ? "Player" : b.Constant)}: {PlatinumScriptBlocks.MoveSummary(b.Text)}"); break;
            case ScriptBlockKind.HidePerson:
                if (b.Text.Length > 0) Flags[b.Text] = true;
                Log($"👻 {b.Constant} disappears");
                break;
            case ScriptBlockKind.ShowPerson:
                if (b.Text.Length > 0) Flags[b.Text] = false;
                Log($"🧍 {b.Constant} appears");
                break;
            case ScriptBlockKind.Mugshot:
                _mugshot = b.Constant is "" or "MUGSHOT_NONE" ? null : b.Constant;
                _mugLeft = b.X == 1;
                History.Add(_mugshot == null ? "🖼 mugshot hidden" : $"🖼 {b.Constant}");
                break;
            case ScriptBlockKind.Picture:
                if (b.Constant.StartsWith("FULLSCREEN_", StringComparison.Ordinal))
                    _pending.Enqueue(new PlayStep(PlayKind.Log, $"🗺 Full-screen picture {b.Constant} (A or B closes it)", null, false, b.Constant));
                else _picture = b.Constant is "" or "PICTURE_NONE" ? null : b.Constant;
                History.Add($"🗺 {b.Constant}");
                break;
            case ScriptBlockKind.Warp:
                Log($"🚪 Warp to {b.Constant} ({b.X}, {b.Z}) – the event ends with the new map");
                Finish();
                break;
        }
    }

    private static string Pretty(string constant, string prefix) =>
        System.Globalization.CultureInfo.InvariantCulture.TextInfo.ToTitleCase(constant.Replace(prefix, "").Replace('_', ' ').ToLowerInvariant());
}
