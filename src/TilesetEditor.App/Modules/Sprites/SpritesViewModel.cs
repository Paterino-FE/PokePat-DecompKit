using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using TilesetEditor.App.Infrastructure;
using TilesetEditor.App.Shell;
using TilesetEditor.Core.Gba;
using TilesetEditor.Core.Imaging;
using TilesetEditor.Core.Sprites;
using TilesetEditor.Export;

namespace TilesetEditor.App.Modules.Sprites;

/// <summary>
/// Overworld sprites of the branch (overview with game palette), check for new sprite sheets (frame size, frame count,
/// colors, matching palette) and registration of a new sprite on an existing palette (SpriteImportPlanner).
/// </summary>
public sealed class SpritesViewModel : ObservableObject, IToolkitModule
{
    private readonly ProjectContext _ctx;
    private List<OverworldSprite> _all = [];
    private Dictionary<string, Rgba32[]> _palettes = [];

    public SpritesViewModel(ProjectContext ctx)
    {
        _ctx = ctx;
        _ctx.ProjectChanged += (_, _) => _ = LoadAsync();
        CheckCommand = new RelayCommand(CheckNewSprite, () => _ctx.IsOpen);
        ImportCommand = new RelayCommand(Import, () => _ctx.IsOpen && _checkedPath != null);
        ReplaceOverworldCommand = new RelayCommand(() =>
        {
            if (Selected?.PngPath is { } rel)
                Replace(rel, Selected.PalettePath, Selected.PalettePath != null
                    ? $"Palette {Selected.PaletteTag} is used by {_all.Count(s => s.PaletteTag == Selected.PaletteTag)} sprite(s) – new colors change all of them." : null);
        }, () => Selected?.PngPath != null);
        SaveOverworldCopyCommand = new RelayCommand(() => { if (_ctx.Root != null && Selected?.PngPath != null) ImageReplaceFlow.SaveCopy(_ctx.Root, Selected.PngPath); }, () => Selected?.PngPath != null);
        ReplacePlayerCommand = new RelayCommand(() =>
        {
            if (SelectedPlayer is { } g) Replace(g.Rel, g.PalRel, g.PaletteShared ? g.Note : g.Title.StartsWith("Battle: front", StringComparison.Ordinal) ? "The battle back picture uses this palette too." : null);
        }, () => SelectedPlayer != null);
        SavePlayerCopyCommand = new RelayCommand(() => { if (_ctx.Root != null && SelectedPlayer != null) ImageReplaceFlow.SaveCopy(_ctx.Root, SelectedPlayer.Rel); }, () => SelectedPlayer != null);
        ReplaceTrainerPicCommand = new RelayCommand(() => { if (SelectedTrainerPic?.PngRel is { } rel) Replace(rel, SelectedTrainerPic.PalRel, null); }, () => SelectedTrainerPic?.PngRel != null);
        SaveTrainerPicCopyCommand = new RelayCommand(() => { if (_ctx.Root != null && SelectedTrainerPic?.PngRel != null) ImageReplaceFlow.SaveCopy(_ctx.Root, SelectedTrainerPic.PngRel); }, () => SelectedTrainerPic?.PngRel != null);
        AddTrainerPicCommand = new RelayCommand(AddTrainerPic, () => _ctx.IsOpen);
        View = new SpritesView { DataContext = this };
        _ = LoadAsync();
    }

    public string Title => "Sprites";
    public string Description => "View, check and register overworld sprites";
    public bool IsAvailable => true;
    public override string ToString() => Title;
    public FrameworkElement View { get; }
    public bool HasUnsavedChanges => false;
    public string? UnsavedChangesDescription => null;

    public ICommand CheckCommand { get; }
    public ICommand ImportCommand { get; }
    public ICommand ReplaceOverworldCommand { get; }
    public ICommand SaveOverworldCopyCommand { get; }
    public ICommand ReplacePlayerCommand { get; }
    public ICommand SavePlayerCopyCommand { get; }
    public ICommand ReplaceTrainerPicCommand { get; }
    public ICommand SaveTrainerPicCopyCommand { get; }
    public ICommand AddTrainerPicCommand { get; }

    // ---------------- Player character and trainer pictures
    public ObservableCollection<PlayerGraphic> PlayerGraphics { get; } = [];
    public ObservableCollection<TrainerPic> TrainerPics { get; } = [];
    private List<TrainerPic> _allPics = [];
    private PlayerGraphic? _selectedPlayer;
    private TrainerPic? _selectedTrainerPic;
    private BitmapSource? _playerPreview, _trainerPicPreview;
    private string _playerDetails = "", _trainerPicDetails = "", _trainerPicFilter = "";
    public PlayerGraphic? SelectedPlayer { get => _selectedPlayer; set { if (Set(ref _selectedPlayer, value)) ShowPlayer(); } }
    public TrainerPic? SelectedTrainerPic { get => _selectedTrainerPic; set { if (Set(ref _selectedTrainerPic, value)) ShowTrainerPic(); } }
    public BitmapSource? PlayerPreview { get => _playerPreview; private set => Set(ref _playerPreview, value); }
    public BitmapSource? TrainerPicPreview { get => _trainerPicPreview; private set => Set(ref _trainerPicPreview, value); }
    public string PlayerDetails { get => _playerDetails; private set => Set(ref _playerDetails, value); }
    public string TrainerPicDetails { get => _trainerPicDetails; private set => Set(ref _trainerPicDetails, value); }
    public string TrainerPicFilter { get => _trainerPicFilter; set { if (Set(ref _trainerPicFilter, value)) ApplyPics(); } }

    private void ApplyPics()
    {
        var keep = SelectedTrainerPic?.Constant;
        TrainerPics.Clear();
        foreach (var p in _allPics.Where(p => TrainerPicFilter.Trim().Length == 0 || p.Constant.Contains(TrainerPicFilter.Trim(), StringComparison.OrdinalIgnoreCase)))
            TrainerPics.Add(p);
        SelectedTrainerPic = TrainerPics.FirstOrDefault(p => p.Constant == keep) ?? TrainerPics.FirstOrDefault();
    }

    /// <summary>Preview of an image in game colors (palette from the .pal if given) and a description line.</summary>
    private (BitmapSource? Image, string Text) Show(string rel, string? palRel, int zoom)
    {
        var root = _ctx.Root;
        if (root == null) return (null, "");
        try
        {
            var png = Png.Load(Path.Combine(root, rel));
            var pal = palRel != null ? JascPalette.Load(Path.Combine(root, palRel)) : png.Palette;
            var img = TilesetEditor.Core.Graphics.GameGraphics.Render(png, pal);
            return (ImageReplaceFlow.Scaled(img, zoom), $"{rel}\n{png.Width}×{png.Height} px, {(png.IsIndexed ? $"{png.BitDepth}-bit indexed" : "not indexed")}\nColors: {palRel ?? "the image's own palette"}");
        }
        catch (Exception ex) when (ex is IOException or PngFormatException or FormatException)
        {
            return (null, $"{rel}\nCould not be read: {ex.Message}");
        }
    }

    private void ShowPlayer()
    {
        if (SelectedPlayer is not { } g) { PlayerPreview = null; PlayerDetails = ""; return; }
        var (img, text) = Show(g.Rel, g.PalRel, 3);
        PlayerPreview = img;
        PlayerDetails = text + "\n" + g.Note;
    }

    private void ShowTrainerPic()
    {
        if (SelectedTrainerPic is not { } p || p.PngRel == null) { TrainerPicPreview = null; TrainerPicDetails = SelectedTrainerPic == null ? "" : "No picture registered for this constant."; return; }
        var (img, text) = Show(p.PngRel, p.PalRel, 3);
        TrainerPicPreview = img;
        TrainerPicDetails = $"{p.Constant} = {p.Id}\n{text}";
    }

    private void Replace(string rel, string? palRel, string? note)
    {
        var root = _ctx.Root;
        if (root == null) return;
        PngImage png;
        Rgba32[] palette;
        try
        {
            png = Png.Load(Path.Combine(root, rel));
            palette = palRel != null ? JascPalette.Load(Path.Combine(root, palRel)) : png.Palette ?? [];
        }
        catch (Exception ex) when (ex is IOException or PngFormatException or FormatException)
        {
            ProjectWriter.Warn("Replace image", ex.Message);
            return;
        }
        var info = TilesetEditor.Core.Graphics.GameGraphics.Inspect(root, rel, png);
        if (palRel != null) info = info with { Source = TilesetEditor.Core.Graphics.PaletteSource.OwnPalFile, PalRel = palRel };
        if (!ImageReplaceFlow.Run(root, rel, png, info, palette, note)) return;
        _ctx.Reload();
    }

    private void AddTrainerPic()
    {
        var root = _ctx.Root;
        if (root == null) return;
        var dlg = new OpenFileDialog { Filter = "PNG|*.png", Title = "Trainer picture (64×64, max. 15 colors + transparency)" };
        if (dlg.ShowDialog(Application.Current.MainWindow) != true) return;
        try
        {
            var problems = TrainerPicPlanner.Check(Png.Load(dlg.FileName));
            if (problems.Count > 0) { ProjectWriter.Warn("New trainer picture", string.Join("\n", problems)); return; }
        }
        catch (PngFormatException ex) { ProjectWriter.Warn("New trainer picture", ex.Message); return; }
        var guess = string.Concat(System.Text.RegularExpressions.Regex.Split(Path.GetFileNameWithoutExtension(dlg.FileName), "[^A-Za-z0-9]+")
            .Where(p => p.Length > 0).Select(p => char.ToUpperInvariant(p[0]) + p[1..]));
        var input = new InputDialog("New trainer picture", "Name of the picture (letters and digits, starting with an uppercase letter). It becomes TRAINER_PIC_<NAME>.",
            ("Name (e.g. SeaCaptain)", guess.Length > 0 && char.IsLetter(guess[0]) ? guess : "NewTrainer"));
        if (input.ShowDialog() != true) return;
        try
        {
            var result = TrainerPicPlanner.Plan(root, input.Values[0], dlg.FileName);
            if (!ImageReplaceFlow.ConfirmComparison(TilesetEditor.Core.Graphics.GameGraphics.Render(Png.Load(dlg.FileName)),
                    TilesetEditor.Core.Graphics.GameGraphics.Render(Png.Decode(result.Plan.Files[0].Content)))) return;
            if (!ProjectWriter.Apply(root, result.Plan.TilesetName, result.Plan.TilesetFolder, result.Plan.Files, [.. result.Plan.Notes])) return;
            TrainerPicFilter = result.Constant;
        }
        catch (ExportValidationException ex)
        {
            ProjectWriter.Warn("New trainer picture", string.Join("\n", ex.Errors));
            return;
        }
        _ctx.Reload();
    }
    /// <summary>Steps 2 and 3 open once a checked PNG without errors is selected.</summary>
    public bool CanImport => _checkedPath != null;
    public ObservableCollection<string> PaletteTags { get; } = [];
    private string? _checkedPath;
    private string _newName = "NewPerson", _paletteTag = "OBJ_EVENT_PAL_TAG_NPC_1";
    private bool _allowNearest;
    public string NewName { get => _newName; set { if (Set(ref _newName, value)) OnPropertyChanged(nameof(ConstantPreview)); } }
    public string PaletteTag { get => _paletteTag; set => Set(ref _paletteTag, value); }
    public bool AllowNearest { get => _allowNearest; set => Set(ref _allowNearest, value); }
    private bool _useNewPalette;
    private string _newPaletteName = "";
    public bool UseNewPalette { get => _useNewPalette; set { if (Set(ref _useNewPalette, value)) { OnPropertyChanged(nameof(UseExistingPalette)); OnPropertyChanged(nameof(PaletteHint)); } } }
    public bool UseExistingPalette { get => !_useNewPalette; set => UseNewPalette = !value; }
    public string NewPaletteName { get => _newPaletteName; set { if (Set(ref _newPaletteName, value)) OnPropertyChanged(nameof(PaletteHint)); } }
    public string PaletteHint => UseNewPalette
        ? (NewPaletteName.Trim().Length > 0 ? TilesetEditor.Export.SpriteImportPlanner.ToPaletteTag(NewPaletteName.Trim()) + " – up to 15 colors + transparency" : "Enter a name for the new palette")
        : "Suggested: a palette that contains all colors of the image";
    public string ConstantPreview => TilesetEditor.Export.SpriteImportPlanner.ToConstant(NewName);
    public ObservableCollection<OverworldSprite> Sprites { get; } = [];
    public IReadOnlyList<string> FrameSizes { get; } = OverworldSprites.FrameSizes.Select(s => $"{s.W}x{s.H}").ToList();

    private string _filter = "", _summary = "", _details = "", _checkReport = "";
    private string _frameSize = "16x32";
    private BitmapSource? _preview, _checkPreview;
    public string Filter { get => _filter; set { if (Set(ref _filter, value)) Apply(); } }
    public string Summary { get => _summary; private set => Set(ref _summary, value); }
    public string Details { get => _details; private set => Set(ref _details, value); }
    public BitmapSource? Preview { get => _preview; private set => Set(ref _preview, value); }
    public string FrameSize { get => _frameSize; set => Set(ref _frameSize, value); }
    public string CheckReport { get => _checkReport; private set => Set(ref _checkReport, value); }
    public BitmapSource? CheckPreview { get => _checkPreview; private set => Set(ref _checkPreview, value); }

    private OverworldSprite? _selected;
    public OverworldSprite? Selected
    {
        get => _selected;
        set
        {
            if (!Set(ref _selected, value)) return;
            ShowSelected();
        }
    }

    private async Task LoadAsync()
    {
        var root = _ctx.Root;
        if (root == null) return;
        Summary = "Reading sprite registration …";
        var (sprites, palettes) = await Task.Run(() =>
        {
            var s = OverworldSprites.Load(root);
            var p = new Dictionary<string, Rgba32[]>();
            foreach (var (tag, path) in OverworldSprites.Palettes(s))
            {
                try { p[tag] = JascPalette.Load(Path.Combine(root, path)); }
                catch (Exception ex) when (ex is IOException or FormatException) { }
            }
            return (s, p);
        });
        _all = sprites;
        _palettes = palettes;
        var keepPlayer = SelectedPlayer?.Rel;
        PlayerGraphics.Clear();
        foreach (var g in CharacterGraphics.Player(root, sprites)) PlayerGraphics.Add(g);
        SelectedPlayer = PlayerGraphics.FirstOrDefault(g => g.Rel == keepPlayer) ?? PlayerGraphics.FirstOrDefault();
        _allPics = await Task.Run(() => CharacterGraphics.TrainerPics(root));
        ApplyPics();
        PaletteTags.Clear();
        foreach (var tag in palettes.Keys.OrderBy(k => k)) PaletteTags.Add(tag);
        Summary = $"{_all.Count} overworld sprites (OBJ_EVENT_GFX_…), {_palettes.Count} palettes · source: src/data/object_events/*";
        Apply();
    }

    private void Apply()
    {
        var f = Filter.Trim();
        Sprites.Clear();
        foreach (var s in _all.Where(s => f.Length == 0 || s.GfxId.Contains(f, StringComparison.OrdinalIgnoreCase) || s.PaletteTag.Contains(f, StringComparison.OrdinalIgnoreCase)))
            Sprites.Add(s);
    }

    private static BitmapSource Scale(RgbaImage img, int factor)
    {
        var big = new RgbaImage(img.Width * factor, img.Height * factor);
        for (int y = 0; y < big.Height; y++) for (int x = 0; x < big.Width; x++) big[x, y] = img[x / factor, y / factor];
        return Imaging.ToBitmap(big);
    }

    private void ShowSelected()
    {
        var s = Selected;
        Preview = null;
        if (s == null || _ctx.Root == null) { Details = ""; return; }
        Details = $"{s.GfxId}\nGraphics info: {s.InfoSymbol}\nFrame size: {s.Width}×{s.Height}\nPalette: {s.PaletteTag} ({s.PalettePath ?? "–"})\nImage: {s.PngPath ?? "–"}";
        if (s.PngPath == null) return;
        try
        {
            var png = Png.Load(Path.Combine(_ctx.Root, s.PngPath));
            Preview = Scale(OverworldSprites.Render(png, _palettes.GetValueOrDefault(s.PaletteTag)), 3);
            Details += $"\nImage size: {png.Width}×{png.Height} = {(s.Width > 0 && s.Height > 0 ? png.Width / s.Width * (png.Height / s.Height) : 0)} frames";
        }
        catch (Exception ex) when (ex is IOException or PngFormatException)
        {
            Details += "\nCould not load image: " + ex.Message;
        }
    }

    private void Import()
    {
        var root = _ctx.Root;
        if (root == null || _checkedPath == null) return;
        var parts = FrameSize.Split('x');
        TilesetEditor.Export.SpriteImportResult result;
        try
        {
            result = TilesetEditor.Export.SpriteImportPlanner.Plan(root, new TilesetEditor.Export.SpriteImportRequest(NewName.Trim(), _checkedPath,
                int.Parse(parts[0]), int.Parse(parts[1]), PaletteTag, AllowNearest, UseNewPalette ? NewPaletteName.Trim() : null), _all, _palettes);
        }
        catch (TilesetEditor.Export.ExportValidationException ex)
        {
            MessageBox.Show(Application.Current.MainWindow, ex.Message, "Register sprite", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (result.ColorChanges.Count > 0 && MessageBox.Show(Application.Current.MainWindow,
                "These colors are not in the palette and will be replaced:\n\n" +
                string.Join("\n", result.ColorChanges.Take(12).Select(c => $"  {c.From.ToHex()} → {c.To.ToHex()}")) + "\n\nContinue?",
                "Confirm color loss", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        if (new TilesetEditor.App.Modules.Tilesets.ExportPlanWindow(result.Plan) { Owner = Application.Current.MainWindow }.ShowDialog() != true) return;
        var applied = new TilesetEditor.Export.ExportApplier().Apply(result.Plan);
        if (!applied.Success)
        {
            MessageBox.Show(Application.Current.MainWindow, "Error: " + applied.Error + (applied.RolledBack ? "\nAll changes were rolled back." : ""),
                "Register sprite", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }
        CheckReport += $"\n\n{result.Constant} was registered. It can now be selected when adding a person to a map and in Porymap.";
        _checkedPath = null;
        OnPropertyChanged(nameof(CanImport));
        _ctx.Reload();
    }

    private void CheckNewSprite()
    {
        var dlg = new OpenFileDialog { Filter = "PNG|*.png", Title = "Check new overworld sprite" };
        if (dlg.ShowDialog(Application.Current.MainWindow) != true) return;
        var parts = FrameSize.Split('x');
        int fw = int.Parse(parts[0]), fh = int.Parse(parts[1]);
        try
        {
            var png = Png.Load(dlg.FileName);
            var r = OverworldSprites.Check(png, fw, fh, _palettes);
            var lines = new List<string>
            {
                $"{Path.GetFileName(dlg.FileName)}: {png.Width}×{png.Height}, {(png.IsIndexed ? "indexed" : "RGBA")}",
                $"Frames ({fw}×{fh}): {r.Frames} · colors: {r.Colors.Count} (+ transparency)",
                r.MatchingPalettes.Count > 0 ? $"Matches existing palette: {string.Join(", ", r.MatchingPalettes)}" : "Matches no existing palette.",
            };
            lines.AddRange(r.Issues.Select(i => (i.IsError ? "Error: " : "Note: ") + i.Message));
            lines.Add(r.HasErrors ? "Result: please fix the errors in the image first." : "Result: OK. Continue with step 2.");
            CheckReport = string.Join("\n", lines);
            _checkedPath = r.HasErrors ? null : dlg.FileName;
            OnPropertyChanged(nameof(CanImport));
            if (r.MatchingPalettes.FirstOrDefault() is { } best) { PaletteTag = best; UseNewPalette = false; }
            else UseNewPalette = r.Colors.Count <= 15;
            NewName = string.Concat(System.Text.RegularExpressions.Regex.Split(Path.GetFileNameWithoutExtension(dlg.FileName), "[^A-Za-z0-9]+")
                .Where(p => p.Length > 0).Select(p => char.ToUpperInvariant(p[0]) + p[1..]));
            if (NewName.Length == 0 || !char.IsLetter(NewName[0])) NewName = "NewPerson";
            if (NewPaletteName.Trim().Length == 0) NewPaletteName = NewName;
            CommandManager.InvalidateRequerySuggested();
            var pal = r.MatchingPalettes.FirstOrDefault() is { } tag ? _palettes[tag] : null;
            CheckPreview = Scale(OverworldSprites.Render(png, png.IsIndexed ? pal : null), 3);
        }
        catch (PngFormatException ex)
        {
            CheckReport = ex.Message;
            CheckPreview = null;
        }
    }
}
