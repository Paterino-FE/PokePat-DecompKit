using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using TilesetEditor.Core.Imaging;

namespace TilesetEditor.App.Infrastructure;

public static class Imaging
{
    public static BitmapSource ToBitmap(RgbaImage img)
    {
        var bgra = new byte[img.Width * img.Height * 4];
        for (int i = 0; i < img.Pixels.Length; i++)
        {
            var c = img.Pixels[i];
            // Premultiplied BGRA
            bgra[i * 4] = (byte)(c.B * c.A / 255);
            bgra[i * 4 + 1] = (byte)(c.G * c.A / 255);
            bgra[i * 4 + 2] = (byte)(c.R * c.A / 255);
            bgra[i * 4 + 3] = c.A;
        }
        var bmp = BitmapSource.Create(img.Width, img.Height, 96, 96, PixelFormats.Pbgra32, null, bgra, img.Width * 4);
        bmp.Freeze();
        return bmp;
    }

    private static readonly SemaphoreSlim Loads = new(8);

    /// <summary>
    /// Fills <paramref name="target"/> with a picture made on a background thread (reading and decoding PNGs over the WSL
    /// share would block the window). With a <paramref name="cache"/>, a picture is made only once.
    /// </summary>
    public static void LoadAsync(System.Windows.Controls.Image target, Func<RgbaImage?> produce,
        System.Collections.Concurrent.ConcurrentDictionary<string, BitmapSource?>? cache = null, string? key = null)
    {
        if (cache != null && key != null && cache.TryGetValue(key, out var hit)) { target.Source = hit; return; }
        var ui = TaskScheduler.FromCurrentSynchronizationContext();
        Task.Run(async () =>
        {
            await Loads.WaitAsync();
            try { return produce() is { } img ? ToBitmap(img) : null; }
            catch (Exception ex) when (ex is System.IO.IOException or PngFormatException or System.IO.InvalidDataException or FormatException or UnauthorizedAccessException) { return null; }
            finally { Loads.Release(); }
        }).ContinueWith(t =>
        {
            var bmp = t.IsCompletedSuccessfully ? t.Result : null;
            if (cache != null && key != null) cache[key] = bmp;
            if (bmp != null) target.Source = bmp;
        }, ui);
    }

    public static Color ToColor(Rgba32 c) => Color.FromArgb(c.A, c.R, c.G, c.B);

    public static SolidColorBrush Brush(Rgba32 c)
    {
        var b = new SolidColorBrush(ToColor(c));
        b.Freeze();
        return b;
    }

    /// <summary>Checkerboard background for transparent areas.</summary>
    public static DrawingBrush Checkerboard(double cell)
    {
        var group = new DrawingGroup();
        group.Children.Add(new GeometryDrawing(Brushes.White, null, new RectangleGeometry(new Rect(0, 0, 2, 2))));
        var g = new GeometryGroup();
        g.Children.Add(new RectangleGeometry(new Rect(0, 0, 1, 1)));
        g.Children.Add(new RectangleGeometry(new Rect(1, 1, 1, 1)));
        group.Children.Add(new GeometryDrawing(new SolidColorBrush(Color.FromRgb(0xDD, 0xDD, 0xDD)), null, g));
        var brush = new DrawingBrush(group)
        {
            TileMode = TileMode.Tile,
            Viewport = new Rect(0, 0, cell * 2, cell * 2),
            ViewportUnits = BrushMappingMode.Absolute,
        };
        brush.Freeze();
        return brush;
    }
}
