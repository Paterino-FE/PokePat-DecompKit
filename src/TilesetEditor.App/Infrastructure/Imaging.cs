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
