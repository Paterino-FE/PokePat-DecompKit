using System.Collections.ObjectModel;
using System.IO;
using System.Text;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using TilesetEditor.App.Infrastructure;
using TilesetEditor.App.Shell;
using TilesetEditor.Core;
using TilesetEditor.Core.GameData;
using TilesetEditor.Core.Imaging;
using TilesetEditor.Core.Platinum;
using TilesetEditor.Core.Project;

namespace TilesetEditor.App.Platinum;

/// <summary>An event of the events JSON, edited key by key (bindings to "[key]"); numbers stay numbers.</summary>
public sealed class EventRowVm(JsonObject node, Action changed) : ObservableObject
{
    public JsonObject Node { get; } = node;

    public string this[string key]
    {
        get => Node[key] switch
        {
            null => "",
            JsonValue v when v.TryGetValue<System.Text.Json.JsonElement>(out var e) => e.ValueKind == System.Text.Json.JsonValueKind.String ? e.GetString() ?? "" : e.GetRawText(),
            JsonValue v when v.TryGetValue<string>(out var s) => s,
            JsonValue v when v.TryGetValue<int>(out var i) => i.ToString(),
            var n => n.ToJsonString(),
        };
        set
        {
            value = (value ?? "").Trim();
            if (value == this[key]) return;
            bool number = Node[key] is JsonValue old && old.TryGetValue<System.Text.Json.JsonElement>(out var oe) ? oe.ValueKind == System.Text.Json.JsonValueKind.Number
                : Node[key] is JsonValue ov && ov.TryGetValue<int>(out _);
            Node[key] = number && int.TryParse(value, out var n) ? JsonValue.Create(n) : JsonValue.Create(value);
            OnPropertyChanged("Item[]");
            changed();
        }
    }

    public int X => int.TryParse(this["x"], out var v) ? v : 0;
    public int Z => int.TryParse(this["z"], out var v) ? v : 0;
}

/// <summary>A message of the map's text bank.</summary>
public sealed class TextRowVm(JsonObject node, Action changed) : ObservableObject
{
    public JsonObject Node { get; } = node;
    public string Id => Node["id"]?.GetValue<string>() ?? "";
    /// <summary>The text on one line, for lists.</summary>
    public string Preview => PlatinumDialogText.ToNatural(PlatinumMapScripts.MessageText(Node)).Replace("\n\n", " ▸ ").Replace('\n', ' ');
    public void Refresh() { OnPropertyChanged(nameof(Text)); OnPropertyChanged(nameof(Preview)); }

    public string Text
    {
        get => JsonText.ToEscapes(PlatinumMapScripts.MessageText(Node));
        set
        {
            var real = JsonText.FromEscapes((value ?? "").Replace("\r\n", "\\n").Replace("\n", "\\n"));
            if (real == PlatinumMapScripts.MessageText(Node)) return;
            PlatinumMapScripts.SetMessageText(Node, real);
            OnPropertyChanged();
            changed();
        }
    }
}

/// <summary>An event in the list on the left: person, sign, hidden item, warp or trigger.</summary>
public sealed class EventItemVm(string list, JsonObject node, string icon, string title, string subtitle)
{
    public string List { get; } = list;
    public JsonObject Node { get; } = node;
    public string Icon { get; } = icon;
    public string Title { get; } = title;
    public string Subtitle { get; } = subtitle;
}

/// <summary>
/// Events & dialogs of a Platinum map, built like the event builder of the Emerald part: on the left the map and its events
/// (people, signs, hidden items, warps, trigger tiles), in the middle what the selected event does – as blocks for scripts
/// made with PokéPat, as readable steps for the game's own scripts – and on the right the editor of the selected block or
/// text with the in-game preview. Every event and every text of the map can be shown and changed; the script file stays
/// editable as code.
/// </summary>
public sealed class PlatinumEventsViewModel : ObservableObject, IToolkitModule
{
    private readonly DeferredLoad _reload;
    private static readonly (string Key, string Title)[] Kinds =
    [
        ("", "All events"), ("object_events", "People"), ("bg_events", "Signs & hidden items"), ("warp_events", "Warps"), ("coord_events", "Trigger tiles"),
        (MapScriptsList, "Map scripts & cutscenes"),
    ];

    private readonly ProjectContext _ctx;

    /// <summary>The list name of the scripts the map starts by itself (init.s) and of scripts no event starts; not part of events.json.</summary>
    private const string MapScriptsList = "map_scripts";

    // left
    private readonly TextBox _mapFilter = new() { Margin = new Thickness(0, 2, 0, 2), ToolTip = "Search a map (part of the name)" };
    private readonly ComboBox _mapBox = new() { Margin = new Thickness(0, 0, 0, 6), IsTextSearchEnabled = true };
    private readonly ComboBox _kindFilter = new() { Margin = new Thickness(0, 2, 0, 4) };
    private readonly ListBox _eventList = new() { HorizontalContentAlignment = HorizontalAlignment.Stretch };
    // middle and right
    private readonly ContentControl _center = new();
    private readonly StackPanel _right = new();
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, Foreground = Brushes.Gray, FontSize = 11 };
    private readonly TabControl _tabs = new() { Margin = new Thickness(4, 4, 4, 0) };
    // texts tab
    private readonly TextBox _textFilter = new() { Margin = new Thickness(0, 0, 0, 4), ToolTip = "Search the texts of this map" };
    private readonly ListBox _textList = new() { HorizontalContentAlignment = HorizontalAlignment.Stretch };
    private readonly StackPanel _textEditor = new();
    private readonly ComboBox _mugshotPreview = new() { Width = 220, HorizontalAlignment = HorizontalAlignment.Left, ToolTip = "Show a mugshot in the preview (mod \"Dialog mugshots\")" };
    // script tab
    private readonly TextBox _script = new()
    {
        AcceptsReturn = true, AcceptsTab = true, FontFamily = new FontFamily("Consolas"), FontSize = 12, IsReadOnly = true, // until a map is open
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, TextWrapping = TextWrapping.NoWrap,
    };
    private readonly ListBox _entries = new() { BorderThickness = new Thickness(0), FontFamily = new FontFamily("Consolas"), FontSize = 11 };
    // map
    private readonly Canvas _canvas = new() { HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top };
    // The map under the event's fields: zoomable (−/+, fit, Ctrl+wheel) and scrollable; the tile under the mouse is shown.
    private readonly ScaleTransform _mapScale = new(1, 1);
    private readonly ScrollViewer _mapScroll = new() { HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, MaxHeight = 560, HorizontalAlignment = HorizontalAlignment.Left };
    private readonly TextBlock _mapZoomText = new() { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 6, 0), MinWidth = 44 };
    private readonly TextBlock _mapCoords = new() { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0), Foreground = Brushes.Gray };
    private StackPanel? _mapPanel;
    private FrameworkElement? _selectedMarker;
    private bool _mapZoomChosen;
    private readonly CheckBox _placeOnClick = new() { Content = "A click on the map moves the selected event there", Margin = new Thickness(0, 4, 0, 4) };
    private (int X, int Y, int W, int H) _area;
    private const int Tile = 8;
    private ImageSource? _mapImage;

    private PlatinumMaps? _mapData;
    private List<MapHeaderItemVm> _allMaps = [];
    private PlatinumMapScripts? _map;
    private PlatinumJsonFile? _events, _texts;
    private string? _scriptOriginal;
    private readonly ObservableCollection<TextRowVm> _textRows = [];
    private PlatinumFont? _font;
    private GameText? _text;
    private ScriptBuilderLists? _lists;
    private PlatinumScriptBlocks.Dialect? _dialect;
    private Dictionary<string, string?> _spritePngs = new(StringComparer.Ordinal);
    private bool _dirty, _loading;

    // the selected event
    private EventItemVm? _selected;
    private readonly ObservableCollection<ScriptBlock> _blocks = [];
    private readonly ListBox _blockList = new() { HorizontalContentAlignment = HorizontalAlignment.Stretch };
    private readonly TextBlock _blockProblem = new() { TextWrapping = TextWrapping.Wrap, Foreground = Brushes.Firebrick, Margin = new Thickness(0, 4, 0, 0) };
    private readonly TextBox _code = new() { IsReadOnly = true, FontFamily = new FontFamily("Consolas"), FontSize = 11, TextWrapping = TextWrapping.NoWrap, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, MaxHeight = 220 };
    private string? _managedLabel, _managedTextPrefix;
    private ScriptStart _managedStart;

    private readonly Action<string> _navigate;

    public PlatinumEventsViewModel(ProjectContext ctx, Action<string>? navigate = null)
    {
        _ctx = ctx;
        _navigate = navigate ?? (_ => { });
        _reload = new DeferredLoad(() => { if (!_dirty) LoadMaps(); _search = null; });
        _ctx.PlatinumChanged += (_, _) => _reload.Request();
        View = BuildView();
        _reload.Attach(View);
    }

    public string Title => "Events & Dialogs";
    public string Description => "People, signs, warps, texts, scripts";
    public bool IsAvailable => true;
    public override string ToString() => Title;
    public FrameworkElement View { get; }
    public bool HasUnsavedChanges => _dirty;
    public string? UnsavedChangesDescription => _dirty ? $"Events & Dialogs: {_map?.Header.FriendlyName} not saved yet." : null;

    private void Touch()
    {
        if (_loading) return;
        _dirty = true;
        _history.Record(State(), mergeMs: 900);
        UpdateStatus();
    }

    // ---- undo / redo of changes not saved yet (events, texts and the script file together)

    private readonly UndoHistory<(string Events, string Texts, string Script)> _history = new((a, b) => a == b);

    private (string, string, string) State() => (_events?.Snapshot() ?? "", _texts?.Snapshot() ?? "", _script.Text);

    private void UndoRedo(bool redo)
    {
        if (_map == null) return;
        if (!(redo ? _history.CanRedo : _history.CanUndo)) { _status.Text = redo ? "Nothing to redo." : "Nothing to undo (saved changes: Backups module)."; return; }
        var state = redo ? _history.Redo() : _history.Undo();
        // Remember which event and text were selected (by position: the objects are replaced).
        (string List, int Index)? sel = _selected != null && _events?.Root[_selected.List] is System.Text.Json.Nodes.JsonArray a ? (_selected.List, a.IndexOf(_selected.Node)) : null;
        var textId = (_textList.SelectedItem as TextRowVm)?.Id;
        _loading = true;
        try
        {
            if (_events != null && state.Events.Length > 0) _events = _events.Restored(state.Events);
            if (_texts != null && state.Texts.Length > 0) _texts = _texts.Restored(state.Texts);
            _script.Text = state.Script;
            _entries.ItemsSource = _scriptOriginal != null ? PlatinumMapScripts.Entries(state.Script) : null;
            _textRows.Clear();
            if (_texts != null) foreach (var m in PlatinumMapScripts.Messages(_texts).OfType<JsonObject>()) _textRows.Add(new TextRowVm(m, Touch));
            FilterTexts();
            _textList.SelectedItem = _textRows.FirstOrDefault(r => r.Id == textId);
        }
        finally { _loading = false; }
        _dirty = _events?.IsChanged == true || _texts?.IsChanged == true || (_scriptOriginal != null && _script.Text != _scriptOriginal);
        _selected = null;
        var node = sel is { } s0 && _events?.Root[s0.List] is System.Text.Json.Nodes.JsonArray arr && s0.Index >= 0 && s0.Index < arr.Count ? arr[s0.Index] as JsonObject : null;
        FillEvents(node);
        if (node == null) { _center.Content = Ui.Hint("Choose an event on the left."); ShowRightHint(); }
        if (_tabs.SelectedIndex == 1) ShowTextEditor();
        UpdateStatus();
        _status.Text = (redo ? "Redone. " : "Undone. ") + _status.Text;
    }

    private static Brush Res(string key) => (Brush)Application.Current.FindResource(key);

    // ------------------------------------------------------------------ layout

    private FrameworkElement BuildView()
    {
        // ---- top bar
        var top = new DockPanel { Margin = new Thickness(8, 6, 8, 0) };
        var buttons = Ui.Row(
            Ui.Button("↶", () => UndoRedo(false), "Undo (Ctrl+Z outside text fields) – changes that are not saved yet"),
            Ui.Button("↷", () => UndoRedo(true), "Redo (Ctrl+Y)"),
            Ui.Button("💾 Save", () => Save(), "Writes the changed events, texts and script file (shown as a file list first)."),
            Ui.Button("Discard changes", Discard));
        DockPanel.SetDock(buttons, Dock.Right);
        top.Children.Add(buttons);
        top.Children.Add(_status);

        // ---- events tab: map & events | what it does | editor
        var grid = new Grid { Margin = new Thickness(8) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(270) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 320 });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(400) });

        var left = new DockPanel { Margin = new Thickness(0, 0, 8, 0) };
        var leftTop = new StackPanel();
        leftTop.Children.Add(new TextBlock { Text = "Map", FontWeight = FontWeights.SemiBold });
        System.Windows.Automation.AutomationProperties.SetName(_mapFilter, "Search map");
        leftTop.Children.Add(_mapFilter);
        VirtualizingPanel.SetIsVirtualizing(_mapBox, true);
        _mapBox.ItemsPanel = new ItemsPanelTemplate(new FrameworkElementFactory(typeof(VirtualizingStackPanel)));
        leftTop.Children.Add(_mapBox);
        leftTop.Children.Add(new TextBlock { Text = "Events on this map", FontWeight = FontWeights.SemiBold });
        foreach (var k in Kinds) _kindFilter.Items.Add(k.Title);
        _kindFilter.SelectedIndex = 0;
        leftTop.Children.Add(_kindFilter);
        DockPanel.SetDock(leftTop, Dock.Top);
        left.Children.Add(leftTop);
        var add = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
        add.Children.Add(Small("+ Person", NewPerson, "A new person who talks – its script is made from blocks."));
        add.Children.Add(Small("+ Trainer", NewTrainerEvent, "A trainer on the map: battles the player who walks into its sight. Team and texts come from the Trainers module."));
        add.Children.Add(Small("+ Sign", NewSign, "A new sign the player can read."));
        add.Children.Add(Small("+ Trigger tile", NewTrigger, "A tile that starts an event when the player steps on it."));
        add.Children.Add(Small("Duplicate", DuplicateEvent, "Copies the selected event one tile to the right (it runs the same script)."));
        add.Children.Add(Small("Delete", RemoveEvent, "Removes the selected event from the map (its script stays in the file)."));
        DockPanel.SetDock(add, Dock.Bottom);
        left.Children.Add(add);
        _eventList.ItemTemplate = EventTemplate();
        VirtualizingPanel.SetIsVirtualizing(_eventList, true);
        ScrollViewer.SetHorizontalScrollBarVisibility(_eventList, ScrollBarVisibility.Disabled);
        left.Children.Add(_eventList);
        grid.Children.Add(left);

        Grid.SetColumn(_center, 1);
        grid.Children.Add(_center);
        _center.Content = Ui.Hint("Choose a map on the left.");

        var rightBorder = new Border
        {
            Margin = new Thickness(8, 0, 0, 0), BorderBrush = Res("BorderBrush"), BorderThickness = new Thickness(1), Background = Res("SurfaceBrush"), Padding = new Thickness(10),
            Child = new ScrollViewer { Content = _right, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled },
        };
        Grid.SetColumn(rightBorder, 2);
        grid.Children.Add(rightBorder);
        _tabs.Items.Add(new TabItem { Header = "Events", Content = grid });

        // ---- texts tab
        var texts = new Grid { Margin = new Thickness(8) };
        texts.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(2, GridUnitType.Star) });
        texts.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(3, GridUnitType.Star) });
        var textLeft = new DockPanel { Margin = new Thickness(0, 0, 10, 0) };
        var textBar = Ui.Row(Ui.Button("+ New text…", NewText, "A new text in the map's text bank – write it on the right."));
        DockPanel.SetDock(textBar, Dock.Top);
        textLeft.Children.Add(textBar);
        DockPanel.SetDock(_textFilter, Dock.Top);
        textLeft.Children.Add(_textFilter);
        _textList.ItemTemplate = TextTemplate();
        VirtualizingPanel.SetIsVirtualizing(_textList, true);
        ScrollViewer.SetHorizontalScrollBarVisibility(_textList, ScrollBarVisibility.Disabled);
        textLeft.Children.Add(_textList);
        texts.Children.Add(textLeft);
        var textRight = new ScrollViewer { Content = _textEditor, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        Grid.SetColumn(textRight, 1);
        texts.Children.Add(textRight);
        _tabs.Items.Add(new TabItem { Header = "All texts", Content = texts });

        // ---- script file tab
        var scriptTab = new Grid { Margin = new Thickness(8) };
        scriptTab.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(260) });
        scriptTab.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        scriptTab.Children.Add(_entries);
        Grid.SetColumn(_script, 1);
        scriptTab.Children.Add(_script);
        _tabs.Items.Add(new TabItem { Header = "Script file (code)", Content = scriptTab });
        _tabs.Items.Add(new TabItem { Header = "🔍 Search all texts", Content = BuildSearch() });

        // ---- events
        _mapFilter.TextChanged += (_, _) => FilterMaps();
        _mapBox.SelectionChanged += (_, _) => { if (_mapBox.SelectedItem is MapHeaderItemVm m && m.Header != _map?.Header) OpenMap(m); };
        _kindFilter.SelectionChanged += (_, _) => FillEvents();
        _eventList.SelectionChanged += (_, _) => { if (_eventList.SelectedItem is EventItemVm e) ShowEvent(e); };
        _textFilter.TextChanged += (_, _) => FilterTexts();
        _textList.SelectionChanged += (_, _) => { if (_tabs.SelectedIndex == 1) ShowTextEditor(); };
        // One mugshot chooser serves both editors: rebuild only the one that is visible.
        _mugshotPreview.SelectionChanged += (_, _) => { _mugshotAddUpdate?.Invoke(); if (_tabs.SelectedIndex == 1) ShowTextEditor(); else _rightRefresh?.Invoke(); };
        _tabs.SelectionChanged += (_, e) =>
        {
            if (!ReferenceEquals(e.OriginalSource, _tabs)) return;
            if (_tabs.SelectedIndex == 1) ShowTextEditor();
            else if (_tabs.SelectedIndex == 0) _rightRefresh?.Invoke();
        };
        _script.TextChanged += (_, _) => { if (!_loading && _script.Text != _scriptOriginal) Touch(); };
        _entries.SelectionChanged += (_, _) =>
        {
            if (_entries.SelectedItem is PlatinumScriptEntry e && e.Line >= 0) GoToLine(e.Line);
        };
        _blockList.ItemsSource = _blocks;
        _blockList.ItemTemplate = PlatinumBlockEditor.BlockTemplate();
        ScrollViewer.SetHorizontalScrollBarVisibility(_blockList, ScrollBarVisibility.Disabled);
        _blockList.SelectionChanged += (_, _) => ShowBlockEditor();

        var root = new DockPanel();
        // Ctrl+Z / Ctrl+Y for the whole module – but text fields keep their own undo while typing.
        root.PreviewKeyDown += (_, e) =>
        {
            if ((System.Windows.Input.Keyboard.Modifiers & ModifierKeys.Control) == 0 || e.OriginalSource is System.Windows.Controls.Primitives.TextBoxBase) return;
            if (e.Key == Key.Z) { UndoRedo((System.Windows.Input.Keyboard.Modifiers & ModifierKeys.Shift) != 0); e.Handled = true; }
            else if (e.Key == Key.Y) { UndoRedo(true); e.Handled = true; }
        };
        DockPanel.SetDock(top, Dock.Top);
        root.Children.Add(top);
        root.Children.Add(_tabs);
        return root;
    }

    private static Button Small(string text, Action click, string tip)
    {
        var b = new Button { Content = text, Padding = new Thickness(6, 2, 6, 2), Margin = new Thickness(0, 0, 4, 4), ToolTip = tip };
        b.Click += (_, _) => click();
        return b;
    }

    private static DataTemplate EventTemplate()
    {
        var dock = new FrameworkElementFactory(typeof(DockPanel));
        dock.SetValue(FrameworkElement.MarginProperty, new Thickness(0, 2, 0, 2));
        var icon = new FrameworkElementFactory(typeof(TextBlock));
        icon.SetBinding(TextBlock.TextProperty, new Binding(nameof(EventItemVm.Icon)));
        icon.SetValue(TextBlock.FontSizeProperty, 16.0);
        icon.SetValue(FrameworkElement.WidthProperty, 26.0);
        icon.SetValue(DockPanel.DockProperty, Dock.Left);
        dock.AppendChild(icon);
        var stack = new FrameworkElementFactory(typeof(StackPanel));
        var t = new FrameworkElementFactory(typeof(TextBlock));
        t.SetBinding(TextBlock.TextProperty, new Binding(nameof(EventItemVm.Title)));
        t.SetValue(TextBlock.FontWeightProperty, FontWeights.SemiBold);
        t.SetValue(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis);
        stack.AppendChild(t);
        var s = new FrameworkElementFactory(typeof(TextBlock));
        s.SetBinding(TextBlock.TextProperty, new Binding(nameof(EventItemVm.Subtitle)));
        s.SetValue(TextBlock.FontSizeProperty, 11.0);
        s.SetValue(TextBlock.ForegroundProperty, Brushes.Gray);
        s.SetValue(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis);
        stack.AppendChild(s);
        dock.AppendChild(stack);
        return new DataTemplate { VisualTree = dock };
    }

    private static DataTemplate TextTemplate()
    {
        var stack = new FrameworkElementFactory(typeof(StackPanel));
        stack.SetValue(FrameworkElement.MarginProperty, new Thickness(0, 2, 0, 2));
        var t = new FrameworkElementFactory(typeof(TextBlock));
        t.SetBinding(TextBlock.TextProperty, new Binding(nameof(TextRowVm.Preview)));
        t.SetValue(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis);
        stack.AppendChild(t);
        var s = new FrameworkElementFactory(typeof(TextBlock));
        s.SetBinding(TextBlock.TextProperty, new Binding(nameof(TextRowVm.Id)));
        s.SetValue(TextBlock.FontSizeProperty, 10.0);
        s.SetValue(TextBlock.FontFamilyProperty, new FontFamily("Consolas"));
        s.SetValue(TextBlock.ForegroundProperty, Brushes.Gray);
        stack.AppendChild(s);
        return new DataTemplate { VisualTree = stack };
    }

    private void GoToLine(int line)
    {
        _tabs.SelectedIndex = 2;
        _script.Focus();
        _script.CaretIndex = _script.GetCharacterIndexFromLineIndex(Math.Min(line, Math.Max(0, _script.LineCount - 1)));
        _script.ScrollToLine(Math.Min(line, Math.Max(0, _script.LineCount - 1)));
    }

    // ------------------------------------------------------------------ search over every text of the game

    private readonly TextBox _searchBox = new() { Width = 360, FontSize = 14, Margin = new Thickness(0, 0, 8, 0), ToolTip = "Words of a text (all must appear) or a text id" };
    private readonly CheckBox _searchMapsOnly = new() { Content = "Only texts of maps", IsChecked = true, VerticalAlignment = VerticalAlignment.Center };
    private readonly ListBox _searchResults = new() { HorizontalContentAlignment = HorizontalAlignment.Stretch };
    private readonly TextBlock _searchStatus = new() { Foreground = Brushes.Gray, Margin = new Thickness(0, 4, 0, 4), TextWrapping = TextWrapping.Wrap };
    private readonly StackPanel _searchDetail = new() { Margin = new Thickness(0, 6, 0, 0) };
    private PlatinumTextSearch? _search;
    private Task<PlatinumTextSearch?>? _searchLoading;
    private System.Windows.Threading.DispatcherTimer? _searchTimer;

    private sealed record HitVm(TextHit Hit)
    {
        public string Where => Hit.MapName != null ? $"{Hit.MapName}  ·  {Hit.Id}" : $"{Hit.Bank}  ·  {Hit.Id}";
        public string Preview => PlatinumDialogText.ToNatural(Hit.Text).Replace("\n\n", " ▸ ").Replace('\n', ' ');
    }

    private FrameworkElement BuildSearch()
    {
        var dock = new DockPanel { Margin = new Thickness(8) };
        var top = new StackPanel();
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        System.Windows.Automation.AutomationProperties.SetName(_searchBox, "Search all texts");
        row.Children.Add(_searchBox);
        row.Children.Add(_searchMapsOnly);
        top.Children.Add(row);
        top.Children.Add(_searchStatus);
        DockPanel.SetDock(top, Dock.Top);
        dock.Children.Add(top);
        var detail = new ScrollViewer { Content = _searchDetail, MaxHeight = 260, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        DockPanel.SetDock(detail, Dock.Bottom);
        dock.Children.Add(detail);
        var stack = new FrameworkElementFactory(typeof(StackPanel));
        stack.SetValue(FrameworkElement.MarginProperty, new Thickness(0, 2, 0, 2));
        var a = new FrameworkElementFactory(typeof(TextBlock));
        a.SetBinding(TextBlock.TextProperty, new Binding(nameof(HitVm.Preview)));
        a.SetValue(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis);
        stack.AppendChild(a);
        var b = new FrameworkElementFactory(typeof(TextBlock));
        b.SetBinding(TextBlock.TextProperty, new Binding(nameof(HitVm.Where)));
        b.SetValue(TextBlock.FontSizeProperty, 10.0);
        b.SetValue(TextBlock.ForegroundProperty, Brushes.Gray);
        stack.AppendChild(b);
        _searchResults.ItemTemplate = new DataTemplate { VisualTree = stack };
        VirtualizingPanel.SetIsVirtualizing(_searchResults, true);
        ScrollViewer.SetHorizontalScrollBarVisibility(_searchResults, ScrollBarVisibility.Disabled);
        _searchResults.SelectionChanged += (_, _) => ShowHit();
        _searchResults.MouseDoubleClick += (_, _) => OpenHit();
        dock.Children.Add(_searchResults);
        _searchBox.TextChanged += (_, _) => ScheduleSearch();
        _searchMapsOnly.Checked += (_, _) => ScheduleSearch();
        _searchMapsOnly.Unchecked += (_, _) => ScheduleSearch();
        _searchStatus.Text = "Type words of a text – every text of the game is searched (double-click a hit to open it).";
        return dock;
    }

    private void ScheduleSearch()
    {
        _searchTimer ??= new System.Windows.Threading.DispatcherTimer(TimeSpan.FromMilliseconds(250), System.Windows.Threading.DispatcherPriority.Background, async (_, _) => { _searchTimer!.Stop(); await RunSearch(); }, System.Windows.Threading.Dispatcher.CurrentDispatcher);
        _searchTimer.Stop();
        _searchTimer.Start();
    }

    private async Task RunSearch()
    {
        var q = _searchBox.Text.Trim();
        if (q.Length < 2) { _searchResults.ItemsSource = null; return; }
        if (_search == null)
        {
            if (_ctx.Platinum is not { } p) return;
            _searchStatus.Text = "Reading every text of the game …";
            _searchLoading ??= Task.Run(() => { try { return PlatinumTextSearch.Load(p); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; } });
            _search = await _searchLoading;
            _searchLoading = null;
            if (_search == null) { _searchStatus.Text = "The texts could not be read."; return; }
        }
        var hits = _search.Search(q, _searchMapsOnly.IsChecked == true);
        _searchResults.ItemsSource = hits.Select(h => new HitVm(h)).ToList();
        _searchStatus.Text = $"{hits.Count}{(hits.Count >= 500 ? "+" : "")} hit(s) in {_search.Count} texts of {_search.Banks} banks." + (hits.Count > 0 ? " Double-click opens a map text in the writer." : "");
    }

    private void ShowHit()
    {
        _searchDetail.Children.Clear();
        if (_searchResults.SelectedItem is not HitVm h) return;
        _searchDetail.Children.Add(new TextBlock { Text = h.Where, FontFamily = new FontFamily("Consolas"), FontSize = 11, Foreground = Brushes.Gray });
        if (_font != null)
        {
            var wrap = new WrapPanel();
            foreach (var page in _font.RenderMessage(h.Hit.Text).Pages)
            {
                var img = Ui.Picture();
                img.Source = Imaging.ToBitmap(page);
                wrap.Children.Add(new Border { Child = img, Margin = new Thickness(0, 4, 8, 4) });
            }
            _searchDetail.Children.Add(wrap);
        }
        _searchDetail.Children.Add(h.Hit.MapHeaderId != null
            ? Ui.Row(Ui.Button("✎ Open in the writer", OpenHit, "Opens the map's texts with this text selected."),
                     Ui.Button("📄 Show in the code", () => { OpenHit(); if (_searchResults.SelectedItem is HitVm { Hit: var hh }) ShowTextInCode(hh.Id, hh.Index); }, "Opens the map's script file at the line that shows this text, highlighted."))
            : Ui.Hint("Not a text of a map – names, items, menus … are changed in Game data or the other modules.", 600));
    }

    /// <summary>
    /// The script file (code tab) at the first line that uses a text: by its id, or – in older pokeplatinum, where scripts
    /// name texts by number – by its number after a message command. The id is highlighted like a Ctrl+F match.
    /// </summary>
    private void ShowTextInCode(string textId, int index)
    {
        var code = _script.Text;
        string? found = null;
        int at = -1;
        if (textId.Length > 0 && System.Text.RegularExpressions.Regex.Match(code, $@"(?<![\w]){System.Text.RegularExpressions.Regex.Escape(textId)}(?![\w])") is { Success: true } m)
            (found, at) = (textId, m.Index);
        else if (index >= 0 && System.Text.RegularExpressions.Regex.Match(code, $@"^[ \t]*\w*(?:Message|Sign)\w*[ \t]+(?:[\w]+,[ \t]*)?({index})\b", System.Text.RegularExpressions.RegexOptions.Multiline) is { Success: true } n)
            (found, at) = (n.Groups[1].Value, n.Groups[1].Index);
        if (found == null)
        {
            ProjectWriter.Warn(Title, $"{textId} is not used in this map's script file – a shared script or the game's code shows it.");
            return;
        }
        _tabs.SelectedIndex = 2;
        _script.Dispatcher.BeginInvoke(() =>
        {
            _script.Focus();
            FindBar.ShowMatch(_script, found, at);
        }, System.Windows.Threading.DispatcherPriority.Loaded);
    }

    private void OpenHit()
    {
        if (_searchResults.SelectedItem is not HitVm { Hit: { MapHeaderId: { } map } hit }) return;
        OpenText(map, hit.Id);
    }

    /// <summary>Opens a map and shows one of its texts in the writer.</summary>
    public void OpenText(string headerId, string textId)
    {
        if (_allMaps.Count == 0) LoadMaps();
        if (_allMaps.FirstOrDefault(m => m.Header.Id == headerId) is not { } item)
        {
            ProjectWriter.Warn(Title, "This map has no events file, so its texts are not shown here.");
            return;
        }
        _mapFilter.Text = "";
        if (_map?.Header != item.Header) { _mapBox.SelectedItem = item; if (_map?.Header != item.Header) OpenMap(item); }
        if (_map?.Header != item.Header) return;   // the user kept unsaved changes of another map
        _textFilter.Text = "";
        _tabs.SelectedIndex = 1;
        _textList.SelectedItem = _textRows.FirstOrDefault(r => r.Id == textId);
        if (_textList.SelectedItem != null) _textList.ScrollIntoView(_textList.SelectedItem);
        ShowTextEditor();
    }

    /// <summary>Opens a map and selects one of its events (from the map editor of the Maps module).</summary>
    public void OpenEvent(string headerId, string list, int index)
    {
        if (_allMaps.Count == 0) LoadMaps();
        if (_allMaps.FirstOrDefault(m => m.Header.Id == headerId) is not { } item) return;
        _tabs.SelectedIndex = 0;
        _mapFilter.Text = "";
        if (_mapBox.SelectedItem != item || _map == null) { _mapBox.SelectedItem = item; if (_map?.Header != item.Header) OpenMap(item); }
        else if (!_dirty) OpenMap(item);   // the file was just written by the map editor
        _kindFilter.SelectedIndex = 0;
        if (_events?.Root[list] is System.Text.Json.Nodes.JsonArray arr && index >= 0 && index < arr.Count && arr[index] is JsonObject node) FillEvents(node);
    }

    /// <summary>Opens a map's script file at a line (1-based; from the build problem list).</summary>
    public void OpenScriptAt(string headerId, int? line)
    {
        OpenEvent(headerId, "", -1);
        if (_map?.Header.Id != headerId) return;
        var target = Math.Max(0, (line ?? 1) - 1);
        // The text box gets its lines only after layout.
        System.Windows.Threading.Dispatcher.CurrentDispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background, () => GoToLine(target));
        _tabs.SelectedIndex = 2;
    }

    // ------------------------------------------------------------------ maps

    private void LoadMaps()
    {
        var p = _ctx.Platinum;
        _mapData = p == null ? null : new PlatinumMaps(p.Root);
        _font = p == null ? null : PlatinumFont.Load(p.Root);
        _text = p == null ? null : GameText.LoadPlatinum(p.Root);
        if (p == null) { _allMaps = []; FilterMaps(); return; }
        var places = p.TextBank("location_names").ToDictionary(x => x.Id, x => x.Text, StringComparer.Ordinal);
        _allMaps = p.MapHeaders.Where(h => h.EventsFile != null && !h.EventsFile.Contains("events_empty"))
            .Select(h => new MapHeaderItemVm(h, h.LabelText != null && places.TryGetValue(h.LabelText, out var n) ? n : null)).ToList();
        var all = p.ConstantList("vars_flags");
        var flags = all.Where(f => f.StartsWith("FLAG_", StringComparison.Ordinal)).ToList();
        flags = flags.Where(f => f.StartsWith("FLAG_UNUSED_", StringComparison.Ordinal)).Concat(flags.Where(f => !f.StartsWith("FLAG_UNUSED_", StringComparison.Ordinal))).ToList();
        var vars = all.Where(v => v.StartsWith("VAR_", StringComparison.Ordinal)).ToList();
        vars = vars.Where(v => v.StartsWith("VAR_UNUSED_", StringComparison.Ordinal)).Concat(vars.Where(v => !v.StartsWith("VAR_UNUSED_", StringComparison.Ordinal))).ToList();
        var mugshots = PlatinumMugshots.IsInstalled(p.Root) ? PlatinumMugshots.List(p.Root).Select(m => m.Constant).ToList() : [];
        _lists = new ScriptBuilderLists(p.Items, p.Species, p.ConstantList("trainers"), flags, vars, p.MapHeaders.Select(h => h.Id).ToList(), mugshots, _font,
            c => { try { return MugshotImage(c); } catch (Exception ex) when (ex is IOException or PngFormatException) { return null; } })
        {
            Pictures = PlatinumMugshots.ListPictures(p.Root).Select(x => x.Constant).ToList(),
            Fullscreens = PlatinumMugshots.ListFullscreen(p.Root).Select(x => x.Constant).ToList(),
            PictureImage = c => { try { return PictureImage(c); } catch (Exception ex) when (ex is IOException or PngFormatException) { return null; } },
        };
        _dialect = PlatinumScriptBlocks.DialectOf(p.Root);
        try { _spritePngs = PlatinumOverworld.List(p.Root).GroupBy(s => s.Gfx).ToDictionary(g => g.Key, g => g.First().PngRel, StringComparer.Ordinal); }
        catch (Exception ex) when (ex is IOException or FormatException) { _spritePngs = new(StringComparer.Ordinal); }
        _spriteCache.Clear();
        FillMugshotChoices();
        var keep = _map?.Header.Id;
        _map = null;
        FilterMaps();
        if (keep != null && _allMaps.FirstOrDefault(m => m.Header.Id == keep) is { } again) _mapBox.SelectedItem = again;
    }

    private void FilterMaps()
    {
        var f = _mapFilter.Text.Trim();
        var keep = _mapBox.SelectedItem;
        _mapBox.ItemsSource = f.Length == 0 ? _allMaps : _allMaps.Where(m => m.Display.Contains(f, StringComparison.OrdinalIgnoreCase) || m.Header.Id.Contains(f, StringComparison.OrdinalIgnoreCase)).ToList();
        var shown = ((IEnumerable<MapHeaderItemVm>)_mapBox.ItemsSource).ToList();
        if (keep != null && shown.Contains(keep)) _mapBox.SelectedItem = keep;
        // Exactly one match (or one whose name is exactly the search): open it right away.
        else if (f.Length > 0 && (shown.Count == 1 || shown.Count(m => m.Header.FriendlyName.Equals(f, StringComparison.OrdinalIgnoreCase)) == 1))
            _mapBox.SelectedItem = shown.Count == 1 ? shown[0] : shown.First(m => m.Header.FriendlyName.Equals(f, StringComparison.OrdinalIgnoreCase));
    }

    private void Discard()
    {
        _dirty = false;
        var m = _mapBox.SelectedItem as MapHeaderItemVm;
        _map = null;
        if (m != null) OpenMap(m);
    }

    private void OpenMap(MapHeaderItemVm item)
    {
        if (_dirty && _map != null)
        {
            var r = AppDialog.Show(Application.Current.MainWindow, $"{_map.Header.FriendlyName} was changed. Save it first?", Title, MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
            if (r == MessageBoxResult.Cancel || (r == MessageBoxResult.Yes && !Save()))
            {
                _mapBox.SelectedItem = _allMaps.FirstOrDefault(m => m.Header == _map.Header);
                return;
            }
        }
        var root = _ctx.Root!;
        _loading = true;
        try
        {
            _map = PlatinumMapScripts.For(root, item.Header);
            _events = _map.EventsPath != null ? PlatinumJsonFile.Load(root, _map.EventsPath, 4) : null;
            _texts = _map.TextPath != null ? PlatinumJsonFile.Load(root, _map.TextPath, 2) : null;
            _scriptOriginal = _map.ScriptPath != null ? File.ReadAllText(ProjectPaths.Combine(root, _map.ScriptPath), Encoding.UTF8) : null;
            _script.Text = _scriptOriginal ?? "";
            _script.IsReadOnly = _scriptOriginal == null;
            _entries.ItemsSource = _scriptOriginal != null ? PlatinumMapScripts.Entries(_scriptOriginal) : null;
            _textRows.Clear();
            if (_texts != null)
                foreach (var m in PlatinumMapScripts.Messages(_texts).OfType<JsonObject>()) _textRows.Add(new TextRowVm(m, Touch));
            FilterTexts();
            _dirty = false;
            _selected = null;
            _history.Reset(State());
            _center.Content = Ui.Hint("Choose an event on the left – or add one with \"+ Person\", \"+ Sign\" or \"+ Trigger tile\".");
            ShowRightHint();
            FillEvents();
            RenderMap();
        }
        catch (Exception ex) when (ex is IOException or FormatException or System.Text.Json.JsonException or InvalidDataException)
        {
            _center.Content = Ui.Hint($"{item.Header.Id} could not be read: {ex.Message}");
            _map = null;
        }
        finally { _loading = false; }
        UpdateStatus();
    }

    private void UpdateStatus()
    {
        if (_map == null) { _status.Text = ""; return; }
        _status.Text = $"{_map.Header.FriendlyName} · {_map.EventsPath} · {_map.ScriptPath} · {_map.TextPath}" +
                       (_dirty ? "  ·  changed – not saved yet" : "") +
                       (_events?.Reformats == true || _texts?.Reformats == true ? "  ·  note: a file has unusual spacing that is tidied when saving" : "");
        _status.Foreground = _dirty ? Res("AccentDarkBrush") : Brushes.Gray;
    }

    private string Prefix => _scriptOriginal != null ? PlatinumMapScripts.Prefix(_script.Text, PlatinumMapScripts.PascalName(_map!.Header.FriendlyName)) : PlatinumMapScripts.PascalName(_map!.Header.FriendlyName);

    // ------------------------------------------------------------------ event list

    private string? MessageOf(string id) =>
        _texts == null ? null : PlatinumMapScripts.Messages(_texts).OfType<JsonObject>().FirstOrDefault(m => m["id"]?.GetValue<string>() == id) is { } m ? PlatinumMapScripts.MessageText(m) : null;

    private Dictionary<string, string> MessageLookup() =>
        _texts == null ? [] : PlatinumMapScripts.Messages(_texts).OfType<JsonObject>()
            .Where(m => m["id"] != null).GroupBy(m => m["id"]!.GetValue<string>()).ToDictionary(g => g.Key, g => PlatinumMapScripts.MessageText(g.First()), StringComparer.Ordinal);

    /// <summary>The script label an event runs (by label or ScriptEntry number), or null.</summary>
    private string? LabelOf(JsonObject node)
    {
        if (PlatinumMapScripts.TrainerOf(node) != null) return null;
        var row = new EventRowVm(node, () => { });
        var s = row["script"];
        if (s.Length == 0 || s == "0") return null;
        if (int.TryParse(s, out var n))
            return (_entries.ItemsSource as List<PlatinumScriptEntry>)?.FirstOrDefault(e => e.Number == n)?.Label;
        return s;
    }

    /// <summary>
    /// The scripts the map starts by itself (entering, cutscenes by a variable – e.g. Mom giving the Running Shoes) and the
    /// scripts of the file no event starts (they are started by other scripts or the game). Same view as the events.
    /// </summary>
    private List<EventItemVm> MapScriptItems(Dictionary<string, string> msgs, JsonObject? keep)
    {
        var result = new List<EventItemVm>();
        if (_map == null || _ctx.Root == null || _script.Text.Length == 0) return result;
        var entries = PlatinumMapScripts.Entries(_script.Text);
        var byNumber = entries.GroupBy(e => e.Number).ToDictionary(g => g.Key, g => g.First().Label);
        var used = new HashSet<string>(StringComparer.Ordinal);
        foreach (var list in PlatinumMapScripts.EventLists)
            foreach (var o in (_events?.Root[list] as JsonArray ?? []).OfType<JsonObject>())
                if (LabelOf(o) is { } l) used.Add(l);
        List<MapInitScript> init;
        try { init = PlatinumMapScripts.InitScripts(_ctx.Root, _map.Header); }
        catch (IOException) { init = []; }
        JsonObject Node(string label, string kind, string? var = null, string? value = null)
        {
            // The same node again when the list is rebuilt, so the selection stays.
            if (keep is { } k && k["pokepat_map_script"]?.GetValue<string>() == kind && k["script"]?.GetValue<string>() == label && k["var"]?.GetValue<string>() == var) return k;
            var n = new JsonObject { ["script"] = label, ["pokepat_map_script"] = kind };
            if (var != null) { n["var"] = var; n["value"] = value; }
            return n;
        }
        foreach (var i in init)
        {
            if (!byNumber.TryGetValue(i.Number, out var label)) continue;
            used.Add(label);
            result.Add(Describe(MapScriptsList, Node(label, i.Kind, i.Var, i.Value), msgs));
        }
        foreach (var e in entries.Where(e => !used.Contains(e.Label)))
            result.Add(Describe(MapScriptsList, Node(e.Label, IsSubroutine(e.Label) ? "Subroutine" : "Other"), msgs));
        return result;
    }

    /// <summary>A part that other scripts run with Call: its own lines end with Return, not End.</summary>
    private bool IsSubroutine(string label)
    {
        var last = PlatinumScriptFlow.Body(_script.Text, label).Select(l => l.Line.Trim()).LastOrDefault(l => l.Length > 0 && !l.StartsWith("//", StringComparison.Ordinal));
        return last != null && last.Split(' ')[0] == "Return";
    }

    private string FirstTextOf(string label, Dictionary<string, string> msgs)
    {
        if (!PlatinumScriptFlow.HasLabel(_script.Text, label)) return label;
        var first = Flatten(PlatinumScriptFlow.Steps(_script.Text, label, id => msgs.GetValueOrDefault(id), 1)).FirstOrDefault(s => s.Kind == ScriptStepKind.Message);
        return first != null ? "“" + first.Text + "”" : label;
    }

    private EventItemVm Describe(string list, JsonObject o, Dictionary<string, string> msgs)
    {
        if (list == MapScriptsList)
        {
            var kind = o["pokepat_map_script"]?.GetValue<string>() ?? "";
            var label = o["script"]?.GetValue<string>() ?? "";
            var init = new MapInitScript(kind, 0, o["var"]?.GetValue<string>(), o["value"]?.GetValue<string>());
            var shortName = PlatinumScriptFlow.Pretty(label.Contains('_') ? label[(label.IndexOf('_') + 1)..] : label);
            return kind switch
            {
                "Other" => new EventItemVm(list, o, "📜", shortName, "started by other scripts · " + FirstTextOf(label, msgs)),
                "Subroutine" => new EventItemVm(list, o, "↪", shortName, "part run by other scripts (Call … Return) · " + FirstTextOf(label, msgs)),
                "OnFrame" => new EventItemVm(list, o, "🎬", $"Cutscene: {shortName}", $"when {init.Var} = {init.Value} · {FirstTextOf(label, msgs)}"),
                _ => new EventItemVm(list, o, "🗺", init.Describe, FirstTextOf(label, msgs)),
            };
        }
        var r = new EventRowVm(o, () => { });
        string FirstText()
        {
            var label = LabelOf(o);
            if (label == null) return "";
            if (_script.Text.Length == 0 || !PlatinumScriptFlow.HasLabel(_script.Text, label)) return label;
            var first = Flatten(PlatinumScriptFlow.Steps(_script.Text, label, id => msgs.GetValueOrDefault(id), 1)).FirstOrDefault(s => s.Kind == ScriptStepKind.Message);
            return first != null ? "“" + first.Text + "”" : label;
        }
        switch (list)
        {
            case "object_events":
                if (PlatinumMapScripts.TrainerOf(o) is { } tr)
                    return new EventItemVm(list, o, "⚔", PlatinumScriptFlow.Pretty(tr.Trainer), $"Trainer · sees {tr.Sight} tile(s) · {PlatinumScriptFlow.Pretty(r["graphics_id"])}");
                var name = PlatinumScriptFlow.Pretty(r["id"]);
                if (name == "?") name = PlatinumScriptFlow.Pretty(r["graphics_id"]);
                bool trainer = r["trainer_type"] is { Length: > 0 } tt && tt != "TRAINER_TYPE_NONE" && tt != "0";
                return new EventItemVm(list, o, trainer ? "⚔" : "👤", name, FirstText());
            case "bg_events":
                if (o.ContainsKey("hidden_item"))
                    return new EventItemVm(list, o, "💎", "Hidden item", PlatinumScriptFlow.Pretty(r["hidden_item"].Replace("FLAG_OBTAINED_HIDDEN_", "")) + $" · {r.X}, {r.Z}");
                return new EventItemVm(list, o, "🪧", $"Sign at {r.X}, {r.Z}", FirstText());
            case "warp_events":
                return new EventItemVm(list, o, "🚪", "Warp to " + PlatinumScriptFlow.Pretty(r["dest_header_id"]), $"{r.X}, {r.Z} → warp {r["dest_warp_id"]}");
            default:
                return new EventItemVm(list, o, "👣", $"Trigger at {r.X}, {r.Z}", $"when {r["var"]} = {r["value"]} · {FirstText()}");
        }
    }

    private static IEnumerable<ScriptStep> Flatten(IEnumerable<ScriptStep> steps)
    {
        foreach (var s in steps)
        {
            yield return s;
            foreach (var c in Flatten(s.Children)) yield return c;
        }
    }

    private void FillEvents(JsonObject? select = null)
    {
        var keep = select ?? _selected?.Node;
        var items = new List<EventItemVm>();
        if (_events != null)
        {
            var msgs = MessageLookup();
            var only = Kinds[Math.Max(0, _kindFilter.SelectedIndex)].Key;
            foreach (var list in PlatinumMapScripts.EventLists)
                if ((only.Length == 0 || only == list) && _events.Root[list] is JsonArray arr)
                    foreach (var o in arr.OfType<JsonObject>()) items.Add(Describe(list, o, msgs));
            if (only.Length == 0 || only == MapScriptsList) items.AddRange(MapScriptItems(msgs, keep));
        }
        _eventList.ItemsSource = items;
        if (keep != null && items.FirstOrDefault(i => ReferenceEquals(i.Node, keep)) is { } again)
        {
            _eventList.SelectedItem = again;
            _eventList.ScrollIntoView(again);
        }
        DrawMarkers();
    }

    /// <summary>Updates title and text of the selected event in the list without rebuilding what is shown.</summary>
    private void RefreshSelectedInList()
    {
        if (_selected == null || _eventList.ItemsSource is not List<EventItemVm> items) return;
        int i = items.FindIndex(x => ReferenceEquals(x.Node, _selected.Node));
        if (i < 0) return;
        var updated = Describe(_selected.List, _selected.Node, MessageLookup());
        items[i] = updated;
        _selected = updated;
        _suppressSelection = true;
        _eventList.Items.Refresh();
        _eventList.SelectedItem = updated;
        _suppressSelection = false;
    }

    private bool _suppressSelection;

    // ------------------------------------------------------------------ the selected event

    private Action? _rightRefresh;

    private void ShowRightHint(string? text = null)
    {
        _rightRefresh = null;
        _right.Children.Clear();
        _right.Children.Add(Ui.Title("Edit"));
        _right.Children.Add(Ui.Hint(text ?? "Click a block or a 💬 step in the middle to change it here.", 360));
    }

    private void ShowEvent(EventItemVm e)
    {
        if (_suppressSelection) return;
        _selected = e;
        _managedLabel = null;
        _blocks.Clear();
        Ui.Detach(_placeOnClick, _blockList, _blockProblem, _code);
        if (_mapPanel != null) Ui.Detach(_mapPanel);
        var row = new EventRowVm(e.Node, () => { Touch(); DrawMarkers(); RefreshSelectedInList(); });

        var panel = new Grid();
        panel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        panel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        panel.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        // header
        var head = new DockPanel { Margin = new Thickness(0, 0, 0, 6) };
        var icon = new TextBlock { Text = e.Icon, FontSize = 26, Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
        DockPanel.SetDock(icon, Dock.Left);
        head.Children.Add(icon);
        if (e.List == "object_events" && SpriteOf(row["graphics_id"]) is { } sprite)
        {
            var img = Ui.Picture(1.5);
            img.Source = sprite;
            DockPanel.SetDock(img, Dock.Left);
            head.Children.Add(img);
        }
        var titles = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        titles.Children.Add(new TextBlock { Text = e.Title, FontSize = 17, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });
        var label = e.List is "warp_events" || e.Node.ContainsKey("hidden_item") ? null : LabelOf(e.Node);
        titles.Children.Add(new TextBlock
        {
            Text = e.List switch
            {
                "object_events" => PlatinumMapScripts.TrainerOf(e.Node) is { } tr0 ? $"Trainer {row["id"]} · {tr0.Trainer} · sight {tr0.Sight}" : $"Person {row["id"]} · runs {label ?? "no script"}",
                "bg_events" => e.Node.ContainsKey("hidden_item") ? $"Hidden item · picked up = {row["hidden_item"]}" : $"Sign · runs {label ?? "no script"}",
                "warp_events" => $"Warp {row["id"]}",
                MapScriptsList => row["pokepat_map_script"] switch
                {
                    "OnFrame" => $"Map script · runs {label} as soon as {row["var"]} = {row["value"]} (map init file)",
                    "Other" => $"Script {label} · no event starts it – other scripts (GoTo) or the game do",
                    "Subroutine" => $"Part {label} · other scripts run it with Call; it goes back with Return",
                    var k => $"Map script · runs {label} – {new MapInitScript(k, 0).Describe.ToLowerInvariant()}",
                },
                _ => $"Trigger tile · runs {label ?? "no script"} while {row["var"]} = {row["value"]}",
            },
            FontSize = 11, Foreground = Brushes.Gray, FontFamily = new FontFamily("Consolas"), TextTrimming = TextTrimming.CharacterEllipsis,
        });
        head.Children.Add(titles);
        panel.Children.Add(head);

        // properties (looks, position, movement …) with the map
        bool scriptEvent = e.List is not "warp_events" && !e.Node.ContainsKey("hidden_item");
        bool mapScript = e.List == MapScriptsList;
        var props = mapScript ? null : new Expander
        {
            Header = scriptEvent ? "Properties – looks, position, movement, when it appears" : "Properties",
            IsExpanded = !scriptEvent || _propsOpen, Content = Properties(e, row), Margin = new Thickness(0, 0, 0, 6),
        };
        if (props != null)
        {
            props.Expanded += (_, _) => _propsOpen = true;
            props.Collapsed += (_, _) => _propsOpen = false;
            Grid.SetRow(props, 1);
            panel.Children.Add(props);
        }
        else
        {
            var hint = Ui.Hint(e.Node["pokepat_map_script"]?.GetValue<string>() == "OnFrame"
                ? "The map checks its cutscene table all the time: when the variable has this value, the script runs. The script itself must change the variable, or it runs again. The table is in the map's init.s."
                : "This script has no place on the map. Change its steps or blocks below like any event.", 700);
            Grid.SetRow(hint, 1);
            panel.Children.Add(hint);
        }

        FrameworkElement body;
        ShowRightHint();
        if (!scriptEvent)
        {
            body = Ui.Hint(e.List == "warp_events"
                ? "A warp takes the player to the warp number \"dest_warp_id\" of the destination map. Change both above."
                : "A hidden item: the player finds it with A or the Dowsing Machine. The flag remembers that it was picked up. Which item it is, is set by the flag's entry in the hidden items list.", 600);
            ShowRightHint("Warps and hidden items have no script – their properties are in the middle.");
        }
        else if (PlatinumMapScripts.TrainerOf(e.Node) is { } tr)
            body = TrainerView(tr.Trainer, tr.Sight);
        else if (label == null)
            body = NoScript(e, "This event runs no script yet.");
        else if (!PlatinumScriptFlow.HasLabel(_script.Text, label))
            body = NoScript(e, $"This event runs {label}, a script that is not in this map's file (a script shared by several maps). You can give it its own script made from blocks instead.");
        else if (PlatinumScriptBlocks.ReadBlocks(_script.Text, label) is { } managed)
            body = BlocksView(e, label, managed.Blocks, managed.TextPrefix);
        else
            body = FlowView(e, label);
        Grid.SetRow(body, 2);
        panel.Children.Add(body);
        _center.Content = panel;
        DrawMarkers();
    }

    private bool _propsOpen;

    private static ScriptStart StartOf(EventItemVm e) => e.List switch
    {
        // Scripts of entering/loading must not lock the player; cutscenes and other scripts lock like trigger tiles.
        MapScriptsList => e.Node["pokepat_map_script"]?.GetValue<string>() switch
        {
            "OnTransition" or "OnLoad" or "OnResume" => ScriptStart.Silent,
            "Subroutine" => ScriptStart.Subroutine,
            _ => ScriptStart.Step,
        },
        "object_events" => ScriptStart.Person,
        "bg_events" => ScriptStart.Sign,
        _ => ScriptStart.Step,
    };

    private FrameworkElement NoScript(EventItemVm e, string why)
    {
        var s = new StackPanel { Margin = new Thickness(0, 8, 0, 0) };
        s.Children.Add(Ui.Hint(why, 600));
        s.Children.Add(Ui.Row(Ui.Button("🧩 Give it a script made from blocks", () => GiveBlocks(e, [PlatinumBlockEditor.NewBlock(ScriptBlockKind.Message, _lists!)]),
            "A new script for this event; you put it together from blocks (message, question, item, battle …).")));
        return s;
    }

    // ---- properties

    private FrameworkElement Properties(EventItemVm e, EventRowVm row)
    {
        var outer = new StackPanel();
        var context = new EventEditContext(_ctx.Platinum!, _events!, _lists!.Flags, _lists.Vars,
            (_entries.ItemsSource as List<PlatinumScriptEntry>)?.Select(x => x.Label).ToList(),
            () => { Touch(); DrawMarkers(); RefreshSelectedInList(); });
        outer.Children.Add(PlatinumEventEditor.Fields(e.Node, context));
        outer.Children.Add(_placeOnClick);
        outer.Children.Add(MapPanel());
        return outer;
    }

    private StackPanel MapPanel()
    {
        if (_mapPanel != null) { Ui.Detach(_mapPanel); return _mapPanel; }
        _mapScroll.Content = new Border { Child = _canvas, LayoutTransform = _mapScale };
        _mapScroll.PreviewMouseWheel += (_, e) =>
        {
            if (!Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) return;
            SetMapZoom(_mapScale.ScaleX * (e.Delta > 0 ? 1.25 : 0.8));
            e.Handled = true;
        };
        _canvas.MouseMove += (_, e) =>
        {
            var p = e.GetPosition(_canvas);
            int x = _area.X * PlatinumLandData.Size + (int)(p.X / Tile), z = _area.Y * PlatinumLandData.Size + (int)(p.Y / Tile);
            _mapCoords.Text = $"tile x {x}, z {z}";
        };
        _canvas.MouseLeave += (_, _) => _mapCoords.Text = "";
        var bar = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 2) };
        Button B(string t, string tip, Action a) { var x = new Button { Content = t, Padding = new Thickness(8, 1, 8, 1), Margin = new Thickness(0, 0, 4, 0), ToolTip = tip }; x.Click += (_, _) => a(); return x; }
        bar.Children.Add(B("−", "Zoom out (Ctrl+mouse wheel)", () => SetMapZoom(_mapScale.ScaleX * 0.8)));
        bar.Children.Add(B("+", "Zoom in (Ctrl+mouse wheel)", () => SetMapZoom(_mapScale.ScaleX * 1.25)));
        bar.Children.Add(B("Fit", "The whole map in the width of the panel", FitMap));
        bar.Children.Add(B("⌖", "Show the selected event", () => _selectedMarker?.BringIntoView()));
        bar.Children.Add(_mapZoomText);
        bar.Children.Add(_mapCoords);
        _mapPanel = new StackPanel();
        _mapPanel.Children.Add(bar);
        _mapPanel.Children.Add(_mapScroll);
        _mapScroll.Loaded += (_, _) => { if (!_mapZoomChosen) FitMap(); };
        return _mapPanel;
    }

    private void SetMapZoom(double scale, bool chosen = true)
    {
        scale = Math.Clamp(scale, 0.25, 8);
        _mapScale.ScaleX = _mapScale.ScaleY = scale;
        _mapZoomText.Text = $"{scale * 100:0} %";
        _mapZoomChosen |= chosen;
        // Keep the selected event in sight.
        _canvas.Dispatcher.BeginInvoke(() => _selectedMarker?.BringIntoView(), System.Windows.Threading.DispatcherPriority.Loaded);
    }

    private void FitMap()
    {
        double width = _mapScroll.ActualWidth > 40 ? _mapScroll.ActualWidth - 20 : 360;
        if (_canvas.Width > 0) SetMapZoom(width / _canvas.Width, chosen: false);
    }

    private readonly Dictionary<(string, int), ImageSource?> _spriteCache = [];

    /// <summary>The person's sprite looking in <paramref name="dir"/> (0 north, 1 south, 2 west, 3 east).</summary>
    private ImageSource? SpriteOf(string gfx, int dir = 1)
    {
        if (_spriteCache.TryGetValue((gfx, dir), out var cached)) return cached;
        ImageSource? src = null;
        try
        {
            if (_ctx.Root is { } root && _spritePngs.TryGetValue(gfx, out var png) && png != null && PlatinumOverworld.Facing(root, png, dir) is { } f) src = Imaging.ToBitmap(f);
        }
        catch (Exception ex) when (ex is IOException or PngFormatException or InvalidDataException) { }
        return _spriteCache[(gfx, dir)] = src;
    }

    // ---- trainers on the map

    private FrameworkElement TrainerView(string trainer, int sight)
    {
        var s = new StackPanel { Margin = new Thickness(0, 8, 0, 0) };
        s.Children.Add(Ui.Hint($"A trainer: when the player walks into its sight ({sight} tile(s) in the direction it looks), it walks up, says its text and battles. " +
                               "Once beaten it only says its text after the battle. Team, items and texts are set in the Trainers module.", 640));
        var root = _ctx.Root!;
        var file = ProjectPaths.Combine(root, $"{PlatinumProject.TrainersDir}/{trainer[8..].ToLowerInvariant()}.json");
        if (!File.Exists(file)) { s.Children.Add(Ui.Hint($"✗ {trainer} has no file {PlatinumProject.TrainersDir}/{trainer[8..].ToLowerInvariant()}.json – choose another trainer above.")); return s; }
        try
        {
            var t = PlatinumTrainers.Parse(file, File.ReadAllText(file));
            s.Children.Add(new TextBlock { Text = $"{t.Name} · {PlatinumScriptFlow.Pretty(t.Class.Replace("TRAINER_CLASS_", "TRAINER_"))}" + (t.DoubleBattle ? " · double battle" : ""), FontWeight = FontWeights.SemiBold, FontSize = 14, Margin = new Thickness(0, 8, 0, 2) });
            s.Children.Add(new TextBlock { Text = "Team: " + string.Join(", ", t.Party.Select(m => $"{PlatinumScriptFlow.Pretty(m.Species)} Lv. {m.Level}")), TextWrapping = TextWrapping.Wrap });
            foreach (var (type, text) in t.Messages.Where(m => m.Type is "TRMSG_PRE_BATTLE" or "TRMSG_DEFEAT" or "TRMSG_POST_BATTLE"))
            {
                var label = type switch { "TRMSG_PRE_BATTLE" => "Before the battle", "TRMSG_DEFEAT" => "When beaten", _ => "Afterwards" };
                s.Children.Add(new TextBlock { Text = label, FontWeight = FontWeights.SemiBold, FontSize = 11, Margin = new Thickness(0, 8, 0, 0) });
                s.Children.Add(new TextBlock { Text = PlatinumDialogText.ToNatural(text.Replace("\n", "\n")).Replace("\n\n", " ▸ ").Replace('\n', ' '), TextWrapping = TextWrapping.Wrap, Foreground = Res("MutedBrush") });
            }
        }
        catch (Exception ex) when (ex is IOException or FormatException or System.Text.Json.JsonException) { s.Children.Add(Ui.Hint(ex.Message)); }
        s.Children.Add(Ui.Row(Ui.Button("⚔ Edit team and texts (Trainers) →", () => _navigate("Trainers"), $"Opens the Trainers module – search for {trainer}.")));
        return s;
    }

    private void NewTrainerEvent()
    {
        if (!CanAdd()) return;
        var p = _ctx.Platinum!;
        var trainers = p.ConstantList("trainers").Where(t => t != "TRAINER_NONE").ToList();
        var gfx = p.ConstantList("object_events_gfx");
        var tcb = new ComboBox { ItemsSource = trainers, IsEditable = true, IsTextSearchEnabled = true, Width = 360, HorizontalAlignment = HorizontalAlignment.Left };
        tcb.ItemsPanel = new ItemsPanelTemplate(new FrameworkElementFactory(typeof(VirtualizingStackPanel)));
        var look = new ComboBox { ItemsSource = gfx, IsEditable = true, IsTextSearchEnabled = true, Width = 360, HorizontalAlignment = HorizontalAlignment.Left };
        look.ItemsPanel = new ItemsPanelTemplate(new FrameworkElementFactory(typeof(VirtualizingStackPanel)));
        var dirs = new ComboBox { ItemsSource = new[] { "Looks up (north)", "Looks down (south)", "Looks left (west)", "Looks right (east)" }, SelectedIndex = 1, Width = 200, HorizontalAlignment = HorizontalAlignment.Left };
        var sight = new TextBox { Text = "4", Width = 60, HorizontalAlignment = HorizontalAlignment.Left };
        tcb.SelectionChanged += (_, _) =>
        {
            // The trainer's class decides the default look (Youngster → OBJ_EVENT_GFX_YOUNGSTER).
            if (tcb.SelectedItem is not string t || _ctx.Root is not { } root) return;
            var file = ProjectPaths.Combine(root, $"{PlatinumProject.TrainersDir}/{t[8..].ToLowerInvariant()}.json");
            try { if (File.Exists(file) && PlatinumMapScripts.GraphicsForClass(PlatinumTrainers.Parse(file, File.ReadAllText(file)).Class, gfx) is { } g) look.Text = g; }
            catch (Exception ex) when (ex is IOException or FormatException or System.Text.Json.JsonException) { }
        };
        var panel = new StackPanel { Margin = new Thickness(14), Width = 420 };
        panel.Children.Add(new TextBlock { TextWrapping = TextWrapping.Wrap, Text = "A trainer who battles the player walking into its sight. New trainers (own team and texts) are made in the Trainers module first." });
        void Field(string l, UIElement el) { panel.Children.Add(new TextBlock { Text = l, Margin = new Thickness(0, 8, 0, 2) }); panel.Children.Add(el); }
        Field("Trainer", tcb);
        Field("Looks like", look);
        Field("Looks in direction", dirs);
        Field("Sight (tiles, 1–15)", sight);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
        var ok = new Button { Content = "Add", IsDefault = true, MinWidth = 90 };
        buttons.Children.Add(ok);
        buttons.Children.Add(new Button { Content = "Cancel", IsCancel = true, MinWidth = 80 });
        panel.Children.Add(buttons);
        var w = new Window { Title = "New trainer on the map", Content = panel, SizeToContent = SizeToContent.WidthAndHeight, ResizeMode = ResizeMode.NoResize, Owner = Application.Current.MainWindow, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        ok.Click += (_, _) =>
        {
            if (!trainers.Contains(tcb.Text.Trim().ToUpperInvariant())) { AppDialog.Show(w, "Choose a trainer of the list.", w.Title); return; }
            if (!gfx.Contains(look.Text.Trim())) { AppDialog.Show(w, "Choose how the trainer looks (an OBJ_EVENT_GFX_ of the list).", w.Title); return; }
            if (!int.TryParse(sight.Text.Trim(), out var sv) || sv < 1 || sv > 15) { AppDialog.Show(w, "The sight is a number from 1 to 15.", w.Title); return; }
            w.DialogResult = true;
        };
        if (w.ShowDialog() != true) return;
        var (x, z) = FreeSpot();
        var node = PlatinumMapScripts.AddTrainer(_events!, tcb.Text.Trim().ToUpperInvariant(), look.Text.Trim(), dirs.SelectedIndex, int.Parse(sight.Text.Trim()), x, z);
        Touch();
        _propsOpen = true;
        _kindFilter.SelectedIndex = 0;
        FillEvents(node);
    }

    // ---- blocks (scripts made with PokéPat)

    private FrameworkElement BlocksView(EventItemVm e, string label, List<ScriptBlock> blocks, string textPrefix)
    {
        _managedLabel = label;
        _managedTextPrefix = textPrefix;
        _managedStart = StartOf(e);
        foreach (var b in blocks) _blocks.Add(Hooked(b));
        ScriptBlockTree.UpdateDepths(_blocks);

        var dock = new DockPanel();
        var top = new StackPanel();
        var play = new Button { Content = "▶ Play event", FontWeight = FontWeights.SemiBold, Padding = new Thickness(10, 3, 10, 3), Margin = new Thickness(0, 0, 0, 6), HorizontalAlignment = HorizontalAlignment.Left,
            ToolTip = "Plays the event like the game: message boxes in the game's font, the Yes/No menu, branches by flags – you decide battles and the bag." };
        play.Click += (_, _) => PlayEvent();
        top.Children.Add(play);
        top.Children.Add(new TextBlock { Text = "Add a block (after the selected one):", FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 2) });
        // Who can move: the selected person first, then the map's other people and the player.
        var own = new EventRowVm(e.Node, () => { })["id"];
        var people = (_events?.Root["object_events"] as System.Text.Json.Nodes.JsonArray)?.OfType<JsonObject>()
            .Select(o => o["id"]?.GetValue<string>() ?? "").Where(x => x.StartsWith("LOCALID_", StringComparison.Ordinal)).ToList() ?? [];
        _lists = _lists! with { People = (own.StartsWith("LOCALID_", StringComparison.Ordinal) ? [own] : new List<string>()).Concat(people.Where(x => x != own)).Append("LOCALID_PLAYER").ToList() };
        top.Children.Add(PlatinumBlockEditor.Palette(AddBlock, _lists!.Mugshots.Count > 0, _lists.Pictures.Count + _lists.Fullscreens.Count > 0));
        DockPanel.SetDock(top, Dock.Top);
        dock.Children.Add(top);

        var bottom = new StackPanel();
        var order = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 0) };
        order.Children.Add(Small("▲ Up", () => MoveBlock(-1), "Move the selected block up."));
        order.Children.Add(Small("▼ Down", () => MoveBlock(+1), "Move the selected block down."));
        order.Children.Add(Small("✕ Remove", RemoveBlock, "Remove the selected block."));
        bottom.Children.Add(order);
        bottom.Children.Add(_blockProblem);
        bottom.Children.Add(new Expander { Header = "Generated script (code)", Content = _code, Margin = new Thickness(0, 4, 0, 0) });
        DockPanel.SetDock(bottom, Dock.Bottom);
        dock.Children.Add(bottom);
        dock.Children.Add(_blockList);
        UpdateCode();
        if (_blocks.Count > 0) _blockList.SelectedIndex = 0;
        return dock;
    }

    private ScriptBlock Hooked(ScriptBlock b)
    {
        b.PropertyChanged += (_, e) => { if (e.PropertyName != nameof(ScriptBlock.Depth)) BlocksChanged(); };
        return b;
    }

    private void AddBlock(ScriptBlockKind kind)
    {
        var b = Hooked(PlatinumBlockEditor.NewBlock(kind, _lists!));
        int at = _blockList.SelectedIndex >= 0 ? _blockList.SelectedIndex + 1 : _blocks.Count;
        _blocks.Insert(at, b);
        // A branch comes with its parts: a question with "Otherwise (No)", every branch with its end.
        if (b.Opens)
        {
            int next = at + 1;
            if (kind == ScriptBlockKind.Ask) _blocks.Insert(next++, Hooked(new ScriptBlock { Kind = ScriptBlockKind.Else }));
            _blocks.Insert(next, Hooked(new ScriptBlock { Kind = ScriptBlockKind.EndIf }));
        }
        _blockList.SelectedItem = b;
        BlocksChanged();
    }

    /// <summary>The blocks that move together with block <paramref name="i"/>: a branch moves with its parts.</summary>
    private (int Start, int End) Group(int i)
    {
        if (_blocks[i].Opens && ScriptBlockTree.PartsOf(_blocks, i) is { End: >= 0 } parts) return (i, parts.End);
        return (i, i);
    }

    private void MoveBlock(int delta)
    {
        if (_blockList.SelectedItem is not ScriptBlock b) return;
        var (start, end) = Group(_blocks.IndexOf(b));
        if (delta < 0 && start > 0) _blocks.Move(start - 1, end);
        else if (delta > 0 && end < _blocks.Count - 1) _blocks.Move(end + 1, start);
        else return;
        _blockList.SelectedItem = b;
        BlocksChanged();
    }

    private void RemoveBlock()
    {
        if (_blockList.SelectedItem is not ScriptBlock b) return;
        int i = _blocks.IndexOf(b);
        // Removing a branch removes its "Otherwise" and its end; the blocks inside stay.
        if (b.Opens)
        {
            var (els, end) = ScriptBlockTree.PartsOf(_blocks, i);
            foreach (var k in new[] { end, els }.Where(k => k > i).OrderDescending()) _blocks.RemoveAt(k);
        }
        _blocks.Remove(b);
        if (_blocks.Count > 0) _blockList.SelectedIndex = Math.Min(i, _blocks.Count - 1);
        BlocksChanged();
    }

    private void PlayEvent()
    {
        if (_lists == null || _blocks.Count == 0) return;
        new PlatinumEventPlayerWindow(_blocks.ToList(), _lists, _font, _managedLabel ?? "Event").ShowDialog();
    }

    private void ShowBlockEditor()
    {
        if (_blockList.SelectedItem is not ScriptBlock b || _lists == null) { if (_managedLabel != null) ShowRightHint(); return; }
        _right.Children.Clear();
        _rightRefresh = null;
        PlatinumBlockEditor.Editor(_right, b, _blocks.ToList(), _lists, () => { });
    }

    /// <summary>Writes the blocks into the script file and the texts into the bank (in memory until "Save").</summary>
    private void BlocksChanged()
    {
        ScriptBlockTree.UpdateDepths(_blocks);
        if (_managedLabel == null || _managedTextPrefix == null || _texts == null || _dialect == null) return;
        try
        {
            var built = PlatinumScriptBlocks.Build(_managedLabel, _managedTextPrefix, _blocks.ToList(), _dialect, _managedStart);
            var (script, _) = PlatinumScriptBlocks.WriteScript(_script.Text, _managedLabel, _managedTextPrefix, _blocks.ToList(), built);
            PlatinumScriptBlocks.WriteMessages(_texts, _managedTextPrefix, built.Messages);
            SyncTextRows();
            if (script != _script.Text) _script.Text = script;
            _blockProblem.Text = "";
            Touch();
            UpdateCode();
            RefreshSelectedInList();
        }
        catch (InvalidDataException ex) { _blockProblem.Text = "⚠ " + ex.Message + " (the script keeps its last working version until this is fixed)"; }
    }

    private void UpdateCode()
    {
        if (_managedLabel == null) return;
        var lines = new List<string> { _managedLabel + ":" };
        foreach (var l in _script.Text.Replace("\r\n", "\n").Split('\n').SkipWhile(l => l.TrimEnd() != _managedLabel + ":").Skip(1))
        {
            if (l.StartsWith(PlatinumScriptBlocks.Marker, StringComparison.Ordinal)) break;   // ".balign" belongs to our movement data
            if (System.Text.RegularExpressions.Regex.IsMatch(l, @"^\w+:\s*$") && !l.StartsWith(_managedLabel + "_", StringComparison.Ordinal)) break;
            lines.Add(l);
        }
        _code.Text = string.Join("\n", lines).TrimEnd();
    }

    /// <summary>Gives an event a new script made from blocks (also replaces a shared or missing script).</summary>
    private void GiveBlocks(EventItemVm e, List<ScriptBlock> blocks, string? replaceLabel = null)
    {
        if (_texts == null || _scriptOriginal == null || _dialect == null || _events == null)
        {
            ProjectWriter.Warn(Title, "This map needs a script file and a text bank for scripts made from blocks.");
            return;
        }
        try
        {
            string label;
            if (replaceLabel != null) label = replaceLabel;
            else
            {
                var r = new EventRowVm(e.Node, () => { });
                var baseName = PlatinumMapScripts.PascalName(e.List switch
                {
                    "object_events" => PlatinumScriptFlow.Pretty(r["id"]) is var n && n != "?" ? n : "Person",
                    "bg_events" => "Sign",
                    _ => "Trigger",
                });
                label = $"{Prefix}_{baseName}";
                for (int i = 2; PlatinumScriptFlow.HasLabel(_script.Text, label); i++) label = $"{Prefix}_{baseName}{i}";
            }
            var name = label.StartsWith(Prefix + "_", StringComparison.Ordinal) ? label[(Prefix.Length + 1)..] : label;
            var textPrefix = PlatinumScriptBlocks.FreeTextPrefix(_texts, $"{Prefix}_Text_{name}");
            var built = PlatinumScriptBlocks.Build(label, textPrefix, blocks, _dialect, StartOf(e));
            var (script, number) = PlatinumScriptBlocks.WriteScript(_script.Text, label, textPrefix, blocks, built);
            PlatinumScriptBlocks.WriteMessages(_texts, textPrefix, built.Messages);
            _script.Text = script;
            _entries.ItemsSource = PlatinumMapScripts.Entries(script);
            if (replaceLabel == null)
            {
                bool byLabel = PlatinumMapScripts.ScriptsByLabel(_events);
                e.Node["script"] = byLabel ? JsonValue.Create(label) : JsonValue.Create(number);
            }
            SyncTextRows();
            Touch();
            FillEvents(e.Node);
            if (_eventList.SelectedItem is EventItemVm again) ShowEvent(again);
        }
        catch (InvalidDataException ex) { ProjectWriter.Warn(Title, ex.Message); }
    }

    // ---- readable steps (the game's own scripts)

    private bool _showTechnical;

    private FrameworkElement FlowView(EventItemVm e, string label)
    {
        var dock = new DockPanel();
        var banner = new Border
        {
            Background = Res("PanelBrush"), CornerRadius = new CornerRadius(4), Padding = new Thickness(8), Margin = new Thickness(0, 0, 0, 6),
        };
        var bannerStack = new StackPanel();
        bannerStack.Children.Add(new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Text = $"{label} is a script of the game. Click a 💬 step to change what is said (right). To change what happens, rebuild it from blocks or edit the code.",
        });
        bannerStack.Children.Add(Ui.Row(
            Ui.Button("🧩 Rebuild from blocks…", () => RebuildWithBlocks(e, label), "Turns this script into blocks: texts, questions with their branches, flags, items, battles, movements …; lines no block knows stay as code blocks."),
            Ui.Button("</> Edit the code", () => { var entry = (_entries.ItemsSource as List<PlatinumScriptEntry>)?.FirstOrDefault(x => x.Label == label); GoToLine(entry?.Line ?? LineOfLabel(label)); }),
            new CheckBox { Content = "Show technical steps", IsChecked = _showTechnical, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) }));
        var techBox = (CheckBox)((StackPanel)bannerStack.Children[1]).Children[2];
        techBox.Checked += (_, _) => { _showTechnical = true; ShowEvent(e); };
        techBox.Unchecked += (_, _) => { _showTechnical = false; ShowEvent(e); };
        banner.Child = bannerStack;
        DockPanel.SetDock(banner, Dock.Top);
        dock.Children.Add(banner);

        var msgs = MessageLookup();
        var steps = PlatinumScriptFlow.Steps(_script.Text, label, id => msgs.GetValueOrDefault(id));
        var tree = new TreeView { FontSize = 13 };
        ScrollViewer.SetHorizontalScrollBarVisibility(tree, ScrollBarVisibility.Disabled);
        void AddSteps(ItemCollection target, IEnumerable<ScriptStep> list)
        {
            foreach (var s in list)
            {
                if (s.Kind == ScriptStepKind.Technical && !_showTechnical && s.Children.Count == 0) continue;
                var header = new DockPanel();
                var ic = new TextBlock { Text = s.Icon, Width = 24 };
                DockPanel.SetDock(ic, Dock.Left);
                header.Children.Add(ic);
                header.Children.Add(new TextBlock
                {
                    Text = s.Text, TextWrapping = TextWrapping.Wrap, MaxWidth = 700,
                    Foreground = s.Kind == ScriptStepKind.Technical ? Brushes.Gray : Res("InkBrush"),
                    FontWeight = s.Kind == ScriptStepKind.Message ? FontWeights.SemiBold : FontWeights.Normal,
                });
                var item = new TreeViewItem { Header = header, Tag = s, IsExpanded = s.Children.Count > 0 && s.Children.Count < 12, Padding = new Thickness(2, 3, 2, 3) };
                AddSteps(item.Items, s.Children);
                target.Add(item);
            }
        }
        AddSteps(tree.Items, steps);
        if (tree.Items.Count == 0) tree.Items.Add(new TreeViewItem { Header = "(this script has no steps)" });
        tree.SelectedItemChanged += (_, _) =>
        {
            if (tree.SelectedItem is TreeViewItem { Tag: ScriptStep s } tvi) ShowStep(s, tvi);
        };
        dock.Children.Add(tree);
        return dock;
    }

    private int LineOfLabel(string label)
    {
        var lines = _script.Text.Replace("\r\n", "\n").Split('\n');
        return Math.Max(0, Array.FindIndex(lines, l => l.TrimEnd() == label + ":"));
    }

    private void ShowStep(ScriptStep s, TreeViewItem item)
    {
        _right.Children.Clear();
        _rightRefresh = null;
        if (s.MessageId != null && _textRows.FirstOrDefault(r => r.Id == s.MessageId) is { } row)
        {
            void Build()
            {
                _right.Children.Clear();
                _right.Children.Add(Ui.Title("💬 Text"));
                _right.Children.Add(new TextBlock { Text = s.MessageId, FontFamily = new FontFamily("Consolas"), FontSize = 11, Foreground = Brushes.Gray });
                var users = UsersOf(s.MessageId);
                if (users.Count > 1) _right.Children.Add(Ui.Hint($"This text is used {users.Count}× ({string.Join(", ", users.Distinct().Take(4))}) – a change shows everywhere.", 360));
                WriterFor(_right, row, "What is said", () =>
                {
                    if (item.Header is DockPanel d && d.Children[1] is TextBlock tb) tb.Text = row.Preview;
                });
                MugshotChooser(_right, s.MessageId, s.LineIndex);
            }
            Build();
            _rightRefresh = Build;
            return;
        }
        _right.Children.Add(Ui.Title($"{s.Icon} Step"));
        _right.Children.Add(new TextBlock { Text = s.Text, TextWrapping = TextWrapping.Wrap, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 6) });
        _right.Children.Add(new TextBox { Text = s.Line, IsReadOnly = true, FontFamily = new FontFamily("Consolas"), FontSize = 11, TextWrapping = TextWrapping.Wrap });
        _right.Children.Add(Ui.Hint("Steps other than texts are changed in the code (line " + (s.LineIndex + 1) + "), or rebuild the script from blocks.", 360));
        _right.Children.Add(Ui.Row(Ui.Button("</> Show in the code", () => GoToLine(s.LineIndex))));
    }

    private List<string> UsersOf(string messageId)
    {
        var result = new List<string>();
        string current = "";
        foreach (var l in _script.Text.Replace("\r\n", "\n").Split('\n'))
        {
            var m = System.Text.RegularExpressions.Regex.Match(l, @"^(\w+):\s*$");
            if (m.Success) { current = m.Groups[1].Value; continue; }
            if (System.Text.RegularExpressions.Regex.IsMatch(l, @"\b" + System.Text.RegularExpressions.Regex.Escape(messageId) + @"\b")) result.Add(current);
        }
        return result;
    }

    private void RebuildWithBlocks(EventItemVm e, string label)
    {
        var msgs = MessageLookup();
        var conv = PlatinumScriptConverter.Convert(_script.Text, label, id => msgs.GetValueOrDefault(id));
        int texts = conv.Blocks.Count(b => b.Kind is ScriptBlockKind.Message or ScriptBlockKind.Ask);
        int branches = conv.Blocks.Count(b => b.Opens);
        var r = AppDialog.Show(Application.Current.MainWindow,
            $"{label} is replaced by a script made from blocks.\n\n" +
            $"• {conv.Understood} command(s) become blocks – {texts} text(s), {branches} branch(es) (flags, Yes/No, variables, items), items, battles, movements …\n" +
            (conv.CodeLines > 0
                ? $"• {conv.CodeLines} line(s) no block knows stay as \"⚙ Script code\" blocks, unchanged (e.g. {string.Join(", ", conv.CodeExamples.Take(3).Select(x => x.Split(' ')[0]))}). They still work: the script's helper labels stay in the file.\n"
                : "• Every line became a block.\n") +
            "• Texts become new texts of the blocks; the original texts stay in the bank.\n" +
            "• The event keeps its script name, so everything that starts it still works.\n\nContinue? Nothing is written to disk before you press Save.",
            Title, MessageBoxButton.OKCancel, MessageBoxImage.Question);
        if (r != MessageBoxResult.OK) return;
        var blocks = conv.Blocks;
        if (blocks.Count == 0) blocks.Add(PlatinumBlockEditor.NewBlock(ScriptBlockKind.Message, _lists!));
        GiveBlocks(e, blocks, label);
    }

    // ------------------------------------------------------------------ texts

    private void SyncTextRows()
    {
        if (_texts == null) return;
        var known = _textRows.ToDictionary(r => r.Node, r => r, ReferenceEqualityComparer.Instance);
        foreach (var m in PlatinumMapScripts.Messages(_texts).OfType<JsonObject>())
        {
            if (known.TryGetValue(m, out var row)) row.Refresh();
            else _textRows.Add(new TextRowVm(m, Touch));
        }
        FilterTexts();
    }

    private void FilterTexts()
    {
        var f = _textFilter.Text.Trim();
        var keep = _textList.SelectedItem;
        _textList.ItemsSource = f.Length == 0 ? _textRows.ToList()
            : _textRows.Where(r => r.Preview.Contains(f, StringComparison.OrdinalIgnoreCase) || r.Id.Contains(f, StringComparison.OrdinalIgnoreCase)).ToList();
        if (keep != null) _textList.SelectedItem = keep;
        if (_tabs.Items.Count > 1) ((TabItem)_tabs.Items[1]).Header = $"All texts ({_textRows.Count})";
    }

    private void FillMugshotChoices()
    {
        var keep = _mugshotPreview.SelectedItem as string;
        var items = new List<string> { "(no mugshot)" };
        if (_ctx.Root is { } root && PlatinumMugshots.IsInstalled(root))
            foreach (var m in PlatinumMugshots.List(root))
            {
                items.Add(m.Constant);
                if (PlatinumMugshots.SupportsPictures(root)) items.Add(m.Constant + " (left)");
            }
        _mugshotPreview.ItemsSource = items;
        _mugshotPreview.SelectedItem = items.Contains(keep ?? "") ? keep : items[0];
    }

    /// <param name="textId">The text shown: offers to put the chosen mugshot into the script right before it.</param>
    /// <param name="lineIndex">Only before this line of the script file (a step of an event), else before every use.</param>
    private void MugshotChooser(Panel target, string? textId = null, int? lineIndex = null)
    {
        if (_mugshotPreview.Items.Count <= 1) return;
        Ui.Detach(_mugshotPreview);
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
        row.Children.Add(new TextBlock { Text = "Preview with mugshot  ", VerticalAlignment = VerticalAlignment.Center });
        row.Children.Add(_mugshotPreview);
        target.Children.Add(row);
        if (textId == null) return;
        var result = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0), MaxWidth = 380, HorizontalAlignment = HorizontalAlignment.Left };
        var add = Ui.Button(lineIndex != null ? "➕ Show it before this text in the script" : "➕ Show it before this text in the scripts",
            () => result.Text = AddMugshotToScript(textId, lineIndex),
            "Writes ShowMugshot for the mugshot chosen above (left = mirrored) on the line before the text in the script file. A mugshot command right above is replaced. Save writes it to disk.");
        void Update() => add.IsEnabled = _mugshotPreview.SelectedIndex > 0 && !_script.IsReadOnly;
        _mugshotAddUpdate = Update;
        Update();
        target.Children.Add(Ui.Row(add));
        target.Children.Add(result);
    }

    private Action? _mugshotAddUpdate;

    private string AddMugshotToScript(string textId, int? lineIndex)
    {
        var choice = _mugshotPreview.SelectedItem as string ?? "";
        if (_mugshotPreview.SelectedIndex <= 0) return "Choose a mugshot above first.";
        bool left = choice.EndsWith(" (left)", StringComparison.Ordinal);
        var mugshot = left ? choice[..^7] : choice;
        var r = PlatinumMapScripts.AddMugshotBefore(_script.Text, textId, mugshot, left, lineIndex);
        if (r.Added + r.Replaced == 0)
            return r.SkippedBlockScripts > 0
                ? "✗ This text belongs to a script made from blocks – add the 🖼 Mugshot block there (before the message block)."
                : "✗ No line of this map's script file shows this text (a shared script or the game's code shows it).";
        _script.Text = r.Script;
        Touch();
        // The steps of the event changed (a new line): show them again.
        if (lineIndex != null && _selected is { } sel)
        {
            var msg = $"✓ {mugshot}{(left ? " (left)" : "")} is shown before this text now (a 🖼 step above it). Save writes it to disk.";
            Application.Current.Dispatcher.BeginInvoke(() => { ShowEvent(sel); ShowRightHint(msg); });
            return msg;
        }
        var where = r.Added + r.Replaced == 1 ? "before the text" : $"before {r.Added + r.Replaced} uses of the text";
        return $"✓ {mugshot}{(left ? " (left)" : "")} shown {where}" + (r.Replaced > 0 ? $" ({r.Replaced} mugshot line(s) replaced)" : "") +
               (r.SkippedBlockScripts > 0 ? $"; {r.SkippedBlockScripts} use(s) in block scripts left alone" : "") + ". Save writes it to disk.";
    }

    private RgbaImage? PictureImage(string? c)
    {
        if (_ctx.Root is not { } root || c == null) return null;
        if (c.StartsWith("FULLSCREEN_", StringComparison.Ordinal))
        {
            var f = PlatinumMugshots.ListFullscreen(root).FirstOrDefault(x => x.Constant == c);
            return f != null && File.Exists(ProjectPaths.Combine(root, f.PngRel)) ? Png.Load(ProjectPaths.Combine(root, f.PngRel)).Rgba : null;
        }
        if (!c.StartsWith("PICTURE_", StringComparison.Ordinal)) return null;
        var p = PlatinumMugshots.ListPictures(root).FirstOrDefault(x => x.Constant == c);
        if (p == null || !File.Exists(ProjectPaths.Combine(root, p.PngRel))) return null;
        var png = Png.Load(ProjectPaths.Combine(root, p.PngRel));
        var img = png.Rgba.Clone();
        if (png.Indices != null) for (int i = 0; i < img.Pixels.Length; i++) if (png.Indices[i] == 0) img.Pixels[i] = Rgba32.Transparent;
        return img;
    }

    private RgbaImage? MugshotImage(string? c)
    {
        if (_ctx.Root is not { } root || c == null || !c.StartsWith("MUGSHOT_", StringComparison.Ordinal)) return null;
        var m = PlatinumMugshots.List(root).FirstOrDefault(x => x.Constant == c);
        if (m == null || !File.Exists(ProjectPaths.Combine(root, m.PngRel))) return null;
        var png = Png.Load(ProjectPaths.Combine(root, m.PngRel));
        var img = png.Rgba.Clone();
        if (png.Indices != null) for (int i = 0; i < img.Pixels.Length; i++) if (png.Indices[i] == 0) img.Pixels[i] = Rgba32.Transparent;
        return img;
    }

    /// <summary>The writer for a text of the bank: changes go straight into the bank (saved with "Save").</summary>
    private void WriterFor(Panel target, TextRowVm row, string label, Action? changed = null)
    {
        var old = PlatinumMapScripts.MessageText(row.Node);
        PlatinumBlockEditor.Writer(target, label, PlatinumDialogText.ToNatural(old), natural =>
        {
            var current = PlatinumMapScripts.MessageText(row.Node);
            var real = PlatinumDialogText.FromNatural(natural, current.EndsWith('\r'));
            if (real == current) return;
            PlatinumMapScripts.SetMessageText(row.Node, real);
            row.Refresh();
            Touch();
            changed?.Invoke();
        }, _font, () =>
        {
            var choice = _mugshotPreview.SelectedItem as string ?? "";
            bool left = choice.EndsWith(" (left)", StringComparison.Ordinal);
            try { return (MugshotImage(left ? choice[..^7] : choice), left, null); }
            catch (Exception ex) when (ex is IOException or PngFormatException) { return (null, false, null); }
        },
        real => _text?.Check(JsonText.ToEscapes(real), 0, true, true).Error);
    }

    private void ShowTextEditor()
    {
        _textEditor.Children.Clear();
        if (_textList.SelectedItem is not TextRowVm row)
        {
            _textEditor.Children.Add(Ui.Hint("Choose a text on the left. Every text of the map is listed – also those that no event of this map uses (e.g. texts of shared scripts).", 600));
            return;
        }
        _textEditor.Children.Add(new TextBlock { Text = row.Id, FontFamily = new FontFamily("Consolas"), FontSize = 12, FontWeight = FontWeights.SemiBold });
        var users = UsersOf(row.Id).Distinct().ToList();
        _textEditor.Children.Add(Ui.Hint(users.Count == 0 ? "No script of this map uses this text (maybe a shared script or the game's code)." : "Used by: " + string.Join(", ", users), 700));
        var textIndex = _textRows.IndexOf(row);
        _textEditor.Children.Add(Ui.Row(Ui.Button("📄 Show in the code", () => ShowTextInCode(row.Id, textIndex), "Opens the script file at the line that shows this text, with the text's id highlighted (F3 = next use).")));
        WriterFor(_textEditor, row, "Text – Enter = next line, an empty line = next message box");
        MugshotChooser(_textEditor, row.Id);
    }

    private void NewText()
    {
        if (_texts == null) { ProjectWriter.Warn(Title, "This map has no text bank."); return; }
        var dlg = new InputDialog("New text", "Name of the text (becomes its id, e.g. OldManGreeting). You write the text itself on the right afterwards.", ("Name", ""));
        if (dlg.ShowDialog() != true) return;
        try
        {
            var id = $"{Prefix}_Text_{PlatinumMapScripts.PascalName(dlg.Values[0])}";
            PlatinumMapScripts.AddMessage(_texts, id, "");
            SyncTextRows();
            _textFilter.Text = "";
            _textList.SelectedItem = _textRows.FirstOrDefault(r => r.Id == id);
            _textList.ScrollIntoView(_textList.SelectedItem);
            Touch();
        }
        catch (InvalidDataException ex) { ProjectWriter.Warn(Title, ex.Message); }
    }

    // ------------------------------------------------------------------ new events

    private (int X, int Z) FreeSpot()
    {
        if (_selected != null)
        {
            var r = new EventRowVm(_selected.Node, () => { });
            return (r.X + 1, r.Z);
        }
        return (_area.X * PlatinumLandData.Size + _area.W * PlatinumLandData.Size / 2, _area.Y * PlatinumLandData.Size + _area.H * PlatinumLandData.Size / 2);
    }

    private bool CanAdd()
    {
        if (_map != null && _events != null && _texts != null && _scriptOriginal != null && _dialect != null && _lists != null) return true;
        ProjectWriter.Warn(Title, _map == null ? "Choose a map first." : "This map needs an events file, a script file and a text bank.");
        return false;
    }

    /// <summary>Adds the event with a script made from <paramref name="blocks"/> and selects it.</summary>
    private void AddWithBlocks(string baseName, ScriptStart start, List<ScriptBlock> blocks, Func<string, int, JsonObject> addEvent)
    {
        try
        {
            var name = PlatinumMapScripts.PascalName(baseName);
            if (name.Length == 0 || char.IsDigit(name[0])) throw new InvalidDataException("The name must start with a letter.");
            var label = $"{Prefix}_{name}";
            for (int i = 2; PlatinumScriptFlow.HasLabel(_script.Text, label); i++) label = $"{Prefix}_{name}{i}";
            var textPrefix = PlatinumScriptBlocks.FreeTextPrefix(_texts!, $"{Prefix}_Text_{label[(Prefix.Length + 1)..]}");
            var built = PlatinumScriptBlocks.Build(label, textPrefix, blocks, _dialect!, start);
            var (script, number) = PlatinumScriptBlocks.WriteScript(_script.Text, label, textPrefix, blocks, built);
            PlatinumScriptBlocks.WriteMessages(_texts!, textPrefix, built.Messages);
            var node = addEvent(label, number);
            _script.Text = script;
            _entries.ItemsSource = PlatinumMapScripts.Entries(script);
            SyncTextRows();
            Touch();
            _propsOpen = true;
            _kindFilter.SelectedIndex = 0;
            FillEvents(node);
        }
        catch (InvalidDataException ex) { ProjectWriter.Warn(Title, ex.Message); }
    }

    private void NewPerson()
    {
        if (!CanAdd()) return;
        var dlg = new InputDialog("New person", "Name of the person (e.g. Old Man, Rival Mom) – used for the script and the id. Looks, position and what the person says are changed afterwards.", ("Name", "Old Man"));
        if (dlg.ShowDialog() != true) return;
        var (x, z) = FreeSpot();
        var gfx = _ctx.Platinum!.ConstantList("object_events_gfx");
        var look = gfx.FirstOrDefault(g => g == "OBJ_EVENT_GFX_OLD_MAN") ?? gfx.FirstOrDefault(g => g.Contains("YOUNGSTER", StringComparison.Ordinal)) ?? gfx.FirstOrDefault() ?? "0";
        var name = dlg.Values[0];
        AddWithBlocks(name, ScriptStart.Person, [PlatinumBlockEditor.NewBlock(ScriptBlockKind.Message, _lists!)],
            (label, number) => PlatinumMapScripts.AddObjectEvent(_events!, PlatinumMapScripts.PascalName(name), look, "MOVEMENT_TYPE_LOOK_SOUTH", 1, 0, x, z, label, number));
    }

    private void NewSign()
    {
        if (!CanAdd()) return;
        var (x, z) = FreeSpot();
        var msg = PlatinumBlockEditor.NewBlock(ScriptBlockKind.Message, _lists!);
        msg.Text = "ROUTE 201\nSandgem Town ← → Twinleaf Town";
        AddWithBlocks("Sign", ScriptStart.Sign, [msg], (label, number) => PlatinumMapScripts.AddSign(_events!, x, z, label, number));
    }

    private void NewTrigger()
    {
        if (!CanAdd()) return;
        var (x, z) = FreeSpot();
        var var = _lists!.Vars.FirstOrDefault(v => v.StartsWith("VAR_UNUSED_", StringComparison.Ordinal)) ?? _lists.Vars.FirstOrDefault() ?? "VAR_MAP_LOCAL_0x00";
        var msg = PlatinumBlockEditor.NewBlock(ScriptBlockKind.Message, _lists);
        msg.Text = "Wait! Don't go out there alone!";
        var stop = new ScriptBlock { Kind = ScriptBlockKind.SetVar, Constant = var, Number = 1 };
        AddWithBlocks("Trigger", ScriptStart.Step, [msg, stop], (label, number) => PlatinumMapScripts.AddTrigger(_events!, x, z, var, 0, label, number));
        ProjectWriter.Warn(Title, $"The trigger runs while {var} = 0; its last block sets it to 1, so it runs once. Change the variable if {var} is used elsewhere (Flags & Variables module).");
    }

    private void DuplicateEvent()
    {
        if (_selected == null || _events?.Root[_selected.List] is not JsonArray arr) { ProjectWriter.Warn(Title, "Select an event first."); return; }
        var copy = (JsonObject)JsonText.Parse(_selected.Node.ToJsonString())!;
        if (copy["x"] is JsonValue xv && xv.TryGetValue<System.Text.Json.JsonElement>(out var xe) && xe.TryGetInt32(out var x)) copy["x"] = x + 1;
        if (copy["id"] is JsonValue idv && idv.GetValue<string>() is { } id && id.Length > 0)
        {
            var used = arr.OfType<JsonObject>().Select(o => o["id"]?.GetValue<string>()).ToHashSet();
            var baseId = id.TrimEnd('0', '1', '2', '3', '4', '5', '6', '7', '8', '9').TrimEnd('_');
            var nid = baseId;
            for (int i = 2; used.Contains(nid); i++) nid = $"{baseId}_{i}";
            copy["id"] = nid;
        }
        arr.Add(copy);
        Touch();
        FillEvents(copy);
    }

    private void RemoveEvent()
    {
        if (_selected == null || _events?.Root[_selected.List] is not JsonArray arr) return;
        var r = AppDialog.Show(Application.Current.MainWindow,
            $"Remove \"{_selected.Title}\" from the map?" + (_selected.List == "warp_events" ? "\n\nWarps of other maps lead here by number – removing a warp changes the numbers of the warps after it." : ""),
            Title, MessageBoxButton.OKCancel, MessageBoxImage.Question);
        if (r != MessageBoxResult.OK) return;
        arr.Remove(_selected.Node);
        _selected = null;
        Touch();
        FillEvents();
        _center.Content = Ui.Hint("Removed. Its script stays in the script file (other events may use it).");
        ShowRightHint();
    }

    // ------------------------------------------------------------------ map with markers

    private async void RenderMap()
    {
        _mapImage = null;
        _canvas.Children.Clear();
        if (_map == null || _mapData == null) return;
        var h = _map.Header;
        var maps = _mapData;
        try
        {
            var img = await Task.Run(() =>
            {
                lock (maps)
                {
                    var matrix = h.Matrix != null ? maps.Matrix(h.Matrix) : null;
                    if (matrix == null || maps.Area(h, matrix) is not { } area) return ((RgbaImage?)null, default((int, int, int, int)));
                    var tex = maps.RenderTextured(matrix, area, Tile, (_, _) => h.AreaData);
                    maps.OverlayCollision(tex, matrix, area, Tile);
                    return (tex, area);
                }
            });
            if (_map?.Header != h || img.Item1 == null) return;
            _area = img.Item2;
            _mapImage = Imaging.ToBitmap(img.Item1);
            _canvas.Width = img.Item1.Width;
            _canvas.Height = img.Item1.Height;
            DrawMarkers();
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or IndexOutOfRangeException or ArgumentOutOfRangeException) { }
    }

    private void DrawMarkers()
    {
        if (_mapImage == null || _events == null) return;
        _canvas.Children.Clear();
        _selectedMarker = null;
        var image = new Image { Source = _mapImage, Stretch = Stretch.None };
        RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.NearestNeighbor);
        image.MouseLeftButtonUp += (_, e) => MapClicked(e.GetPosition(_canvas));
        _canvas.Children.Add(image);
        int ox = _area.X * PlatinumLandData.Size, oz = _area.Y * PlatinumLandData.Size;
        var lists = PlatinumMapScripts.EventLists;
        for (int k = 0; k < lists.Length; k++)
        {
            if (_events.Root[lists[k]] is not JsonArray arr) continue;
            foreach (var o in arr.OfType<JsonObject>())
            {
                var row = new EventRowVm(o, () => { });
                bool isSelected = _selected != null && ReferenceEquals(_selected.Node, o);
                if (k == 0 && SpriteOf(row["graphics_id"], PlatinumMaps.Direction(row["initial_dir"])) is { } spr)
                {
                    var pic = new Image { Source = spr, Width = 2 * Tile, Height = 2 * Tile, Stretch = Stretch.Fill, Cursor = Cursors.Hand, ToolTip = $"{row["id"]} · {row.X},{row.Z}" };
                    RenderOptions.SetBitmapScalingMode(pic, BitmapScalingMode.NearestNeighbor);
                    if (isSelected) { pic.Effect = new System.Windows.Media.Effects.DropShadowEffect { Color = Colors.Yellow, BlurRadius = 8, ShadowDepth = 0, Opacity = 1 }; _selectedMarker = pic; }
                    pic.MouseLeftButtonUp += (_, e) => { e.Handled = true; SelectNode(lists[0], o); };
                    Canvas.SetLeft(pic, (row.X - ox) * Tile - Tile / 2.0);
                    Canvas.SetTop(pic, (row.Z - oz) * Tile - Tile);
                    _canvas.Children.Add(pic);
                    continue;
                }
                var shape = new Ellipse
                {
                    Width = Tile + 2, Height = Tile + 2,
                    Fill = new SolidColorBrush(k switch { 0 => Color.FromRgb(230, 50, 50), 1 => Color.FromRgb(250, 190, 30), 2 => Color.FromRgb(160, 60, 230), _ => Color.FromRgb(40, 200, 255) }),
                    Stroke = isSelected ? Brushes.Black : Brushes.White, StrokeThickness = isSelected ? 3 : 1,
                    ToolTip = $"{row["id"]}{row["graphics_id"]}{row["dest_header_id"]} · {row.X},{row.Z}", Cursor = Cursors.Hand,
                };
                var list = lists[k];
                if (isSelected) _selectedMarker = shape;
                shape.MouseLeftButtonUp += (_, e) => { e.Handled = true; SelectNode(list, o); };
                Canvas.SetLeft(shape, (row.X - ox) * Tile - 1);
                Canvas.SetTop(shape, (row.Z - oz) * Tile - 1);
                _canvas.Children.Add(shape);
            }
        }
    }

    private void SelectNode(string list, JsonObject o)
    {
        if (_kindFilter.SelectedIndex != 0 && Kinds[_kindFilter.SelectedIndex].Key != list) _kindFilter.SelectedIndex = 0;
        var item = (_eventList.ItemsSource as List<EventItemVm>)?.FirstOrDefault(i => ReferenceEquals(i.Node, o));
        if (item != null) { _eventList.SelectedItem = item; _eventList.ScrollIntoView(item); }
    }

    private void MapClicked(Point p)
    {
        if (_placeOnClick.IsChecked != true || _selected == null) return;
        var row = new EventRowVm(_selected.Node, () => { });
        int x = _area.X * PlatinumLandData.Size + (int)(p.X / Tile), z = _area.Y * PlatinumLandData.Size + (int)(p.Y / Tile);
        row["x"] = x.ToString();
        row["z"] = z.ToString();
        Touch();
        var node = _selected.Node;
        FillEvents(node);
    }

    // ------------------------------------------------------------------ save

    private bool Save()
    {
        if (_map == null) return false;
        var root = _ctx.Root!;
        var changes = new List<PlatinumFileChange>();
        try
        {
            void Check(string? rel, string? original)
            {
                if (rel == null || original == null) return;
                var current = File.ReadAllText(ProjectPaths.Combine(root, rel), Encoding.UTF8);
                if (current != original) throw new InvalidDataException($"{rel} was changed outside PokéPat. Discard your changes to reload it.");
            }
            var utf8 = new UTF8Encoding(false);
            if (_events != null && _events.IsChanged) { Check(_events.Path, _events.OriginalText); changes.Add(new(_events.Path, utf8.GetBytes(_events.Serialize()), "events")); }
            if (_texts != null && _texts.IsChanged)
            {
                Check(_texts.Path, _texts.OriginalText);
                if (_text != null)
                    foreach (var row in _textRows)
                        if (_text.Check(row.Text, 0, true, true).Error is { } err) throw new InvalidDataException($"{row.Id}: {err}");
                changes.Add(new(_texts.Path, utf8.GetBytes(_texts.Serialize()), "texts"));
            }
            if (_map.ScriptPath != null && _scriptOriginal != null && _script.Text != _scriptOriginal)
            {
                Check(_map.ScriptPath, _scriptOriginal);
                changes.Add(new(_map.ScriptPath, utf8.GetBytes(_script.Text.Replace("\r\n", "\n")), "script file"));
            }
        }
        catch (InvalidDataException ex) { ProjectWriter.Warn(Title, ex.Message); return false; }
        if (changes.Count == 0) { _dirty = false; UpdateStatus(); return true; }
        _dirty = false; // the reload after writing must pick up the new files
        if (!PlatinumModules.Write(_ctx, "Events & Dialogs", changes,
                "The JSON files keep their format; only edited events and texts change.",
                "Scripts made from blocks carry a comment line \"/* PokePat blocks: … */\" so PokéPat can open them as blocks again – it does not change the ROM.",
                "Build the ROM afterwards and try the events in-game."))
        {
            _dirty = true;
            return false;
        }
        return true;
    }
}
