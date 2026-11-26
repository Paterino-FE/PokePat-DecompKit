using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using TilesetEditor.App.Infrastructure;
using TilesetEditor.Core.Graphics;
using TilesetEditor.Core.Imaging;
using TilesetEditor.Core.Project;
using TilesetEditor.Export;

namespace TilesetEditor.App.Shell;

/// <summary>
/// Shared "replace this image" flow (Graphics, Sprites, player character, trainer pictures): pick a PNG of the same size,
/// choose new colors or the current palette, confirm color loss, compare before/after, show the file list, write.
/// </summary>
public static class ImageReplaceFlow
{
    /// <param name="info">Image facts; Source/PalRel decide whether new colors may be written (and to which .pal).</param>
    /// <param name="paletteNote">Extra warning shown with the color question, e.g. that the palette is shared by other sprites.</param>
    public static bool Run(string root, string rel, PngImage current, GraphicInfo info, IReadOnlyList<Rgba32> palette, string? paletteNote = null)
    {
        var dlg = new OpenFileDialog { Filter = "PNG|*.png", Title = $"New image for {Path.GetFileName(rel)} ({info.Width}×{info.Height})" };
        if (dlg.ShowDialog(Application.Current.MainWindow) != true) return false;
        PngImage replacement;
        try { replacement = Png.Load(dlg.FileName); }
        catch (Exception ex) when (ex is IOException or PngFormatException)
        {
            ProjectWriter.Warn("Replace image", ex.Message);
            return false;
        }

        bool newColors = false;
        if (info.Source != PaletteSource.Shared)
        {
            var choice = AppDialog.Show(Application.Current.MainWindow,
                "Which colors should the game use?\n\nYes = the new image's colors (the palette is replaced" +
                (info.PalRel != null ? $", in {info.PalRel}" : "") + ")\nNo = keep the current palette and map the new image onto it" +
                (paletteNote != null ? "\n\n" + paletteNote : ""),
                "Replace image", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
            if (choice == MessageBoxResult.Cancel) return false;
            newColors = choice == MessageBoxResult.Yes;
        }
        var result = GameGraphics.Convert(current, info, palette, replacement, newColors, allowNearest: false);
        if (result.Errors.Count > 0 && result.Changes.Count > 0 && !newColors)
        {
            if (AppDialog.Show(Application.Current.MainWindow,
                    $"{result.Changes.Count} color(s) are not in the current palette and would be replaced by the closest one:\n\n" +
                    string.Join("\n", result.Changes.Take(12).Select(c => $"  {c.From.ToHex()} → {c.To.ToHex()}")) + "\n\nContinue?",
                    "Confirm color loss", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return false;
            result = GameGraphics.Convert(current, info, palette, replacement, newColors, allowNearest: true);
        }
        if (result.Errors.Count > 0) { ProjectWriter.Warn("Replace image", string.Join("\n", result.Errors)); return false; }
        if (!ConfirmComparison(GameGraphics.Render(current, palette), result.Preview)) return false;

        var files = new List<PlannedFile>
        {
            new(rel, PlannedAction.Modify, result.Png, $"New image ({info.Width}×{info.Height}, {info.BitDepth}-bit indexed)",
                Hashing.Sha256(File.ReadAllBytes(ProjectPaths.Combine(root, rel)))),
        };
        if (result.Pal != null && info.PalRel != null)
            files.Add(new PlannedFile(info.PalRel, PlannedAction.Modify, result.Pal, "New colors" + (paletteNote != null ? " (shared palette)" : ""),
                Hashing.Sha256(File.ReadAllBytes(ProjectPaths.Combine(root, info.PalRel)))));
        return ProjectWriter.Apply(root, $"Replace {Path.GetFileName(rel)}", Path.GetDirectoryName(rel)!.Replace('\\', '/'), files,
            "The previous files are backed up before writing and restored if anything fails.",
            "Build the ROM to see the change in game (make rebuilds the converted .4bpp/.gbapal files).");
    }

    /// <summary>Image with a checkerboard behind transparent pixels, scaled by an integer factor.</summary>
    public static BitmapSource Scaled(RgbaImage img, int factor)
    {
        var big = new RgbaImage(img.Width * factor, img.Height * factor);
        for (int y = 0; y < big.Height; y++)
            for (int x = 0; x < big.Width; x++)
            {
                var p = img[x / factor, y / factor];
                big[x, y] = p.A > 0 ? p : ((x / 8 + y / 8) % 2 == 0 ? new Rgba32(236, 236, 236) : new Rgba32(214, 214, 214));
            }
        return Imaging.ToBitmap(big);
    }

    /// <summary>Before/after window; returns true on "Use this image".</summary>
    public static bool ConfirmComparison(RgbaImage before, RgbaImage after)
    {
        int factor = Math.Max(1, Math.Min(3, 360 / Math.Max(before.Width, 1)));
        Image Pic(RgbaImage img) => new() { Source = Scaled(img, factor), Stretch = Stretch.None, Margin = new Thickness(0, 4, 0, 0) };
        var win = new Window
        {
            Title = "Before / after",
            Owner = Application.Current.MainWindow,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            SizeToContent = SizeToContent.WidthAndHeight,
            ResizeMode = ResizeMode.NoResize,
        };
        var ok = new Button { Content = "Use this image…", IsDefault = true, Margin = new Thickness(0, 0, 8, 0), MinWidth = 120 };
        ok.Click += (_, _) => win.DialogResult = true;
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(new StackPanel { Margin = new Thickness(0, 0, 16, 0), Children = { new TextBlock { Text = "Now (game colors)", FontWeight = FontWeights.SemiBold }, Pic(before) } });
        row.Children.Add(new StackPanel { Children = { new TextBlock { Text = "After (game colors)", FontWeight = FontWeights.SemiBold }, Pic(after) } });
        win.Content = new StackPanel
        {
            Margin = new Thickness(14),
            Children =
            {
                new ScrollViewer { Content = row, MaxHeight = 700, MaxWidth = 1200, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Auto },
                new StackPanel
                {
                    Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0),
                    Children = { ok, new Button { Content = "Cancel", IsCancel = true, MinWidth = 80 } },
                },
            },
        };
        return win.ShowDialog() == true;
    }

    public static void SaveCopy(string root, string rel)
    {
        var dlg = new SaveFileDialog { Filter = "PNG|*.png", FileName = Path.GetFileName(rel), Title = "Save a copy to edit" };
        if (dlg.ShowDialog(Application.Current.MainWindow) != true) return;
        File.Copy(ProjectPaths.Combine(root, rel), dlg.FileName, overwrite: true);
    }
}
