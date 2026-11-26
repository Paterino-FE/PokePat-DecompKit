using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using TilesetEditor.App.Infrastructure;
using TilesetEditor.App.Modules.Tilesets;
using TilesetEditor.App.Shell;
using TilesetEditor.Core.Events;
using TilesetEditor.Core.Project;
using TilesetEditor.Core.Text;
using TilesetEditor.Export;
using TilesetEditor.Toolkit;
using EventTrigger = TilesetEditor.Core.Events.EventTrigger;

namespace TilesetEditor.App.Modules.Events;

/// <summary>
/// "Dialogs & Events": build event scripts from blocks, preview them like in the game and write them as
/// toolkit-managed Poryscript. Hand-written map scripts are only read (for name collisions), never changed.
/// </summary>
public sealed class EventsViewModel : ObservableObject, IToolkitModule
{
    private readonly ProjectContext _ctx;
    private ToolkitEventDocument _doc = new();
    private string _savedJson = new ToolkitEventDocument().Serialize();
    private GbaFont _font = GbaFont.Fallback();
    private Charmap? _charmap;
    private DialogAssets _assets = new();
    private ProjectSymbols _symbols = new() { Flags = [], Items = [] };
    private HashSet<string> _existingLabels = [];
    private readonly List<ScriptAssignment> _pendingAssignments = [];
    private bool _handEdited;

    public EventsViewModel(ProjectContext ctx)
    {
        _ctx = ctx;
        _ctx.ProjectChanged += (_, _) =>
        {
            // Re-read after outside changes only if nothing is pending here.
            if (!HasUnsavedChanges || _ctx.LastChangeKind == ProjectChangeKind.Opened) _ = LoadAsync();
        };
        NewEventCommand = new RelayCommand(NewEvent, () => SelectedMap != null);
        DeleteEventCommand = new RelayCommand(DeleteEvent, () => SelectedEvent != null);
        AddStepCommand = new RelayCommand(p => AddStep(p as string ?? ""), _ => SelectedEvent != null);
        RemoveStepCommand = new RelayCommand(RemoveStep, () => SelectedNode is StepNode);
        MoveUpCommand = new RelayCommand(() => Move(-1), () => SelectedNode is StepNode);
        MoveDownCommand = new RelayCommand(() => Move(1), () => SelectedNode is StepNode);
        CheckCommand = new AsyncRelayCommand(() => CheckAsync(showOk: true), () => _ctx.IsOpen && !IsBusy);
        SaveCommand = new AsyncRelayCommand(SaveAsync, () => _ctx.IsOpen && !IsBusy && _doc.Events.Count > 0);
        PreviewCommand = new RelayCommand(OpenPreview, () => SelectedEvent != null);
        AssignCommand = new RelayCommand(Assign, () => SelectedEvent != null && SelectedSlot != null);
        NewPersonCommand = new RelayCommand(NewPerson, () => SelectedMap != null && _ctx.IsOpen);
        ShowMapScriptsCommand = new RelayCommand(() =>
        {
            if (_ctx.Root != null && SelectedMap != null)
                new MapScriptsWindow(_ctx.Root, SelectedMap, _ctx.Project) { Owner = Application.Current.MainWindow }.Show();
        }, () => SelectedMap != null && _ctx.IsOpen);
        Existing = new ExistingDialogsViewModel(RenderBoxes, CreateFromExisting, (map, label) =>
        {
            if (_ctx.Root != null) new MapScriptsWindow(_ctx.Root, map, _ctx.Project, label) { Owner = Application.Current.MainWindow }.Show();
        });
        View = new EventsView { DataContext = this };
        _ = LoadAsync();
    }

    /// <summary>The "Existing in the game" tab: every person, sign, trigger and text the project already has.</summary>
    public ExistingDialogsViewModel Existing { get; }

    private int _selectedTab;
    /// <summary>0 = my events (builder), 1 = existing in the game.</summary>
    public int SelectedTab { get => _selectedTab; set => Set(ref _selectedTab, value); }

    /// <summary>A new event in <paramref name="map"/> with the texts of an existing one as message blocks (a starting point).</summary>
    private void CreateFromExisting(string map, string title, IReadOnlyList<string> texts)
    {
        if (!_allMaps.Contains(map)) return;
        if (!Maps.Contains(map)) MapFilter = "";
        SelectedMap = map;
        var e = new ToolkitEvent
        {
            Map = map,
            Title = "Copy of " + title,
            Trigger = EventTrigger.Person,
            Steps = texts.Take(10).Select(t => (EventStep)new MessageStep { Text = t }).ToList(),
        };
        if (e.Steps.Count == 0) e.Steps.Add(new MessageStep { Text = "Hello!" });
        e.Label = PoryscriptGenerator.SuggestLabel(map, "Event", _doc.Events.Select(x => x.Label).Concat(_existingLabels));
        _doc.Events.Add(e);
        Events.Add(e);
        SelectedEvent = e;
        DocumentChanged();
        SelectedTab = 0;
        Status = $"New event in {map} with {e.Steps.Count} text(s) of \"{title}\". Change it, then assign it to a person or sign.";
    }

    // ---- IToolkitModule
    public string Title => "Dialogs & Events";
    public string Description => "Event builder with dialog preview";
    public bool IsAvailable => true;
    public override string ToString() => Title;
    public FrameworkElement View { get; }
    public bool HasUnsavedChanges => _doc.Serialize() != _savedJson || _pendingAssignments.Count > 0;
    public string? UnsavedChangesDescription => HasUnsavedChanges ? "Dialogs & Events: event changes have not been written to the project yet." : null;

    // ---- Commands
    public ICommand NewEventCommand { get; }
    public ICommand DeleteEventCommand { get; }
    public ICommand AddStepCommand { get; }
    public ICommand RemoveStepCommand { get; }
    public ICommand MoveUpCommand { get; }
    public ICommand MoveDownCommand { get; }
    public ICommand CheckCommand { get; }
    public ICommand SaveCommand { get; }
    public ICommand PreviewCommand { get; }
    public ICommand AssignCommand { get; }
    public ICommand NewPersonCommand { get; }
    public ICommand ShowMapScriptsCommand { get; }

    // ---- State
    private bool _isBusy;
    public bool IsBusy { get => _isBusy; private set { if (Set(ref _isBusy, value)) CommandManager.InvalidateRequerySuggested(); } }
    private string _status = "";
    public string Status { get => _status; private set => Set(ref _status, value); }

    public ObservableCollection<string> Maps { get; } = [];
    private List<string> _allMaps = [];
    private string _mapFilter = "";
    /// <summary>Search text for the map list; the best match is selected automatically.</summary>
    public string MapFilter
    {
        get => _mapFilter;
        set
        {
            if (!Set(ref _mapFilter, value)) return;
            ApplyMapFilter();
        }
    }

    private void ApplyMapFilter()
    {
        var f = MapFilter.Trim();
        var keep = SelectedMap;
        Maps.Clear();
        foreach (var m in _allMaps.Where(m => f.Length == 0 || m.Contains(f, StringComparison.OrdinalIgnoreCase))) Maps.Add(m);
        var exact = Maps.FirstOrDefault(m => string.Equals(m, f, StringComparison.OrdinalIgnoreCase));
        SelectedMap = exact ?? (keep != null && Maps.Contains(keep) ? keep : Maps.FirstOrDefault());
    }
    private string? _selectedMap;
    public string? SelectedMap
    {
        get => _selectedMap;
        set
        {
            if (!Set(ref _selectedMap, value)) return;
            RefreshEventList();
            LoadSlots();
        }
    }

    public ObservableCollection<ToolkitEvent> Events { get; } = [];
    private ToolkitEvent? _selectedEvent;
    public ToolkitEvent? SelectedEvent
    {
        get => _selectedEvent;
        set
        {
            if (!Set(ref _selectedEvent, value)) return;
            OnPropertyChanged(nameof(EventTitle));
            OnPropertyChanged(nameof(EventLabel));
            OnPropertyChanged(nameof(EventTrigger));
            OnPropertyChanged(nameof(HasEvent));
            RebuildTree();
            FilterSlots();
        }
    }
    public bool HasEvent => SelectedEvent != null;

    public string EventTitle
    {
        get => SelectedEvent?.Title ?? "";
        set
        {
            if (SelectedEvent == null || SelectedEvent.Title == value) return;
            SelectedEvent.Title = value;
            OnPropertyChanged();
            RefreshEventListKeepSelection();
            DocumentChanged();
        }
    }

    public string EventLabel
    {
        get => SelectedEvent?.Label ?? "";
        set
        {
            if (SelectedEvent == null || SelectedEvent.Label == value) return;
            SelectedEvent.Label = value.Trim();
            OnPropertyChanged();
            DocumentChanged();
        }
    }

    public IReadOnlyList<EventTrigger> Triggers { get; } = Enum.GetValues<EventTrigger>();
    public EventTrigger EventTrigger
    {
        get => SelectedEvent?.Trigger ?? EventTrigger.Person;
        set
        {
            if (SelectedEvent == null || SelectedEvent.Trigger == value) return;
            SelectedEvent.Trigger = value;
            OnPropertyChanged();
            FilterSlots();
            DocumentChanged();
        }
    }

    // ---- Block library
    private static readonly IReadOnlyList<PaletteItem> BuiltIn =
    [
        new("message", "💬", "Message", "Show text in the text box."),
        new("yesno", "❓", "Yes/No question", "Question with YES/NO and one branch for each."),
        new("ifflag", "🔀", "If flag is set", "Branch on a flag."),
        new("setflag", "🚩", "Set flag", "Remembers something permanently in the save file."),
        new("clearflag", "🏳", "Clear flag", "Resets a flag."),
        new("giveitem", "🎁", "Give item", "Gives an item with the message \"Obtained the …!\"."),
    ];

    // Category names come from BlockCatalog (Core); each is looked up via a representative block id,
    // so the built-in blocks and the sort order follow whatever wording Core uses.
    private static string CategoryOf(string blockId, string fallback) => BlockCatalog.Find(blockId)?.Category ?? fallback;

    private static readonly Dictionary<string, string> BuiltInCategory = new()
    {
        ["message"] = CategoryOf("note", "Dialog"), ["yesno"] = CategoryOf("note", "Dialog"),
        ["ifflag"] = CategoryOf("set_var", "Flags & Variables"), ["setflag"] = CategoryOf("set_var", "Flags & Variables"),
        ["clearflag"] = CategoryOf("set_var", "Flags & Variables"), ["giveitem"] = CategoryOf("mart", "Items & Money"),
    };

    private static readonly string[] CategoryOrder =
        new[] { "note", "trainer_single", "give_mon", "mart", "set_var", "move", "warp", "sound", "special" }
            .Select(id => BlockCatalog.Find(id)?.Category).OfType<string>().Distinct().ToArray();

    private string _paletteFilter = "";
    public string PaletteFilter { get => _paletteFilter; set { if (Set(ref _paletteFilter, value)) OnPropertyChanged(nameof(Palette)); } }

    /// <summary>All blocks grouped by category, filtered by the search text.</summary>
    public IReadOnlyList<PaletteGroup> Palette
    {
        get
        {
            var items = BuiltIn.Select(b => (Category: BuiltInCategory[b.Id], Item: b))
                .Concat(BlockCatalog.All.Select(d => (d.Category, Item: new PaletteItem(d.Id, d.Icon, d.Title, d.Help))));
            var f = PaletteFilter.Trim();
            if (f.Length > 0) items = items.Where(x => x.Item.Title.Contains(f, StringComparison.OrdinalIgnoreCase) || x.Item.Help.Contains(f, StringComparison.OrdinalIgnoreCase));
            return items.GroupBy(x => x.Category).OrderBy(g => Array.IndexOf(CategoryOrder, g.Key) is var i && i < 0 ? 99 : i)
                .Select(g => new PaletteGroup(g.Key, g.Select(x => x.Item).ToList())).ToList();
        }
    }

    public ObservableCollection<NodeVm> Tree { get; } = [];
    private NodeVm? _selectedNode;
    public NodeVm? SelectedNode
    {
        get => _selectedNode;
        set
        {
            if (!Set(ref _selectedNode, value)) return;
            Editor = CreateEditor(value);
        }
    }

    private StepEditor? _editor;
    public StepEditor? Editor { get => _editor; private set => Set(ref _editor, value); }

    private string _code = "";
    public string Code { get => _code; private set => Set(ref _code, value); }
    public ObservableCollection<string> Problems { get; } = [];

    // Assignment to map events
    private List<MapEventSlot> _allSlots = [];
    public ObservableCollection<MapEventSlot> Slots { get; } = [];
    private MapEventSlot? _selectedSlot;
    public MapEventSlot? SelectedSlot { get => _selectedSlot; set => Set(ref _selectedSlot, value); }
    public ObservableCollection<string> PendingAssignments { get; } = [];

    public string ManagedInfo => _handEdited
        ? $"Warning: {EventsPlanner.PoryFile} was edited by hand. Saving overwrites the file only after asking."
        : $"Managed files: {EventsPlanner.JsonFile}, {EventsPlanner.PoryFile}, {EventsPlanner.IncFile}. Map scripts are left untouched.";

    // =====================================================================

    private async Task LoadAsync()
    {
        var root = _ctx.Root;
        if (root == null) return;
        IsBusy = true;
        Status = "Loading project data …";
        try
        {
            var loaded = await Task.Run(() =>
            {
                var font = GbaFont.LoadFromProject(root);
                var cm = File.Exists(Path.Combine(root, "charmap.txt")) ? Charmap.Load(Path.Combine(root, "charmap.txt")) : null;
                var assets = DialogAssets.Load(root);
                var symbols = ProjectSymbols.Load(root);
                var doc = EventsPlanner.LoadDocument(root);
                var handEdited = !EventsPlanner.GeneratedFileIsUnmodified(root);
                var labels = EventsPlanner.ExistingMapLabels(root);
                var maps = new List<string>();
                var groups = Path.Combine(root, "data", "maps", "map_groups.json");
                if (File.Exists(groups))
                {
                    using var j = JsonDocument.Parse(File.ReadAllText(groups));
                    if (j.RootElement.TryGetProperty("group_order", out var order))
                        foreach (var g in order.EnumerateArray())
                            if (j.RootElement.TryGetProperty(g.GetString() ?? "", out var names))
                                maps.AddRange(names.EnumerateArray().Select(n => n.GetString() ?? "").Where(n => n.Length > 0));
                }
                return (font, cm, assets, symbols, doc, handEdited, labels, maps);
            });
            (_font, _charmap, _assets, _symbols, _doc, _handEdited, _existingLabels, var mapList) = loaded;
            _savedJson = _doc.Serialize();
            _pendingAssignments.Clear();
            PendingAssignments.Clear();
            var keep = SelectedMap;
            _allMaps = mapList.OrderBy(m => m, StringComparer.OrdinalIgnoreCase).ToList();
            _selectedMap = null;
            ApplyMapFilter();
            var start = keep ?? _doc.Events.FirstOrDefault()?.Map;
            if (start != null && Maps.Contains(start)) SelectedMap = start;
            OnPropertyChanged(nameof(ManagedInfo));
            _ = Existing.LoadAsync(root, SelectedMap);
            Status = $"{_doc.Events.Count} toolkit event(s) in project · {_symbols.Flags.Count} flags ({_symbols.Flags.Count(f => f.IsUnused)} free) · {_symbols.Items.Count} items · " +
                     (PoryscriptRunner.IsAvailable(root) ? "Poryscript found" : "tools/poryscript missing: check/save not possible");
            DocumentChanged(markDirty: false);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void RefreshEventList()
    {
        Events.Clear();
        foreach (var e in _doc.Events.Where(e => e.Map == SelectedMap)) Events.Add(e);
        SelectedEvent = Events.FirstOrDefault();
    }

    private void RefreshEventListKeepSelection()
    {
        var keep = SelectedEvent;
        // ToolkitEvent has no change notification; re-adding updates the list text.
        Events.Clear();
        foreach (var e in _doc.Events.Where(e => e.Map == SelectedMap)) Events.Add(e);
        _selectedEvent = keep;
        OnPropertyChanged(nameof(SelectedEvent));
    }

    private void LoadSlots()
    {
        _allSlots = [];
        if (_ctx.Root != null && SelectedMap != null)
        {
            var f = Path.Combine(_ctx.Root, "data", "maps", SelectedMap, "map.json");
            try
            {
                if (File.Exists(f)) _allSlots = MapEventScripts.Read(File.ReadAllText(f));
            }
            catch (JsonException) { }
        }
        FilterSlots();
    }

    private void FilterSlots()
    {
        Slots.Clear();
        var kind = EventTrigger switch
        {
            EventTrigger.Person => MapEventKind.Object,
            EventTrigger.Sign => MapEventKind.Bg,
            _ => MapEventKind.Coord,
        };
        foreach (var s in _allSlots.Where(s => s.Kind == kind)) Slots.Add(s);
        SelectedSlot = Slots.FirstOrDefault();
    }

    // ---- Events

    private void NewEvent()
    {
        if (SelectedMap == null) return;
        var e = new ToolkitEvent
        {
            Map = SelectedMap,
            Title = "New event",
            Trigger = EventTrigger.Person,
            Steps = [new MessageStep { Text = "Hello! Nice to meet you." }],
        };
        e.Label = PoryscriptGenerator.SuggestLabel(SelectedMap, "Event", _doc.Events.Select(x => x.Label).Concat(_existingLabels));
        _doc.Events.Add(e);
        Events.Add(e);
        SelectedEvent = e;
        DocumentChanged();
    }

    private void DeleteEvent()
    {
        if (SelectedEvent == null) return;
        if (AppDialog.Show(Application.Current.MainWindow, $"Delete event \"{SelectedEvent.Title}\" ({SelectedEvent.Label})?\n\n" +
                "If it is assigned to a person/sign, the script name stays there and the build fails " +
                "until the assignment is changed in Porymap.", "Delete event", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        _doc.Events.Remove(SelectedEvent);
        RefreshEventList();
        DocumentChanged();
    }

    // ---- Tree

    private void RebuildTree(EventStep? select = null)
    {
        Tree.Clear();
        _selectedNode = null;
        Editor = null;
        if (SelectedEvent == null) return;
        NodeVm? toSelect = null;
        void Add(ObservableCollection<NodeVm> target, List<EventStep> list)
        {
            foreach (var step in list)
            {
                var node = new StepNode(step, list);
                if (ReferenceEquals(step, select)) toSelect = node;
                foreach (var (caption, branch) in step.Branches())
                {
                    var b = new BranchNode(caption, branch);
                    Add(b.Children, branch);
                    node.Children.Add(b);
                }
                target.Add(node);
            }
        }
        Add(Tree, SelectedEvent.Steps);
        if (toSelect != null) toSelect.IsSelected = true;
        OnPropertyChanged(nameof(SelectedNode));
    }

    /// <summary>Rebuilds the tree but keeps the current editor (so typing is not interrupted).</summary>
    private void RebuildTreeKeepEditor(EventStep select)
    {
        var editor = Editor;
        RebuildTree(select);
        _editor = editor;
        OnPropertyChanged(nameof(Editor));
    }

    private void AddStep(string kind)
    {
        if (SelectedEvent == null) return;
        EventStep step = kind switch
        {
            "message" => new MessageStep { Text = "" },
            "yesno" => new YesNoStep { Question = "Would you like that?" },
            "ifflag" => new IfFlagStep(),
            "setflag" => new SetFlagStep(),
            "clearflag" => new ClearFlagStep(),
            "giveitem" => new GiveItemStep(),
            _ when BlockCatalog.Find(kind) is { } def => CommandStep.Create(def),
            _ => new MessageStep(),
        };
        // Insert after the selected block, or into the selected branch.
        switch (SelectedNode)
        {
            case BranchNode b: b.List.Add(step); break;
            case StepNode s: s.Owner.Insert(s.Owner.IndexOf(s.Step) + 1, step); break;
            default: SelectedEvent.Steps.Add(step); break;
        }
        RebuildTree(step);
        DocumentChanged();
    }

    private void RemoveStep()
    {
        if (SelectedNode is not StepNode s) return;
        if (s.Step.Branches().Any(b => b.Steps.Count > 0) &&
            AppDialog.Show(Application.Current.MainWindow, "This block contains other blocks. Remove everything?", "Remove block",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        int i = s.Owner.IndexOf(s.Step);
        s.Owner.RemoveAt(i);
        RebuildTree(s.Owner.Count > 0 ? s.Owner[Math.Min(i, s.Owner.Count - 1)] : null);
        DocumentChanged();
    }

    private void Move(int delta)
    {
        if (SelectedNode is not StepNode s) return;
        int i = s.Owner.IndexOf(s.Step), j = i + delta;
        if (j < 0 || j >= s.Owner.Count) return;
        (s.Owner[i], s.Owner[j]) = (s.Owner[j], s.Owner[i]);
        RebuildTree(s.Step);
        DocumentChanged();
    }

    private StepEditor? CreateEditor(NodeVm? node)
    {
        void Changed()
        {
            node?.RefreshHeader();
            DocumentChanged();
        }
        return node switch
        {
            StepNode { Step: MessageStep m } => new TextEditorVm("Message text",
                "Just type. Blank line = new text box, line break = new line. Long sentences wrap automatically. " +
                "Placeholders such as {PLAYER} for the player's name are allowed.",
                () => m.Text, v => m.Text = v, RenderBoxes, Changed),
            StepNode { Step: YesNoStep y } => new TextEditorVm("Question (followed by YES/NO)",
                "What happens on YES or NO is set in the two branches below.",
                () => y.Question, v => y.Question = v, RenderBoxes, Changed),
            StepNode { Step: IfFlagStep f } => new FlagEditorVm("Condition: is this flag set?", () => f.Flag, v => f.Flag = v, _symbols.Flags, SuggestFreeFlag, Changed),
            StepNode { Step: SetFlagStep f } => new FlagEditorVm("Set this flag", () => f.Flag, v => f.Flag = v, _symbols.Flags, SuggestFreeFlag, Changed),
            StepNode { Step: ClearFlagStep f } => new FlagEditorVm("Clear this flag", () => f.Flag, v => f.Flag = v, _symbols.Flags, SuggestFreeFlag, Changed),
            StepNode { Step: GiveItemStep g } => new ItemEditorVm(g, _symbols.Items, Changed),
            StepNode { Step: CommandStep c } => new CommandEditorVm(c, _symbols, Targets(), RenderBoxes, () =>
            {
                // Menus: adding/removing an answer adds/removes a branch in the tree.
                int before = node.Children.Count;
                Changed();
                if (c.Def is { IsSwitch: true } && c.CaseBranches().Count != before) RebuildTreeKeepEditor(c);
            }, node is StepNode sn ? () => ShowMovementsOnMap(sn, c) : null),
            BranchNode b => new BranchEditorVm("Branch selected: new blocks are inserted here. An empty branch means \"do nothing\"."),
            _ => null,
        };
    }

    /// <summary>
    /// "Show on map…" of a movement block: draws the event's map with the paths of this block. Movement blocks before it
    /// in the same list run first (so people start where the earlier blocks left them).
    /// </summary>
    private void ShowMovementsOnMap(StepNode node, CommandStep step)
    {
        var ev = SelectedEvent;
        var project = _ctx.Project;
        var map = ev is { Map.Length: > 0 } ? ev.Map : SelectedMap;
        if (project == null || ev == null || map == null) return;
        int index = node.Owner.IndexOf(step);
        var blocks = node.Owner.Take(index < 0 ? 0 : index + 1).OfType<CommandStep>().ToList();
        if (index < 0) blocks.Add(step);
        var phases = TilesetEditor.Core.Maps.ScriptMovementPhases.FromBlocks(blocks);
        var notes = new List<string>();
        if (phases.Count > 1) notes.Add($"The {phases.Count - 1} movement block(s) before this one in the event run first (phases 1–{phases.Count - 1}).");
        // A person/trigger chosen in this session but not saved yet is not in map.json.
        var pending = _pendingAssignments.LastOrDefault(a => a.Map == map && a.Label == ev.Label);
        int? person = pending?.Kind == MapEventKind.Object ? pending.Index : null;
        int? trigger = pending?.Kind == MapEventKind.Coord ? pending.Index : null;
        try
        {
            new MovementPreviewWindow(project, map, phases, $"{ev.Title} ({ev.Label}) – {step.Title}", ev.Label, notes, person, trigger)
            {
                Owner = Application.Current.MainWindow,
            }.Show();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or FormatException)
        {
            AppDialog.Show(Application.Current.MainWindow, $"The map could not be shown:\n{ex.Message}", "Show on map", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    /// <summary>Who can be moved/turned: the player and the persons of the map (local ids 1…n).</summary>
    private IReadOnlyList<string> Targets() =>
        new[] { "OBJ_EVENT_ID_PLAYER" }.Concat(_allSlots.Where(s => s.Kind == MapEventKind.Object).Select(s => (s.Index + 1).ToString())).ToList();

    private string? SuggestFreeFlag()
    {
        var used = new HashSet<string>(_doc.Events.SelectMany(e => Flatten(e.Steps)).Select(s => s switch
        {
            IfFlagStep f => f.Flag,
            SetFlagStep f => f.Flag,
            ClearFlagStep f => f.Flag,
            _ => "",
        }));
        return _symbols.Flags.FirstOrDefault(f => f.IsUnused && !used.Contains(f.Name))?.Name;
    }

    private static IEnumerable<EventStep> Flatten(IEnumerable<EventStep> steps) =>
        steps.SelectMany(s => new[] { s }.Concat(s.Branches().SelectMany(b => Flatten(b.Steps))));

    /// <summary>Static preview of every text box state (two visible lines; scrolling shows the next pair).</summary>
    private (IReadOnlyList<BitmapSource>, IReadOnlyList<TextProblem>) RenderBoxes(string text)
    {
        var formatted = TextLayout.Format(PreviewText(text), _font, _charmap);
        var result = new List<BitmapSource>();
        if (_charmap == null) return (result, formatted.Problems);
        var renderer = new DialogRenderer(_assets, _font, _charmap);
        foreach (var box in formatted.Boxes.Take(12))
        {
            for (int start = 0; start == 0 || start + 1 < box.Count; start++)
            {
                var img = renderer.Render(new DialogScene { Lines = box.Skip(start).Take(2).ToList(), ShowArrow = true });
                var crop = new TilesetEditor.Core.Imaging.RgbaImage(240, 48);
                crop.Blit(img, 0, 112, 0, 0, 240, 48);
                result.Add(Imaging.ToBitmap(crop));
                if (box.Count <= 2) break;
            }
        }
        return (result, TextLayout.Format(text, _font, _charmap).Problems);
    }

    /// <summary>Placeholders as they could look in game (for previews only).</summary>
    public string PreviewText(string text) => text.Replace("{PLAYER}", PlayerName);

    public string PlayerName { get; set; } = "PLAYER";

    // ---- Code and checks

    private void DocumentChanged(bool markDirty = true)
    {
        Code = _charmap == null && _doc.Events.Count == 0 ? "" : PoryscriptGenerator.Generate(_doc, _font, _charmap);
        Problems.Clear();
        foreach (var p in PoryscriptGenerator.Validate(_doc, _font, _charmap, _symbols, _existingLabels))
            Problems.Add($"{(p.IsError ? "Error" : "Warning")} · {p.Event}: {p.Message}");
        OnPropertyChanged(nameof(ProblemSummary));
        CommandManager.InvalidateRequerySuggested();
    }

    public string ProblemSummary => Problems.Count == 0 ? "No problems." : $"{Problems.Count(p => p.StartsWith("Error"))} error(s), {Problems.Count(p => p.StartsWith("Warning"))} warning(s)";

    /// <summary>Validates and compiles with the project's Poryscript. Returns the compiled .inc or null.</summary>
    private async Task<(string Pory, string Inc)?> CheckAsync(bool showOk)
    {
        var root = _ctx.Root;
        if (root == null) return null;
        DocumentChanged(markDirty: false);
        if (Problems.Any(p => p.StartsWith("Error")))
        {
            Status = "Please fix the errors in the list first.";
            return null;
        }
        if (!PoryscriptRunner.IsAvailable(root))
        {
            Status = "tools/poryscript is missing from the project, so the code cannot be checked.";
            return null;
        }
        IsBusy = true;
        Status = "Checking with the project's Poryscript …";
        try
        {
            var pory = Code;
            var result = await Task.Run(() => new PoryscriptRunner(_ctx.Tools).CompileAsync(root, pory, CancellationToken.None));
            if (!result.Success)
            {
                Status = "Poryscript reported an error (see list).";
                Problems.Insert(0, "Error · Poryscript: " + result.Output);
                OnPropertyChanged(nameof(ProblemSummary));
                return null;
            }
            Status = $"Checked with Poryscript {result.Version}: OK.";
            if (showOk) AppDialog.Show(Application.Current.MainWindow, "The events are valid and compile with the project's Poryscript.",
                "Check", MessageBoxButton.OK, MessageBoxImage.Information);
            return (pory, result.IncText!);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task SaveAsync()
    {
        var root = _ctx.Root;
        if (root == null) return;
        var compiled = await CheckAsync(showOk: false);
        if (compiled == null) return;
        bool overwrite = false;
        if (!EventsPlanner.GeneratedFileIsUnmodified(root))
        {
            if (AppDialog.Show(Application.Current.MainWindow,
                    $"{EventsPlanner.PoryFile} was edited by hand. Those changes will be lost when saving. Overwrite anyway?",
                    "File edited by hand", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
            overwrite = true;
        }
        ExportPlan plan;
        try
        {
            plan = EventsPlanner.Plan(root, _doc, compiled.Value.Pory, compiled.Value.Inc, _pendingAssignments, overwrite);
        }
        catch (Exception ex) when (ex is ExportValidationException or InvalidOperationException or IOException)
        {
            AppDialog.Show(Application.Current.MainWindow, ex.Message, "Cannot save", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (plan.Files.Count == 0) { Status = "Nothing to save: the project is already up to date."; return; }
        var dlg = new ExportPlanWindow(plan) { Owner = Application.Current.MainWindow };
        if (dlg.ShowDialog() != true) return;
        var result = new ExportApplier().Apply(plan);
        if (!result.Success)
        {
            AppDialog.Show(Application.Current.MainWindow, $"Saving failed: {result.Error}" + (result.RolledBack ? "\n\nAll changes were rolled back." : ""),
                "Save", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }
        _savedJson = _doc.Serialize();
        _pendingAssignments.Clear();
        PendingAssignments.Clear();
        _handEdited = false;
        OnPropertyChanged(nameof(ManagedInfo));
        Status = $"Saved ({result.Written.Count} files). Use \"Build ROM\" to get it into the game.";
        _ctx.Reload();
        LoadSlots();
    }

    // ---- Assignment

    private void Assign()
    {
        if (SelectedEvent == null || SelectedSlot == null || SelectedMap == null) return;
        var slot = SelectedSlot;
        var existing = slot.Script;
        if (existing.Length > 0 && existing != "0" && existing != SelectedEvent.Label && !_doc.Events.Any(e => e.Label == existing) &&
            AppDialog.Show(Application.Current.MainWindow,
                $"{slot.Display}\n\nalready has the script \"{existing}\". It stays in the project but will no longer be triggered by this event. Replace?",
                "Assign script", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        _pendingAssignments.RemoveAll(a => a.Map == SelectedMap && a.Kind == slot.Kind && a.Index == slot.Index);
        _pendingAssignments.Add(new ScriptAssignment(SelectedMap, slot.Kind, slot.Index, SelectedEvent.Label));
        PendingAssignments.Clear();
        foreach (var a in _pendingAssignments) PendingAssignments.Add($"{a.Map}: {a.Kind} #{a.Index + 1} → {a.Label}");
        Status = "Assignment queued: it is written to map.json when you save.";
        if (SelectedEvent.Trigger == EventTrigger.Trigger)
            AppDialog.Show(Application.Current.MainWindow, "Note: a trigger tile only fires when its variable has the configured value (in Porymap: Var/Var Value).",
                "Trigger tile", MessageBoxButton.OK, MessageBoxImage.Information);
        CommandManager.InvalidateRequerySuggested();
    }

    // ---- New person

    private void NewPerson()
    {
        var root = _ctx.Root;
        if (root == null || SelectedMap == null) return;
        var rel = $"data/maps/{SelectedMap}/map.json";
        var full = Path.Combine(root, "data", "maps", SelectedMap, "map.json");
        if (!File.Exists(full)) return;
        var json = File.ReadAllText(full);
        int w = 0, h = 0;
        using (var d = JsonDocument.Parse(json))
            if (d.RootElement.TryGetProperty("layout", out var l) && _ctx.Project?.Layouts.FirstOrDefault(x => x.Id == l.GetString()) is { } layout)
                (w, h) = (layout.Width, layout.Height);
        var kind = SelectedEvent?.Trigger ?? EventTrigger.Person;
        var dlg = new NewPersonWindow(SelectedMap, w, h, _symbols.ObjectGraphics, _symbols.MovementTypes, SelectedEvent?.Label, kind, _symbols.Vars)
        {
            Owner = Application.Current.MainWindow,
        };
        if (dlg.ShowDialog() != true) return;
        string updated;
        try
        {
            var script = dlg.AssignSelectedEvent && SelectedEvent != null ? SelectedEvent.Label : "0x0";
            updated = kind switch
            {
                EventTrigger.Sign => MapEventScripts.AddSign(json, new Dictionary<string, object>
                    { ["x"] = dlg.PosX, ["y"] = dlg.PosY, ["elevation"] = dlg.ElevationValue, ["script"] = script }),
                EventTrigger.Trigger => MapEventScripts.AddTrigger(json, new Dictionary<string, object>
                    { ["x"] = dlg.PosX, ["y"] = dlg.PosY, ["elevation"] = dlg.ElevationValue, ["var"] = dlg.Var, ["var_value"] = dlg.VarValueText, ["script"] = script }),
                _ => MapEventScripts.AddObjectEvent(json, new Dictionary<string, object>
                {
                    ["graphics_id"] = dlg.GraphicsId, ["x"] = dlg.PosX, ["y"] = dlg.PosY, ["elevation"] = dlg.ElevationValue,
                    ["movement_type"] = dlg.MovementType, ["script"] = script,
                }),
            };
        }
        catch (InvalidOperationException ex)
        {
            AppDialog.Show(Application.Current.MainWindow, ex.Message, "New on map", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        var original = File.ReadAllBytes(full);
        string what = kind switch { EventTrigger.Sign => "Sign", EventTrigger.Trigger => "Trigger tile", _ => "Person" };
        var plan = ExportPlan.ForFiles(root, $"New on {SelectedMap}: {what}", $"data/maps/{SelectedMap}",
            [new PlannedFile(rel, PlannedAction.Modify, new System.Text.UTF8Encoding(false).GetBytes(updated),
                $"Append {what.ToLowerInvariant()} at ({dlg.PosX},{dlg.PosY})" + (kind == EventTrigger.Person ? $": {dlg.GraphicsId}" : ""), Hashing.Sha256(original))]);
        plan.Notes.Add("Only the matching list (object_events, bg_events or coord_events) gets one new entry; all other lines stay unchanged.");
        if (dlg.AssignSelectedEvent && HasUnsavedChanges)
            plan.Notes.Add("The assigned event is not saved yet. Use \"Save to project…\" afterwards, or the build will fail.");
        if (new ExportPlanWindow(plan) { Owner = Application.Current.MainWindow }.ShowDialog() != true) return;
        var result = new ExportApplier().Apply(plan);
        if (!result.Success)
        {
            AppDialog.Show(Application.Current.MainWindow, "Error: " + result.Error, "New on map", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }
        LoadSlots();
        SelectedSlot = Slots.LastOrDefault();
        Status = kind == EventTrigger.Person
            ? $"Person created at ({dlg.PosX},{dlg.PosY}) on {SelectedMap} (local ID {_allSlots.Count(s => s.Kind == MapEventKind.Object)})."
            : $"{what} created at ({dlg.PosX},{dlg.PosY}) on {SelectedMap}.";
    }

    // ---- Preview

    private void OpenPreview()
    {
        if (SelectedEvent == null || _charmap == null) return;
        var window = new DialogPreviewWindow(SelectedEvent, _font, _charmap, _assets, _symbols, _ctx.Root!)
        {
            Owner = Application.Current.MainWindow,
        };
        window.Show();
    }
}
