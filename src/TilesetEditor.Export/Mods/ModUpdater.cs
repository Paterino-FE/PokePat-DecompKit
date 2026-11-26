using System.Text;
using TilesetEditor.Core.Gba;
using TilesetEditor.Core.Imaging;
using TilesetEditor.Core.Platinum;

namespace TilesetEditor.Export.Mods;

/// <summary>What an update keeps of the user's work.</summary>
public sealed record ModUpdateKeep(List<(ModSetting Setting, string Value)> Settings, List<(string Name, byte[] Png)> Mugshots, List<(string Name, byte[] Png)> Pictures)
{
    public List<(string Name, byte[] Png)> Fullscreens { get; init; } = [];

    public string Describe()
    {
        var parts = new List<string>();
        if (Settings.Count > 0) parts.Add($"{Settings.Count} setting value(s)");
        if (Mugshots.Count > 0) parts.Add($"{Mugshots.Count} mugshot(s): {string.Join(", ", Mugshots.Select(m => m.Name))}");
        if (Pictures.Count > 0) parts.Add($"{Pictures.Count} picture(s): {string.Join(", ", Pictures.Select(p => p.Name))}");
        if (Fullscreens.Count > 0) parts.Add($"{Fullscreens.Count} full-screen picture(s): {string.Join(", ", Fullscreens.Select(p => p.Name))}");
        return string.Join("; ", parts);
    }
}

/// <summary>
/// Brings an installed mod to a newer version: the old version is switched off (original files back), the new one on, and
/// then the user's work is put back – the values of the settings and, for dialog mugshots, every own mugshot and picture
/// (they live in the mod's folder, which switching off removes). Each step is written with a backup.
/// </summary>
public static class ModUpdater
{
    public static ModUpdateKeep WhatToKeep(string root, ModPackage pkg)
    {
        var settings = pkg.Manifest.Settings.Select(s => (s, ModPlanner.ReadSettingText(root, s))).Where(x => x.Item2 != null).Select(x => (x.s, x.Item2!)).ToList();
        var mugshots = new List<(string, byte[])>();
        var pictures = new List<(string, byte[])>();
        if (pkg.Manifest.Id == PlatinumMugshots.ModId)
        {
            foreach (var m in PlatinumMugshots.List(root).Where(m => m.Constant != "MUGSHOT_EXAMPLE"))
                if (File.Exists(Path.Combine(root, m.PngRel))) mugshots.Add((m.Constant[8..], File.ReadAllBytes(Path.Combine(root, m.PngRel))));
            foreach (var p in PlatinumMugshots.ListPictures(root))
                if (File.Exists(Path.Combine(root, p.PngRel))) pictures.Add((p.Constant[8..], File.ReadAllBytes(Path.Combine(root, p.PngRel))));
            var fullscreens = PlatinumMugshots.ListFullscreen(root).Where(p => p.Constant != "FULLSCREEN_EXAMPLE" && File.Exists(Path.Combine(root, p.PngRel)))
                .Select(p => (p.Constant[11..], File.ReadAllBytes(Path.Combine(root, p.PngRel)))).ToList();
            return new ModUpdateKeep(settings, mugshots, pictures) { Fullscreens = fullscreens };
        }
        if (pkg.Manifest.Id == DialogPortraits.ModId)
        {
            // Stored as plain colour pictures: adding them again gives the same 16 colours.
            static byte[]? Colored(string full, Func<PngImage, Rgba32[]?, RgbaImage> render)
            {
                if (!File.Exists(full)) return null;
                var png = Png.Load(full);
                var palPath = Path.ChangeExtension(full, ".pal");
                return Png.EncodeRgba(render(png, File.Exists(palPath) ? JascPalette.Load(palPath) : null));
            }
            foreach (var p in DialogPortraits.List(root).Where(p => p.PngRel != null))
                if (Colored(Path.Combine(root, p.PngRel!), (png, pal) => DialogPortraits.WithColors(png, pal)) is { } bytes) mugshots.Add((p.Constant[9..], bytes));
            foreach (var p in DialogPortraits.ListPictures(root).Where(p => p.PngRel != null))
                if (Colored(Path.Combine(root, p.PngRel!), DialogPortraits.PictureForDisplay) is { } bytes) pictures.Add((p.Constant[8..], bytes));
        }
        return new ModUpdateKeep(settings, mugshots, pictures);
    }

    /// <summary>Runs the update; returns null on success or the reason it stopped.</summary>
    public static string? Update(string root, InstalledMod installed, ModPackage pkg, string? expansionVersion, ModUpdateKeep keep, string? backupRoot = null)
    {
        var (off, kept) = ModPlanner.PlanUninstall(root, installed);
        // A change of the old version that was edited by hand afterwards cannot be taken out: the new version would then be
        // added next to it (e.g. script macros defined twice). Stop before anything is written.
        if (kept.Count > 0)
            return "The old version cannot be switched off cleanly, nothing was changed: " + string.Join(" ", kept) +
                   " – undo the hand-made changes there (or switch the mod off and on by hand).";
        var resOff = new ExportApplier(backupRoot: backupRoot).Apply(off);
        if (!resOff.Success) return "Switching the old version off failed: " + resOff.Error;
        var on = ModPlanner.PlanInstall(root, pkg, expansionVersion);
        var resOn = new ExportApplier(backupRoot: backupRoot).Apply(on);
        if (!resOn.Success) return "The old version is off, but switching the new one on failed: " + resOn.Error + " – switch it on by hand.";

        var utf8 = new UTF8Encoding(false);
        foreach (var g in keep.Settings.GroupBy(x => x.Setting.File))
        {
            var full = Path.Combine(root, g.Key);
            if (!File.Exists(full)) continue;
            var text = g.Aggregate(File.ReadAllText(full), (cur, x) => { try { return ModPlanner.WriteSettingText(cur, x.Setting, x.Value); } catch (InvalidOperationException) { return cur; } });
            File.WriteAllText(full, text, utf8);
        }
        static RgbaImage Picture(byte[] png)
        {
            var p = Png.Decode(png);
            var img = p.Rgba.Clone();
            if (p.Indices != null) for (int i = 0; i < img.Pixels.Length; i++) if (p.Indices[i] == 0) img.Pixels[i] = Rgba32.Transparent;
            return img;
        }
        void Write(List<PlatinumFileChange> files) { foreach (var f in files) File.WriteAllBytes(Path.Combine(root, f.Path), f.Content); }
        if (pkg.Manifest.Id == DialogPortraits.ModId)
        {
            void WritePlan(ExportPlan plan) { foreach (var f in plan.Files) { var full = Path.Combine(root, f.RelativePath); Directory.CreateDirectory(Path.GetDirectoryName(full)!); File.WriteAllBytes(full, f.Content); } }
            // The picture files are not part of the mod, so switching it off left them: they are written again with their entries.
            void Drop(string folder, string name)
            {
                foreach (var ext in new[] { ".png", ".pal" })
                {
                    var f = Path.Combine(root, folder, name.ToLowerInvariant() + ext);
                    if (File.Exists(f)) File.Delete(f);
                }
            }
            try
            {
                foreach (var (name, png) in keep.Mugshots) { Drop(DialogPortraits.Folder, name); WritePlan(DialogPortraits.PlanAdd(root, name, Png.Decode(png))); }
                foreach (var (name, png) in keep.Pictures) { Drop(DialogPortraits.Folder + "/pictures", name); WritePlan(DialogPortraits.PlanAddPicture(root, name, Picture(png))); }
            }
            catch (ExportValidationException ex) { return "The new version is on, but some portraits could not be added again: " + string.Join(" ", ex.Errors); }
            return null;
        }
        foreach (var (name, png) in keep.Mugshots) Write(PlatinumMugshots.PlanAdd(root, name, Picture(png)).Files);
        foreach (var (name, png) in keep.Pictures) Write(PlatinumMugshots.PlanAddPicture(root, name, Picture(png)).Files);
        foreach (var (name, png) in keep.Fullscreens)
            if (PlatinumMugshots.SupportsFullscreen(root)) Write(PlatinumMugshots.PlanAddFullscreen(root, name, Png.Decode(png).Rgba).Files);
        return null;
    }
}
