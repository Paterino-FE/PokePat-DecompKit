using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using TilesetEditor.App.Infrastructure;
using TilesetEditor.App.Modules.GameData;
using TilesetEditor.App.Shell;
using TilesetEditor.Core.Platinum;
using TilesetEditor.Core.Project;
using TilesetEditor.Export;
using TilesetEditor.Toolkit;

namespace TilesetEditor.App.Platinum;

/// <summary>The modules of a Pokémon Platinum (pokeplatinum) project, in navigation order.</summary>
public static class PlatinumModules
{
    public static IEnumerable<IToolkitModule> Create(ProjectContext ctx, Action<string> navigate)
    {
        var events = new PlatinumEventsViewModel(ctx, navigate);
        return
    [
        new PlatinumOverviewViewModel(ctx, navigate),
        new PlatinumPokemonViewModel(ctx, navigate),
        new PlatinumTrainersViewModel(ctx),
        new PlatinumSpritesViewModel(ctx),
        events,
        new PlatinumMugshotsViewModel(ctx, navigate),
        new PlatinumFlagsViewModel(ctx, navigate),
        new GameDataViewModel(ctx, PlatinumGameData.Instance),
        new PlatinumMapsViewModel(ctx, (map, list, index) =>
        {
            navigate("Events & Dialogs");
            // After the module has shown itself (and re-read the files the map editor just saved).
            System.Windows.Application.Current.Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background, () => events.OpenEvent(map, list, index));
        }),
        new PlatinumEncountersViewModel(ctx),
        new PlatinumShopsViewModel(ctx),
        new PlatinumGraphicsViewModel(ctx),
        new Modules.Mods.ModsViewModel(ctx, GameKind.Platinum),
        new BackupsViewModel(ctx),
    ];
    }

    /// <summary>Writes planned files through the shared preview (file list → confirm → backup → apply, rolled back on error).</summary>
    public static bool Write(ProjectContext ctx, string title, IReadOnlyList<PlatinumFileChange> changes, params string[] notes)
    {
        var root = ctx.Root;
        if (root == null || changes.Count == 0) return false;
        var files = changes.Select(c =>
        {
            var full = ProjectPaths.Combine(root, c.Path);
            var exists = File.Exists(full);
            return new PlannedFile(c.Path, exists ? PlannedAction.Modify : PlannedAction.Create, c.Content, c.Description,
                exists ? Hashing.Sha256(File.ReadAllBytes(full)) : null);
        }).ToList();
        if (!ProjectWriter.Apply(root, title, "res", files, notes)) return false;
        ctx.Reload();
        return true;
    }
}

/// <summary>Small helpers for views built in code.</summary>
internal static class Ui
{
    public static TextBlock Title(string text) => new() { Text = text, Style = (Style)Application.Current.Resources["SectionTitle"] };

    public static TextBlock Hint(string text, double maxWidth = 900) => new()
    {
        Text = text, Style = (Style)Application.Current.Resources["Hint"], TextWrapping = TextWrapping.Wrap, MaxWidth = maxWidth,
        HorizontalAlignment = HorizontalAlignment.Left,
    };

    public static Button Button(string text, ICommand command, string? tip = null)
    {
        var b = new Button { Content = text, Command = command };
        if (tip != null) b.ToolTip = tip;
        return b;
    }

    public static Button Button(string text, Action click, string? tip = null)
    {
        var b = new Button { Content = text };
        b.Click += (_, _) => click();
        if (tip != null) b.ToolTip = tip;
        return b;
    }

    public static StackPanel Row(params UIElement[] children)
    {
        var p = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 4) };
        foreach (var c in children) p.Children.Add(c);
        return p;
    }

    public static Border Card(UIElement child, Thickness? margin = null) => new()
    {
        BorderBrush = (Brush)Application.Current.FindResource("BorderBrush"),
        Background = (Brush)Application.Current.FindResource("SurfaceBrush"),
        BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(6),
        Padding = new Thickness(10),
        Margin = margin ?? new Thickness(0, 0, 0, 10),
        Child = child,
    };

    public static TextBox Box(string bindingPath, double width = double.NaN, bool mono = false)
    {
        var t = new TextBox { Width = width, Margin = new Thickness(0, 2, 0, 2) };
        if (mono) t.FontFamily = new FontFamily("Consolas");
        t.SetBinding(TextBox.TextProperty, new Binding(bindingPath) { UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged });
        return t;
    }

    /// <summary>A searchable list on the left: filter box above a list.</summary>
    public static DockPanel FilteredList(ListBox list, TextBox filter, string filterName)
    {
        var dock = new DockPanel();
        System.Windows.Automation.AutomationProperties.SetName(filter, filterName);
        filter.Margin = new Thickness(0, 0, 0, 6);
        DockPanel.SetDock(filter, Dock.Top);
        dock.Children.Add(filter);
        dock.Children.Add(list);
        return dock;
    }

    public static Image Picture(double scale = 1)
    {
        var img = new Image { Stretch = Stretch.None, SnapsToDevicePixels = true, Margin = new Thickness(2) };
        RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.NearestNeighbor);
        if (scale != 1) img.LayoutTransform = new ScaleTransform(scale, scale);
        return img;
    }

    /// <summary>Takes a control out of its current parent, so a rebuilt view can host it again.</summary>
    public static void Detach(params FrameworkElement[] elements)
    {
        foreach (var e in elements)
        {
            switch (e.Parent)
            {
                case Panel p: p.Children.Remove(e); break;
                case Decorator d: d.Child = null; break;
                case ContentControl c: c.Content = null; break;
                case ItemsControl i: i.Items.Remove(e); break;
            }
        }
    }

    public static void OpenFolder(string path)
    {
        if (File.Exists(path)) Process.Start("explorer.exe", $"/select,\"{path}\"");
        else if (Directory.Exists(path)) Process.Start("explorer.exe", $"\"{path}\"");
    }
}

/// <summary>Project module for pokeplatinum: facts, build settings and what PokéPat offers for Platinum.</summary>
public sealed class PlatinumOverviewViewModel : ObservableObject, IToolkitModule
{
    private readonly DeferredLoad _reload;
    private readonly ProjectContext _ctx;
    private readonly Action<string> _navigate;
    private readonly TextBlock _facts = new() { FontFamily = new FontFamily("Consolas"), TextWrapping = TextWrapping.Wrap };
    private bool _dirty;

    public PlatinumOverviewViewModel(ProjectContext ctx, Action<string> navigate)
    {
        _ctx = ctx;
        _navigate = navigate;
        _reload = new DeferredLoad(Refresh);
        _ctx.PlatinumChanged += (_, _) => _reload.Request();
        SaveCommand = new RelayCommand(Save, () => _ctx.IsOpen && _dirty);
        BrowseEmulatorCommand = new RelayCommand(() =>
        {
            var dlg = new Microsoft.Win32.OpenFileDialog { Filter = "Programs|*.exe|All files|*.*", Title = "Choose the NDS emulator (e.g. melonDS.exe or DeSmuME.exe)" };
            if (dlg.ShowDialog(Application.Current.MainWindow) == true) EmulatorExe = dlg.FileName;
        });
        View = BuildView();
        _reload.Attach(View);
        Refresh();
    }

    public string Title => "Project";
    public string Description => "Overview and build settings";
    public bool IsAvailable => true;
    public override string ToString() => Title;
    public FrameworkElement View { get; }
    public bool HasUnsavedChanges => _dirty;
    public string? UnsavedChangesDescription => _dirty ? "Project: build settings are not saved yet." : null;

    public ICommand SaveCommand { get; }
    public ICommand BrowseEmulatorCommand { get; }

    private string _buildCommand = "", _romPath = "", _emulatorExe = "", _distro = "";
    public string BuildCommand { get => _buildCommand; set { if (Set(ref _buildCommand, value)) Dirty(); } }
    public string RomPath { get => _romPath; set { if (Set(ref _romPath, value)) Dirty(); } }
    public string EmulatorExe { get => _emulatorExe; set { if (Set(ref _emulatorExe, value)) Dirty(); } }
    public string BuildDistro { get => _distro; set { if (Set(ref _distro, value)) Dirty(); } }

    private bool _refreshing;
    private void Dirty()
    {
        if (_refreshing) return;
        _dirty = true;
        CommandManager.InvalidateRequerySuggested();
    }

    private FrameworkElement BuildView()
    {
        var p = new StackPanel { Margin = new Thickness(20), MaxWidth = 1000, HorizontalAlignment = HorizontalAlignment.Left };
        p.Children.Add(Ui.Title("Pokémon Platinum project"));
        p.Children.Add(Ui.Card(_facts));
        p.Children.Add(Ui.Row(
            Ui.Button("🐾 Pokémon & sprites →", () => _navigate("Pokémon")),
            Ui.Button("⚔ Trainers →", () => _navigate("Trainers")),
            Ui.Button("📖 Game data →", () => _navigate("Game data")),
            Ui.Button("🗺 Map preview →", () => _navigate("Maps")),
            Ui.Button("🧩 Mods →", () => _navigate("Mods"))));

        p.Children.Add(Ui.Title("What PokéPat does for Platinum"));
        p.Children.Add(Ui.Hint(
            "• Pokémon: battle sprites (normal and shiny) with replacing, level-up moves, evolutions.\n" +
            "• Trainers: names, classes, teams (species, level, item, moves), double battles, texts.\n" +
            "• Game data: base stats, types, abilities, moves, items, names of abilities, types, natures, trainer classes and places.\n" +
            "• Maps: a top-down preview of every map – walkable and blocked tiles, grass, water, doors, props and events. " +
            "Platinum maps are 3D models; there is no map editor for pokeplatinum yet, so maps cannot be edited here.\n" +
            "• Events & Dialogs: people, signs, warps, triggers, texts with in-game preview, script files; new talking people in one step.\n" +
            "• Graphics: the battle healthboxes – checked for true 4-bit PNGs and the right palette order, repaired and replaced.\n" +
            "• Mods: Exp. Share as an on/off key item, level caps by badges (adjustable), reusable TMs, HMs without the move, dialog mugshots – switched on and off (off = original files).\n" +
            "Every change shows the list of files first, makes a backup and is rolled back completely if anything fails."));

        p.Children.Add(Ui.Title("Build settings"));
        var grid = new Grid { Margin = new Thickness(0, 0, 0, 10) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(170) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        void AddRow(string label, FrameworkElement editor, FrameworkElement? extra = null, string? tip = null)
        {
            int r = grid.RowDefinitions.Count;
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var l = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center };
            Grid.SetRow(l, r);
            grid.Children.Add(l);
            Grid.SetRow(editor, r); Grid.SetColumn(editor, 1);
            if (tip != null) editor.ToolTip = tip;
            grid.Children.Add(editor);
            if (extra != null) { Grid.SetRow(extra, r); Grid.SetColumn(extra, 2); extra.Margin = new Thickness(6, 0, 0, 0); grid.Children.Add(extra); }
        }
        AddRow("Build command", Ui.Box(nameof(BuildCommand), mono: true), tip: "Runs in the project folder (WSL: bash -lc). pokeplatinum: make release (plain make also compares the ROM with the original game and fails once you changed something).");
        AddRow("ROM file (relative)", Ui.Box(nameof(RomPath), mono: true), tip: "Empty = build/pokeplatinum.us.nds");
        AddRow("WSL distribution", Ui.Box(nameof(BuildDistro), 160));
        AddRow("Emulator (NDS)", Ui.Box(nameof(EmulatorExe)), Ui.Button("…", BrowseEmulatorCommand), "melonDS or DeSmuME; opened after a successful build.");
        p.Children.Add(grid);
        p.Children.Add(Ui.Row(Ui.Button("Save settings", SaveCommand)));
        p.Children.Add(Ui.Hint("The first build of pokeplatinum downloads its compiler (metroskrew) and takes several minutes. " +
                               "Build tools on Ubuntu: sudo apt install bison flex g++ gcc-arm-none-eabi git make ninja-build pkg-config python3 – \"Setup…\" can install them."));
        return new ScrollViewer { Content = p, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, DataContext = this };
    }

    private void Refresh()
    {
        _refreshing = true;
        try
        {
            var pl = _ctx.Platinum;
            if (pl == null) { _facts.Text = "No Platinum project is open."; return; }
            var env = _ctx.Environment;
            var lines = new List<string>
            {
                $"Folder:       {pl.Root}",
                $"Git:          {(env?.IsGitRepository == true ? $"{env.GitBranch} ({env.GitCommit})" : "–")}",
                $"Pokémon:      {pl.Species.Count}",
                $"Moves:        {pl.Moves.Count}",
                $"Items:        {pl.Items.Count}",
                $"Trainers:     {pl.TrainerCount}",
                $"Map headers:  {pl.MapHeaders.Count}",
                $"ROM:          {_ctx.RomRelativePath}",
            };
            if (pl.Warnings.Count > 0) lines.Add("Notes:        " + string.Join("\n              ", pl.Warnings));
            _facts.Text = string.Join("\n", lines);
            var t = _ctx.Tools;
            BuildCommand = t.BuildCommand;
            RomPath = t.RomPath ?? "";
            EmulatorExe = t.EmulatorExe ?? "";
            BuildDistro = t.BuildWslDistro;
            _dirty = false;
        }
        finally { _refreshing = false; }
    }

    private void Save()
    {
        if (_ctx.Root == null) return;
        var t = _ctx.Tools;
        t.BuildCommand = string.IsNullOrWhiteSpace(BuildCommand) ? ProjectToolSettings.DefaultBuildCommand : BuildCommand.Trim();
        t.RomPath = string.IsNullOrWhiteSpace(RomPath) ? null : RomPath.Trim();
        t.EmulatorExe = string.IsNullOrWhiteSpace(EmulatorExe) ? null : EmulatorExe.Trim();
        if (!string.IsNullOrWhiteSpace(BuildDistro)) t.BuildWslDistro = BuildDistro.Trim();
        _ctx.Save();
        _dirty = false;
        CommandManager.InvalidateRequerySuggested();
        Refresh();
    }
}
