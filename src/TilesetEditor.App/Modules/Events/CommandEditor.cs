using System.Collections.ObjectModel;
using System.Windows.Media.Imaging;
using TilesetEditor.App.Infrastructure;
using TilesetEditor.Core.Events;
using TilesetEditor.Core.Text;

namespace TilesetEditor.App.Modules.Events;

public sealed record PaletteItem(string Id, string Icon, string Title, string Help)
{
    public string Text => $"{Icon} {Title}";
}

public sealed record PaletteGroup(string Category, IReadOnlyList<PaletteItem> Items);

/// <summary>One input field of a block (rendered according to its kind).</summary>
public sealed class ParamFieldVm : ObservableObject
{
    private readonly Func<string> _get;
    private readonly Action<string> _set;
    private readonly Func<string, (IReadOnlyList<BitmapSource>, IReadOnlyList<TextProblem>)>? _render;

    public ParamFieldVm(string label, string help, ParamKind kind, IReadOnlyList<string> options, Func<string> get, Action<string> set,
        Func<string, (IReadOnlyList<BitmapSource>, IReadOnlyList<TextProblem>)>? render, IReadOnlyList<string>? movementActions = null,
        bool multiline = false)
    {
        _multiline = multiline;
        Label = label;
        Help = help;
        Kind = kind;
        Options = options;
        _get = get;
        _set = set;
        _render = render;
        MovementActions = movementActions ?? [];
        UpdatePreview();
    }

    public string Label { get; }
    public string Help { get; }
    public ParamKind Kind { get; }
    public IReadOnlyList<string> Options { get; }
    public IReadOnlyList<string> MovementActions { get; }

    public bool IsMessage => Kind == ParamKind.Message;
    public bool IsMovement => Kind == ParamKind.Movement;
    public bool IsList => !IsMessage && !IsMovement && Options.Count > 0;
    public bool IsPlain => !IsMessage && !IsMovement && Options.Count == 0;
    public bool HasHelp => Help.Length > 0;
    /// <summary>Lists (menu answers, shop items) are entered one per line.</summary>
    public bool IsMultiline => IsPlain && _multiline;
    private readonly bool _multiline;

    public string Value
    {
        get => _get();
        set
        {
            value ??= "";
            if (_get() == value) return;
            _set(value);
            OnPropertyChanged();
            UpdatePreview();
        }
    }

    // Movement helper: pick an action + repeat count and append it.
    private string? _newAction;
    private int _newCount = 1;
    public string? NewAction { get => _newAction; set => Set(ref _newAction, value); }
    public int NewCount { get => _newCount; set => Set(ref _newCount, Math.Clamp(value, 1, 99)); }
    public RelayCommand AddMovementCommand => new(() =>
    {
        if (string.IsNullOrWhiteSpace(NewAction)) return;
        var step = NewCount > 1 ? $"{NewAction}*{NewCount}" : NewAction;
        Value = Value.Trim().Length == 0 ? step : Value.TrimEnd().TrimEnd(',') + ", " + step;
    });

    public ObservableCollection<BitmapSource> Boxes { get; } = [];
    public ObservableCollection<string> Problems { get; } = [];

    private void UpdatePreview()
    {
        if (!IsMessage || _render == null) return;
        var (boxes, problems) = _render(Value);
        Boxes.Clear();
        foreach (var b in boxes) Boxes.Add(b);
        Problems.Clear();
        foreach (var p in problems) Problems.Add((p.IsError ? "Error: " : "Warning: ") + p.Message);
    }
}

/// <summary>Editor for a catalog block: one field per parameter; the generic command block rebuilds its fields per command.</summary>
public sealed class CommandEditorVm : StepEditor
{
    private readonly CommandStep _step;
    private readonly ProjectSymbols _symbols;
    private readonly IReadOnlyList<string> _targets;
    private readonly Func<string, (IReadOnlyList<BitmapSource>, IReadOnlyList<TextProblem>)> _render;
    private readonly Action _changed;

    public CommandEditorVm(CommandStep step, ProjectSymbols symbols, IReadOnlyList<string> targets,
        Func<string, (IReadOnlyList<BitmapSource>, IReadOnlyList<TextProblem>)> render, Action changed, Action? showOnMap = null) : base(changed)
    {
        _step = step;
        _symbols = symbols;
        _targets = targets;
        _render = render;
        _changed = changed;
        CanShowOnMap = showOnMap != null && step.Block is "move" or "move_together";
        ShowOnMapCommand = new RelayCommand(() => showOnMap?.Invoke(), () => CanShowOnMap);
        BuildFields();
    }

    /// <summary>Movement blocks: opens the map preview with the walking paths.</summary>
    public bool CanShowOnMap { get; }
    public RelayCommand ShowOnMapCommand { get; }

    public string Title => _step.Def?.Title ?? _step.Block;
    public string Help => _step.Def?.Help ?? "";
    public ObservableCollection<ParamFieldVm> Fields { get; } = [];
    private string _commandInfo = "";
    public string CommandInfo { get => _commandInfo; private set => Set(ref _commandInfo, value); }

    private IReadOnlyList<string> OptionsFor(ParamKind kind, BlockParam? p) => kind switch
    {
        ParamKind.Flag => _symbols.Flags.Select(f => f.Name).ToList(),
        ParamKind.Var => _symbols.Vars,
        ParamKind.Item => _symbols.Items.Select(i => i.Name).ToList(),
        ParamKind.Species => _symbols.Species,
        ParamKind.Trainer => _symbols.Trainers,
        ParamKind.Map => _symbols.Maps,
        ParamKind.Music => _symbols.Music,
        ParamKind.Sound => _symbols.Sounds,
        ParamKind.Weather => _symbols.Weathers,
        ParamKind.Special => _symbols.Specials,
        ParamKind.Target => _targets,
        ParamKind.Choice => p?.Choices ?? [],
        ParamKind.Macro => _symbols.Macros.Select(m => m.Name).ToList(),
        _ => [],
    };

    private void BuildFields()
    {
        Fields.Clear();
        var def = _step.Def;
        if (def == null) return;
        foreach (var p in def.Params)
        {
            var key = p.Key;
            Fields.Add(new ParamFieldVm(p.Label + (p.Optional ? " (optional)" : ""), p.Help, p.Kind, OptionsFor(p.Kind, p),
                () => _step.Args.TryGetValue(key, out var v) ? v : "",
                v =>
                {
                    _step.Args[key] = v;
                    if (p.Kind == ParamKind.Macro) BuildMacroFields();
                    _changed();
                },
                _render, p.Kind == ParamKind.Movement ? _symbols.MovementActions : null,
                multiline: key is "options" or "items" or "group"));
        }
        if (def.Id == BlockCatalog.MacroBlockId) BuildMacroFields();
    }

    /// <summary>For the generic command block: one field per macro parameter (arg0, arg1, …).</summary>
    private void BuildMacroFields()
    {
        while (Fields.Count > 1) Fields.RemoveAt(Fields.Count - 1);
        var name = _step.Args.TryGetValue("command", out var c) ? c : "";
        var macro = _symbols.Macros.FirstOrDefault(m => m.Name == name);
        if (macro == null) { CommandInfo = name.Length == 0 ? "" : "Unknown command."; return; }
        CommandInfo = $"{macro.Signature}\n{macro.Description}";
        for (int i = 0; i < macro.Params.Count; i++)
        {
            var mp = macro.Params[i];
            var key = $"arg{i}";
            var kind = GuessKind(mp.Name);
            // "song" is used for music and sound effects alike (playse, playbgm, playfanfare).
            var options = mp.Name == "song" ? _symbols.Music.Concat(_symbols.Sounds).ToList() : OptionsFor(kind, null);
            Fields.Add(new ParamFieldVm(
                mp.Name + (mp.Required ? "" : mp.Default != null ? $" (optional, default {mp.Default})" : " (optional)"),
                "", kind, options,
                () => _step.Args.TryGetValue(key, out var v) ? v : "",
                v => { _step.Args[key] = v; _changed(); },
                _render));
        }
    }

    /// <summary>Picks a list for a macro parameter by its name (flag, var, item, species, …).</summary>
    private static ParamKind GuessKind(string name)
    {
        var n = name.ToLowerInvariant();
        if (n.Contains("flag")) return ParamKind.Flag;
        if (n.Contains("species") || n == "mon") return ParamKind.Species;
        if (n.Contains("item")) return ParamKind.Item;
        if (n.Contains("trainer")) return ParamKind.Trainer;
        if (n.Contains("song") || n.Contains("music") || n.Contains("fanfare")) return ParamKind.Music;
        if (n.Contains("weather")) return ParamKind.Weather;
        if (n == "map" || n.EndsWith("map")) return ParamKind.Map;
        if (n.Contains("localid") || n == "objectid") return ParamKind.Target;
        if (n.StartsWith("var") || n.EndsWith("var")) return ParamKind.Var;
        return ParamKind.Free;
    }
}
