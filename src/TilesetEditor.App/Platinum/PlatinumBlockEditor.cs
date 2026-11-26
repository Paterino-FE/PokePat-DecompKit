using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using TilesetEditor.App.Infrastructure;
using TilesetEditor.Core.Imaging;
using TilesetEditor.Core.Platinum;

namespace TilesetEditor.App.Platinum;

/// <summary>Lists the building blocks can choose from, and what the dialog preview needs.</summary>
public sealed record ScriptBuilderLists(
    IReadOnlyList<string> Items, IReadOnlyList<string> Species, IReadOnlyList<string> Trainers, IReadOnlyList<string> Flags, IReadOnlyList<string> Vars,
    IReadOnlyList<string> Maps, IReadOnlyList<string> Mugshots, PlatinumFont? Font, Func<string?, RgbaImage?> MugshotImage)
{
    /// <summary>PICTURE_ constants (mod 1.1) and their pictures, for the block "Picture".</summary>
    public IReadOnlyList<string> Pictures { get; init; } = [];
    public Func<string?, RgbaImage?> PictureImage { get; init; } = _ => null;
    /// <summary>FULLSCREEN_ constants (mod 1.2): the block "Picture" can show them on a screen of their own.</summary>
    public IReadOnlyList<string> Fullscreens { get; init; } = [];
    /// <summary>Who can move: the people of the map (LOCALID_…) and the player; the selected event first.</summary>
    public IReadOnlyList<string> People { get; init; } = ["LOCALID_PLAYER"];
}

/// <summary>
/// The parts of the event builder: the block palette, how a block looks in the list, the editor of one block and the
/// dialog writer (Enter = next line, empty line = next box) with its in-game preview.
/// </summary>
public static class PlatinumBlockEditor
{
    public static readonly (string Category, ScriptBlockKind[] Kinds)[] Categories =
    [
        ("Talk", [ScriptBlockKind.Message, ScriptBlockKind.Ask, ScriptBlockKind.Mugshot, ScriptBlockKind.Picture]),
        ("Branch", [ScriptBlockKind.IfFlag, ScriptBlockKind.IfVar, ScriptBlockKind.IfItem, ScriptBlockKind.Else, ScriptBlockKind.EndIf, ScriptBlockKind.EndEvent]),
        ("Give", [ScriptBlockKind.GiveItem, ScriptBlockKind.GivePokemon, ScriptBlockKind.GiveMoney, ScriptBlockKind.HealParty]),
        ("Battle", [ScriptBlockKind.TrainerBattle, ScriptBlockKind.WildBattle]),
        ("Story", [ScriptBlockKind.OnlyOnce, ScriptBlockKind.SetFlag, ScriptBlockKind.ClearFlag, ScriptBlockKind.SetVar]),
        ("Scene", [ScriptBlockKind.Move, ScriptBlockKind.HidePerson, ScriptBlockKind.ShowPerson, ScriptBlockKind.Wait, ScriptBlockKind.Fanfare, ScriptBlockKind.Warp]),
        ("Code", [ScriptBlockKind.Code]),
    ];

    public static string Help(ScriptBlockKind k) => k switch
    {
        ScriptBlockKind.Message => "Text in a message box.",
        ScriptBlockKind.Ask => "A question with the Yes/No menu. The blocks below it run for YES; after \"Otherwise (No)\" come the blocks for NO; \"End of the branch\" closes it. Both are added for you.",
        ScriptBlockKind.IfFlag => "The blocks below run only when the flag is set (or not set). Optionally \"Otherwise\" for the other case; \"End of the branch\" closes it.",
        ScriptBlockKind.IfVar => "The blocks below run only when the variable has this value. Optionally \"Otherwise\"; \"End of the branch\" closes it.",
        ScriptBlockKind.IfItem => "The blocks below run only when the player has the item (at least this many) in the bag.",
        ScriptBlockKind.Else => "Starts the other case of the branch above (for a question: the answer NO). Select a block inside a branch, then press this.",
        ScriptBlockKind.EndIf => "Closes the branch: what follows runs again in every case.",
        ScriptBlockKind.EndEvent => "The event stops here (e.g. at the end of the NO answer). The flag of \"Only once\" is still set.",
        ScriptBlockKind.HidePerson => "The person disappears. With its hide flag it stays away (also after leaving the map).",
        ScriptBlockKind.ShowPerson => "The person appears (again). With its hide flag cleared it stays.",
        ScriptBlockKind.GiveMoney => "Adds money to the player's wallet.",
        ScriptBlockKind.Code => "Script commands as they are written in the file (asm/macros/scrcmd.inc) – for everything no block does yet. Written unchanged; Ctrl+F searches in it.",
        ScriptBlockKind.Question => "A question with a Yes/No menu and an answer for each.",
        ScriptBlockKind.Mugshot => "A face next to the following messages – right, or left (mirrored) – mod Dialog mugshots.",
        ScriptBlockKind.Picture => "A bigger picture (e.g. a treasure map) in the middle of the screen, until it is hidden or the box closes – or a full-screen picture (FULLSCREEN_…) on a screen of its own until A or B is pressed.",
        ScriptBlockKind.GiveItem => "\"… obtained …!\" with fanfare; the item goes into the bag.",
        ScriptBlockKind.GivePokemon => "A Pokémon for the party (or the PC).",
        ScriptBlockKind.HealParty => "Heals every Pokémon of the party.",
        ScriptBlockKind.TrainerBattle => "A battle against a trainer of the Trainers module.",
        ScriptBlockKind.WildBattle => "A battle against a wild Pokémon.",
        ScriptBlockKind.OnlyOnce => "The event runs only once; afterwards a different text.",
        ScriptBlockKind.SetFlag => "Remembers something (a flag), e.g. to hide a person.",
        ScriptBlockKind.ClearFlag => "Forgets a flag again.",
        ScriptBlockKind.Fanfare => "A short jingle.",
        ScriptBlockKind.Wait => "A pause.",
        ScriptBlockKind.SetVar => "Gives a variable a number, e.g. so a trigger tile does not run again.",
        ScriptBlockKind.Move => "A person (or the player) walks, turns, jumps or shows ! – for small cutscenes.",
        _ => "Takes the player to another map.",
    };

    /// <summary>Block buttons in rows by category (wrap with the width of the panel).</summary>
    public static FrameworkElement Palette(Action<ScriptBlockKind> add, bool hasMugshots, bool hasPictures = false)
    {
        var panel = new StackPanel();
        foreach (var (category, kinds) in Categories)
        {
            var row = new DockPanel { Margin = new Thickness(0, 1, 0, 1) };
            var label = new TextBlock { Text = category, Width = 70, FontSize = 11, Foreground = (Brush)Application.Current.FindResource("MutedBrush"), Margin = new Thickness(0, 5, 0, 0) };
            DockPanel.SetDock(label, Dock.Left);
            row.Children.Add(label);
            var wrap = new WrapPanel();
            foreach (var k in kinds)
            {
                if (k == ScriptBlockKind.Mugshot && !hasMugshots) continue;
                if (k == ScriptBlockKind.Picture && !hasPictures) continue;
                var b = new Button { Content = ScriptBlock.Title(k), Padding = new Thickness(6, 2, 6, 2), Margin = new Thickness(0, 0, 4, 4), FontSize = 12, ToolTip = Help(k) };
                b.Click += (_, _) => add(k);
                wrap.Children.Add(b);
            }
            row.Children.Add(wrap);
            panel.Children.Add(row);
        }
        return panel;
    }

    /// <summary>A free flag for a new block – never flag 0, which the game reads as "no flag".</summary>
    private static string FreeFlag(ScriptBuilderLists lists) =>
        lists.Flags.FirstOrDefault(f => f.StartsWith("FLAG_UNUSED_", StringComparison.Ordinal) && !f.EndsWith("_0x0000", StringComparison.Ordinal)) ?? "";

    public static ScriptBlock NewBlock(ScriptBlockKind kind, ScriptBuilderLists lists)
    {
        var b = new ScriptBlock { Kind = kind };
        switch (kind)
        {
            case ScriptBlockKind.GiveItem: b.Constant = "ITEM_POTION"; break;
            case ScriptBlockKind.GivePokemon: b.Constant = "SPECIES_EEVEE"; b.Number = 10; break;
            case ScriptBlockKind.WildBattle: b.Constant = "SPECIES_BIDOOF"; b.Number = 5; break;
            case ScriptBlockKind.TrainerBattle: b.Constant = lists.Trainers.FirstOrDefault(t => t != "TRAINER_NONE") ?? ""; break;
            case ScriptBlockKind.Fanfare: b.Constant = "SEQ_FANFA4"; break;
            case ScriptBlockKind.Wait: b.Number = 30; break;
            case ScriptBlockKind.Mugshot: b.Constant = lists.Mugshots.FirstOrDefault() ?? ""; break;
            case ScriptBlockKind.Picture: b.Constant = lists.Pictures.FirstOrDefault() ?? lists.Fullscreens.FirstOrDefault() ?? "PICTURE_NONE"; break;
            case ScriptBlockKind.Ask: b.Text = "Do you want it?"; break;
            case ScriptBlockKind.IfFlag: b.Constant = FreeFlag(lists); break;
            case ScriptBlockKind.IfVar: b.Constant = lists.Vars.FirstOrDefault() ?? ""; b.Number = 1; break;
            case ScriptBlockKind.IfItem: b.Constant = "ITEM_POKE_BALL"; break;
            case ScriptBlockKind.GiveMoney: b.Number = 1000; break;
            case ScriptBlockKind.HidePerson or ScriptBlockKind.ShowPerson: b.Constant = lists.People.FirstOrDefault(x => x != "LOCALID_PLAYER") ?? ""; break;
            case ScriptBlockKind.Message: b.Text = "Hello!"; break;
            case ScriptBlockKind.Question: b.Text = "Do you like Pokémon?"; b.YesText = "Me too!"; b.NoText = "Oh… that's a pity."; break;
            case ScriptBlockKind.SetFlag or ScriptBlockKind.ClearFlag: b.Constant = FreeFlag(lists); break;
            case ScriptBlockKind.OnlyOnce: b.Text = "I already gave you everything."; b.Constant = FreeFlag(lists); break;
            case ScriptBlockKind.Warp: b.Constant = lists.Maps.FirstOrDefault() ?? ""; break;
            case ScriptBlockKind.SetVar: b.Constant = lists.Vars.FirstOrDefault() ?? ""; b.Number = 1; break;
            case ScriptBlockKind.Move: b.Constant = lists.People.FirstOrDefault() ?? "LOCALID_PLAYER"; b.Text = "EmoteExclamationMark;WalkNormalSouth 2"; b.Number = 1; break;
        }
        return b;
    }

    public static DataTemplate BlockTemplate()
    {
        var panel = new FrameworkElementFactory(typeof(StackPanel));
        panel.SetBinding(FrameworkElement.MarginProperty, new System.Windows.Data.Binding(nameof(ScriptBlock.Depth)) { Converter = new DepthIndent() });
        var title = new FrameworkElementFactory(typeof(TextBlock));
        title.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding(nameof(ScriptBlock.Kind)) { Converter = new KindTitle() });
        title.SetValue(TextBlock.FontWeightProperty, FontWeights.SemiBold);
        panel.AppendChild(title);
        var sum = new FrameworkElementFactory(typeof(TextBlock));
        sum.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding(nameof(ScriptBlock.Summary)));
        sum.SetValue(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis);
        sum.SetValue(TextBlock.ForegroundProperty, Brushes.Gray);
        panel.AppendChild(sum);
        return new DataTemplate { VisualTree = panel };
    }

    /// <summary>Blocks inside a branch are indented (a bar shows the branch).</summary>
    private sealed class DepthIndent : System.Windows.Data.IValueConverter
    {
        public object Convert(object value, Type t, object p, System.Globalization.CultureInfo c) => new Thickness(2 + 18 * (value is int d ? d : 0), 3, 2, 3);
        public object ConvertBack(object value, Type t, object p, System.Globalization.CultureInfo c) => throw new NotSupportedException();
    }

    private sealed class KindTitle : System.Windows.Data.IValueConverter
    {
        public object Convert(object value, Type t, object p, System.Globalization.CultureInfo c) => value is ScriptBlockKind k ? ScriptBlock.Title(k) : "";
        public object ConvertBack(object value, Type t, object p, System.Globalization.CultureInfo c) => throw new NotSupportedException();
    }

    /// <summary>
    /// A dialog writer: the text as the player reads it (Enter = next line, empty line = next box), buttons for names and
    /// line breaks, and the boxes as they look in the game – with the mugshot, if one is shown.
    /// </summary>
    public static void Writer(Panel target, string label, string natural, Action<string> set, PlatinumFont? font, Func<RgbaImage?> mugshot, Func<string, string?>? check = null) =>
        Writer(target, label, natural, set, font, () => (mugshot(), false, null), check);

    /// <param name="scene">What is on screen besides the box: the mugshot (and whether it is on the left) and a picture.</param>
    public static void Writer(Panel target, string label, string natural, Action<string> set, PlatinumFont? font,
        Func<(RgbaImage? Mugshot, bool Left, RgbaImage? Picture)> scene, Func<string, string?>? check = null)
    {
        target.Children.Add(new TextBlock { Text = label, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 8, 0, 2), TextWrapping = TextWrapping.Wrap });
        var tb = new TextBox
        {
            Text = natural, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 90, MaxHeight = 260, FontSize = 14,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto, ToolTip = "Enter = next line, an empty line = next message box.",
        };
        var tools = new WrapPanel { Margin = new Thickness(0, 3, 0, 0) };
        var images = new WrapPanel();
        var notes = new StackPanel();
        Button T(string t, string tip, Action a) { var x = new Button { Content = t, FontSize = 11, Padding = new Thickness(6, 1, 6, 1), Margin = new Thickness(0, 0, 4, 4), ToolTip = tip }; x.Click += (_, _) => a(); return x; }
        void Insert(string s) { int c = tb.CaretIndex; tb.Text = tb.Text.Insert(c, s); tb.CaretIndex = c + s.Length; tb.Focus(); }
        tools.Children.Add(T("+ Player name", "The player's name (the script buffers it automatically).", () => Insert(PlatinumDialogText.PlayerName)));
        tools.Children.Add(T("+ Rival name", "Barry's name (the script buffers it automatically).", () => Insert(PlatinumDialogText.RivalName)));
        if (font != null) tools.Children.Add(T("↵ Auto line breaks", "Breaks the lines so they fit the box (216 pixels); empty lines between boxes stay.", () => tb.Text = PlatinumDialogText.Wrap(font, tb.Text)));
        tools.Children.Add(T("💬 New box", "Starts the next message box here.", () => Insert("\n\n")));
        void Preview()
        {
            images.Children.Clear();
            notes.Children.Clear();
            var real = PlatinumDialogText.FromNatural(tb.Text, false);
            if (check?.Invoke(real) is { } err) notes.Children.Add(new TextBlock { Text = "✗ " + err, Foreground = (Brush)Application.Current.FindResource("AccentDarkBrush"), TextWrapping = TextWrapping.Wrap });
            if (font == null) { notes.Children.Add(Ui.Hint("No preview: res/fonts/font_message.png was not found.")); return; }
            var unknown = font.UnknownCharacters(real);
            if (unknown.Count > 0)
                notes.Children.Add(new TextBlock { Text = "✗ The game cannot show: " + string.Join(" ", unknown.Select(u => u == " " ? "(space)" : u)) + " – the build would stop. Use other characters.", Foreground = (Brush)Application.Current.FindResource("AccentDarkBrush"), TextWrapping = TextWrapping.Wrap });
            var (pages, warnings) = font.RenderMessage(real);
            foreach (var w in warnings) notes.Children.Add(new TextBlock { Text = "⚠ " + w, Foreground = (Brush)Application.Current.FindResource("WarnTextBrush"), TextWrapping = TextWrapping.Wrap });
            (RgbaImage? Mugshot, bool Left, RgbaImage? Picture) shown = (null, false, null);
            try { shown = scene(); } catch (Exception ex) when (ex is System.IO.IOException or PngFormatException) { }
            for (int i = 0; i < pages.Count; i++)
            {
                var img = Ui.Picture();
                img.Source = Imaging.ToBitmap(PlatinumFont.Screen(pages[i], shown.Mugshot, shown.Left, shown.Picture));
                var box = new Border { Child = img, Margin = new Thickness(0, 2, 6, 4), ToolTip = $"Box {i + 1} of {pages.Count}" };
                images.Children.Add(new Viewbox { Child = box, StretchDirection = StretchDirection.DownOnly, MaxWidth = 264, HorizontalAlignment = HorizontalAlignment.Left });
            }
        }
        tb.TextChanged += (_, _) => { set(tb.Text); Preview(); };
        target.Children.Add(tb);
        target.Children.Add(tools);
        target.Children.Add(notes);
        target.Children.Add(images);
        Preview();
    }

    /// <summary>Who moves, the steps (with buttons for walking, turning, jumping, !, pauses) and whether the script waits.</summary>
    private static void MoveEditor(Panel target, ScriptBlock b, ScriptBuilderLists lists, Action changed)
    {
        target.Children.Add(new TextBlock { Text = "Who moves", FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 8, 0, 2) });
        var who = new ComboBox { ItemsSource = lists.People, IsEditable = true, Text = b.Constant };
        who.SelectionChanged += (_, _) => { if (who.SelectedItem is string w && w != b.Constant) { b.Constant = w; changed(); } };
        who.LostFocus += (_, _) => { var w = who.Text.Trim().ToUpperInvariant(); if (w != b.Constant) { b.Constant = w; changed(); } };
        target.Children.Add(who);
        target.Children.Add(Ui.Hint("LOCALID_PLAYER is the player; the others are the people of this map.", 380));

        var steps = new ListBox { Height = 140, Margin = new Thickness(0, 6, 0, 4), FontFamily = new FontFamily("Consolas"), FontSize = 12 };
        void Refresh(int select = -1)
        {
            steps.ItemsSource = PlatinumScriptBlocks.MoveSteps(b.Text).Select(x => x.Count > 1 ? $"{x.Step} ×{x.Count}" : x.Step).ToList();
            if (select >= 0) steps.SelectedIndex = Math.Min(select, steps.Items.Count - 1);
        }
        void Set(List<(string Step, int Count)> list, int select) { b.Text = string.Join(";", list.Select(x => x.Count > 1 ? $"{x.Step} {x.Count}" : x.Step)); changed(); Refresh(select); }
        void Add(string step, int count)
        {
            var list = PlatinumScriptBlocks.MoveSteps(b.Text);
            int at = steps.SelectedIndex >= 0 ? steps.SelectedIndex + 1 : list.Count;
            // The same step again: one step with a higher count.
            if (at > 0 && list[at - 1].Step == step) { list[at - 1] = (step, Math.Min(99, list[at - 1].Count + count)); Set(list, at - 1); return; }
            list.Insert(at, (step, count));
            Set(list, at);
        }
        target.Children.Add(new TextBlock { Text = "Steps (in this order)", FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 8, 0, 0) });
        target.Children.Add(steps);
        var order = new WrapPanel();
        Button B(string t, string tip, Action a) { var x = new Button { Content = t, FontSize = 11, Padding = new Thickness(6, 1, 6, 1), Margin = new Thickness(0, 0, 4, 4), ToolTip = tip }; x.Click += (_, _) => a(); return x; }
        order.Children.Add(B("▲", "Move the step up", () => { int i = steps.SelectedIndex; var l = PlatinumScriptBlocks.MoveSteps(b.Text); if (i > 0) { (l[i - 1], l[i]) = (l[i], l[i - 1]); Set(l, i - 1); } }));
        order.Children.Add(B("▼", "Move the step down", () => { int i = steps.SelectedIndex; var l = PlatinumScriptBlocks.MoveSteps(b.Text); if (i >= 0 && i < l.Count - 1) { (l[i + 1], l[i]) = (l[i], l[i + 1]); Set(l, i + 1); } }));
        order.Children.Add(B("✕", "Remove the step", () => { int i = steps.SelectedIndex; var l = PlatinumScriptBlocks.MoveSteps(b.Text); if (i >= 0) { l.RemoveAt(i); Set(l, i); } }));
        target.Children.Add(order);

        var speed = new ComboBox { ItemsSource = new[] { "Slow", "Normal", "Fast", "Run" }, SelectedIndex = 1, Width = 80, Margin = new Thickness(0, 0, 6, 0) };
        var count = new TextBox { Text = "1", Width = 34, Margin = new Thickness(0, 0, 6, 0), ToolTip = "Tiles" };
        string Walk(string dir) => speed.SelectedIndex switch { 0 => "WalkSlow", 2 => "WalkFast", 3 => "Run", _ => "WalkNormal" } + dir;
        int N() => int.TryParse(count.Text, out var n) ? Math.Clamp(n, 1, 99) : 1;
        var walk = new WrapPanel { Margin = new Thickness(0, 4, 0, 0) };
        walk.Children.Add(new TextBlock { Text = "Walk ", VerticalAlignment = VerticalAlignment.Center });
        walk.Children.Add(speed);
        walk.Children.Add(count);
        foreach (var (arrow, dir) in new[] { ("↑", "North"), ("↓", "South"), ("←", "West"), ("→", "East") })
            walk.Children.Add(B(arrow, $"Walk {dir.ToLowerInvariant()}", () => Add(Walk(dir), N())));
        target.Children.Add(walk);
        var more = new WrapPanel();
        more.Children.Add(new TextBlock { Text = "Face ", VerticalAlignment = VerticalAlignment.Center });
        foreach (var (arrow, dir) in new[] { ("↑", "North"), ("↓", "South"), ("←", "West"), ("→", "East") })
            more.Children.Add(B(arrow, $"Turn {dir.ToLowerInvariant()}", () => Add("Face" + dir, 1)));
        more.Children.Add(new TextBlock { Text = "  Jump ", VerticalAlignment = VerticalAlignment.Center });
        foreach (var (arrow, dir) in new[] { ("↑", "North"), ("↓", "South"), ("←", "West"), ("→", "East") })
            more.Children.Add(B(arrow, $"Jump one tile {dir.ToLowerInvariant()}", () => Add("JumpNearFast" + dir, 1)));
        target.Children.Add(more);
        var extra = new WrapPanel();
        extra.Children.Add(B("! ", "Exclamation mark over the head", () => Add("EmoteExclamationMark", 1)));
        extra.Children.Add(B("!!", "Double exclamation mark", () => Add("EmoteDoubleExclamationMark", 1)));
        extra.Children.Add(B("⏱ Pause", "A short pause (16 frames)", () => Add("Delay16", 1)));
        extra.Children.Add(B("Hide", "The person disappears (invisible)", () => Add("SetInvisible", 1)));
        extra.Children.Add(B("Show", "The person appears again", () => Add("SetVisible", 1)));
        target.Children.Add(extra);

        var wait = new CheckBox { Content = "Wait until the movement is finished", IsChecked = b.Number != 0, Margin = new Thickness(0, 8, 0, 0) };
        wait.Checked += (_, _) => { b.Number = 1; changed(); };
        wait.Unchecked += (_, _) => { b.Number = 0; changed(); };
        target.Children.Add(wait);
        target.Children.Add(Ui.Hint("Without waiting, the next blocks run while the person walks (e.g. two people walking at the same time: the first without waiting).", 380));
        Refresh();
    }

    /// <summary>The fields of one block, into <paramref name="target"/>.</summary>
    public static void Editor(Panel target, ScriptBlock b, IReadOnlyList<ScriptBlock> all, ScriptBuilderLists lists, Action changed)
    {
        target.Children.Add(new TextBlock { Text = ScriptBlock.Title(b.Kind), FontWeight = FontWeights.SemiBold, FontSize = 15 });
        target.Children.Add(Ui.Hint(Help(b.Kind), 380));
        // What the blocks before this one put on screen: the last mugshot (and its side) and the last picture.
        (RgbaImage?, bool, RgbaImage?) Scene()
        {
            int i = all.ToList().IndexOf(b);
            var before = all.Take(Math.Max(0, i)).ToList();
            var mug = before.LastOrDefault(x => x.Kind == ScriptBlockKind.Mugshot);
            var pic = before.LastOrDefault(x => x.Kind == ScriptBlockKind.Picture && !x.Constant.StartsWith("FULLSCREEN_", StringComparison.Ordinal))?.Constant;
            return (mug is null || mug.Constant is "" or "MUGSHOT_NONE" ? null : lists.MugshotImage(mug.Constant), mug?.X == 1,
                    pic is null or "" or "PICTURE_NONE" ? null : lists.PictureImage(pic));
        }
        void Text(string label, Func<string> get, Action<string> set) => Writer(target, label, get(), v => { set(v); changed(); }, lists.Font, Scene);
        void Choice(string label, IReadOnlyList<string> items, Func<string> get, Action<string> set)
        {
            target.Children.Add(new TextBlock { Text = label, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 8, 0, 2) });
            var cb = new ComboBox { ItemsSource = items, IsEditable = true, IsTextSearchEnabled = true, Text = get() };
            VirtualizingPanel.SetIsVirtualizing(cb, true);
            cb.ItemsPanel = new ItemsPanelTemplate(new FrameworkElementFactory(typeof(VirtualizingStackPanel)));
            cb.LostFocus += (_, _) => { var v = cb.Text.Trim().ToUpperInvariant(); if (v != get()) { set(v); changed(); } };
            cb.SelectionChanged += (_, _) => { if (cb.SelectedItem is string s && s != get()) { set(s); changed(); } };
            target.Children.Add(cb);
        }
        void Number(string label, Func<int> get, Action<int> set)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
            row.Children.Add(new TextBlock { Text = label + "  ", FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center });
            var tb = new TextBox { Text = get().ToString(), Width = 80 };
            tb.TextChanged += (_, _) => { if (int.TryParse(tb.Text.Trim(), out var v) && v != get()) { set(v); changed(); } };
            row.Children.Add(tb);
            target.Children.Add(row);
        }
        switch (b.Kind)
        {
            case ScriptBlockKind.Message:
                Text("What is said", () => b.Text, v => b.Text = v);
                break;
            case ScriptBlockKind.Question:
                Text("Question (a Yes/No menu follows)", () => b.Text, v => b.Text = v);
                Text("Answer when the player says YES", () => b.YesText, v => b.YesText = v);
                Text("Answer when the player says NO", () => b.NoText, v => b.NoText = v);
                target.Children.Add(Ui.Hint("After the answer the event goes on with the next block either way.", 380));
                break;
            case ScriptBlockKind.GiveItem:
                Choice("Item", lists.Items, () => b.Constant, v => b.Constant = v);
                Number("Count", () => b.Number, v => b.Number = v);
                break;
            case ScriptBlockKind.GivePokemon:
            case ScriptBlockKind.WildBattle:
                Choice("Pokémon", lists.Species, () => b.Constant, v => b.Constant = v);
                Number("Level", () => b.Number, v => b.Number = v);
                if (b.Kind == ScriptBlockKind.WildBattle) target.Children.Add(Ui.Hint("If the player loses, they black out like after any lost battle.", 380));
                break;
            case ScriptBlockKind.TrainerBattle:
                Choice("Trainer", lists.Trainers, () => b.Constant, v => b.Constant = v);
                target.Children.Add(Ui.Hint("Team and texts of the trainer are set in the Trainers module. If the player loses, they black out.", 380));
                break;
            case ScriptBlockKind.SetFlag:
            case ScriptBlockKind.ClearFlag:
                Choice("Flag", lists.Flags, () => b.Constant, v => b.Constant = v);
                target.Children.Add(Ui.Hint("FLAG_UNUSED_… flags are free for your own events. The Flags & Variables module shows what every flag is used for.", 380));
                break;
            case ScriptBlockKind.OnlyOnce:
                Choice("Flag that remembers it", lists.Flags, () => b.Constant, v => b.Constant = v);
                Text("What is said when the player comes back", () => b.Text, v => b.Text = v);
                target.Children.Add(Ui.Hint("The other blocks run only the first time; the flag is set at the end. Use a flag nobody else uses (FLAG_UNUSED_…).", 380));
                break;
            case ScriptBlockKind.Move:
                MoveEditor(target, b, lists, changed);
                break;
            case ScriptBlockKind.Ask:
                Text("Question (the Yes/No menu follows)", () => b.Text, v => b.Text = v);
                target.Children.Add(Ui.Hint("YES: the blocks below up to \"Otherwise (No)\". NO: the blocks after it up to \"End of the branch\". Put \"End event here\" into a branch to stop there.", 380));
                break;
            case ScriptBlockKind.IfFlag:
            {
                Choice("Flag", lists.Flags, () => b.Constant, v => b.Constant = v);
                var not = new CheckBox { Content = "… is NOT set", IsChecked = b.X == 1, Margin = new Thickness(0, 6, 0, 0) };
                not.Checked += (_, _) => { b.X = 1; changed(); };
                not.Unchecked += (_, _) => { b.X = 0; changed(); };
                target.Children.Add(not);
                break;
            }
            case ScriptBlockKind.IfVar:
                Choice("Variable", lists.Vars, () => b.Constant, v => b.Constant = v);
                Number("… has the value", () => b.Number, v => b.Number = v);
                break;
            case ScriptBlockKind.IfItem:
                Choice("Item", lists.Items, () => b.Constant, v => b.Constant = v);
                Number("At least", () => b.Number, v => b.Number = v);
                break;
            case ScriptBlockKind.HidePerson:
            case ScriptBlockKind.ShowPerson:
                Choice("Person", lists.People.Where(x => x != "LOCALID_PLAYER").ToList(), () => b.Constant, v => b.Constant = v);
                Choice(b.Kind == ScriptBlockKind.HidePerson ? "Keep hidden with flag (optional – the person's hide flag)" : "Clear the person's hide flag (optional)",
                    new[] { "" }.Concat(lists.Flags).ToList(), () => b.Text, v => b.Text = v);
                break;
            case ScriptBlockKind.GiveMoney:
                Number("Money (₽)", () => b.Number, v => b.Number = v);
                break;
            case ScriptBlockKind.Else:
            case ScriptBlockKind.EndIf:
            case ScriptBlockKind.EndEvent:
                break;
            case ScriptBlockKind.Code:
            {
                target.Children.Add(new TextBlock { Text = "Commands (one per line)", FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 8, 0, 2) });
                var code = new TextBox
                {
                    Text = b.Text, AcceptsReturn = true, AcceptsTab = true, FontFamily = new FontFamily("Consolas"), FontSize = 12, MinHeight = 140, MaxHeight = 420,
                    TextWrapping = TextWrapping.NoWrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                };
                code.LostFocus += (_, _) => { if (code.Text != b.Text) { b.Text = code.Text; changed(); } };
                target.Children.Add(code);
                target.Children.Add(Ui.Hint("Jumps to other labels of this file (GoTo, Call …) work: helper labels of hand-written scripts stay in the file. Changes are taken over when you leave the box.", 380));
                break;
            }
            case ScriptBlockKind.SetVar:
                Choice("Variable", lists.Vars, () => b.Constant, v => b.Constant = v);
                Number("Value", () => b.Number, v => b.Number = v);
                target.Children.Add(Ui.Hint("A trigger tile runs while its variable has the trigger's value – set another value here and it stops.", 380));
                break;
            case ScriptBlockKind.Fanfare:
                Choice("Fanfare", PlatinumScriptBlocks.Fanfares, () => b.Constant, v => b.Constant = v);
                break;
            case ScriptBlockKind.Wait:
                Number("Frames (60 = 1 second)", () => b.Number, v => b.Number = v);
                break;
            case ScriptBlockKind.Mugshot:
            {
                Choice("Mugshot for the next messages", new[] { "MUGSHOT_NONE" }.Concat(lists.Mugshots).ToList(), () => b.Constant, v => b.Constant = v);
                var side = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
                var right = new RadioButton { Content = "Right of the box", IsChecked = b.X != 1, Margin = new Thickness(0, 0, 14, 0), GroupName = "side" + b.GetHashCode() };
                var left = new RadioButton { Content = "Left (mirrored)", IsChecked = b.X == 1, GroupName = "side" + b.GetHashCode() };
                var shot = new Image();
                RenderOptions.SetBitmapScalingMode(shot, BitmapScalingMode.NearestNeighbor);
                void Show()
                {
                    try { shot.Source = Imaging.ToBitmap(PlatinumFont.Screen(null, lists.MugshotImage(b.Constant), b.X == 1, null)); }
                    catch (Exception ex) when (ex is System.IO.IOException or PngFormatException) { shot.Source = null; }
                }
                right.Checked += (_, _) => { b.X = 0; changed(); Show(); };
                left.Checked += (_, _) => { b.X = 1; changed(); Show(); };
                b.PropertyChanged += (_, _) => Show();
                side.Children.Add(right);
                side.Children.Add(left);
                target.Children.Add(side);
                target.Children.Add(new Viewbox { Child = shot, Width = 256, Height = 192, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 6, 0, 0) });
                Show();
                target.Children.Add(Ui.Hint("Shown next to the following messages until the box closes. On the left the picture is mirrored, so the person looks at the text. MUGSHOT_NONE hides it.", 380));
                break;
            }
            case ScriptBlockKind.Picture:
            {
                Choice("Picture", new[] { "PICTURE_NONE" }.Concat(lists.Pictures).Concat(lists.Fullscreens).ToList(), () => b.Constant, v => b.Constant = v);
                var shot = new Image();
                RenderOptions.SetBitmapScalingMode(shot, BitmapScalingMode.NearestNeighbor);
                void Show()
                {
                    try
                    {
                        shot.Source = b.Constant.StartsWith("FULLSCREEN_", StringComparison.Ordinal)
                            ? lists.PictureImage(b.Constant) is { } full ? Imaging.ToBitmap(full) : null
                            : Imaging.ToBitmap(PlatinumFont.Screen(null, null, false, lists.PictureImage(b.Constant)));
                    }
                    catch (Exception ex) when (ex is System.IO.IOException or PngFormatException) { shot.Source = null; }
                }
                b.PropertyChanged += (_, _) => Show();
                target.Children.Add(new Viewbox { Child = shot, Width = 256, Height = 192, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 6, 0, 0) });
                Show();
                target.Children.Add(Ui.Hint("Shown in the middle of the screen (messages can follow below it) until a \"Picture\" block with PICTURE_NONE or the message box closes. Add pictures in the Mugshots module.", 380));
                break;
            }
            case ScriptBlockKind.Warp:
                Choice("Map", lists.Maps, () => b.Constant, v => b.Constant = v);
                Number("x (tile)", () => b.X, v => b.X = v);
                Number("z (tile)", () => b.Z, v => b.Z = v);
                target.Children.Add(Ui.Hint("The screen fades out and the player stands at x/z of the map, facing down. Blocks after a warp do not run.", 380));
                break;
        }
    }
}
