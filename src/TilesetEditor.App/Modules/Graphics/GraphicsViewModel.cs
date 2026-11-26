using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using TilesetEditor.App.Infrastructure;
using TilesetEditor.App.Shell;
using TilesetEditor.Core.Gba;
using TilesetEditor.Core.Graphics;
using TilesetEditor.Core.Imaging;
using TilesetEditor.Core.Project;
using TilesetEditor.Export;

namespace TilesetEditor.App.Modules.Graphics;

public sealed record PaletteSwatch(Brush Brush, string Tip);

/// <summary>
/// In-game graphics (title screen, menus, text windows, battle backgrounds …): browse with the game's colors and
/// replace an image with a same-sized one – either mapped onto the current palette or with the new image's colors.
/// </summary>
public sealed class GraphicsViewModel : ObservableObject, IToolkitModule
{
    private readonly ProjectContext _ctx;
    private PngImage? _png;
    private GraphicInfo? _info;
    private Rgba32[]? _palette;

    public GraphicsViewModel(ProjectContext ctx)
    {
        _ctx = ctx;
        _ctx.ProjectChanged += (_, _) => _ = LoadAsync();
        ReplaceCommand = new RelayCommand(Replace, () => _info is { Indexed: true });
        ExportCommand = new RelayCommand(ExportCopy, () => _png != null);
        OpenFolderCommand = new RelayCommand(() =>
        {
            if (_ctx.Root != null && SelectedFile != null) Process.Start("explorer.exe", $"/select,\"{ProjectPaths.Combine(_ctx.Root, SelectedFile)}\"");
        }, () => SelectedFile != null);
        View = new GraphicsView { DataContext = this };
        _ = LoadAsync();
    }

    public string Title => "Graphics";
    public string Description => "Title screen, menus, windows …";
    public bool IsAvailable => true;
    public override string ToString() => Title;
    public FrameworkElement View { get; }
    public bool HasUnsavedChanges => false;
    public string? UnsavedChangesDescription => null;

    public ICommand ReplaceCommand { get; }
    public ICommand ExportCommand { get; }
    public ICommand OpenFolderCommand { get; }
    public ObservableCollection<GraphicsFolder> Folders { get; } = [];
    public ObservableCollection<string> Files { get; } = [];
    public ObservableCollection<PaletteSwatch> Swatches { get; } = [];

    private string _details = "", _summary = "";
    private BitmapSource? _preview;
    private int _zoom = 2;
    public string Details { get => _details; private set => Set(ref _details, value); }
    public string Summary { get => _summary; private set => Set(ref _summary, value); }
    public BitmapSource? Preview { get => _preview; private set => Set(ref _preview, value); }
    public IReadOnlyList<int> Zooms { get; } = [1, 2, 3, 4];
    public int Zoom { get => _zoom; set { if (Set(ref _zoom, value)) ShowFile(); } }

    private GraphicsFolder? _folder;
    public GraphicsFolder? Folder
    {
        get => _folder;
        set
        {
            if (!Set(ref _folder, value)) return;
            Files.Clear();
            if (value != null && _ctx.Root != null)
                foreach (var f in GameGraphics.Files(_ctx.Root, value.Rel)) Files.Add(f);
            SelectedFile = Files.FirstOrDefault();
        }
    }

    private string? _selectedFile;
    public string? SelectedFile { get => _selectedFile; set { if (Set(ref _selectedFile, value)) ShowFile(); } }

    private async Task LoadAsync()
    {
        var root = _ctx.Root;
        if (root == null) return;
        var keep = Folder?.Rel;
        var keepFile = SelectedFile;
        List<GraphicsFolder> folders;
        // Runs in the background on every project change (also for a Platinum project, which has no graphics/ folder).
        try { folders = await Task.Run(() => GameGraphics.Folders(root)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { folders = []; }
        Folders.Clear();
        foreach (var f in folders) Folders.Add(f);
        Summary = $"{folders.Sum(f => f.Count)} images in {folders.Count} folders under graphics/ (Pokémon, trainers, overworld sprites and tilesets have their own modules)";
        _folder = null;
        Folder = Folders.FirstOrDefault(f => f.Rel == keep) ?? Folders.FirstOrDefault();
        if (keepFile != null && Files.Contains(keepFile)) SelectedFile = keepFile;
    }

    private void ShowFile()
    {
        Preview = null;
        Swatches.Clear();
        _png = null;
        _info = null;
        _palette = null;
        var root = _ctx.Root;
        if (root == null || SelectedFile == null) { Details = ""; CommandManager.InvalidateRequerySuggested(); return; }
        try
        {
            _png = Png.Load(ProjectPaths.Combine(root, SelectedFile));
            _info = GameGraphics.Inspect(root, SelectedFile, _png);
            _palette = _info.PalRel != null ? JascPalette.Load(ProjectPaths.Combine(root, _info.PalRel)) : _png.Palette;
            Preview = ImageReplaceFlow.Scaled(GameGraphics.Render(_png, _palette), Zoom);
            if (_palette != null)
                for (int i = 0; i < _palette.Length; i++)
                {
                    var c = GbaColor.ToDisplayed(_palette[i] with { A = 255 });
                    Swatches.Add(new PaletteSwatch(new SolidColorBrush(Color.FromRgb(c.R, c.G, c.B)), $"Index {i}: {c.R},{c.G},{c.B}" + (i == 0 ? " (transparent)" : "")));
                }
            Details = $"{SelectedFile}\n{_info.Width}×{_info.Height} px · {(_info.Indexed ? $"indexed, {_info.BitDepth}-bit" : "not indexed")} · " +
                      $"{_info.UsedColors} colors used of {_info.PaletteSize}\n{_info.SourceText}" +
                      (File.Exists(Path.ChangeExtension(ProjectPaths.Combine(root, SelectedFile), ".bin"))
                          ? "\nHas a tilemap (.bin): the game arranges these tiles differently on screen – keep the layout of the tiles when you edit."
                          : "");
        }
        catch (Exception ex) when (ex is IOException or PngFormatException or FormatException)
        {
            Details = $"{SelectedFile}\nCould not be read: {ex.Message}";
        }
        CommandManager.InvalidateRequerySuggested();
    }

    private void ExportCopy()
    {
        if (_ctx.Root != null && SelectedFile != null) ImageReplaceFlow.SaveCopy(_ctx.Root, SelectedFile);
    }

    private void Replace()
    {
        var root = _ctx.Root;
        if (root == null || SelectedFile == null || _png == null || _info == null || _palette == null) return;
        if (ImageReplaceFlow.Run(root, SelectedFile, _png, _info, _palette)) ShowFile();
    }
}