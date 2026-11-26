using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using TilesetEditor.App.Infrastructure;
using TilesetEditor.App.Shell;
using TilesetEditor.Core.Imaging;
using TilesetEditor.Core.Platinum;

namespace TilesetEditor.App.Platinum;

/// <summary>
/// The pictures of the mod "Dialog mugshots" (appears in the navigation while the mod is switched on): all mugshots with
/// their constant, and adding new ones from a PNG or from a trainer class's battle picture.
/// </summary>
public sealed class PlatinumMugshotsViewModel : ObservableObject, IToolkitModule, IOptionalModule
{
    private readonly DeferredLoad _reload;
    private readonly ProjectContext _ctx;
    private readonly Action<string> _navigate;
    private readonly WrapPanel _gallery = new();
    private readonly WrapPanel _pictures = new();
    private readonly TextBlock _pictureStatus = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 8) };
    private readonly WrapPanel _fullscreens = new();
    private readonly TextBlock _fullscreenStatus = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 8) };
    private readonly CheckBox _leftPreview = new() { Content = "Show them on the left (mirrored, as ShowMugshot …, MUGSHOT_POS_LEFT draws them)", Margin = new Thickness(0, 0, 0, 8) };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 8) };

    public PlatinumMugshotsViewModel(ProjectContext ctx, Action<string> navigate)
    {
        _ctx = ctx;
        _navigate = navigate;
        _reload = new DeferredLoad(Refresh);
        _ctx.PlatinumChanged += (_, _) => _reload.Request();
        View = BuildView();
        _reload.Attach(View);
        Refresh();
    }

    public string Title => "Mugshots";
    public string Description => "Pictures next to dialog boxes (mod)";
    public bool IsAvailable => true;
    public bool IsShown => _ctx.Root is { } root && PlatinumMugshots.IsInstalled(root);
    public override string ToString() => Title;
    public FrameworkElement View { get; }
    public bool HasUnsavedChanges => false;
    public string? UnsavedChangesDescription => null;

    private FrameworkElement BuildView()
    {
        var panel = new StackPanel { Margin = new Thickness(16) };
        panel.Children.Add(Ui.Title("Dialog mugshots"));
        panel.Children.Add(Ui.Hint("64×64 pictures shown above the message box. In the Events & Dialogs module, pick one in the text preview or use the block \"🖼 Mugshot\" of the event builder; in a script by hand: ShowMugshot MUGSHOT_NAME … ClearMugshot."));
        panel.Children.Add(Ui.Row(
            Ui.Button("+ From PNG…", AddFromPng, "A PNG of any size – other sizes than 64×64 are scaled down (proportions kept); 15 colours + transparency."),
            Ui.Button("+ From trainer class…", AddFromTrainerClass, "Head and shoulders of a trainer class's battle picture."),
            Ui.Button("🧩 Mods →", () => _navigate("Mods"), "Switch the mod off (removes all mugshots again).")));
        panel.Children.Add(_status);
        _leftPreview.Checked += (_, _) => Refresh();
        _leftPreview.Unchecked += (_, _) => Refresh();
        panel.Children.Add(_leftPreview);
        panel.Children.Add(_gallery);

        panel.Children.Add(Ui.Title("Pictures"));
        panel.Children.Add(Ui.Hint("Bigger pictures shown in the middle of the screen, e.g. a treasure map or a letter: ShowPicture PICTURE_NAME … HidePicture " +
                                   $"(or the block \"🗺 Picture\"). Up to {PlatinumMugshots.MaxPictureWidth}×{PlatinumMugshots.MaxPictureHeight} pixels and " +
                                   $"{PlatinumMugshots.MaxPictureTiles} tiles of 8×8 – e.g. 128×96, 96×128 or 256×48; bigger PNGs are scaled down automatically (proportions kept). 15 colours + transparency."));
        panel.Children.Add(Ui.Row(Ui.Button("+ Picture from PNG…", AddPicture, "A PNG of any size – bigger ones are scaled to the largest size that fits (e.g. 128×96, or 256×48 for a wide one).")));
        panel.Children.Add(_pictureStatus);
        panel.Children.Add(_pictures);

        panel.Children.Add(Ui.Title("Full-screen pictures"));
        panel.Children.Add(Ui.Hint("A picture that fills the whole upper screen (256×192, up to 255 colours) on a screen of its own, like the Town Map: " +
                                   "the field fades out, the picture stays until the player presses A or B, then the field comes back. In a script: " +
                                   "ShowFullscreenPicture FULLSCREEN_NAME (after the message box is closed), or the block \"🗺 Picture\". Bigger PNGs are scaled down, smaller ones centred on black."));
        panel.Children.Add(Ui.Row(Ui.Button("+ Full-screen picture from PNG…", AddFullscreen, "Ideally 256×192 – any size is fine (proportions kept).")));
        panel.Children.Add(_fullscreenStatus);
        panel.Children.Add(_fullscreens);
        return new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    }

    private void Refresh()
    {
        _gallery.Children.Clear();
        _pictures.Children.Clear();
        _fullscreens.Children.Clear();
        if (_ctx.Root is not { } root || !PlatinumMugshots.IsInstalled(root))
        {
            _status.Text = "The mod \"Dialog mugshots\" is switched off – switch it on in Mods.";
            return;
        }
        var list = PlatinumMugshots.List(root);
        bool v11 = PlatinumMugshots.SupportsPictures(root);
        _leftPreview.Visibility = v11 ? Visibility.Visible : Visibility.Collapsed;
        _status.Text = $"{list.Count} mugshot(s)." + (v11 ? "" : " This project has version 1.0 of the mod: switch it off and on again in Mods to get left/right mugshots and pictures (your mugshots are kept as PNGs – add them again).");
        foreach (var m in list)
        {
            var img = Ui.Picture(2);
            img.Source = Load(root, m.PngRel, v11 && _leftPreview.IsChecked == true);
            var card = new StackPanel { Width = 150 };
            card.Children.Add(new Border { Child = img, Width = 136, Height = 136, Background = (Brush)Application.Current.FindResource("PanelBrush") });
            card.Children.Add(new TextBox { Text = m.Constant, IsReadOnly = true, FontSize = 11, Margin = new Thickness(0, 4, 0, 0), ToolTip = "Copy this into a script: ShowMugshot " + m.Constant });
            card.Children.Add(new TextBlock { Text = $"No. {m.Number}", FontSize = 10, Foreground = Brushes.Gray });
            _gallery.Children.Add(Ui.Card(card, new Thickness(0, 0, 8, 8)));
        }
        FillPictures(root);
        FillFullscreens(root);
    }

    private void FillFullscreens(string root)
    {
        if (!PlatinumMugshots.SupportsFullscreen(root)) { _fullscreenStatus.Text = "Needs version 1.2 of the mod – press \"⬆ Update\" in Mods (your mugshots and pictures are kept)."; return; }
        var list = PlatinumMugshots.ListFullscreen(root);
        _fullscreenStatus.Text = list.Count == 0 ? "No full-screen pictures yet." : $"{list.Count} full-screen picture(s).";
        foreach (var p in list)
        {
            var img = Ui.Picture();
            img.Source = Load(root, p.PngRel);
            var card = new StackPanel { Width = 280 };
            card.Children.Add(new Border { Width = 256, Height = 192, Background = Brushes.Black, Child = img });
            card.Children.Add(new TextBox { Text = p.Constant, IsReadOnly = true, FontSize = 11, Margin = new Thickness(0, 4, 0, 0), ToolTip = "Copy this into a script: ShowFullscreenPicture " + p.Constant });
            card.Children.Add(new TextBlock { Text = $"256×192 · no. {p.Number}", FontSize = 10, Foreground = Brushes.Gray });
            _fullscreens.Children.Add(Ui.Card(card, new Thickness(0, 0, 8, 8)));
        }
    }

    private void AddFullscreen()
    {
        if (_ctx.Root is not { } root) return;
        var dlg = new Microsoft.Win32.OpenFileDialog { Filter = "PNG pictures|*.png", Title = "Full-screen picture (256×192 is best; up to 255 colours)" };
        if (dlg.ShowDialog(Application.Current.MainWindow) != true) return;
        var name = new InputDialog("New full-screen picture", "Name of the picture (e.g. Old photo) – becomes FULLSCREEN_NAME.", ("Name", Path.GetFileNameWithoutExtension(dlg.FileName)));
        if (name.ShowDialog() != true) return;
        try
        {
            var (files, constant) = PlatinumMugshots.PlanAddFullscreen(root, name.Values[0], Png.Load(dlg.FileName).Rgba);
            if (PlatinumModules.Write(_ctx, "New full-screen picture", files,
                    $"Show it in a script with: ShowFullscreenPicture {constant}  (#include \"constants/mugshots.h\" at the top), or with the block \"🗺 Picture\".",
                    "The picture belongs to the mod: switching the mod off removes it again.", "Build the ROM afterwards."))
                Refresh();
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or PngFormatException) { ProjectWriter.Warn("New full-screen picture", ex.Message); }
    }

    private static BitmapSource? Load(string root, string rel, bool mirror = false)
    {
        try
        {
            var full = Path.Combine(root, rel);
            if (!File.Exists(full)) return null;
            var png = Png.Load(full);
            var rgba = png.Rgba.Clone();
            if (png.Indices != null) for (int i = 0; i < rgba.Pixels.Length; i++) if (png.Indices[i] == 0) rgba.Pixels[i] = Rgba32.Transparent;
            return Imaging.ToBitmap(mirror ? PlatinumMugshots.Mirror(rgba) : rgba);
        }
        catch (Exception ex) when (ex is IOException or PngFormatException) { return null; }
    }

    private void FillPictures(string root)
    {
        if (!PlatinumMugshots.SupportsPictures(root)) { _pictureStatus.Text = "Needs version 1.1 of the mod (switch it off and on again in Mods)."; return; }
        var pictures = PlatinumMugshots.ListPictures(root);
        _pictureStatus.Text = pictures.Count == 0 ? "No pictures yet." : $"{pictures.Count} picture(s).";
        foreach (var p in pictures)
        {
            var img = Ui.Picture();
            img.Source = Load(root, p.PngRel);
            var card = new StackPanel { Width = 280 };
            card.Children.Add(new Border { Height = 160, Background = (Brush)Application.Current.FindResource("PanelBrush"),
                Child = new Viewbox { Child = new Border { Child = img, Background = Imaging.Checkerboard(8) }, StretchDirection = StretchDirection.DownOnly } });
            card.Children.Add(new TextBox { Text = p.Constant, IsReadOnly = true, FontSize = 11, Margin = new Thickness(0, 4, 0, 0), ToolTip = "Copy this into a script: ShowPicture " + p.Constant });
            card.Children.Add(new TextBlock { Text = $"{p.Width}×{p.Height} pixels · {p.TilesW * p.TilesH} tiles · no. {p.Number}", FontSize = 10, Foreground = Brushes.Gray });
            _pictures.Children.Add(Ui.Card(card, new Thickness(0, 0, 8, 8)));
        }
    }

    private void AddPicture()
    {
        if (_ctx.Root is not { } root) return;
        var dlg = new Microsoft.Win32.OpenFileDialog { Filter = "PNG pictures|*.png", Title = "Picture (e.g. 128×96, up to 192 tiles of 8×8, 15 colours + transparency)" };
        if (dlg.ShowDialog(Application.Current.MainWindow) != true) return;
        var name = new InputDialog("New picture", "Name of the picture (e.g. Treasure map) – becomes PICTURE_NAME.", ("Name", Path.GetFileNameWithoutExtension(dlg.FileName)));
        if (name.ShowDialog() != true) return;
        try
        {
            var (files, constant) = PlatinumMugshots.PlanAddPicture(root, name.Values[0], Png.Load(dlg.FileName).Rgba);
            if (PlatinumModules.Write(_ctx, "New picture", files,
                    $"Show it in a script with: ShowPicture {constant} … HidePicture  (#include \"constants/mugshots.h\" at the top), or with the block \"🗺 Picture\".",
                    "The picture belongs to the mod: switching the mod off removes it again.", "Build the ROM afterwards."))
                Refresh();
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or PngFormatException) { ProjectWriter.Warn("New picture", ex.Message); }
    }

    private void AddFromPng()
    {
        if (_ctx.Root is not { } root) return;
        var dlg = new Microsoft.Win32.OpenFileDialog { Filter = "PNG pictures|*.png", Title = "Mugshot (64×64, up to 15 colours + transparency)" };
        if (dlg.ShowDialog(Application.Current.MainWindow) != true) return;
        var name = new InputDialog("New mugshot", "Name of the mugshot (e.g. Rowan, Mom, Barry) – becomes MUGSHOT_NAME.", ("Name", Path.GetFileNameWithoutExtension(dlg.FileName)));
        if (name.ShowDialog() != true) return;
        try { Add(root, name.Values[0], Png.Load(dlg.FileName).Rgba); }
        catch (Exception ex) when (ex is IOException or PngFormatException) { ProjectWriter.Warn("New mugshot", ex.Message); }
    }

    private void AddFromTrainerClass()
    {
        if (_ctx.Root is not { } root) return;
        var classes = _ctx.Platinum?.TrainerClasses.Where(c => PlatinumTrainers.ClassPicture(root, c) != null).ToList() ?? [];
        var filter = new TextBox { Margin = new Thickness(0, 0, 0, 6) };
        var list = new ListBox { ItemsSource = classes, Height = 320 };
        filter.TextChanged += (_, _) => list.ItemsSource = classes.Where(c => c.Contains(filter.Text.Trim(), StringComparison.OrdinalIgnoreCase)).ToList();
        var ok = new Button { Content = "Use", IsDefault = true, Margin = new Thickness(0, 6, 0, 0) };
        var panel = new DockPanel { Margin = new Thickness(12) };
        DockPanel.SetDock(filter, Dock.Top);
        DockPanel.SetDock(ok, Dock.Bottom);
        panel.Children.Add(filter);
        panel.Children.Add(ok);
        panel.Children.Add(list);
        var w = new Window { Title = "Mugshot from a trainer class picture", Content = panel, Width = 380, SizeToContent = SizeToContent.Height, Owner = Application.Current.MainWindow, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        ok.Click += (_, _) => w.DialogResult = list.SelectedItem != null;
        list.MouseDoubleClick += (_, _) => w.DialogResult = list.SelectedItem != null;
        if (w.ShowDialog() != true || list.SelectedItem is not string cls) return;
        try
        {
            var png = Png.Load(PlatinumTrainers.ClassPicture(root, cls)!);
            var rgba = png.Rgba.Clone();
            if (png.Indices != null) for (int i = 0; i < rgba.Pixels.Length; i++) if (png.Indices[i] == 0) rgba.Pixels[i] = Rgba32.Transparent;
            Add(root, cls.Replace("TRAINER_CLASS_", ""), PlatinumMugshots.FromTrainerPicture(rgba));
        }
        catch (Exception ex) when (ex is IOException or PngFormatException) { ProjectWriter.Warn("New mugshot", ex.Message); }
    }

    private void Add(string root, string name, RgbaImage picture)
    {
        try
        {
            var (files, constant) = PlatinumMugshots.PlanAdd(root, name, picture);
            if (PlatinumModules.Write(_ctx, "New mugshot", files,
                    $"Use it in a script with: ShowMugshot {constant}  (and #include \"constants/mugshots.h\" at the top).",
                    "The picture belongs to the mod: switching the mod off removes it again.", "Build the ROM afterwards."))
                Refresh();
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException) { ProjectWriter.Warn("New mugshot", ex.Message); }
    }
}
