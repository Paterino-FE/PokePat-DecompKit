using TilesetEditor.Core.Graphics;
using TilesetEditor.Core.Imaging;
using TilesetEditor.Core.Maps;
using TilesetEditor.Core.Project;
using TilesetEditor.Core.Sprites;

namespace TilesetEditor.Core.SecretBases;

/// <summary>Pictures for the secret base module: maps with tile markers and decoration previews.</summary>
public static class SecretBasePreview
{
    public static readonly Rgba32 Selected = new(239, 68, 68);
    public static readonly Rgba32 Other = new(249, 115, 22);
    public static readonly Rgba32 Entrance = new(34, 197, 94);
    public static readonly Rgba32 Computer = new(59, 130, 246);

    /// <summary>Draws a 2-pixel frame and a light tint over block (x, y).</summary>
    public static void MarkTile(RgbaImage img, int x, int y, Rgba32 color, bool fill = true)
    {
        int s = MapRenderer.BlockSize, ox = x * s, oy = y * s;
        if (ox < 0 || oy < 0 || ox + s > img.Width || oy + s > img.Height) return;
        for (int py = 0; py < s; py++)
            for (int px = 0; px < s; px++)
            {
                bool border = px < 2 || py < 2 || px >= s - 2 || py >= s - 2;
                var c = img[ox + px, oy + py];
                if (border) img[ox + px, oy + py] = color;
                else if (fill) img[ox + px, oy + py] = new Rgba32((byte)((c.R * 3 + color.R) / 4), (byte)((c.G * 3 + color.G) / 4), (byte)((c.B * 3 + color.B) / 4));
            }
    }

    /// <summary>The layout whose tilesets the decoration metatiles are drawn with (SecretBase + SecretBaseRedCave, as decoration.c does).</summary>
    public static LayoutInfo? DecorationLayout(ProjectInfo project) =>
        project.Layouts.FirstOrDefault(l => l.SecondaryTileset == "gTileset_SecretBaseRedCave")
        ?? project.Layouts.FirstOrDefault(l => l.PrimaryTileset == "gTileset_SecretBase");

    /// <summary>Icon PNG if the decoration has one, else the object sprite's first frame, else its metatiles.</summary>
    public static RgbaImage? Decoration(ProjectInfo project, Decoration d, DecorationData data, IReadOnlyList<OverworldSprite> sprites,
        IReadOnlyDictionary<string, Rgba32[]> palettes, bool preferIcon = true)
    {
        try
        {
            if (preferIcon && d.IconPng != null) return GameGraphics.Render(Png.Load(project.FullPath(d.IconPng)));
            if (d.IsSprite) return Sprite(project, d.ObjectGfx, sprites, palettes);
            var layout = DecorationLayout(project);
            if (layout == null || d.Tiles.Count == 0) return null;
            var (w, h) = d.ShapeSize;
            var blocks = new ushort[w * h];
            for (int i = 0; i < blocks.Length; i++)
                blocks[i] = (ushort)(Decorations.ResolveTile(d.Tiles[Math.Min(i, d.Tiles.Count - 1)], data.MetatileLabels, project.Constants.NumMetatilesInPrimary) ?? 0);
            return MapRenderer.Render(project, layout, new MapGrid(w, h, blocks));
        }
        catch (Exception ex) when (ex is IOException or PngFormatException or FormatException or ArgumentException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>First frame of an overworld sprite in its game palette.</summary>
    public static RgbaImage? Sprite(ProjectInfo project, string? gfx, IReadOnlyList<OverworldSprite> sprites, IReadOnlyDictionary<string, Rgba32[]> palettes)
    {
        var s = sprites.FirstOrDefault(x => x.GfxId == gfx);
        if (s?.PngPath == null) return null;
        var full = project.FullPath(s.PngPath);
        if (!File.Exists(full)) return null;
        var sheet = OverworldSprites.Render(Png.Load(full), palettes.GetValueOrDefault(s.PaletteTag));
        int w = s.Width > 0 ? Math.Min(s.Width, sheet.Width) : sheet.Width, h = s.Height > 0 ? Math.Min(s.Height, sheet.Height) : sheet.Height;
        var frame = new RgbaImage(w, h);
        frame.Blit(sheet, 0, 0, 0, 0, w, h);
        return frame;
    }

    /// <summary>Copies the image onto a square canvas of <paramref name="size"/> (centered, bottom aligned for tall sprites).</summary>
    public static RgbaImage Fit(RgbaImage img, int size)
    {
        if (img.Width > size || img.Height > size) return img;
        var canvas = new RgbaImage(size, size);
        canvas.Blit(img, 0, 0, (size - img.Width) / 2, (size - img.Height) / 2, img.Width, img.Height);
        return canvas;
    }
}
