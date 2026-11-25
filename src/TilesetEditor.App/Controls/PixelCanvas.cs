using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using TilesetEditor.App.Infrastructure;

namespace TilesetEditor.App.Controls;

public sealed record CanvasMarker(int TileX, int TileY, bool IsError);

/// <summary>
/// Shows a pixel image at an integer zoom with a 16×16 metatile grid, an optional 8×8 tile grid,
/// issue markers at tile positions and the selected tile. Reports clicks in image pixel coordinates.
/// </summary>
public sealed class PixelCanvas : FrameworkElement
{
    public static readonly DependencyProperty ImageProperty = DependencyProperty.Register(nameof(Image), typeof(BitmapSource), typeof(PixelCanvas),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty ZoomProperty = DependencyProperty.Register(nameof(Zoom), typeof(double), typeof(PixelCanvas),
        new FrameworkPropertyMetadata(4.0, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty ShowTileGridProperty = DependencyProperty.Register(nameof(ShowTileGrid), typeof(bool), typeof(PixelCanvas),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty MarkersProperty = DependencyProperty.Register(nameof(Markers), typeof(IReadOnlyList<CanvasMarker>), typeof(PixelCanvas),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty SelectedTileProperty = DependencyProperty.Register(nameof(SelectedTile), typeof(Point?), typeof(PixelCanvas),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty TileClickCommandProperty = DependencyProperty.Register(nameof(TileClickCommand), typeof(ICommand), typeof(PixelCanvas));

    public BitmapSource? Image { get => (BitmapSource?)GetValue(ImageProperty); set => SetValue(ImageProperty, value); }
    public double Zoom { get => (double)GetValue(ZoomProperty); set => SetValue(ZoomProperty, value); }
    public bool ShowTileGrid { get => (bool)GetValue(ShowTileGridProperty); set => SetValue(ShowTileGridProperty, value); }
    public IReadOnlyList<CanvasMarker>? Markers { get => (IReadOnlyList<CanvasMarker>?)GetValue(MarkersProperty); set => SetValue(MarkersProperty, value); }
    public Point? SelectedTile { get => (Point?)GetValue(SelectedTileProperty); set => SetValue(SelectedTileProperty, value); }
    /// <summary>Executed with a <see cref="Point"/> (image pixel coordinates) on click.</summary>
    public ICommand? TileClickCommand { get => (ICommand?)GetValue(TileClickCommandProperty); set => SetValue(TileClickCommandProperty, value); }

    private static readonly Brush Checker = Imaging.Checkerboard(8);
    private static readonly Pen MetaPen = Frozen(new Pen(new SolidColorBrush(Color.FromArgb(150, 20, 20, 20)), 1));
    private static readonly Pen TilePen = Frozen(new Pen(new SolidColorBrush(Color.FromArgb(70, 20, 20, 20)), 1) { DashStyle = DashStyles.Dot });
    private static readonly Pen ErrorPen = Frozen(new Pen(new SolidColorBrush(Color.FromRgb(220, 30, 30)), 2));
    private static readonly Pen WarnPen = Frozen(new Pen(new SolidColorBrush(Color.FromRgb(235, 150, 0)), 2));
    private static readonly Brush ErrorFill = Frozen(new SolidColorBrush(Color.FromArgb(60, 220, 30, 30)));
    private static readonly Brush WarnFill = Frozen(new SolidColorBrush(Color.FromArgb(40, 235, 150, 0)));
    private static readonly Pen SelPen = Frozen(new Pen(new SolidColorBrush(Color.FromRgb(0, 120, 255)), 3));

    private static T Frozen<T>(T f) where T : Freezable { f.Freeze(); return f; }

    public PixelCanvas()
    {
        RenderOptions.SetBitmapScalingMode(this, BitmapScalingMode.NearestNeighbor);
        Cursor = Cursors.Cross;
    }

    protected override Size MeasureOverride(Size availableSize) =>
        Image == null ? new Size(0, 0) : new Size(Image.PixelWidth * Zoom, Image.PixelHeight * Zoom);

    protected override void OnRender(DrawingContext dc)
    {
        var img = Image;
        if (img == null) return;
        double z = Zoom, w = img.PixelWidth * z, h = img.PixelHeight * z;
        dc.DrawRectangle(Checker, null, new Rect(0, 0, w, h));
        dc.DrawImage(img, new Rect(0, 0, w, h));

        if (ShowTileGrid && z >= 2)
        {
            for (int x = 8; x < img.PixelWidth; x += 16) dc.DrawLine(TilePen, new Point(x * z + 0.5, 0), new Point(x * z + 0.5, h));
            for (int y = 8; y < img.PixelHeight; y += 16) dc.DrawLine(TilePen, new Point(0, y * z + 0.5), new Point(w, y * z + 0.5));
        }
        for (int x = 0; x <= img.PixelWidth; x += 16) dc.DrawLine(MetaPen, new Point(x * z + 0.5, 0), new Point(x * z + 0.5, h));
        for (int y = 0; y <= img.PixelHeight; y += 16) dc.DrawLine(MetaPen, new Point(0, y * z + 0.5), new Point(w, y * z + 0.5));

        if (Markers != null)
        {
            foreach (var m in Markers.OrderBy(m => m.IsError))
            {
                var r = new Rect(m.TileX * 8 * z + 1, m.TileY * 8 * z + 1, 8 * z - 2, 8 * z - 2);
                dc.DrawRectangle(m.IsError ? ErrorFill : WarnFill, m.IsError ? ErrorPen : WarnPen, r);
            }
        }
        if (SelectedTile is { } s)
            dc.DrawRectangle(null, SelPen, new Rect(s.X * 8 * z, s.Y * 8 * z, 8 * z, 8 * z));
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        if (Image == null) return;
        var p = e.GetPosition(this);
        var px = new Point(Math.Floor(p.X / Zoom), Math.Floor(p.Y / Zoom));
        if (px.X < 0 || px.Y < 0 || px.X >= Image.PixelWidth || px.Y >= Image.PixelHeight) return;
        if (TileClickCommand?.CanExecute(px) == true) TileClickCommand.Execute(px);
    }
}
