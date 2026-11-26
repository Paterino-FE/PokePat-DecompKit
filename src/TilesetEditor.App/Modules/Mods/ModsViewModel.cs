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
    /// <summary>The project has an older version of the mod than PokéPat brings.</summary>
    public bool CanUpdate => Package != null && Installed is { Detected: false } i && System.Version.TryParse(Package.Manifest.Version, out var n) && System.Version.TryParse(i.Version, out var o) && n > o;
    public string State => IsInstalled ? (Installed!.Detected ? "● Active (found in the project)" : CanUpdate ? $"● Active (version {Installed!.Version}) – version {Package!.Manifest.Version} available" : $"● Active (version {Installed!.Version})")
        : Check is { CanInstall: true } ? "○ Off" : "Does not fit this project";
    public string StateColor => CanUpdate ? "#B45309" : IsInstalled ? "#15803D" : Check is { CanInstall: true } ? "#7D6B69" : "#B8212B";
    public string UpdateBadge => CanUpdate ? $"⬆ {Installed!.Version} → {Package!.Manifest.Version}" : "";
    public string Description => Package?.Manifest.Description ?? "The mod folder is not available any more – it can still be uninstalled.";
    public string Usage => Package?.Manifest.Usage ?? "";
    public string Problems => Check == null || IsInstalled || Check.Problems.Count == 0 ? "" : "Why it does not fit:\n• " + string.Join("\n• ", Check.Problems);
    public string Changes => Package == null ? "" :
        $"New files: {string.Join(", ", Package.Manifest.Files.Select(f => f.To))}\n" +
        $"Changed files: {string.Join(", ", Package.Manifest.Edits.Select(e => e.File).Distinct())}";
}

public sealed record PortraitVm(string Constant, BitmapSource? Image);

/// <summary>An editable setting of an installed mod (e.g. the level cap of one badge).</summary>
public sealed class ModSettingVm(ModSetting setting, string? value) : ObservableObject
{
    public ModSetting Setting { get; } = setting;
    public string Label => Setting.Label;
    public string Hint => Setting.Hint.Length > 0 ? Setting.Hint : Setting.Kind == "name" ? $"up to {Setting.MaxLength} characters" : $"{Setting.Min}–{Setting.Max}";
    public string? Original { get; } = value;
    private string _value = value ?? "";
    public string Value { get => _value; set => Set(ref _value, value ?? ""); }
    public bool IsChanged => (Original ?? "") != Value.Trim();
    public bool IsChoice => Setting.Kind == "choice";
    public bool IsText => !IsChoice;
    public IReadOnlyList<string> Choices => Setting.Choices ?? [];
}

/// <summary>
/// Mods: extensions that PokePat installs into the game (new C code, script commands, data) – checked against the project
/// first, shown as a file list, installed with a backup and removable again. Mods with own content (dialog portraits) get
/// their tools here as well.
/// </summary>
public sealed class ModsViewModel : ObservableObject, IToolkitModule
{
    private readonly DeferredLoad _reload;
    private readonly ProjectContext _ctx;
    private readonly Core.Project.GameKind _game;

    public ModsViewModel(ProjectContext ctx) : this(ctx, Core.Project.GameKind.Emerald) { }

    /// <summary>The mods of one game: only mods whose mod.json names <paramref name="game"/> are listed.</summary>
    public ModsViewModel(ProjectContext ctx, Core.Project.GameKind game)
    {
        _ctx = ctx;
        _game = game;
        // Also while hidden (shortly after the start): outdated mods show up in the navigation without opening the module.
        _reload = new DeferredLoad(() => { Refresh(); return null; }, prefetch: true);
        _ctx.ChangedFor(game, _reload.Request);
        SaveSettingsCommand = new RelayCommand(SaveSettings, () => Settings.Any(x => x.IsChanged) && _ctx.Root != null);
        InstallCommand = new RelayCommand(Install, () => Selected is { IsInstalled: false, Check.CanInstall: true } && _ctx.IsOpen);
        UninstallCommand = new RelayCommand(Uninstall, () => Selected is { IsInstalled: true } && _ctx.IsOpen);
        UpdateCommand = new RelayCommand(Update, () => Selected is { CanUpdate: true } && _ctx.IsOpen);
        UpdateAllCommand = new RelayCommand(UpdateAll, () => UpdateCount > 0 && _ctx.IsOpen);
        OpenModsFolderCommand = new RelayCommand(() =>
        {
            Directory.CreateDirectory(UserModsFolder);
            Process.Start("explorer.exe", $"\"{UserModsFolder}\"");
        });
        AddModCommand = new RelayCommand(AddMod);
        AddPortraitCommand = new RelayCommand(AddPortraitFromPng, () => ShowPortraits);
        AddTrainerPortraitCommand = new RelayCommand(AddPortraitFromTrainer, () => ShowPortraits);
        AddPictureCommand = new RelayCommand(AddPictureFromPng, () => ShowPicturesSection && _ctx.Root != null && DialogPortraits.SupportsPictures(_ctx.Root));
        View = new ModsView { DataContext = this };
        _reload.Attach(View);
        Refresh();
    }

    public string Title => "Mods";
    public string Description => UpdateCount switch
    {
        0 => "Switch extensions on and off",
        1 => "⬆ 1 update available",
        var n => $"⬆ {n} updates available",
    };

    /// <summary>Active mods with a newer version in PokéPat.</summary>
    public int UpdateCount => Mods.Count(m => m.CanUpdate);
    public bool HasUpdates => UpdateCount > 0;
    public string UpdatesText => UpdateCount == 1
        ? $"⬆ An update is available: {Mods.First(m => m.CanUpdate).Name} {Mods.First(m => m.CanUpdate).UpdateBadge[2..]}."
        : $"⬆ {UpdateCount} updates are available: {string.Join(", ", Mods.Where(m => m.CanUpdate).Select(m => m.Name))}.";
    public ICommand UpdateAllCommand { get; }
    public bool IsAvailable => true;
    public override string ToString() => Title;
    public FrameworkElement View { get; }
    public bool HasUnsavedChanges => false;
    public string? UnsavedChangesDescription => null;

    public ICommand InstallCommand { get; }
    public ICommand SaveSettingsCommand { get; }

    /// <summary>Settings of the selected mod when it is installed (e.g. level caps).</summary>
    public ObservableCollection<ModSettingVm> Settings { get; } = [];
    public bool HasSettings => Settings.Count > 0;

    private void LoadSettings()
    {
        Settings.Clear();
        if (Selected is { IsInstalled: true, Package: { } p } && _ctx.Root is { } root)
            foreach (var s in p.Manifest.Settings) Settings.Add(new ModSettingVm(s, ModPlanner.ReadSettingText(root, s)));
        OnPropertyChanged(nameof(HasSettings));
    }

    private void SaveSettings()
    {
        if (_ctx.Root is not { } root || Selected?.Package is not { } p) return;
        var files = new List<PlannedFile>();
        try
        {
            foreach (var g in Settings.Where(x => x.IsChanged).GroupBy(x => x.Setting.File))
                files.Add(ProjectWriter.ModifyText(root, g.Key, text => g.Aggregate(text, (cur, x) => ModPlanner.WriteSettingText(cur, x.Setting, x.Value)),
                    string.Join(", ", g.Select(x => $"{x.Label}: {x.Original} → {x.Value.Trim()}"))));
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException)
        {
            ProjectWriter.Warn(p.Manifest.Name, ex.Message);
            return;
        }
        if (ProjectWriter.Apply(root, $"{p.Manifest.Name}: settings", "src", files, "Build the ROM afterwards.")) _ctx.Reload();
        LoadSettings();
    }
    public ICommand UninstallCommand { get; }
    public ICommand UpdateCommand { get; }
    public ICommand OpenModsFolderCommand { get; }
    public ICommand AddModCommand { get; }
    public ICommand AddPortraitCommand { get; }
    public ICommand AddTrainerPortraitCommand { get; }
    public ICommand AddPictureCommand { get; }
    public ObservableCollection<PortraitVm> Pictures { get; } = [];
    /// <summary>Emerald: the pictures of the dialog portraits mod (the Platinum mugshot mod has its own module).</summary>
    public bool ShowPicturesSection => ShowPortraits && !IsMugshots;
    public string PicturesHint => _ctx.Root != null && DialogPortraits.SupportsPictures(_ctx.Root)
        ? "Bigger pictures (a map, a letter, a photo) shown in the middle of the screen with the block \"Show picture\". They disappear when the message box closes."
        : "Pictures need version 1.1 of this mod – update it (button above) to add them. Your portraits are kept.";

    public ObservableCollection<ModVm> Mods { get; } = [];
    public ObservableCollection<PortraitVm> Portraits { get; } = [];

    private ModVm? _selected;
    public ModVm? Selected
    {
        get => _selected;
        set { if (Set(ref _selected, value)) { OnPropertyChanged(nameof(ShowPortraits)); OnPropertyChanged(nameof(ShowPicturesSection)); OnPropertyChanged(nameof(PicturesHint)); LoadPortraits(); LoadSettings(); } }
    }

    private string _summary = "";
    public string Summary { get => _summary; private set => Set(ref _summary, value); }

    public bool ShowPortraits => Selected is { IsInstalled: true } && (Selected.Id is DialogPortraits.ModId or Core.Platinum.PlatinumMugshots.ModId) && _ctx.Root != null;
    private bool IsMugshots => Selected?.Id == Core.Platinum.PlatinumMugshots.ModId;

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
        var (all, problems) = ModPackage.LoadAll(ModFolders());
        var packages = all.Where(p => p.Manifest.GameKind == _game).ToList();
        var root = _ctx.Root;
        var installed = root != null ? ModRegistry.Installed(root) : [];
        foreach (var p in packages)
        {
            var check = root != null ? ModPlanner.Check(root, p, _ctx.Project?.ExpansionVersion) : null;
            Mods.Add(new ModVm(p, installed.FirstOrDefault(i => i.Id == p.Manifest.Id) ?? check?.Installed, check));
        }
        foreach (var i in installed.Where(i => all.All(p => p.Manifest.Id != i.Id)))
            Mods.Add(new ModVm(null, i, null));
        OnPropertyChanged(nameof(Description));
        OnPropertyChanged(nameof(UpdateCount));
        OnPropertyChanged(nameof(HasUpdates));
        OnPropertyChanged(nameof(UpdatesText));
        Summary = $"{Mods.Count} mod(s), {Mods.Count(m => m.IsInstalled)} active in this project" +
                  (problems.Count > 0 ? " · could not read: " + string.Join("; ", problems) : "") +
                  (root == null ? " · open a project to install mods" : "");
        Selected = Mods.FirstOrDefault(m => m.Id == keep) ?? Mods.FirstOrDefault();
        CommandManager.InvalidateRequerySuggested();
    }

    private void Install()
    {
        if (Selected?.Package is not { } p || _ctx.Root is not { } root) return;
        ExportPlan plan;
        try { plan = Infrastructure.Perf.Time($"plan mod {p.Manifest.Id}", () => ModPlanner.PlanInstall(root, p, _ctx.Project?.ExpansionVersion)); }
        catch (ExportValidationException ex) { ProjectWriter.Warn("Install mod", string.Join("\n", ex.Errors)); return; }
        if (ProjectWriter.Apply(root, plan.TilesetName, plan.TilesetFolder, plan.Files.ToList(), [.. plan.Notes])) Infrastructure.Perf.Time("reload after mod", _ctx.Reload);
        Infrastructure.Perf.Time("mods refresh", Refresh);
    }

    private void Uninstall()
    {
        if (Selected?.Installed is not { } i || _ctx.Root is not { } root) return;
        var (plan, _) = ModPlanner.PlanUninstall(root, i);
        if (ProjectWriter.Apply(root, plan.TilesetName, plan.TilesetFolder, plan.Files.ToList(), [.. plan.Notes])) _ctx.Reload();
        Refresh();
    }

    /// <summary>
    /// Brings an installed mod to the version PokéPat has: switches the old version off and the new one on, then puts back
    /// what belongs to the user – the values of the settings and, for dialog mugshots, every mugshot and picture.
    /// Each step makes a backup; the steps are shown in one question first.
    /// </summary>
    private void Update()
    {
        if (Selected is { CanUpdate: true } m) UpdateMods([m]);
    }

    private void UpdateAll() => UpdateMods(Mods.Where(m => m.CanUpdate).ToList());

    /// <summary>Updates the mods one after another after one question; says what was kept and what failed.</summary>
    private void UpdateMods(List<ModVm> mods)
    {
        if (_ctx.Root is not { } root || mods.Count == 0) return;
        var plans = new List<(ModVm Mod, ModUpdateKeep Keep)>();
        try { foreach (var m in mods) plans.Add((m, ModUpdater.WhatToKeep(root, m.Package!))); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { ProjectWriter.Warn("Update mods", ex.Message); return; }
        var lines = plans.Select(p => $"• {p.Mod.Name}: {p.Mod.Installed!.Version} → {p.Mod.Package!.Manifest.Version}" + (p.Keep.Describe() is { Length: > 0 } k ? $" (kept: {k})" : ""));
        var r = AppDialog.Show(Application.Current.MainWindow,
            $"Update {(mods.Count == 1 ? "this mod" : $"these {mods.Count} mods")}?\n\n{string.Join("\n", lines)}\n\n" +
            "Each old version is switched off (original files back) and the new one switched on; your settings and pictures are put back. Every step makes a backup.",
            "Update mods", MessageBoxButton.OKCancel, MessageBoxImage.Question);
        if (r != MessageBoxResult.OK) return;
        var report = new List<string>();
        foreach (var (m, keep) in plans)
        {
            string? failed;
            try { failed = Perf.Time($"update mod {m.Id}", () => ModUpdater.Update(root, m.Installed!, m.Package!, _ctx.Project?.ExpansionVersion, keep)); }
            catch (Exception ex) when (ex is ExportValidationException or IOException or InvalidDataException or PngFormatException or UnauthorizedAccessException)
            {
                failed = ex is ExportValidationException ev ? string.Join("; ", ev.Errors) : ex.Message;
            }
            report.Add(failed == null ? $"✓ {m.Name} is now version {m.Package!.Manifest.Version}." : $"✗ {m.Name}: {failed}");
            if (failed != null) break; // the next ones may depend on a working project
        }
        _ctx.Reload();
        Refresh();
        ProjectWriter.Warn("Update mods", string.Join("\n", report) + "\n\nBuild the ROM afterwards.");
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
        Pictures.Clear();
        OnPropertyChanged(nameof(PicturesHint));
        if (!ShowPortraits || _ctx.Root is not { } root) return;
        if (IsMugshots)
        {
            foreach (var m in Core.Platinum.PlatinumMugshots.List(root))
            {
                BitmapSource? img = null;
                try
                {
                    var full = Path.Combine(root, m.PngRel);
                    if (File.Exists(full))
                    {
                        var png = Png.Load(full);
                        var rgba = png.Rgba.Clone();
                        if (png.Indices != null) for (int i = 0; i < rgba.Pixels.Length; i++) if (png.Indices[i] == 0) rgba.Pixels[i] = Rgba32.Transparent;
                        img = Imaging.ToBitmap(rgba);
                    }
                }
                catch (Exception ex) when (ex is IOException or PngFormatException) { }
                Portraits.Add(new PortraitVm($"{m.Constant} ({m.Number})", img));
            }
            return;
        }
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
        foreach (var p in DialogPortraits.ListPictures(root))
        {
            BitmapSource? img = null;
            if (p.PngRel != null && File.Exists(Path.Combine(root, p.PngRel)))
            {
                try
                {
                    var palPath = Path.ChangeExtension(Path.Combine(root, p.PngRel), ".pal");
                    img = Imaging.ToBitmap(DialogPortraits.PictureForDisplay(Png.Load(Path.Combine(root, p.PngRel)), File.Exists(palPath) ? JascPalette.Load(palPath) : null));
                }
                catch (Exception ex) when (ex is IOException or FormatException or PngFormatException) { }
            }
            Pictures.Add(new PortraitVm(p.Constant, img));
        }
    }

    private void AddPictureFromPng()
    {
        if (_ctx.Root is not { } root) return;
        var dlg = new OpenFileDialog { Filter = "PNG images|*.png", Title = "Picture (any size – shown at up to 128×96 pixels, 15 colors)" };
        if (dlg.ShowDialog(Application.Current.MainWindow) != true) return;
        var name = new InputDialog("New picture", "Name of the picture (e.g. Treasure map, Letter).", ("Name", Path.GetFileNameWithoutExtension(dlg.FileName)));
        if (name.ShowDialog() != true) return;
        ExportPlan plan;
        try
        {
            var png = Png.Load(dlg.FileName);
            var rgba = png.Rgba.Clone();
            if (png.Indices != null && png.Palette is { Length: > 0 } && png.Palette[0].A == 0)
                for (int i = 0; i < rgba.Pixels.Length; i++) if (png.Indices[i] == 0) rgba.Pixels[i] = Rgba32.Transparent;
            plan = DialogPortraits.PlanAddPicture(root, name.Values[0], rgba);
        }
        catch (ExportValidationException ex) { ProjectWriter.Warn("New picture", string.Join("\n", ex.Errors)); return; }
        catch (Exception ex) when (ex is IOException or PngFormatException) { ProjectWriter.Warn("New picture", ex.Message); return; }
        if (ProjectWriter.Apply(root, plan.TilesetName, plan.TilesetFolder, plan.Files.ToList(), [.. plan.Notes])) _ctx.Reload();
        LoadPortraits();
    }

    private void AddPortraitFromPng()
    {
        if (_ctx.Root is not { } root) return;
        var dlg = new OpenFileDialog { Filter = "PNG images|*.png", Title = "Portrait (up to 64×64 pixels, at most 15 colors + transparency)" };
        if (dlg.ShowDialog(Application.Current.MainWindow) != true) return;
        var name = new InputDialog("New portrait", "Name of the portrait (e.g. Birch, Mom, Rival).", ("Name", Path.GetFileNameWithoutExtension(dlg.FileName)));
        if (name.ShowDialog() != true) return;
        if (IsMugshots) { AddMugshot(root, name.Values[0], Png.Load(dlg.FileName).Rgba); return; }
        AddPortrait(root, name.Values[0], Png.Load(dlg.FileName), null);
    }

    private void AddPortraitFromTrainer()
    {
        if (_ctx.Root is not { } root) return;
        if (IsMugshots) { AddMugshotFromTrainerClass(root); return; }
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

    private void AddMugshotFromTrainerClass(string root)
    {
        var classes = _ctx.Platinum?.TrainerClasses.Where(c => Core.Platinum.PlatinumTrainers.ClassPicture(root, c) != null).ToList() ?? [];
        var list = new System.Windows.Controls.ListBox { ItemsSource = classes, Height = 320 };
        var ok = new System.Windows.Controls.Button { Content = "Use", IsDefault = true };
        var panel = new System.Windows.Controls.DockPanel { Margin = new Thickness(12) };
        System.Windows.Controls.DockPanel.SetDock(ok, System.Windows.Controls.Dock.Bottom);
        panel.Children.Add(ok);
        panel.Children.Add(list);
        var w = new Window { Title = "Mugshot from a trainer class picture", Content = panel, Width = 380, SizeToContent = SizeToContent.Height, Owner = Application.Current.MainWindow, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        ok.Click += (_, _) => w.DialogResult = list.SelectedItem != null;
        list.MouseDoubleClick += (_, _) => w.DialogResult = list.SelectedItem != null;
        if (w.ShowDialog() != true || list.SelectedItem is not string cls) return;
        try
        {
            var png = Png.Load(Core.Platinum.PlatinumTrainers.ClassPicture(root, cls)!);
            var rgba = png.Rgba.Clone();
            if (png.Indices != null) for (int i = 0; i < rgba.Pixels.Length; i++) if (png.Indices[i] == 0) rgba.Pixels[i] = Rgba32.Transparent;
            AddMugshot(root, cls.Replace("TRAINER_CLASS_", ""), Core.Platinum.PlatinumMugshots.FromTrainerPicture(rgba));
        }
        catch (Exception ex) when (ex is IOException or PngFormatException) { ProjectWriter.Warn("New mugshot", ex.Message); }
    }

    private void AddMugshot(string root, string name, RgbaImage picture)
    {
        try
        {
            var (files, constant) = Core.Platinum.PlatinumMugshots.PlanAdd(root, name, picture);
            if (Platinum.PlatinumModules.Write(_ctx, "New mugshot", files,
                    $"Use it in a script with: ShowMugshot {constant}  (and #include \"constants/mugshots.h\" at the top).",
                    "The picture belongs to the mod: switching the mod off removes it again.", "Build the ROM afterwards."))
                LoadPortraits();
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException) { ProjectWriter.Warn("New mugshot", ex.Message); }
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
