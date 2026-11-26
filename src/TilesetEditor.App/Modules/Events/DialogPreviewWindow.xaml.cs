using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Microsoft.Win32;
using TilesetEditor.App.Infrastructure;
using TilesetEditor.Core.Events;
using TilesetEditor.Core.Imaging;
using TilesetEditor.Core.Text;

namespace TilesetEditor.App.Modules.Events;

public sealed class PreviewFlag(string name) : ObservableObject
{
    private bool _isSet;
    public string Name { get; } = name;
    public bool IsSet { get => _isSet; set => Set(ref _isSet, value); }
}

/// <summary>Plays an event like the game (60 frames per second) with the project's font and window graphics.</summary>
public partial class DialogPreviewWindow : Window
{
    private readonly ToolkitEvent _event;
    private readonly DialogRenderer _renderer;
    private readonly GbaFont _font;
    private readonly Charmap _charmap;
    private readonly ProjectSymbols _symbols;
    private readonly DispatcherTimer _timer;
    private readonly ObservableCollection<PreviewFlag> _flags = [];
    private DialogSession? _session;
    private PngImage? _customFrame;
    private int _shownVersion = -1;

    public DialogPreviewWindow(ToolkitEvent ev, GbaFont font, Charmap charmap, DialogAssets assets, ProjectSymbols symbols, string projectRoot)
    {
        InitializeComponent();
        _event = ev;
        _symbols = symbols;
        _font = font;
        _charmap = charmap;
        _renderer = new DialogRenderer(assets, font, charmap);
        Title = $"Preview: {ev.Title} ({ev.Label})";

        FrameChoice.Items.Add(new ComboBoxItem { Content = "Dialog box (message_box.png)", Tag = 0 });
        foreach (var n in assets.StdFrames.Keys.OrderBy(k => k))
            FrameChoice.Items.Add(new ComboBoxItem { Content = $"Window frame {n} (text_window/{n}.png)", Tag = n });
        FrameChoice.SelectedIndex = 0;

        foreach (var f in UsedFlags(ev.Steps).Distinct()) _flags.Add(new PreviewFlag(f));
        FlagList.ItemsSource = _flags;
        if (_flags.Count == 0) FlagList.ItemsSource = new[] { new PreviewFlag("(no flags in this event)") };

        _timer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(1000.0 / 60) };
        _timer.Tick += (_, _) => Frame();
        Loaded += (_, _) => { Restart(); _timer.Start(); Focus(); };
        Closed += (_, _) => _timer.Stop();
    }

    private static IEnumerable<string> UsedFlags(IEnumerable<EventStep> steps)
    {
        foreach (var s in steps)
        {
            if (s is IfFlagStep i && i.Flag.Length > 0) yield return i.Flag;
            if (s is SetFlagStep f && f.Flag.Length > 0) yield return f.Flag;
            if (s is ClearFlagStep c && c.Flag.Length > 0) yield return c.Flag;
            foreach (var (_, b) in s.Branches())
                foreach (var x in UsedFlags(b)) yield return x;
        }
    }

    private void Restart()
    {
        var flags = new HashSet<string>(_flags.Where(f => f.IsSet).Select(f => f.Name));
        string ItemName(string item) => _symbols.Items.FirstOrDefault(i => i.Name == item) is { DisplayName.Length: > 0 } i ? i.DisplayName : item.Replace("ITEM_", "");
        _session = new DialogSession(_event, _font, _charmap, flags, ItemName)
        {
            TextSpeed = SpeedChoice.SelectedItem is ComboBoxItem { Tag: string t } ? int.Parse(t) : 4,
            PlayerName = string.IsNullOrWhiteSpace(PlayerName.Text) ? "PLAYER" : PlayerName.Text.Trim(),
        };
        _shownVersion = -1;
        Frame();
    }

    private void Frame()
    {
        if (_session == null) return;
        _session.Tick();
        if (_session.Version == _shownVersion) return;
        _shownVersion = _session.Version;
        var style = FrameChoice.SelectedItem is ComboBoxItem { Tag: int n } && n > 0 ? DialogFrameStyle.Standard : DialogFrameStyle.Dialog;
        int frame = FrameChoice.SelectedItem is ComboBoxItem { Tag: int m } && m > 0 ? m : 1;
        Screen.Source = Imaging.ToBitmap(_renderer.Render(_session.Scene(style, frame, _customFrame)));
        StateText.Text = _session.State switch
        {
            DialogState.Typing => "Text is printing …",
            DialogState.WaitingForA => "Press A to continue",
            DialogState.Choosing when _session.MenuOptions.Count > 0 => "Pick a menu item (↑/↓, A), B cancels",
            DialogState.Choosing => "Choose YES or NO",
            DialogState.Deciding => $"Condition: {_session.DecisionPrompt}  (YES = met, NO = not met)",
            _ => "Event finished",
        };
        YesButton.IsEnabled = NoButton.IsEnabled = (_session.State == DialogState.Choosing && _session.MenuOptions.Count == 0) || _session.State == DialogState.Deciding;
        BButton.IsEnabled = _session.State == DialogState.Choosing;
        LogList.ItemsSource = _session.Log.ToList();
        foreach (var f in _flags) f.IsSet = _session.Flags.Contains(f.Name);
    }

    private void OnA(object sender, RoutedEventArgs e) { _session?.PressA(); Frame(); }
    private void OnYes(object sender, RoutedEventArgs e) { _session?.Choose(true); Frame(); }
    private void OnNo(object sender, RoutedEventArgs e) { _session?.Choose(false); Frame(); }
    private void OnB(object sender, RoutedEventArgs e) { _session?.PressB(); Frame(); }
    private void OnScreenClick(object sender, MouseButtonEventArgs e) { _session?.PressA(); Frame(); }
    private void OnRestart(object sender, RoutedEventArgs e) => Restart();

    private void OnOptionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded) return;
        if (sender == SpeedChoice && _session != null && SpeedChoice.SelectedItem is ComboBoxItem { Tag: string t }) _session.TextSpeed = int.Parse(t);
        if (sender == FrameChoice) _customFrame = null;
        _shownVersion = -1;
        Frame();
    }

    private void OnKey(object sender, KeyEventArgs e)
    {
        if (e.OriginalSource is TextBox) return;
        switch (e.Key)
        {
            case Key.Enter or Key.Space or Key.X: _session?.PressA(); break;
            case Key.Up: _session?.MoveCursor(-1); break;
            case Key.Down: _session?.MoveCursor(1); break;
            case Key.Escape or Key.Back or Key.Z: _session?.PressB(); break;
            default: return;
        }
        e.Handled = true;
        Frame();
    }

    private void OnLoadCustomFrame(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog { Filter = "PNG (indexed)|*.png", Title = "Custom window graphic" };
        if (dlg.ShowDialog(this) != true) return;
        try
        {
            var png = Png.Load(dlg.FileName);
            if (!png.IsIndexed || !((png.Width == 56 && png.Height == 16) || (png.Width == 24 && png.Height == 24)))
            {
                AppDialog.Show(this, $"Expected an indexed PNG of 56×16 (dialog box) or 24×24 pixels (window frame). This file: {png.Width}×{png.Height}, {(png.IsIndexed ? "indexed" : "not indexed")}.",
                    "Custom window graphic", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            _customFrame = png;
            CustomInfo.Text = $"Custom graphic: {System.IO.Path.GetFileName(dlg.FileName)} ({png.Width}×{png.Height}). Preview only, not copied into the project.";
            _shownVersion = -1;
            Frame();
        }
        catch (PngFormatException ex)
        {
            AppDialog.Show(this, ex.Message, "Custom window graphic", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
}
