using System.Collections.ObjectModel;
using System.Windows.Media.Imaging;
using TilesetEditor.App.Infrastructure;
using TilesetEditor.Core.Events;
using TilesetEditor.Core.Text;

namespace TilesetEditor.App.Modules.Events;

/// <summary>Node in the block tree: either a step or a branch ("If YES", "Else", …) that holds steps.</summary>
public abstract class NodeVm : ObservableObject
{
    private bool _isSelected, _isExpanded = true;
    public ObservableCollection<NodeVm> Children { get; } = [];
    public abstract string Header { get; }
    public abstract string Icon { get; }
    public bool IsSelected { get => _isSelected; set => Set(ref _isSelected, value); }
    public bool IsExpanded { get => _isExpanded; set => Set(ref _isExpanded, value); }
    public void RefreshHeader() => OnPropertyChanged(nameof(Header));
}

public sealed class StepNode(EventStep step, List<EventStep> owner) : NodeVm
{
    public EventStep Step { get; } = step;
    /// <summary>The list this step belongs to (event steps or a branch).</summary>
    public List<EventStep> Owner { get; } = owner;
    public override string Header => $"{Step.Title}: {Step.Summary}";
    public override string Icon => Step switch
    {
        MessageStep => "💬",
        YesNoStep => "❓",
        IfFlagStep => "🔀",
        SetFlagStep => "🚩",
        ClearFlagStep => "🏳",
        GiveItemStep => "🎁",
        _ => "•",
    };
}

public sealed class BranchNode(string caption, List<EventStep> list) : NodeVm
{
    public List<EventStep> List { get; } = list;
    public override string Header => List.Count == 0 ? $"{caption}  (empty: add blocks here)" : caption;
    public override string Icon => "↳";
}

// ---- Editors for the selected block (one DataTemplate each in EventsView.xaml)

public abstract class StepEditor(Action changed) : ObservableObject
{
    protected void Changed() => changed();
}

public sealed class TextEditorVm : StepEditor
{
    private readonly Func<string> _get;
    private readonly Action<string> _set;
    private readonly Func<string, (IReadOnlyList<BitmapSource> Boxes, IReadOnlyList<TextProblem> Problems)> _render;

    public TextEditorVm(string caption, string hint, Func<string> get, Action<string> set,
        Func<string, (IReadOnlyList<BitmapSource>, IReadOnlyList<TextProblem>)> render, Action changed) : base(changed)
    {
        Caption = caption;
        Hint = hint;
        _get = get;
        _set = set;
        _render = render;
        UpdatePreview();
    }

    public string Caption { get; }
    public string Hint { get; }
    public string Text
    {
        get => _get();
        set
        {
            if (_get() == value) return;
            _set(value);
            OnPropertyChanged();
            UpdatePreview();
            Changed();
        }
    }

    public ObservableCollection<BitmapSource> Boxes { get; } = [];
    public ObservableCollection<string> Problems { get; } = [];

    private void UpdatePreview()
    {
        var (boxes, problems) = _render(Text);
        Boxes.Clear();
        foreach (var b in boxes) Boxes.Add(b);
        Problems.Clear();
        foreach (var p in problems) Problems.Add((p.IsError ? "Error: " : "Warning: ") + p.Message);
    }
}

public sealed class FlagEditorVm(string caption, Func<string> get, Action<string> set, IReadOnlyList<FlagSymbol> flags,
    Func<string?> suggestFree, Action changed) : StepEditor(changed)
{
    public string Caption { get; } = caption;
    public IReadOnlyList<FlagSymbol> Flags { get; } = flags;
    public string Flag
    {
        get => get();
        set
        {
            value = (value ?? "").Trim();
            if (get() == value) return;
            set(value);
            OnPropertyChanged();
            OnPropertyChanged(nameof(Info));
            Changed();
        }
    }

    public string Info
    {
        get
        {
            var f = Flags.FirstOrDefault(x => x.Name == Flag);
            if (Flag.Length == 0) return "Flags permanently remember things in the save file (e.g. \"item already received\").";
            if (f == null) return "Unknown flag. Please pick one from the list.";
            return f.IsUnused ? "Free flag (marked \"Unused\" in the project): good for your own events." : $"Used by the game{(f.Comment.Length > 0 ? ": " + f.Comment : "")}. Only change it if you know what it stands for.";
        }
    }

    public RelayCommand UseFreeFlagCommand => new(() => { if (suggestFree() is { } f) Flag = f; });
}

public sealed class ItemEditorVm(GiveItemStep step, IReadOnlyList<ItemSymbol> items, Action changed) : StepEditor(changed)
{
    public IReadOnlyList<ItemSymbol> Items { get; } = items;
    public string Item
    {
        get => step.Item;
        set
        {
            value = (value ?? "").Trim();
            if (step.Item == value) return;
            step.Item = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(Info));
            Changed();
        }
    }

    public int Count
    {
        get => step.Count;
        set
        {
            if (step.Count == value) return;
            step.Count = Math.Clamp(value, 1, 999);
            OnPropertyChanged();
            Changed();
        }
    }

    public string Info => Items.FirstOrDefault(i => i.Name == Item) is { } i
        ? $"In game: \"{(i.DisplayName.Length > 0 ? i.DisplayName : i.Name)}\". The game automatically shows \"Obtained the …!\" and puts the item in the bag."
        : "Unknown item. Please pick one from the list.";
}

public sealed class BranchEditorVm(string text) : StepEditor(() => { })
{
    public string Text { get; } = text;
}
