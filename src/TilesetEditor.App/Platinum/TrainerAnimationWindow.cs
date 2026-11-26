using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using TilesetEditor.App.Infrastructure;
using TilesetEditor.App.Shell;
using TilesetEditor.Core.Imaging;
using TilesetEditor.Core.Platinum;
using TilesetEditor.Core.Project;

namespace TilesetEditor.App.Platinum;

/// <summary>
/// The battle picture of a trainer class with its animation (Barry, gym leaders, Elite Four …): play it, look at every
/// frame, replace one frame or the whole sheet, export the sheet for a paint program, and change which frame is shown how long.
/// </summary>
public sealed class TrainerAnimationWindow : Window
{
    private readonly ProjectContext _ctx;
    private readonly string _cls;
    private TrainerAnimation _anim = null!;
    private readonly Image _preview = new() { Width = 240, Height = 240, Stretch = Stretch.Uniform };
    private readonly WrapPanel _frames = new();
    private readonly StackPanel _steps = new();
    private readonly TextBlock _total = new() { Margin = new Thickness(0, 4, 0, 0) };
    private readonly ComboBox _sequence = new() { MinWidth = 220 };
    private readonly Button _play = new() { Content = "⏸ Pause", Padding = new Thickness(8, 2, 8, 2) };
    private readonly DispatcherTimer _timer = new(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(1000.0 / 60) };
    private List<TrainerAnimStep> _edit = [];
    private BitmapSource?[] _cellImages = [];
    private int _step, _left;
    private bool _dirty;

    public bool Changed { get; private set; }

    public TrainerAnimationWindow(ProjectContext ctx, string trainerClass, string title)
    {
        _ctx = ctx;
        _cls = trainerClass;
        Title = "Battle picture – " + title;
        Owner = Application.Current.MainWindow;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Width = 980;
        Height = 760;
        RenderOptions.SetBitmapScalingMode(_preview, BitmapScalingMode.NearestNeighbor);
        _timer.Tick += (_, _) => Tick();
        _play.Click += (_, _) => { if (_timer.IsEnabled) { _timer.Stop(); _play.Content = "▶ Play"; } else { _timer.Start(); _play.Content = "⏸ Pause"; } };
        _sequence.SelectionChanged += (_, _) => { if (_sequence.SelectedIndex >= 0) LoadSteps(_sequence.SelectedIndex); };
        Closing += (_, e) =>
        {
            if (_dirty && AppDialog.Show(this, "The animation was changed but not saved. Close anyway?", Title, MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
                e.Cancel = true;
            else _timer.Stop();
        };
        Content = Build();
        Reload();
    }

    private UIElement Build()
    {
        var left = new StackPanel { Width = 280, Margin = new Thickness(12) };
        left.Children.Add(new Border { Child = _preview, Background = Imaging.Checkerboard(12), Padding = new Thickness(8), HorizontalAlignment = HorizontalAlignment.Left });
        left.Children.Add(Ui.Row(_play, Ui.Button("⟲ From the start", () => { _step = 0; _left = 0; Tick(); })));
        left.Children.Add(Ui.Hint("The preview plays the steps on the right as the game does (60 frames = 1 second), with the little hops of each cell.", 260));
        left.Children.Add(Ui.Title("Pictures"));
        left.Children.Add(Ui.Button("Replace whole sheet…", ReplaceSheet,
            "A PNG 80 wide with all frames below each other – as tall as the exported sheet (with the 1-pixel lines) or without the lines. An indexed PNG keeps its palette (colour 0 = transparent)."));
        left.Children.Add(Ui.Button("Export sheet…", ExportSheet, "Saves front.png to edit it in a paint program; load it again with \"Replace whole sheet\"."));

        var right = new StackPanel { Margin = new Thickness(12) };
        right.Children.Add(Ui.Title("Frames"));
        right.Children.Add(Ui.Hint("Every frame of the sheet. Click one to look at it; \"Replace…\" draws a new 80×80 picture with the sheet's colours (all frames share one palette)."));
        right.Children.Add(_frames);
        right.Children.Add(Ui.Title("Animation"));
        right.Children.Add(Ui.Row(new TextBlock { Text = "Sequence ", VerticalAlignment = VerticalAlignment.Center }, _sequence));
        right.Children.Add(Ui.Hint("Which frame is shown, for how long (frames of 1/60 s). Sequence 0 is the still picture; the longest one plays when the battle starts."));
        right.Children.Add(_steps);
        right.Children.Add(_total);
        right.Children.Add(Ui.Row(
            Ui.Button("+ Step", () => { _edit.Add(new TrainerAnimStep(_edit.LastOrDefault()?.Cell ?? 0, 8)); Edited(); }),
            Ui.Button("💾 Save animation", SaveTiming, "Writes front_anim.json (only this sequence changes)."),
            Ui.Button("Undo changes", () => LoadSteps(_sequence.SelectedIndex))));

        var grid = new DockPanel();
        DockPanel.SetDock(left, Dock.Left);
        grid.Children.Add(left);
        grid.Children.Add(new ScrollViewer { Content = right, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        return grid;
    }

    private void Reload()
    {
        try { _anim = PlatinumTrainerAnimations.Load(_ctx.Root!, _cls); }
        catch (Exception ex) when (ex is IOException or PngFormatException or System.Text.Json.JsonException or InvalidOperationException)
        {
            ProjectWriter.Warn(Title, ex.Message);
            Close();
            return;
        }
        _cellImages = Enumerable.Range(0, _anim.Cells.Count).Select(c => (BitmapSource?)Imaging.ToBitmap(_anim.Render(c))).ToArray();
        _frames.Children.Clear();
        for (int k = 0; k < _anim.Frames.Count; k++)
        {
            int frame = k;
            var img = new Image { Source = Imaging.ToBitmap(_anim.Frames[k]), Width = 80, Height = 80 };
            RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.NearestNeighbor);
            var stack = new StackPanel { Margin = new Thickness(0, 0, 8, 8) };
            var face = new Button { Content = new Border { Child = img, Background = Imaging.Checkerboard(8) }, Padding = new Thickness(2), ToolTip = "Show this frame" };
            face.Click += (_, _) => { _timer.Stop(); _play.Content = "▶ Play"; _preview.Source = frame < _cellImages.Length ? _cellImages[frame] : Imaging.ToBitmap(_anim.Frames[frame]); };
            stack.Children.Add(face);
            stack.Children.Add(new TextBlock { Text = $"Frame {k}", HorizontalAlignment = HorizontalAlignment.Center });
            stack.Children.Add(Ui.Button("Replace…", () => ReplaceFrame(frame)));
            _frames.Children.Add(stack);
        }
        _sequence.Items.Clear();
        for (int s = 0; s < _anim.Sequences.Count; s++)
            _sequence.Items.Add($"{s}: {_anim.Sequences[s].Count} step(s){(s == _anim.MainSequence ? " – battle start" : s == 0 ? " – still" : "")}");
        _sequence.SelectedIndex = _anim.Sequences.Count == 0 ? -1 : _anim.MainSequence;
        if (_anim.Sequences.Count == 0) _preview.Source = _cellImages.FirstOrDefault();
        _step = 0;
        _left = 0;
        _timer.Start();
        _play.Content = "⏸ Pause";
    }

    private void LoadSteps(int sequence)
    {
        if (sequence < 0 || sequence >= _anim.Sequences.Count) return;
        _edit = [.. _anim.Sequences[sequence]];
        _dirty = false;
        FillSteps();
    }

    private void Edited()
    {
        _dirty = true;
        _step = 0;
        _left = 0;
        FillSteps();
    }

    private void FillSteps()
    {
        _steps.Children.Clear();
        var cells = Enumerable.Range(0, _anim.Cells.Count).Select(c => c < _anim.Frames.Count ? $"Frame {c}" : $"Cell {c} (empty)").ToList();
        for (int i = 0; i < _edit.Count; i++)
        {
            int at = i;
            var cell = new ComboBox { ItemsSource = cells, SelectedIndex = Math.Clamp(_edit[i].Cell, 0, Math.Max(0, cells.Count - 1)), Width = 130 };
            cell.SelectionChanged += (_, _) => { if (cell.SelectedIndex >= 0 && cell.SelectedIndex != _edit[at].Cell) { _edit[at] = _edit[at] with { Cell = cell.SelectedIndex }; _dirty = true; } };
            var delay = new TextBox { Text = _edit[i].Delay.ToString(), Width = 50, Margin = new Thickness(6, 0, 0, 0), VerticalContentAlignment = VerticalAlignment.Center };
            delay.LostFocus += (_, _) =>
            {
                if (int.TryParse(delay.Text, out var d) && d is >= 1 and <= 255) { if (d != _edit[at].Delay) { _edit[at] = _edit[at] with { Delay = d }; _dirty = true; UpdateTotal(); } }
                else delay.Text = _edit[at].Delay.ToString();
            };
            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 2) };
            row.Children.Add(new TextBlock { Text = $"{i + 1}.", Width = 28, VerticalAlignment = VerticalAlignment.Center });
            row.Children.Add(cell);
            row.Children.Add(delay);
            row.Children.Add(new TextBlock { Text = " frames", VerticalAlignment = VerticalAlignment.Center });
            var flash = new CheckBox { Content = "⚡ flash", IsChecked = _edit[i].Flash, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0),
                ToolTip = "The white screen flash of the ball throw starts at this step (the original animations have it at the throw)." };
            flash.Checked += (_, _) => { _edit[at] = _edit[at] with { Attribute = 1 }; _dirty = true; UpdateTotal(); };
            flash.Unchecked += (_, _) => { _edit[at] = _edit[at] with { Attribute = 0 }; _dirty = true; UpdateTotal(); };
            row.Children.Add(flash);
            row.Children.Add(Small("▲", () => { if (at > 0) { (_edit[at], _edit[at - 1]) = (_edit[at - 1], _edit[at]); Edited(); } }));
            row.Children.Add(Small("▼", () => { if (at < _edit.Count - 1) { (_edit[at], _edit[at + 1]) = (_edit[at + 1], _edit[at]); Edited(); } }));
            row.Children.Add(Small("✕", () => { if (_edit.Count > 1) { _edit.RemoveAt(at); Edited(); } }));
            _steps.Children.Add(row);
        }
        UpdateTotal();
    }

    private static Button Small(string text, Action click)
    {
        var b = new Button { Content = text, Padding = new Thickness(6, 0, 6, 0), Margin = new Thickness(4, 0, 0, 0) };
        b.Click += (_, _) => click();
        return b;
    }

    private void UpdateTotal() => _total.Text = $"{_edit.Count} step(s), {_edit.Sum(s => s.Delay)} frames ≈ {_edit.Sum(s => s.Delay) / 60.0:0.0} s" + (_dirty ? " – not saved" : "");

    private void Tick()
    {
        if (_edit.Count == 0) return;
        if (_left <= 0)
        {
            if (_step >= _edit.Count) _step = 0;
            var s = _edit[_step++];
            _left = s.Delay;
            _preview.Source = s.Cell < _cellImages.Length ? _cellImages[s.Cell] : null;
        }
        _left--;
    }

    private void SaveTiming()
    {
        try
        {
            var change = PlatinumTrainerAnimations.PlanTiming(_ctx.Root!, _cls, _sequence.SelectedIndex, _edit);
            if (!PlatinumModules.Write(_ctx, "Trainer animation", [change], "Build the ROM afterwards.")) return;
            _dirty = false;
            Changed = true;
            int keep = _sequence.SelectedIndex;
            Reload();
            _sequence.SelectedIndex = keep;
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or System.Text.Json.JsonException) { ProjectWriter.Warn("Trainer animation", ex.Message); }
    }

    private string? PickPng(string title)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog { Filter = "PNG pictures|*.png", Title = title };
        return dlg.ShowDialog(this) == true ? dlg.FileName : null;
    }

    private void ReplaceSheet()
    {
        if (PickPng($"New sheet: 80 wide, {_anim.Frames.Count} frame(s) below each other") is not { } file) return;
        try
        {
            var change = PlatinumTrainerAnimations.PlanReplaceSheet(_ctx.Root!, _cls, Png.Load(file));
            if (PlatinumModules.Write(_ctx, "Trainer picture", [change], "The animation steps stay; the scan picture (front_scan.png) is not changed.", "Build the ROM afterwards.")) { Changed = true; Reload(); }
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or PngFormatException) { ProjectWriter.Warn("Trainer picture", ex.Message); }
    }

    private void ReplaceFrame(int frame)
    {
        if (PickPng($"Frame {frame}: 80×80") is not { } file) return;
        try
        {
            var change = PlatinumTrainerAnimations.PlanReplaceFrame(_ctx.Root!, _cls, frame, Png.Load(file));
            if (PlatinumModules.Write(_ctx, "Trainer picture", [change], "Build the ROM afterwards.")) { Changed = true; Reload(); }
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or PngFormatException) { ProjectWriter.Warn("Trainer picture", ex.Message); }
    }

    private void ExportSheet()
    {
        var dlg = new Microsoft.Win32.SaveFileDialog { Filter = "PNG pictures|*.png", FileName = Path.GetFileName(_anim.Folder) + "_front.png" };
        if (dlg.ShowDialog(this) != true) return;
        File.Copy(ProjectPaths.Combine(_ctx.Root!, _anim.Folder + "/front.png"), dlg.FileName, true);
    }
}
