using System.Collections.ObjectModel;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using TilesetEditor.App.Infrastructure;
using TilesetEditor.Core.Events;
using TilesetEditor.Core.Text;

namespace TilesetEditor.App.Modules.Events;

/// <summary>One entry of the list: an event (person, sign, trigger, map script) or a text on its own.</summary>
public sealed class ExistingRow
{
    public ExistingEvent? Event { get; init; }
    public ExistingDialog? Dialog { get; init; }
    public required string Map { get; init; }
    public required string Title { get; init; }
    public required string Subtitle { get; init; }
    public required string Icon { get; init; }
    public IReadOnlyList<ExistingDialog> Dialogs => Event?.Dialogs ?? (Dialog != null ? [Dialog] : []);
    public string Search { get; init; } = "";
}

/// <summary>A dialog of the selected entry with its boxes as the game shows them.</summary>
public sealed record ExistingDialogVm(ExistingDialog Dialog, IReadOnlyList<BitmapSource> Boxes)
{
    public string Header => $"{Dialog.Label}   ·   {Dialog.File}:{Dialog.Line}" + (Dialog.UsedByScripts.Count > 0 ? $"   ·   shown by {string.Join(", ", Dialog.UsedByScripts.Take(3))}" : "");
    public string Text => Dialog.EditorText;
}

/// <summary>
/// "Existing in the game": every person, sign, trigger and text the project already has, searchable by text, map or script,
/// with the dialogs rendered like in game. An entry can be copied into a new event of the event builder.
/// </summary>
public sealed class ExistingDialogsViewModel : ObservableObject
{
    private readonly Func<string, (IReadOnlyList<BitmapSource> Boxes, IReadOnlyList<TextProblem> Problems)> _render;
    private readonly Action<string, string, IReadOnlyList<string>> _useAsTemplate;
    private readonly Action<string, string> _openScript;
    private List<ExistingRow> _events = [], _texts = [];
    private int _generation;

    public const string AllMaps = "(all maps)";
    public static readonly string[] Modes = ["People, signs & triggers", "All texts"];

    public ExistingDialogsViewModel(Func<string, (IReadOnlyList<BitmapSource>, IReadOnlyList<TextProblem>)> render,
        Action<string, string, IReadOnlyList<string>> useAsTemplate, Action<string, string> openScript)
    {
        _render = render;
        _useAsTemplate = useAsTemplate;
        _openScript = openScript;
        UseAsTemplateCommand = new RelayCommand(() =>
        {
            if (Selected is { } r) _useAsTemplate(r.Map, r.Title, r.Dialogs.Select(d => d.EditorText).ToList());
        }, () => Selected is { Dialogs.Count: > 0 } && Selected.Map != "(shared)");
        OpenScriptCommand = new RelayCommand(() =>
        {
            if (Selected is { } r && r.Map != "(shared)") _openScript(r.Map, r.Event?.Script ?? r.Dialog?.UsedByScripts.FirstOrDefault() ?? "");
        }, () => Selected != null && Selected.Map != "(shared)");
    }

    public ICommand UseAsTemplateCommand { get; }
    public ICommand OpenScriptCommand { get; }

    public ObservableCollection<ExistingRow> Rows { get; } = [];
    public ObservableCollection<ExistingDialogVm> SelectedDialogs { get; } = [];
    public ObservableCollection<string> Maps { get; } = [AllMaps];
    public IReadOnlyList<string> ModeList => Modes;

    private string _search = "", _map = AllMaps, _mode = Modes[0], _summary = "", _details = "";
    public string Search { get => _search; set { if (Set(ref _search, value)) Apply(); } }
    public string Map { get => _map; set { if (Set(ref _map, value ?? AllMaps)) Apply(); } }
    public string Mode { get => _mode; set { if (Set(ref _mode, value ?? Modes[0])) Apply(); } }
    public string Summary { get => _summary; private set => Set(ref _summary, value); }
    public string Details { get => _details; private set => Set(ref _details, value); }

    private bool _isLoading;
    public bool IsLoading { get => _isLoading; private set => Set(ref _isLoading, value); }

    private ExistingRow? _selected;
    public ExistingRow? Selected
    {
        get => _selected;
        set
        {
            if (!Set(ref _selected, value)) return;
            SelectedDialogs.Clear();
            if (value == null) { Details = ""; return; }
            var e = value.Event;
            Details = e != null
                ? $"{e.KindText} {e.Who} in {e.Map}" + (e.X >= 0 ? $" at ({e.X},{e.Y})" : "") + $"\nScript: {e.Script}" +
                  (e.Dialogs.Count == 0 ? "\nShows no text of its own (only movements, items, battles … or a shared text)." : $"\n{e.Dialogs.Count} text(s)")
                : $"Text {value.Dialog!.Label} in {value.Dialog.File}, line {value.Dialog.Line}";
            foreach (var d in value.Dialogs.Take(20)) SelectedDialogs.Add(new ExistingDialogVm(d, _render(d.EditorText).Boxes));
        }
    }

    public async Task LoadAsync(string? root, string? focusMap)
    {
        int gen = ++_generation;
        if (root == null) { _events = []; _texts = []; Apply(); return; }
        IsLoading = true;
        Summary = "Reading the scripts of all maps …";
        var result = await Task.Run(() => DialogCatalog.Load(root));
        if (gen != _generation) return;
        _events = result.Events.Select(e => new ExistingRow
        {
            Event = e, Map = e.Map, Icon = e.Kind switch { ExistingEventKind.Person => "🧍", ExistingEventKind.Sign => "🪧", ExistingEventKind.Trigger => "⬚", _ => "🗺" },
            Title = $"{e.Map} · {e.KindText} {e.Who}",
            Subtitle = e.Dialogs.Count > 0 ? Short(e.Dialogs[0].Preview) : e.Script,
            Search = string.Join(" ", e.Dialogs.Select(d => d.EditorText).Append(e.Script).Append(e.Who).Append(e.Map)),
        }).ToList();
        _texts = result.Dialogs.Select(d => new ExistingRow
        {
            Dialog = d, Map = d.Map, Icon = "💬",
            Title = $"{d.Map} · {d.Label}",
            Subtitle = Short(d.Preview),
            Search = d.EditorText + " " + d.Label + " " + d.Map,
        }).ToList();
        Maps.Clear();
        Maps.Add(AllMaps);
        foreach (var m in result.Maps) Maps.Add(m);
        if (!result.Maps.Contains("(shared)")) Maps.Add("(shared)");
        IsLoading = false;
        _map = focusMap != null && result.Maps.Contains(focusMap) ? focusMap : AllMaps;
        OnPropertyChanged(nameof(Map));
        Apply();
    }

    private static string Short(string s) => s.Length > 90 ? s[..90] + " …" : s;

    private void Apply()
    {
        var source = Mode == Modes[1] ? _texts : _events;
        var f = Search.Trim();
        Rows.Clear();
        int shown = 0;
        foreach (var r in source)
        {
            if (Map != AllMaps && r.Map != Map) continue;
            if (f.Length > 0 && !r.Search.Contains(f, StringComparison.OrdinalIgnoreCase)) continue;
            if (shown++ < 1500) Rows.Add(r);
        }
        Summary = IsLoading ? Summary
            : $"{shown} of {source.Count} {(Mode == Modes[1] ? "texts" : "people, signs and triggers")}" + (shown > 1500 ? " (the first 1500 are listed – narrow the search)" : "");
        Selected = Rows.FirstOrDefault();
    }
}
