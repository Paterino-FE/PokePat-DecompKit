using TilesetEditor.Core.Gba;
using TilesetEditor.Core.Imaging;

namespace TilesetEditor.Core.Text;

public enum DialogFrameStyle
{
    /// <summary>The overworld message box (graphics/text_window/message_box.png), as used by msgbox.</summary>
    Dialog,
    /// <summary>A standard window frame (graphics/text_window/1–20.png, or a custom 24×24 PNG).</summary>
    Standard,
}

/// <summary>Graphics of the project used for the preview. Missing files fall back to simple drawn shapes.</summary>
public sealed class DialogAssets
{
    public PngImage? MessageBox { get; init; }
    public IReadOnlyDictionary<int, PngImage> StdFrames { get; init; } = new Dictionary<int, PngImage>();
    public PngImage? DownArrow { get; init; }
    public Rgba32 TextBackground { get; init; } = new(255, 255, 255);
    public Rgba32 TextForeground { get; init; } = new(98, 98, 98);
    public Rgba32 TextShadow { get; init; } = new(213, 213, 205);

    public static DialogAssets Load(string projectRoot)
    {
        string P(params string[] parts) => Path.Combine([projectRoot, .. parts]);
        PngImage? TryPng(string path) { try { return File.Exists(path) ? Png.Load(path) : null; } catch (PngFormatException) { return null; } }
        var frames = new Dictionary<int, PngImage>();
        for (int i = 1; i <= 20; i++)
            if (TryPng(P("graphics", "text_window", $"{i}.png")) is { } f) frames[i] = f;
        Rgba32 bg = new(255, 255, 255), fg = new(98, 98, 98), sh = new(213, 213, 205);
        var pal = P("graphics", "text_window", "text_pal1.pal");
        if (File.Exists(pal))
        {
            try
            {
                var c = JascPalette.Load(pal);
                if (c.Length >= 4) { bg = c[1]; fg = c[2]; sh = c[3]; }
            }
            catch (FormatException) { }
        }
        return new DialogAssets
        {
            MessageBox = TryPng(P("graphics", "text_window", "message_box.png")),
            StdFrames = frames,
            DownArrow = TryPng(P("graphics", "fonts", "down_arrow.png")),
            TextBackground = bg,
            TextForeground = GbaColor.ToDisplayed(fg),
            TextShadow = GbaColor.ToDisplayed(sh),
        };
    }
}

/// <summary>What is on screen at one moment of the preview.</summary>
public sealed class DialogScene
{
    public DialogFrameStyle Style { get; init; } = DialogFrameStyle.Dialog;
    public int StdFrame { get; init; } = 1;
    /// <summary>Optional custom frame graphic: 56×16 (message box layout) or 24×24 (standard frame layout).</summary>
    public PngImage? CustomFrame { get; init; }
    /// <summary>Visible lines of the message box (at most 2), already cut to the revealed characters.</summary>
    public IReadOnlyList<string> Lines { get; init; } = [];
    public bool ShowMessageBox { get; init; } = true;
    public bool ShowArrow { get; init; }
    public int ArrowFrame { get; init; }
    /// <summary>Show the YES/NO menu with the cursor on 0 (YES) or 1 (NO).</summary>
    public int? YesNoCursor { get; init; }
    /// <summary>Multichoice menu (options, cursor, top-left tile position).</summary>
    public IReadOnlyList<string>? MenuOptions { get; init; }
    public int MenuCursor { get; init; }
    public int MenuX { get; init; }
    public int MenuY { get; init; }
    public RgbaImage? Background { get; init; }
}

/// <summary>Draws the GBA screen (240×160) the way src/menu.c composes message box and YES/NO window.</summary>
public sealed class DialogRenderer(DialogAssets assets, GbaFont font, Charmap charmap)
{
    public const int ScreenWidth = 240, ScreenHeight = 160;
    // sStandardTextBox_WindowTemplates and sYesNo_WindowTemplates (in tiles)
    private const int MsgL = 2, MsgT = 15, MsgW = 27, MsgH = 4;
    private const int YesNoL = 21, YesNoT = 9, YesNoW = 5, YesNoH = 4;
    private static readonly Rgba32 DefaultBackground = new(104, 168, 112);

    public RgbaImage Render(DialogScene scene)
    {
        var img = new RgbaImage(ScreenWidth, ScreenHeight);
        if (scene.Background is { Width: ScreenWidth, Height: ScreenHeight } bgImg) img.Blit(bgImg, 0, 0, 0, 0, ScreenWidth, ScreenHeight);
        else img.Fill(DefaultBackground);

        if (scene.ShowMessageBox)
        {
            var custom = scene.CustomFrame;
            bool dialogLayout = custom != null ? custom.Width == 56 && custom.Height == 16 : scene.Style == DialogFrameStyle.Dialog;
            if (dialogLayout) DrawDialogFrame(img, custom ?? assets.MessageBox, MsgL, MsgT, MsgW, MsgH);
            else DrawStdFrame(img, custom ?? Frame(scene.StdFrame), MsgL, MsgT, MsgW, MsgH);
            FillRect(img, MsgL * 8, MsgT * 8, MsgW * 8, MsgH * 8, assets.TextBackground);
            for (int i = 0; i < scene.Lines.Count && i < 2; i++)
                DrawText(img, scene.Lines[i], MsgL * 8, MsgT * 8 + 1 + i * 16);
            if (scene.ShowArrow) DrawArrow(img, MsgL * 8 + MsgW * 8 - 16, MsgT * 8 + 17, scene.ArrowFrame);
        }

        if (scene.MenuOptions is { Count: > 0 } options)
        {
            int textW = options.Max(o => font.Width(TextLayout.Units(o)));
            int w = Math.Max(2, (textW + 8 + 7) / 8 + 1), h = options.Count * 2;
            int l = Math.Clamp(scene.MenuX + 1, 1, 29 - w), t = Math.Clamp(scene.MenuY + 1, 1, 19 - h);
            DrawStdFrame(img, Frame(scene.StdFrame), l, t, w, h);
            FillRect(img, l * 8, t * 8, w * 8, h * 8, assets.TextBackground);
            for (int i = 0; i < options.Count; i++) DrawText(img, options[i], l * 8 + 8, t * 8 + 1 + i * 16);
            DrawText(img, "▶", l * 8, t * 8 + 1 + scene.MenuCursor * 16);
        }

        if (scene.YesNoCursor is { } cursor)
        {
            DrawStdFrame(img, Frame(scene.StdFrame), YesNoL, YesNoT, YesNoW, YesNoH);
            FillRect(img, YesNoL * 8, YesNoT * 8, YesNoW * 8, YesNoH * 8, assets.TextBackground);
            DrawText(img, "YES", YesNoL * 8 + 8, YesNoT * 8 + 1);
            DrawText(img, "NO", YesNoL * 8 + 8, YesNoT * 8 + 17);
            DrawText(img, "▶", YesNoL * 8, YesNoT * 8 + 1 + cursor * 16);
        }
        return img;
    }

    private PngImage? Frame(int n) => assets.StdFrames.TryGetValue(n, out var f) ? f : assets.StdFrames.Values.FirstOrDefault();

    /// <summary>WindowFunc_DrawDialogueFrame: tiles of the 7×2 message box sheet around the text window.</summary>
    private static void DrawDialogFrame(RgbaImage img, PngImage? sheet, int l, int t, int w, int h)
    {
        if (sheet == null) { FillRect(img, (l - 2) * 8, (t - 1) * 8, (w + 3) * 8, (h + 2) * 8, new Rgba32(248, 248, 248)); return; }
        void Row(int y, bool vflip)
        {
            Tile(img, sheet, 1, l - 2, y, vflip);
            Tile(img, sheet, 3, l - 1, y, vflip);
            for (int x = l; x < l + w - 1; x++) Tile(img, sheet, 4, x, y, vflip);
            Tile(img, sheet, 5, l + w - 1, y, vflip);
            Tile(img, sheet, 6, l + w, y, vflip);
        }
        Row(t - 1, false);
        for (int y = t; y < t + h; y++)
        {
            Tile(img, sheet, 7, l - 2, y, false);
            for (int x = l - 1; x < l + w; x++) Tile(img, sheet, 9, x, y, false);
            Tile(img, sheet, 10, l + w, y, false);
        }
        Row(t + h, true);
    }

    /// <summary>WindowFunc_DrawStdFrameWithCustomTileAndPalette: 3×3 frame tiles around the window.</summary>
    private static void DrawStdFrame(RgbaImage img, PngImage? sheet, int l, int t, int w, int h)
    {
        if (sheet == null) { FillRect(img, (l - 1) * 8, (t - 1) * 8, (w + 2) * 8, (h + 2) * 8, new Rgba32(80, 80, 96)); return; }
        Tile(img, sheet, 0, l - 1, t - 1, false);
        for (int x = l; x < l + w; x++) Tile(img, sheet, 1, x, t - 1, false);
        Tile(img, sheet, 2, l + w, t - 1, false);
        for (int y = t; y < t + h; y++)
        {
            Tile(img, sheet, 3, l - 1, y, false);
            Tile(img, sheet, 5, l + w, y, false);
        }
        Tile(img, sheet, 6, l - 1, t + h, false);
        for (int x = l; x < l + w; x++) Tile(img, sheet, 7, x, t + h, false);
        Tile(img, sheet, 8, l + w, t + h, false);
    }

    /// <summary>Draws 8×8 tile n of an indexed sheet at tile position (tx, ty). Color 0 is transparent.</summary>
    private static void Tile(RgbaImage img, PngImage sheet, int n, int tx, int ty, bool vflip)
    {
        if (sheet.Indices == null || sheet.Palette == null) return;
        int cols = sheet.Width / 8;
        int sx = (n % cols) * 8, sy = (n / cols) * 8;
        if (sy + 8 > sheet.Height) return;
        for (int y = 0; y < 8; y++)
            for (int x = 0; x < 8; x++)
            {
                int idx = sheet.Indices[(sy + (vflip ? 7 - y : y)) * sheet.Width + sx + x] & 0xF;
                if (idx == 0) continue;
                int px = tx * 8 + x, py = ty * 8 + y;
                if (px < 0 || py < 0 || px >= img.Width || py >= img.Height) continue;
                img[px, py] = GbaColor.ToDisplayed(sheet.Palette[Math.Min(idx, sheet.Palette.Length - 1)] with { A = 255 });
            }
    }

    private static void FillRect(RgbaImage img, int x0, int y0, int w, int h, Rgba32 c)
    {
        for (int y = Math.Max(0, y0); y < Math.Min(img.Height, y0 + h); y++)
            for (int x = Math.Max(0, x0); x < Math.Min(img.Width, x0 + w); x++)
                img[x, y] = c with { A = 255 };
    }

    /// <summary>Draws text with the project font: glyph color 1 = text, 2 = shadow (FONT_NORMAL, dark gray on white).</summary>
    public int DrawText(RgbaImage img, string text, int x, int y)
    {
        var glyphs = font.Glyphs;
        foreach (var unit in TextLayout.Units(text))
        {
            int? g = charmap.GlyphOf(unit);
            if (glyphs?.Indices != null && g is { } gi)
            {
                int cols = glyphs.Width / GbaFont.CellSize;
                int gx = (gi % cols) * GbaFont.CellSize, gy = (gi / cols) * GbaFont.CellSize;
                if (gy + GbaFont.CellSize <= glyphs.Height)
                {
                    for (int py = 0; py < GbaFont.CellSize; py++)
                        for (int px = 0; px < GbaFont.CellSize; px++)
                        {
                            int v = glyphs.Indices[(gy + py) * glyphs.Width + gx + px];
                            if (v is not (1 or 2)) continue;
                            int ox = x + px, oy = y + py;
                            if (ox < 0 || oy < 0 || ox >= img.Width || oy >= img.Height) continue;
                            img[ox, oy] = v == 1 ? assets.TextForeground : assets.TextShadow;
                        }
                }
            }
            x += font.Width(unit);
        }
        return x;
    }

    private void DrawArrow(RgbaImage img, int x, int y, int frame)
    {
        var a = assets.DownArrow;
        if (a?.Indices == null || a.Palette == null)
        {
            for (int i = 0; i < 4; i++) FillRect(img, x + i, y + 4 + i + (frame % 2), 8 - 2 * i, 1, assets.TextForeground);
            return;
        }
        int frames = Math.Max(1, a.Height / 16);
        int fy = (frame % frames) * 16;
        for (int py = 0; py < 16 && fy + py < a.Height; py++)
            for (int px = 0; px < a.Width; px++)
            {
                int idx = a.Indices[(fy + py) * a.Width + px] & 0xF;
                if (idx is 0 or 1) continue; // 0 transparent, 1 = window background
                if (x + px < img.Width && y + py < img.Height)
                    img[x + px, y + py] = GbaColor.ToDisplayed(a.Palette[Math.Min(idx, a.Palette.Length - 1)] with { A = 255 });
            }
    }
}
