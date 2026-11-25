using System.Collections.ObjectModel;
using System.Diagnostics;
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
using TilesetEditor.Export.Mods;

namespace TilesetEditor.App.Modules.Mods;

/// <summary>A mod in the list with its state for the open project.</summary>
public sealed class ModVm(ModPackage? package, InstalledMod? installed, ModCheck? check)
{
    public ModPackage? Package { get; } = package;
    public InstalledMod? Installed { get; } = installed;
    public ModCheck? Check { get; } = check;
    public string Id => Package?.Manifest.Id ?? Installed!.Id;
    public string Name => Package?.Manifest.Name ?? Installed!.Name;
    public string Version => Package?.Manifest.Version ?? Installed!.Version;
    public string Category => Package?.Manifest.Category ?? "";
    public bool IsInstalled => Installed != null;
    public string State => IsInstalled ? $"Installed ({Installed!.Version})"
        : Check is { CanInstall: true } ? "Available" : "Does not fit this project";
    public string StateColor => IsInstalled ? "#15803D" : Check is { CanInstall: true } ? "#7D6B69" : "#B8212B";
    public string Description => Package?.Manifest.Description ?? "The mod folder is not available any more – it can still be uninstalled.";
    public string Usage => Package?.Manifest.Usage ?? "";
    public string Problems => Check == null || IsInstalled || Check.Problems.Count == 0 ? "" : "Why it does not fit:\n• " + string.Join("\n• ", Check.Problems);
    public string Changes => Package == null ? "" :
        $"New files: {string.Join(", ", Package.Manifest.Files.Select(f => f.To))}\n" +
        $"Changed files: {string.Join(", ", Package.Manifest.Edits.Select(e => e.File).Distinct())}";
}

public sealed record PortraitVm(string Constant, BitmapSource? Image);

/// <summary>
/// Mods: extensions that PokePat installs into the game (new C code, script commands, data) – checked against the project
/// first, shown as a file list, installed with a backup and removable again. Mods with own content (dialog portraits) get
/// their tools here as well.
/// </summary>
public sealed class ModsViewModel : ObservableObject, IToolkitModule
{
    private readonly ProjectContext _ctx;

    public ModsViewModel(ProjectContext ctx)
    {
        _ctx = ctx;
        _ctx.ProjectChanged += (_, _) => Refresh();
        InstallCommand = new RelayCommand(Install, () => Selected is { IsInstalled: false, Check.CanInstall: true } && _ctx.IsOpen);
        UninstallCommand = new RelayCommand(Uninstall, () => Selected is { IsInstalled: true } && _ctx.IsOpen);
        OpenModsFolderCommand = new RelayCommand(() =>
        {
            Directory.CreateDirectory(UserModsFolder);
            Process.Start("explorer.exe", $"\"{UserModsFolder}\"");
        });
        AddModCommand = new RelayCommand(AddMod);
        AddPortraitCommand = new RelayCommand(AddPortraitFromPng, () => ShowPortraits);
        AddTrainerPortraitCommand = new RelayCommand(AddPortraitFromTrainer, () => ShowPortraits);
        View = new ModsView { DataContext = this };
        Refresh();
    }

    public string Title => "Mods";
    public string Description => "Install extensions into your game";
    public bool IsAvailable => true;
    public override string ToString() => Title;
    public FrameworkElement View { get; }
    public bool HasUnsavedChanges => false;
    public string? UnsavedChangesDescription => null;

    public ICommand InstallCommand { get; }
    public ICommand UninstallCommand { get; }
    public ICommand OpenModsFolderCommand { get; }
    public ICommand AddModCommand { get; }
    public ICommand AddPortraitCommand { get; }
    public ICommand AddTrainerPortraitCommand { get; }

    public ObservableCollection<ModVm> Mods { get; } = [];
    public ObservableCollection<PortraitVm> Portraits { get; } = [];

    private ModVm? _selected;
    public ModVm? Selected
    {
        get => _selected;
        set { if (Set(ref _selected, value)) { OnPropertyChanged(nameof(ShowPortraits)); LoadPortraits(); } }
    }

    private string _summary = "";
    public string Summary { get => _summary; private set => Set(ref _summary, value); }

    public bool ShowPortraits => Selected is { Id: DialogPortraits.ModId, IsInstalled: true } && _ctx.Root != null;

    /// <summary>Mods shipped with PokePat (next to the program) and the user's own (Documents\PokePat Mods).</summary>
    public static IEnumerable<string> ModFolders()
    {
        yield return Path.Combine(AppContext.BaseDirectory, "mods");
        // While developing: the repository's mods folder.
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "TilesetEditor.sln"))) { yield return Path.Combine(d.FullName, "mods"); break; }
        yield return UserModsFolder;
    }

    public static string UserModsFolder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "PokePat Mods");

    private void Refresh()
    {
        var keep = Selected?.Id;
        Mods.Clear();
        var (packages, problems) = ModPackage.LoadAll(ModFolders());
        var root = _ctx.Root;
        var installed = root != null ? ModRegistry.Installed(root) : [];
        foreach (var p in packages)
            Mods.Add(new ModVm(p, installed.FirstOrDefault(i => i.Id == p.Manifest.Id), root != null ? ModPlanner.Check(root, p, _ctx.Project?.ExpansionVersion) : null));
        foreach (var i in installed.Where(i => packages.All(p => p.Manifest.Id != i.Id)))
            Mods.Add(new ModVm(null, i, null));
        Summary = $"{Mods.Count} mod(s), {Mods.Count(m => m.IsInstalled)} installed in this project" +
                  (problems.Count > 0 ? " · could not read: " + string.Join("; ", problems) : "") +
                  (root == null ? " · open a project to install mods" : "");
        Selected = Mods.FirstOrDefault(m => m.Id == keep) ?? Mods.FirstOrDefault();
        CommandManager.InvalidateRequerySuggested();
    }

    private void Install()
    {
        if (Selected?.Package is not { } p || _ctx.Root is not { } root) return;
        ExportPlan plan;
        try { plan = ModPlanner.PlanInstall(root, p, _ctx.Project?.ExpansionVersion); }
        catch (ExportValidationException ex) { ProjectWriter.Warn("Install mod", string.Join("\n", ex.Errors)); return; }
        if (ProjectWriter.Apply(root, plan.TilesetName, plan.TilesetFolder, plan.Files.ToList(), [.. plan.Notes])) _ctx.Reload();
        Refresh();
    }

    private void Uninstall()
    {
        if (Selected?.Installed is not { } i || _ctx.Root is not { } root) return;
        var (plan, _) = ModPlanner.PlanUninstall(root, i);
        if (ProjectWriter.Apply(root, plan.TilesetName, plan.TilesetFolder, plan.Files.ToList(), [.. plan.Notes])) _ctx.Reload();
        Refresh();
    }

    /// <summary>Copies a mod folder (with mod.json) into Documents\PokePat Mods.</summary>
    private void AddMod()
    {
        var dlg = new OpenFolderDialog { Title = "Folder of a PokePat mod (contains mod.json)" };
        if (dlg.ShowDialog(Application.Current.MainWindow) != true) return;
        if (!File.Exists(Path.Combine(dlg.FolderName, ModPackage.ManifestFile)))
        {
            ProjectWriter.Warn("Add mod", "This folder has no mod.json – it is not a PokePat mod.");
            return;
        }
        try
        {
            var target = Path.Combine(UserModsFolder, Path.GetFileName(dlg.FolderName.TrimEnd('\\', '/')));
            foreach (var f in Directory.EnumerateFiles(dlg.FolderName, "*", SearchOption.AllDirectories))
            {
                var t = Path.Combine(target, Path.GetRelativePath(dlg.FolderName, f));
                Directory.CreateDirectory(Path.GetDirectoryName(t)!);
                File.Copy(f, t, overwrite: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { ProjectWriter.Warn("Add mod", ex.Message); }
        Refresh();
    }

    // ---- Dialog portraits

    private void LoadPortraits()
    {
        Portraits.Clear();
        if (!ShowPortraits || _ctx.Root is not { } root) return;
        foreach (var p in DialogPortraits.List(root))
        {
            BitmapSource? img = null;
            if (p.PngRel != null && File.Exists(Path.Combine(root, p.PngRel)))
            {
                try
                {
                    var png = Png.Load(Path.Combine(root, p.PngRel));
                    var palPath = Path.ChangeExtension(Path.Combine(root, p.PngRel), ".pal");
                    img = Imaging.ToBitmap(OverworldSprites.Render(png, File.Exists(palPath) ? JascPalette.Load(palPath) : null));
                }
                catch (Exception ex) when (ex is IOException or FormatException or PngFormatException) { }
            }
            Portraits.Add(new PortraitVm(p.Constant, img));
        }
    }

    private void AddPortraitFromPng()
    {
        if (_ctx.Root is not { } root) return;
        var dlg = new OpenFileDialog { Filter = "PNG images|*.png", Title = "Portrait (up to 64×64 pixels, at most 15 colors + transparency)" };
        if (dlg.ShowDialog(Application.Current.MainWindow) != true) return;
        var name = new InputDialog("New portrait", "Name of the portrait (e.g. Birch, Mom, Rival).", ("Name", Path.GetFileNameWithoutExtension(dlg.FileName)));
        if (name.ShowDialog() != true) return;
        AddPortrait(root, name.Values[0], Png.Load(dlg.FileName), null);
    }

    private void AddPortraitFromTrainer()
    {
        if (_ctx.Root is not { } root) return;
        var pics = CharacterGraphics.TrainerPics(root).Where(p => p.PngRel != null).ToList();
        var list = new System.Windows.Controls.ListBox { ItemsSource = pics.Select(p => p.Constant).ToList(), Height = 320 };
        var ok = new System.Windows.Controls.Button { Content = "Use", IsDefault = true };
        var panel = new System.Windows.Controls.DockPanel { Margin = new Thickness(12) };
        System.Windows.Controls.DockPanel.SetDock(ok, System.Windows.Controls.Dock.Bottom);
        panel.Children.Add(ok);
        panel.Children.Add(list);
        var w = new Window { Title = "Portrait from a trainer picture", Content = panel, Width = 380, SizeToContent = SizeToContent.Height, Owner = Application.Current.MainWindow, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        ok.Click += (_, _) => w.DialogResult = list.SelectedItem != null;
        list.MouseDoubleClick += (_, _) => w.DialogResult = list.SelectedItem != null;
        if (w.ShowDialog() != true || pics.FirstOrDefault(p => p.Constant == list.SelectedItem as string) is not { } pic) return;
        var png = Png.Load(Path.Combine(root, pic.PngRel!));
        var pal = pic.PalRel != null && File.Exists(Path.Combine(root, pic.PalRel)) ? JascPalette.Load(Path.Combine(root, pic.PalRel)) : null;
        // Trainer pictures are indexed with a separate palette: give the picture its real colors before converting.
        if (pal != null && png.Indices != null)
        {
            var img = new RgbaImage(png.Width, png.Height);
            for (int i = 0; i < img.Pixels.Length; i++)
            {
                int idx = png.Indices[i] & 0xF;
                img.Pixels[i] = idx == 0 || idx >= pal.Length ? Rgba32.Transparent : pal[idx] with { A = 255 };
            }
            png = Png.Decode(Png.EncodeRgba(img));
        }
        AddPortrait(root, pic.Constant.Replace("TRAINER_PIC_", ""), png, null);
    }

    private void AddPortrait(string root, string name, PngImage png, Rgba32[]? palette)
    {
        ExportPlan plan;
        try { plan = DialogPortraits.PlanAdd(root, name, png, palette); }
        catch (ExportValidationException ex) { ProjectWriter.Warn("New portrait", string.Join("\n", ex.Errors)); return; }
        if (ProjectWriter.Apply(root, plan.TilesetName, plan.TilesetFolder, plan.Files.ToList(), [.. plan.Notes])) _ctx.Reload();
        LoadPortraits();
    }
}
