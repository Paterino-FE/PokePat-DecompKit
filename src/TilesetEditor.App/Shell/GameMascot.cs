using System.IO;
using TilesetEditor.Core.Imaging;
using TilesetEditor.Core.Platinum;
using TilesetEditor.Core.Project;

namespace TilesetEditor.App.Shell;

/// <summary>
/// The mascot of the game in the corner of the window – Rayquaza for Emerald, Giratina for Platinum – taken from the open
/// project's own Pokémon sprites (PokéPat ships no Pokémon artwork). Cropped to the visible pixels; null if missing.
/// </summary>
public static class GameMascot
{
    public static RgbaImage? Load(string root, GameKind game)
    {
        try
        {
            RgbaImage? img = game == GameKind.Platinum
                ? PlatinumSpriteSet.Load(root, "SPECIES_GIRATINA").Frame(PlatinumSpritePart.MaleFront, 0, false)
                : Core.Trainers.TrainerGraphics.SpeciesFront(root, "SPECIES_RAYQUAZA") is { } path ? Core.Trainers.TrainerGraphics.LoadSprite(path) : null;
            return img == null ? null : Crop(img);
        }
        catch (Exception ex) when (ex is IOException or PngFormatException or InvalidDataException or FormatException or System.Text.Json.JsonException or ArgumentException) { return null; }
    }

    /// <summary>The smallest square around the visible pixels (so the mascot fills the logo spot).</summary>
    public static RgbaImage Crop(RgbaImage img)
    {
        int minX = img.Width, minY = img.Height, maxX = -1, maxY = -1;
        for (int y = 0; y < img.Height; y++)
            for (int x = 0; x < img.Width; x++)
                if (img[x, y].A > 0) { minX = Math.Min(minX, x); maxX = Math.Max(maxX, x); minY = Math.Min(minY, y); maxY = Math.Max(maxY, y); }
        if (maxX < 0) return img;
        int w = maxX - minX + 1, h = maxY - minY + 1, size = Math.Max(w, h);
        var result = new RgbaImage(size, size);
        result.Blit(img, minX, minY, (size - w) / 2, (size - h) / 2, w, h);
        return result;
    }
}
