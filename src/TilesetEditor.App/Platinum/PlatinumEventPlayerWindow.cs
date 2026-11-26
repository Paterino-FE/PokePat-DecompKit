using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using TilesetEditor.App.Infrastructure;
using TilesetEditor.Core.Imaging;
using TilesetEditor.Core.Platinum;

namespace TilesetEditor.App.Platinum;

/// <summary>
/// Plays an event made of blocks like the game: message boxes in the game's font (with mugshot and picture), the Yes/No
/// menu, branches by flags and variables. The flags start as ticked on the right; battles and the bag are decided here.
/// </summary>
public sealed class PlatinumEventPlayerWindow : Window
{
    private readonly List<ScriptBlock> _blocks;
    private readonly ScriptBuilderLists _lists;
    private readonly PlatinumFont? _font;
    private readonly Image _screen = new() { Width = 512, Height = 384, Stretch = Stretch.Uniform };
    private readonly TextBlock _plain = new() { TextWrapping = TextWrapping.Wrap, FontSize = 16, Margin = new Thickness(0, 6, 0, 0) };
    private readonly WrapPanel _buttons = new() { Margin = new Thickness(0, 8, 0, 0) };
    private readonly StackPanel _flagsPanel = new();
    private readonly ListBox _log = new() { Height = 220, FontSize = 12 };
    private readonly Dictionary<string, CheckBox> _flagBoxes = new(StringComparer.Ordinal);
    private readonly Dictionary<string, TextBox> _varBoxes = new(StringComparer.Ordinal);
    private PlatinumEventRunner _run = null!;
    private readonly Queue<RgbaImage> _pages = new();
    private PlayStep? _step;

    public PlatinumEventPlayerWindow(List<ScriptBlock> blocks, ScriptBuilderLists lists, PlatinumFont? font, string title)
    {
        _blocks = blocks;
        _lists = lists;
        _font = font;
        Title = "▶ Play – " + title;
        Owner = Application.Current.MainWindow;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Width = 980;
        Height = 640;
        RenderOptions.SetBitmapScalingMode(_screen, BitmapScalingMode.NearestNeighbor);
        Content = Build();
        PreviewKeyDown += Keys;
        Restart();
    }

    private UIElement Build()
    {
        var left = new StackPanel { Margin = new Thickness(12) };
        left.Children.Add(new Border { Child = _screen, Background = Brushes.Black, HorizontalAlignment = HorizontalAlignment.Left });
        left.Children.Add(_plain);
        left.Children.Add(_buttons);
        left.Children.Add(Ui.Hint("Enter / Space = A (next, Yes) · N = No · R = from the start · Esc = close", 512));

        var right = new StackPanel { Margin = new Thickness(12), Width = 380 };
        right.Children.Add(Ui.Title("At the start"));
        right.Children.Add(Ui.Hint("The flags and variables this event uses – tick what is already set in the game, then ⟲ start again.", 360));
        right.Children.Add(_flagsPanel);
        right.Children.Add(Ui.Row(Ui.Button("⟲ Start again", Restart, "Plays the event from the beginning with the values above.")));
        right.Children.Add(Ui.Title("What happened"));
        right.Children.Add(_log);

        var flags = _blocks.SelectMany(b => b.Kind switch
        {
            ScriptBlockKind.IfFlag or ScriptBlockKind.SetFlag or ScriptBlockKind.ClearFlag or ScriptBlockKind.OnlyOnce => new[] { b.Constant },
            ScriptBlockKind.HidePerson or ScriptBlockKind.ShowPerson => new[] { b.Text },
            _ => [],
        }).Where(f => f.StartsWith("FLAG_", StringComparison.Ordinal)).Distinct().ToList();
        foreach (var f in flags)
        {
            var cb = new CheckBox { Content = f + " is set", Margin = new Thickness(0, 2, 0, 2), FontFamily = new FontFamily("Consolas") };
            _flagBoxes[f] = cb;
            _flagsPanel.Children.Add(cb);
        }
        foreach (var v in _blocks.Where(b => b.Kind is ScriptBlockKind.IfVar or ScriptBlockKind.SetVar).Select(b => b.Constant).Where(v => v.Length > 0).Distinct())
        {
            var tb = new TextBox { Text = "0", Width = 60, Margin = new Thickness(6, 0, 0, 0) };
            _varBoxes[v] = tb;
            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 2) };
            row.Children.Add(new TextBlock { Text = v + " =", FontFamily = new FontFamily("Consolas"), VerticalAlignment = VerticalAlignment.Center });
            row.Children.Add(tb);
            _flagsPanel.Children.Add(row);
        }
        if (flags.Count == 0 && _varBoxes.Count == 0) _flagsPanel.Children.Add(Ui.Hint("This event uses no flags or variables.", 360));

        var dock = new DockPanel();
        DockPanel.SetDock(right, Dock.Right);
        dock.Children.Add(right);
        dock.Children.Add(new ScrollViewer { Content = left, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        return dock;
    }

    private void Restart()
    {
        var flags = _flagBoxes.ToDictionary(x => x.Key, x => x.Value.IsChecked == true);
        _run = new PlatinumEventRunner(_blocks, flags);
        foreach (var (v, tb) in _varBoxes) _run.Vars[v] = int.TryParse(tb.Text, out var n) ? n : 0;
        _log.Items.Clear();
        _pages.Clear();
        Show(_run.Next());
    }

    private void Keys(object sender, KeyEventArgs e)
    {
        if (e.OriginalSource is TextBox) return;
        switch (e.Key)
        {
            case Key.Enter or Key.Space or Key.A: Answer(true); e.Handled = true; break;
            case Key.N or Key.B: if (_step?.Kind is PlayKind.Question or PlayKind.Decision) { Answer(false); e.Handled = true; } break;
            case Key.R: Restart(); e.Handled = true; break;
            case Key.Escape: Close(); e.Handled = true; break;
        }
    }

    private void Answer(bool yes)
    {
        if (_step == null) return;
        if (_step.Kind == PlayKind.End) return;
        if (_pages.Count > 0 && _step.Kind == PlayKind.Message) { DrawPage(_pages.Dequeue()); return; }
        Show(_run.Next(yes));
    }

    private void Show(PlayStep step)
    {
        _step = step;
        // The log shows what the runner did since the last step.
        while (_log.Items.Count < _run.History.Count) _log.Items.Add(_run.History[_log.Items.Count]);
        if (_log.Items.Count > 0) _log.ScrollIntoView(_log.Items[^1]);
        _buttons.Children.Clear();
        _plain.Text = "";
        _pages.Clear();
        switch (step.Kind)
        {
            case PlayKind.Message:
            case PlayKind.Question:
                if (_font != null)
                {
                    var real = PlatinumDialogText.FromNatural(step.Text, false);
                    foreach (var p in _font.RenderMessage(real, Placeholder).Pages) _pages.Enqueue(p);
                    if (_pages.Count > 0) DrawPage(_pages.Dequeue());
                }
                else { _screen.Source = Imaging.ToBitmap(Scene(null, step)); _plain.Text = step.Text; }
                if (step.Kind == PlayKind.Question) { Btn("✔ Yes", () => Answer(true)); Btn("✘ No", () => Answer(false)); }
                else Btn("A ▶ Next", () => Answer(true));
                break;
            case PlayKind.Decision:
                _screen.Source = Imaging.ToBitmap(Scene(null, step));
                _plain.Text = step.Text;
                Btn(step.Yes, () => Answer(true));
                Btn(step.No, () => Answer(false));
                break;
            case PlayKind.Log:
                _screen.Source = Imaging.ToBitmap(step.Picture is { } fs && fs.StartsWith("FULLSCREEN_", StringComparison.Ordinal) && _lists.PictureImage(fs) is { } full ? full : Scene(null, step));
                _plain.Text = step.Text;
                Btn("A ▶ Next", () => Answer(true));
                break;
            case PlayKind.End:
                _screen.Source = Imaging.ToBitmap(Scene(null, step));
                _plain.Text = "■ " + step.Text;
                Btn("⟲ Play again", Restart);
                Btn("Close", Close);
                break;
        }
    }

    private void DrawPage(RgbaImage page) => _screen.Source = Imaging.ToBitmap(Scene(page, _step!));

    private RgbaImage Scene(RgbaImage? page, PlayStep step)
    {
        RgbaImage? mug = null, pic = null;
        try
        {
            mug = step.Mugshot != null ? _lists.MugshotImage(step.Mugshot) : null;
            pic = step.Picture != null && !step.Picture.StartsWith("FULLSCREEN_", StringComparison.Ordinal) ? _lists.PictureImage(step.Picture) : null;
        }
        catch (Exception ex) when (ex is System.IO.IOException or PngFormatException) { }
        return PlatinumFont.Screen(page, mug, step.MugshotLeft, pic);
    }

    private static string Placeholder(string code) =>
        code.Contains("3, 0", StringComparison.Ordinal) ? "PLAYER" : code.Contains("3, 1", StringComparison.Ordinal) ? "BARRY" : "…";

    private void Btn(string text, Action click)
    {
        var b = new Button { Content = text, Padding = new Thickness(14, 4, 14, 4), Margin = new Thickness(0, 0, 8, 0), FontSize = 14 };
        b.Click += (_, _) => click();
        _buttons.Children.Add(b);
    }
}
