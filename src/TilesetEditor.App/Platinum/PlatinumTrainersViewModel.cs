using System.Collections.ObjectModel;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using TilesetEditor.App.Infrastructure;
using TilesetEditor.App.Shell;
using TilesetEditor.Core.Imaging;
using TilesetEditor.Core.Platinum;

namespace TilesetEditor.App.Platinum;

/// <summary>One Pokémon of the party grid; moves as four columns, MOVE_NONE kept as written.</summary>
public sealed class PartyRowVm : ObservableObject
{
    private string _species = "SPECIES_BIDOOF", _level = "5", _item = "", _iv = "0", _form = "0";
    private string _m1 = "", _m2 = "", _m3 = "", _m4 = "";
    private bool _movesWereNull = true;
    private int _ballSeal;

    public string Species { get => _species; set => Set(ref _species, (value ?? "").Trim().ToUpperInvariant()); }
    public string Level { get => _level; set => Set(ref _level, (value ?? "").Trim()); }
    public string Item { get => _item; set => Set(ref _item, (value ?? "").Trim().ToUpperInvariant()); }
    public string Move1 { get => _m1; set => Set(ref _m1, (value ?? "").Trim().ToUpperInvariant()); }
    public string Move2 { get => _m2; set => Set(ref _m2, (value ?? "").Trim().ToUpperInvariant()); }
    public string Move3 { get => _m3; set => Set(ref _m3, (value ?? "").Trim().ToUpperInvariant()); }
    public string Move4 { get => _m4; set => Set(ref _m4, (value ?? "").Trim().ToUpperInvariant()); }
    public string IvScale { get => _iv; set => Set(ref _iv, (value ?? "").Trim()); }
    public string Form { get => _form; set => Set(ref _form, (value ?? "").Trim()); }

    public static PartyRowVm From(PlatinumTrainerMon m)
    {
        var moves = m.Moves ?? [];
        return new PartyRowVm
        {
            _species = m.Species, _level = m.Level.ToString(), _item = m.Item ?? "", _iv = m.IvScale.ToString(), _form = m.Form.ToString(),
            _m1 = moves.ElementAtOrDefault(0) ?? "", _m2 = moves.ElementAtOrDefault(1) ?? "", _m3 = moves.ElementAtOrDefault(2) ?? "", _m4 = moves.ElementAtOrDefault(3) ?? "",
            _movesWereNull = m.Moves == null, _ballSeal = m.BallSeal,
        };
    }

    /// <summary>Back to the file model; null on a value that is not a number.</summary>
    public PlatinumTrainerMon? ToMon(out string? problem)
    {
        problem = null;
        if (!int.TryParse(Level, out var lvl)) { problem = $"{Species}: \"{Level}\" is not a level."; return null; }
        if (!int.TryParse(IvScale, out var iv)) { problem = $"{Species}: IV scale \"{IvScale}\" is not a number."; return null; }
        if (!int.TryParse(Form, out var form)) { problem = $"{Species}: form \"{Form}\" is not a number."; return null; }
        var moves = new[] { Move1, Move2, Move3, Move4 }.ToList();
        while (moves.Count > 0 && moves[^1].Length == 0) moves.RemoveAt(moves.Count - 1);
        List<string>? list = moves.Count == 0 && _movesWereNull ? null : moves.Select(x => x.Length == 0 ? "MOVE_NONE" : x).ToList();
        return new PlatinumTrainerMon { Species = Species, Level = lvl, Item = Item.Length == 0 ? null : Item, Moves = list, IvScale = iv, Form = form, BallSeal = _ballSeal };
    }
}

/// <summary>A message of the trainer (before battle, defeat …) with escapes (\n, \r) for editing.</summary>
public sealed class TrainerMessageVm(string type, string text) : ObservableObject
{
    public string Type { get; } = type;
    public string Label => PlatinumNames.Pretty(Type, "TRMSG_");
    private string _text = JsonText.ToEscapes(text);
    public string Text { get => _text; set => Set(ref _text, value ?? ""); }
}

/// <summary>
/// Trainers of a Platinum project (res/trainers/data/*.json): name, class (with picture), double battle, items, team and texts.
/// The file is rewritten in its own format, so only the edited values change.
/// </summary>
public sealed class PlatinumTrainersViewModel : ObservableObject, IToolkitModule
{
    private readonly DeferredLoad _reload;
    private readonly ProjectContext _ctx;
    private readonly ListBox _list = new() { BorderThickness = new Thickness(0) };
    private readonly TextBox _filter = new();
    private readonly ContentControl _detail = new() { Margin = new Thickness(16, 0, 0, 0) };
    private List<PlatinumTrainer> _all = [];
    private PlatinumTrainer? _current;
    private bool _dirty, _loadingDetail;

    public PlatinumTrainersViewModel(ProjectContext ctx)
    {
        _ctx = ctx;
        _reload = new DeferredLoad(() => _dirty ? null : LoadAsync(), prefetch: true);
        _ctx.PlatinumChanged += (_, _) => _reload.Request();
        Party.CollectionChanged += (_, _) => { Touch(); if (!_loadingDetail) BuildPartyCards(); };
        Messages.CollectionChanged += (_, _) => Touch();
        View = BuildView();
        _reload.Attach(View);
    }

    public string Title => "Trainers";
    public string Description => "Teams, classes, texts";
    public bool IsAvailable => true;
    public override string ToString() => Title;
    public FrameworkElement View { get; }
    public bool HasUnsavedChanges => _dirty;
    public string? UnsavedChangesDescription => _dirty ? $"Trainers: {_current?.Name} ({_current?.Id}) not saved yet." : null;

    public ObservableCollection<PartyRowVm> Party { get; } = [];
    public ObservableCollection<TrainerMessageVm> Messages { get; } = [];

    private string _name = "", _class = "", _items = "", _aiFlags = "";
    private bool _double;
    public string TrainerName { get => _name; set { if (Set(ref _name, value)) Touch(); } }
    public string TrainerClass { get => _class; set { if (Set(ref _class, (value ?? "").Trim().ToUpperInvariant())) { Touch(); UpdatePicture(); } } }
    public bool DoubleBattle { get => _double; set { if (Set(ref _double, value)) Touch(); } }
    /// <summary>Items the trainer uses in battle, comma-separated.</summary>
    public string Items { get => _items; set { if (Set(ref _items, value)) Touch(); } }
    public string AiFlags { get => _aiFlags; set { if (Set(ref _aiFlags, value)) Touch(); } }

    private readonly Image _picture = Ui.Picture();

    private void Touch()
    {
        if (_loadingDetail) return;
        _dirty = true;
        OnPropertyChanged(nameof(HasUnsavedChanges));
    }

    private FrameworkElement BuildView()
    {
        var grid = new Grid { Margin = new Thickness(16) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(300) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        VirtualizingPanel.SetIsVirtualizing(_list, true);
        _list.SelectionChanged += (_, _) => { if (_list.SelectedItem is PlatinumTrainer t && t != _current) Select(t); };
        _filter.TextChanged += (_, _) => ApplyFilter();
        var left = Ui.FilteredList(_list, _filter, "Filter trainers");
        var classButtons = Ui.Row(
            Ui.Button("+ New trainer…", NewTrainer, "A new trainer, copied from the selected one (team, texts) – with its own defeated flag."),
            Ui.Button("+ New trainer class…", NewClass, "A new class with its own name and battle picture (no existing class changes)."));
        DockPanel.SetDock(classButtons, Dock.Bottom);
        left.Children.Insert(1, classButtons);
        grid.Children.Add(left);
        var scroll = new ScrollViewer { Content = _detail, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        Grid.SetColumn(scroll, 1);
        grid.Children.Add(scroll);
        _detail.Content = Ui.Hint("Choose a trainer on the left.");
        return grid;
    }

    private async Task LoadAsync()
    {
        var p = _ctx.Platinum;
        if (p == null) { _all = []; ApplyFilter(); return; }
        try
        {
            var list = await Task.Run(() => PlatinumTrainers.LoadAll(p.Root));
            if (_ctx.Platinum != p) return;
            _all = list;
        }
        catch (Exception ex) when (ex is IOException or FormatException or System.Text.Json.JsonException)
        {
            _all = [];
            _detail.Content = Ui.Hint("The trainers could not be read: " + ex.Message);
        }
        var keep = _current?.File;
        _current = null;
        ApplyFilter();
        if (keep != null && _all.FirstOrDefault(t => t.File == keep) is { } again) _list.SelectedItem = again;
    }

    private void ApplyFilter()
    {
        var f = _filter.Text.Trim();
        _list.ItemsSource = f.Length == 0 ? _all : _all.Where(t =>
            t.Name.Contains(f, StringComparison.OrdinalIgnoreCase) || t.Class.Contains(f, StringComparison.OrdinalIgnoreCase) ||
            t.Id.Contains(f, StringComparison.OrdinalIgnoreCase) || t.Party.Any(m => m.Species.Contains(f, StringComparison.OrdinalIgnoreCase))).ToList();
        if (_current != null) _list.SelectedItem = _current;
    }

    private void Select(PlatinumTrainer t)
    {
        if (_dirty && _current != null)
        {
            var r = AppDialog.Show(Application.Current.MainWindow, $"{_current.Name} ({_current.Id}) was changed. Save it first?", "Trainers",
                MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
            if (r == MessageBoxResult.Cancel || (r == MessageBoxResult.Yes && !Save())) { _list.SelectedItem = _current; return; }
        }
        _dirty = false;
        _current = t;
        _loadingDetail = true;
        TrainerName = t.Name;
        TrainerClass = t.Class;
        DoubleBattle = t.DoubleBattle;
        Items = string.Join(", ", t.Items);
        AiFlags = string.Join(", ", t.AiFlags);
        Party.Clear();
        foreach (var m in t.Party) Party.Add(TrackRow(PartyRowVm.From(m)));
        Messages.Clear();
        foreach (var (type, text) in t.Messages) { var vm = new TrainerMessageVm(type, text); vm.PropertyChanged += (_, _) => Touch(); Messages.Add(vm); }
        _loadingDetail = false;
        _detail.Content = BuildDetail();
        UpdatePicture();
    }

    private PartyRowVm TrackRow(PartyRowVm row)
    {
        row.PropertyChanged += (_, _) => Touch();
        return row;
    }

    private void UpdatePicture()
    {
        var root = _ctx.Root;
        _picture.Source = null;
        if (root == null) return;
        var path = PlatinumTrainers.ClassPicture(root, TrainerClass);
        if (path == null) return;
        try
        {
            var png = Png.Load(path);
            var img = png.Rgba.Clone();
            if (png.IsIndexed && png.Indices != null)
                for (int i = 0; i < img.Pixels.Length; i++) if (png.Indices[i] == 0) img.Pixels[i] = Rgba32.Transparent;
            _picture.Source = Imaging.ToBitmap(img);
        }
        catch (Exception ex) when (ex is IOException or PngFormatException) { }
    }

    private FrameworkElement BuildDetail()
    {
        var p = _ctx.Platinum!;
        var t = _current!;
        var panel = new StackPanel { DataContext = this };
        panel.Children.Add(Ui.Title($"{t.Name}  ·  {t.Id}"));

        var head = new Grid();
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(100) });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        // The picture is one Image for all trainers: take it out of the previous trainer's frame first.
        if (_picture.Parent is Border old) old.Child = null;
        var pic = new Border { Child = _picture, Width = 88, Height = 88, Background = Imaging.Checkerboard(8), VerticalAlignment = VerticalAlignment.Top };
        var picColumn = new StackPanel();
        picColumn.Children.Add(pic);
        var replacePic = new Button { Content = "Replace…", FontSize = 11, Margin = new Thickness(0, 4, 0, 0), HorizontalAlignment = HorizontalAlignment.Left,
            ToolTip = "New battle picture for this trainer class (PNG 80×80) – used by every trainer of the class." };
        replacePic.Click += (_, _) => ReplaceClassPicture();
        picColumn.Children.Add(replacePic);
        head.Children.Add(picColumn);
        var fields = new StackPanel();
        Grid.SetColumn(fields, 1);
        fields.Children.Add(new TextBlock { Text = "Name" });
        fields.Children.Add(Ui.Box(nameof(TrainerName), 260));
        fields.Children.Add(new TextBlock { Text = "Trainer class", Margin = new Thickness(0, 4, 0, 0) });
        var cls = new ComboBox { ItemsSource = p.TrainerClasses, IsEditable = true, Width = 320, HorizontalAlignment = HorizontalAlignment.Left };
        cls.SetBinding(ComboBox.TextProperty, new Binding(nameof(TrainerClass)) { UpdateSourceTrigger = UpdateSourceTrigger.LostFocus });
        fields.Children.Add(cls);
        var dbl = new CheckBox { Content = "Double battle", Margin = new Thickness(0, 6, 0, 0) };
        dbl.SetBinding(System.Windows.Controls.Primitives.ToggleButton.IsCheckedProperty, new Binding(nameof(DoubleBattle)));
        fields.Children.Add(dbl);
        fields.Children.Add(new TextBlock { Text = "Items used in battle (comma-separated, at most 4)", Margin = new Thickness(0, 6, 0, 0) });
        fields.Children.Add(Ui.Box(nameof(Items), 420, mono: true));
        fields.Children.Add(new TextBlock { Text = "AI flags (comma-separated)", Margin = new Thickness(0, 4, 0, 0) });
        fields.Children.Add(Ui.Box(nameof(AiFlags), 420, mono: true));
        head.Children.Add(fields);
        panel.Children.Add(Ui.Card(head));

        panel.Children.Add(Ui.Title("Team"));
        _partyPanel = new WrapPanel();
        BuildPartyCards();
        panel.Children.Add(_partyPanel);
        panel.Children.Add(Ui.Row(
            Ui.Button("+ Add Pokémon", () =>
            {
                if (Party.Count >= PlatinumTrainers.MaxParty) { ProjectWriter.Warn("Trainers", $"A team has at most {PlatinumTrainers.MaxParty} Pokémon."); return; }
                var last = Party.LastOrDefault();
                Party.Add(TrackRow(new PartyRowVm { Species = last?.Species ?? "SPECIES_BIDOOF", Level = last?.Level ?? "5", IvScale = last?.IvScale ?? "0" }));
            })));
        panel.Children.Add(Ui.Hint("Empty moves = the moves the Pokémon knows at its level. IVs: 0 = weakest, 255 = all IVs 31. Items: ITEM_… or empty."));

        if (Messages.Count > 0)
        {
            panel.Children.Add(Ui.Title("Texts"));
            foreach (var m in Messages)
            {
                panel.Children.Add(new TextBlock { Text = m.Label, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 6, 0, 2) });
                var box = new TextBox { TextWrapping = TextWrapping.Wrap, AcceptsReturn = false, FontFamily = new FontFamily("Consolas"), MaxWidth = 760, HorizontalAlignment = HorizontalAlignment.Left, MinWidth = 420, DataContext = m };
                box.SetBinding(TextBox.TextProperty, new Binding(nameof(TrainerMessageVm.Text)) { UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged });
                panel.Children.Add(box);
            }
            panel.Children.Add(Ui.Hint("\\n = new line, \\r = next text box (after a key press), \\f = scroll. Other languages in the file stay unchanged."));
        }

        panel.Children.Add(Ui.Row(
            Ui.Button("💾 Save trainer", () => Save()),
            Ui.Button("Discard changes", () => { _dirty = false; var t2 = _current; _current = null; if (t2 != null) Select(t2); }),
            Ui.Button("Show file", () => { if (_ctx.Root != null) Ui.OpenFolder(Core.Project.ProjectPaths.Combine(_ctx.Root, t.File)); })));
        return panel;
    }

    private void NewTrainer()
    {
        var p = _ctx.Platinum;
        if (p == null) return;
        if (_dirty) { ProjectWriter.Warn("New trainer", "Save or discard the changes of the current trainer first."); return; }
        var template = _current ?? _all.FirstOrDefault(t => t.Party.Count > 0);
        if (template == null) { ProjectWriter.Warn("New trainer", "No trainer to copy from."); return; }
        // Name and class; the team and texts are copied from the selected trainer and changed afterwards.
        var name = new TextBox { Text = "Reya", Width = 200, HorizontalAlignment = HorizontalAlignment.Left, MaxLength = 7 };
        var cls = new ComboBox { ItemsSource = p.TrainerClasses, IsEditable = true, IsTextSearchEnabled = true, Text = template.Class, Width = 320, HorizontalAlignment = HorizontalAlignment.Left };
        var panel = new StackPanel { Margin = new Thickness(14), Width = 440 };
        panel.Children.Add(new TextBlock { TextWrapping = TextWrapping.Wrap, Text = $"The new trainer starts as a copy of {template.Name} ({template.Id}) – team, items, AI and texts; change them afterwards. It also gets its own \"defeated\" flag." });
        panel.Children.Add(new TextBlock { Text = "Name (up to 7 characters)", Margin = new Thickness(0, 10, 0, 2) });
        panel.Children.Add(name);
        panel.Children.Add(new TextBlock { Text = "Trainer class", Margin = new Thickness(0, 8, 0, 2) });
        panel.Children.Add(cls);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
        var ok = new Button { Content = "Create", IsDefault = true, MinWidth = 90 };
        buttons.Children.Add(ok);
        buttons.Children.Add(new Button { Content = "Cancel", IsCancel = true, MinWidth = 80 });
        panel.Children.Add(buttons);
        var w = new Window { Title = "New trainer", Content = panel, SizeToContent = SizeToContent.WidthAndHeight, ResizeMode = ResizeMode.NoResize, Owner = Application.Current.MainWindow, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        ok.Click += (_, _) => w.DialogResult = true;
        if (w.ShowDialog() != true) return;
        try
        {
            var root = p.Root;
            var (files, constant) = PlatinumNewTrainer.Plan(root, template, cls.Text.Trim().ToUpperInvariant(), name.Text, n => PlatinumNewTrainer.IsReferenced(root, n));
            if (PlatinumModules.Write(_ctx, "New trainer", files,
                    $"{constant} is the trainer's constant: use it in the event builder (block \"Trainer battle\") or as \"trainer\" of a person.",
                    "The trainer is added after the last trainer, so no other trainer changes its number.", "Build the ROM afterwards."))
                ProjectWriter.Warn("New trainer", $"{constant} was added – select it in the list to change its team.");
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or System.Text.Json.JsonException)
        {
            ProjectWriter.Warn("New trainer", ex.Message);
        }
    }

    private void NewClass()
    {
        var p = _ctx.Platinum;
        if (p == null) return;
        if (_dirty) { ProjectWriter.Warn("New trainer class", "Save or discard the changes of the current trainer first."); return; }
        var dlg = new NewTrainerClassWindow(p.Root, p.TrainerClasses, _current?.Class);
        if (dlg.ShowDialog() != true || dlg.Result is not { } c) return;
        try
        {
            var (files, notes) = PlatinumTrainerClasses.PlanAdd(p.Root, c);
            if (PlatinumModules.Write(_ctx, "New trainer class", files, [.. notes, $"Choose {c.Constant} as class of a trainer afterwards, then build the ROM."]))
                ProjectWriter.Warn("New trainer class", $"{c.Constant} was added. It is now in the class list of every trainer.");
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or PngFormatException or System.Text.Json.JsonException)
        {
            ProjectWriter.Warn("New trainer class", ex.Message);
        }
    }

    private void ReplaceClassPicture()
    {
        var p = _ctx.Platinum;
        if (p == null || TrainerClass.Length == 0) return;
        var dlg = new Microsoft.Win32.OpenFileDialog { Filter = "PNG pictures|*.png", Title = $"New battle picture for {TrainerClass} (80×80)" };
        if (dlg.ShowDialog(Application.Current.MainWindow) != true) return;
        try
        {
            var (file, notes) = PlatinumTrainerClasses.PlanReplacePicture(p.Root, TrainerClass, Png.Load(dlg.FileName).Rgba);
            if (PlatinumModules.Write(_ctx, "Replace class picture", [file], [.. notes, $"Every trainer of {TrainerClass} uses the new picture. Build the ROM afterwards."]))
                UpdatePicture();
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or PngFormatException)
        {
            ProjectWriter.Warn("Replace class picture", ex.Message);
        }
    }

    private WrapPanel? _partyPanel;
    private readonly Dictionary<string, System.Windows.Media.Imaging.BitmapSource?> _sprites = new(StringComparer.Ordinal);

    private System.Windows.Media.Imaging.BitmapSource? Sprite(string species)
    {
        if (_sprites.TryGetValue(species, out var b)) return b;
        b = null;
        try
        {
            if (_ctx.Root != null && PlatinumSpriteSet.Load(_ctx.Root, species).Frame(PlatinumSpritePart.MaleFront, 0, false) is { } img) b = Imaging.ToBitmap(img);
        }
        catch (Exception ex) when (ex is IOException or PngFormatException or InvalidDataException) { }
        return _sprites[species] = b;
    }

    /// <summary>One card per Pokémon of the team, like the Trainers module of Emerald.</summary>
    private void BuildPartyCards()
    {
        var p = _ctx.Platinum;
        if (_partyPanel == null || p == null) return;
        _partyPanel.Children.Clear();
        var items = new List<string> { "" };
        items.AddRange(p.Items);
        var moves = new List<string> { "" };
        moves.AddRange(p.Moves);
        for (int index = 0; index < Party.Count; index++)
        {
            var mon = Party[index];
            var card = new Grid { Width = 360 };
            card.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(92) });
            card.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var left = new StackPanel();
            var sprite = Ui.Picture();
            sprite.Source = Sprite(mon.Species);
            left.Children.Add(new Border { Child = sprite, Width = 84, Height = 84, Background = (Brush)Application.Current.FindResource("PanelBrush") });
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 0) };
            int i = index;
            Button Small(string text, string tip, Action a)
            {
                var b = new Button { Content = text, ToolTip = tip, Style = (Style)Application.Current.FindResource("IconButton") };
                b.Click += (_, _) => a();
                return b;
            }
            buttons.Children.Add(Small("◀", "Move left", () => { if (i > 0) Party.Move(i, i - 1); }));
            buttons.Children.Add(Small("▶", "Move right", () => { if (i < Party.Count - 1) Party.Move(i, i + 1); }));
            buttons.Children.Add(Small("✕", "Remove from the team", () => Party.Remove(mon)));
            left.Children.Add(buttons);
            card.Children.Add(left);

            var fields = new Grid { DataContext = mon };
            Grid.SetColumn(fields, 1);
            fields.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(60) });
            fields.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            int row = 0;
            void Field(string label, FrameworkElement editor)
            {
                fields.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                var l = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center, FontSize = 11, Foreground = (Brush)Application.Current.FindResource("MutedBrush") };
                Grid.SetRow(l, row);
                fields.Children.Add(l);
                Grid.SetRow(editor, row);
                Grid.SetColumn(editor, 1);
                editor.Margin = new Thickness(0, 1, 0, 1);
                fields.Children.Add(editor);
                row++;
            }
            ComboBox Combo(string path, IReadOnlyList<string> list)
            {
                var c = new ComboBox { ItemsSource = list, IsEditable = true, IsTextSearchEnabled = true, FontSize = 11 };
                c.SetBinding(ComboBox.TextProperty, new Binding(path) { UpdateSourceTrigger = UpdateSourceTrigger.LostFocus });
                return c;
            }
            TextBox Box(string path, double width)
            {
                var t = new TextBox { Width = width, HorizontalAlignment = HorizontalAlignment.Left, FontSize = 11 };
                t.SetBinding(TextBox.TextProperty, new Binding(path) { UpdateSourceTrigger = UpdateSourceTrigger.LostFocus });
                return t;
            }
            var species = Combo(nameof(PartyRowVm.Species), p.Species);
            species.FontWeight = FontWeights.SemiBold;
            Field("Pokémon", species);
            var numbers = new StackPanel { Orientation = Orientation.Horizontal };
            numbers.Children.Add(Box(nameof(PartyRowVm.Level), 40));
            numbers.Children.Add(new TextBlock { Text = "  IVs ", VerticalAlignment = VerticalAlignment.Center, FontSize = 11, ToolTip = "0 = weakest, 255 = all IVs 31" });
            numbers.Children.Add(Box(nameof(PartyRowVm.IvScale), 45));
            numbers.Children.Add(new TextBlock { Text = "  Form ", VerticalAlignment = VerticalAlignment.Center, FontSize = 11 });
            numbers.Children.Add(Box(nameof(PartyRowVm.Form), 30));
            Field("Level", numbers);
            Field("Item", Combo(nameof(PartyRowVm.Item), items));
            Field("Move 1", Combo(nameof(PartyRowVm.Move1), moves));
            Field("Move 2", Combo(nameof(PartyRowVm.Move2), moves));
            Field("Move 3", Combo(nameof(PartyRowVm.Move3), moves));
            Field("Move 4", Combo(nameof(PartyRowVm.Move4), moves));
            card.Children.Add(fields);
            mon.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(PartyRowVm.Species)) sprite.Source = Sprite(mon.Species); };

            _partyPanel.Children.Add(new Border
            {
                Child = card, BorderBrush = (Brush)Application.Current.FindResource("BorderBrush"), BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(4), Padding = new Thickness(8), Margin = new Thickness(0, 0, 8, 8),
                Background = (Brush)Application.Current.FindResource("SurfaceBrush"),
            });
        }
    }

    private static List<string> SplitList(string s) =>
        s.Split([',', ';', ' ', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(x => x.ToUpperInvariant()).ToList();

    /// <summary>Checks and writes the current trainer; true if written (or nothing to write).</summary>
    private bool Save()
    {
        var p = _ctx.Platinum;
        var t = _current;
        if (p == null || t == null) return false;
        var edited = new PlatinumTrainer { File = t.File, Raw = t.Raw, OriginalText = t.OriginalText };
        edited.Name = TrainerName.Trim();
        edited.Class = TrainerClass;
        edited.DoubleBattle = DoubleBattle;
        edited.Items = SplitList(Items);
        edited.AiFlags = SplitList(AiFlags);
        foreach (var row in Party)
        {
            var mon = row.ToMon(out var problem);
            if (mon == null) { ProjectWriter.Warn("Save trainer", problem!); return false; }
            edited.Party.Add(mon);
        }
        edited.Messages = Messages.Select(m => (m.Type, JsonText.FromEscapes(m.Text))).ToList();
        var problems = PlatinumTrainers.Check(edited, p).ToList();
        var unknownItems = edited.Items.Where(i => p.Items.Count > 0 && !p.Items.Contains(i)).ToList();
        if (unknownItems.Count > 0) problems.Add("Unknown items: " + string.Join(", ", unknownItems));
        var flags = p.ConstantList("ai_flags");
        var unknownFlags = edited.AiFlags.Where(f => flags.Count > 0 && !flags.Contains(f)).ToList();
        if (unknownFlags.Count > 0) problems.Add("Unknown AI flags: " + string.Join(", ", unknownFlags));
        if (problems.Count > 0) { ProjectWriter.Warn("Save trainer", string.Join("\n", problems)); return false; }

        string current;
        try { current = File.ReadAllText(Core.Project.ProjectPaths.Combine(p.Root, t.File), Encoding.UTF8); }
        catch (IOException ex) { ProjectWriter.Warn("Save trainer", ex.Message); return false; }
        if (current != t.OriginalText) { ProjectWriter.Warn("Save trainer", $"{t.File} was changed outside PokéPat. Discard your changes to reload it."); return false; }
        var text = PlatinumTrainers.Serialize(edited);
        if (text == current) { _dirty = false; return true; }
        _dirty = false; // the reload after writing must pick up the new file
        if (!PlatinumModules.Write(_ctx, "Save trainer", [new PlatinumFileChange(t.File, new UTF8Encoding(false).GetBytes(text), $"{edited.Name}: {edited.Party.Count} Pokémon")],
                "The file is written in its own format (4 spaces, one value per line); only edited values change.",
                "Build the ROM afterwards. The trainer's level, class and team decide the prize money."))
        {
            _dirty = true;
            return false;
        }
        return true;
    }
}
