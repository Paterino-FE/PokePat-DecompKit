using TilesetEditor.Core.Events;

namespace TilesetEditor.Core.Text;

public enum DialogState
{
    Typing,
    /// <summary>Box full or message finished: the arrow blinks until A is pressed.</summary>
    WaitingForA,
    /// <summary>YES/NO menu is shown.</summary>
    Choosing,
    /// <summary>A condition the preview cannot know (item in bag, trainer beaten …): the user decides.</summary>
    Deciding,
    Finished,
}

/// <summary>
/// Plays a toolkit event frame by frame like the game: text appears character by character
/// (text speed = frames per character), \n continues on line 2, \l waits for A and scrolls, \p waits for A
/// and starts a new box, MSGBOX_YESNO shows the YES/NO menu, flags are simulated.
/// </summary>
public sealed class DialogSession
{
    private readonly GbaFont _font;
    private readonly Charmap? _charmap;
    private readonly Func<string, string> _itemName;
    private readonly Stack<(List<EventStep> List, int Index)> _stack = new();
    private readonly Queue<string> _pending = new();
    private readonly string[] _lines = ["", ""];
    private int _line;
    private int _wait;
    private bool _afterWaitScroll, _afterWaitClear, _menuAfterText, _waitAtEnd;
    private YesNoStep? _question;
    private CommandStep? _decision;
    private CommandStep? _menuStep;
    private List<string> _menuOptions = [];
    private bool _menuCancel;
    private int _menuX, _menuY;
    private readonly Queue<PreviewAction> _previewQueue = new();
    private readonly Dictionary<string, string> _strVars = new() { ["STR_VAR_1"] = "???", ["STR_VAR_2"] = "???", ["STR_VAR_3"] = "???" };

    /// <summary>Question shown while <see cref="DialogState.Deciding"/>.</summary>
    public string? DecisionPrompt { get; private set; }
    private int _arrowCounter;

    public DialogSession(ToolkitEvent ev, GbaFont font, Charmap? charmap, ISet<string> flags, Func<string, string>? itemName = null)
    {
        _font = font;
        _charmap = charmap;
        Flags = flags;
        _itemName = itemName ?? (s => s.Replace("ITEM_", "").Replace('_', ' '));
        _stack.Push((ev.Steps, 0));
    }

    private bool _started;

    /// <summary>Starts on the first frame/input, so settings like <see cref="PlayerName"/> apply to the first text.</summary>
    private void EnsureStarted()
    {
        if (_started) return;
        _started = true;
        Next();
    }

    /// <summary>Frames per character (Emerald options: slow 8, mid 4, fast 1).</summary>
    public int TextSpeed { get; set; } = 4;
    public string PlayerName { get; set; } = "PLAYER";
    public ISet<string> Flags { get; }
    public DialogState State { get; private set; }
    public int YesNoCursor { get; private set; }
    /// <summary>Increments whenever the picture changes.</summary>
    public int Version { get; private set; }
    public List<string> Log { get; } = [];
    public bool BoxVisible { get; private set; }

    public IReadOnlyList<string> Lines => _lines;
    public bool ShowArrow => State == DialogState.WaitingForA && (_arrowCounter / 16) % 2 == 0;
    public int ArrowFrame => _arrowCounter / 8;

    public DialogScene Scene(DialogFrameStyle style, int stdFrame, Imaging.PngImage? customFrame) => new()
    {
        Style = style,
        StdFrame = stdFrame,
        CustomFrame = customFrame,
        ShowMessageBox = BoxVisible,
        Lines = _lines,
        ShowArrow = State == DialogState.WaitingForA,
        ArrowFrame = ArrowFrame,
        YesNoCursor = State == DialogState.Choosing && _menuStep == null ? YesNoCursor : null,
        MenuOptions = State == DialogState.Choosing && _menuStep != null ? _menuOptions : null,
        MenuCursor = YesNoCursor,
        MenuX = _menuX,
        MenuY = _menuY,
    };

    /// <summary>Advances one frame (1/60 s).</summary>
    public void Tick()
    {
        if (!_started) { EnsureStarted(); return; }
        if (State == DialogState.WaitingForA)
        {
            _arrowCounter++;
            if (_arrowCounter % 8 == 0) Version++;
            return;
        }
        if (State != DialogState.Typing) return;
        if (--_wait > 0) return;
        _wait = TextSpeed;
        TypeNext();
    }

    /// <summary>A button: skips the typing to the next stop, or continues after a stop.</summary>
    public void PressA()
    {
        if (!_started) { EnsureStarted(); return; }
        switch (State)
        {
            case DialogState.Typing:
                while (State == DialogState.Typing) TypeNext();
                break;
            case DialogState.WaitingForA:
                if (_afterWaitScroll) { _lines[0] = _lines[1]; _lines[1] = ""; _line = 1; _afterWaitScroll = false; State = DialogState.Typing; }
                else if (_afterWaitClear) { _lines[0] = _lines[1] = ""; _line = 0; _afterWaitClear = false; State = DialogState.Typing; }
                else Next();
                Version++;
                break;
            case DialogState.Choosing:
                if (_menuStep != null) ChooseMenu(YesNoCursor);
                else Choose(YesNoCursor == 0);
                break;
        }
    }

    public void MoveCursor(int delta)
    {
        if (State != DialogState.Choosing) return;
        YesNoCursor = Math.Clamp(YesNoCursor + delta, 0, _menuStep != null ? _menuOptions.Count - 1 : 1);
        Version++;
    }

    /// <summary>Options of the open menu (empty for YES/NO).</summary>
    public IReadOnlyList<string> MenuOptions => _menuStep != null ? _menuOptions : [];

    /// <summary>B button: NO in a YES/NO question, cancel in a menu (if allowed).</summary>
    public void PressB()
    {
        if (State != DialogState.Choosing) return;
        if (_menuStep == null) { Choose(false); return; }
        if (_menuCancel) ChooseMenu(_menuOptions.Count);
    }

    /// <summary>Selects menu option <paramref name="index"/>; index = option count means "cancelled with B".</summary>
    public void ChooseMenu(int index)
    {
        if (State != DialogState.Choosing || _menuStep == null) return;
        var branches = _menuStep.CaseBranches();
        Log.Add(index < _menuOptions.Count ? $"Choice: {_menuOptions[index]}" : "Menu cancelled with B");
        var steps = index < branches.Count ? branches[index].Steps : [];
        _menuStep = null;
        if (steps.Count > 0) _stack.Push((steps, 0));
        Next();
    }

    public void Choose(bool yes)
    {
        if (State == DialogState.Deciding && _decision != null)
        {
            Log.Add($"{DecisionPrompt} → {(yes ? "yes" : "no")}");
            var b = yes ? _decision.Then : _decision.Else;
            _decision = null;
            DecisionPrompt = null;
            if (b.Count > 0) _stack.Push((b, 0));
            Next();
            return;
        }
        if (State != DialogState.Choosing || _question == null) return;
        Log.Add(yes ? "Answer: YES" : "Answer: NO");
        var branch = yes ? _question.Yes : _question.No;
        _question = null;
        if (branch.Count > 0) _stack.Push((branch, 0));
        Next();
    }

    private void TypeNext()
    {
        if (_pending.Count == 0)
        {
            if (_menuAfterText) { _menuAfterText = false; State = DialogState.Choosing; YesNoCursor = 0; Version++; return; }
            if (_waitAtEnd) { State = DialogState.WaitingForA; _arrowCounter = 0; Version++; return; }
            Next();
            return;
        }
        var u = _pending.Dequeue();
        switch (u)
        {
            case "\\n": _line = 1; break;
            case "\\l": _afterWaitScroll = true; State = DialogState.WaitingForA; _arrowCounter = 0; break;
            case "\\p": _afterWaitClear = true; State = DialogState.WaitingForA; _arrowCounter = 0; break;
            default: _lines[_line] += u; break;
        }
        Version++;
    }

    private void StartText(string text, bool menuAfter)
    {
        foreach (var (k, v) in _strVars) text = text.Replace("{" + k + "}", v);
        var encoded = TextLayout.Format(text.Replace("{PLAYER}", PlayerName), _font, _charmap).Encoded;
        _pending.Clear();
        var units = TextLayout.Units(encoded);
        for (int i = 0; i < units.Count; i++)
        {
            if (units[i] == "\\" && i + 1 < units.Count) { _pending.Enqueue("\\" + units[++i]); continue; }
            _pending.Enqueue(units[i]);
        }
        _lines[0] = _lines[1] = "";
        _line = 0;
        _menuAfterText = menuAfter;
        _waitAtEnd = !menuAfter;
        BoxVisible = true;
        State = DialogState.Typing;
        _wait = 1;
        Version++;
    }

    /// <summary>Runs non-visual steps until something has to be shown.</summary>
    private void Next()
    {
        while (_previewQueue.Count > 0 || _stack.Count > 0)
        {
            if (_previewQueue.Count > 0)
            {
                var action = _previewQueue.Dequeue();
                if (action.Text != null) { StartText(action.Text, menuAfter: false); return; }
                if (action.Log != null) Log.Add(action.Log);
                continue;
            }
            var (list, index) = _stack.Pop();
            if (index >= list.Count) continue;
            _stack.Push((list, index + 1));
            switch (list[index])
            {
                case MessageStep m:
                    StartText(m.Text, menuAfter: false);
                    return;
                case YesNoStep y:
                    _question = y;
                    StartText(y.Question, menuAfter: true);
                    return;
                case IfFlagStep f:
                    bool set = Flags.Contains(f.Flag);
                    Log.Add($"Check {f.Flag}: {(set ? "set" : "not set")}");
                    var branch = set ? f.Then : f.Else;
                    if (branch.Count > 0) _stack.Push((branch, 0));
                    break;
                case SetFlagStep s:
                    Flags.Add(s.Flag);
                    Log.Add($"{s.Flag} set");
                    break;
                case ClearFlagStep c:
                    Flags.Remove(c.Flag);
                    Log.Add($"{c.Flag} cleared");
                    break;
                case CommandStep c when c.Def is { } def:
                    var args = new BlockArgs(def, c.Args);
                    if (def.Id == "buffer_species") _strVars[args["slot"]] = args["species"].Replace("SPECIES_", "");
                    if (def.Id == "buffer_item") _strVars[args["slot"]] = _itemName(args["item"]);
                    foreach (var a in def.Preview(args)) _previewQueue.Enqueue(a);
                    if (def.IsSwitch && def.Menu is { } menu)
                    {
                        (var options, _menuCancel) = menu(args);
                        _menuOptions = options.ToList();
                        int.TryParse(args["x"], out _menuX);
                        int.TryParse(args["y"], out _menuY);
                        _menuStep = c;
                        StartText(args["question"], menuAfter: true);
                        return;
                    }
                    if (def.IsBranch)
                    {
                        // Show pending texts first, then ask.
                        while (_previewQueue.Count > 0)
                        {
                            var a = _previewQueue.Dequeue();
                            if (a.Log != null) Log.Add(a.Log);
                        }
                        _decision = c;
                        DecisionPrompt = def.DecisionPrompt?.Invoke(args) ?? def.Title + "?";
                        BoxVisible = false;
                        State = DialogState.Deciding;
                        Version++;
                        return;
                    }
                    break;
                case GiveItemStep g:
                    Log.Add($"Item received: {g.Item} × {g.Count}");
                    // data/text/obtain_item.inc: gText_ObtainedTheItem
                    StartText(g.Count > 1 ? $"Obtained {g.Count} {_itemName(g.Item)}s!" : $"Obtained the {_itemName(g.Item)}!", menuAfter: false);
                    return;
            }
        }
        BoxVisible = false;
        State = DialogState.Finished;
        Log.Add("End (closemessage, release)");
        Version++;
    }
}
