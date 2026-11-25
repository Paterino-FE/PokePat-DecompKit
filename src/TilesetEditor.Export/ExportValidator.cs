using System.Text;
using TilesetEditor.Core.Gba;
using TilesetEditor.Core.Project;

namespace TilesetEditor.Export;

/// <summary>Checks the compiled data against the engine limits and the planned project state against the parser.</summary>
public static class ExportValidator
{
    public static List<string> ValidateCompiled(ProjectInfo project, TilesetData compiled, TilesetData primary)
    {
        var c = project.Constants;
        var errors = new List<string>();
        if (compiled.Tiles.Count == 0) errors.Add("The compiled tileset contains no tiles.");
        if (compiled.Tiles.Count > c.MaxSecondaryTiles)
            errors.Add($"{compiled.Tiles.Count} tiles exceed the capacity of a secondary tileset ({c.MaxSecondaryTiles}).");
        if (compiled.MetatileCount == 0) errors.Add("The compiled tileset contains no metatiles.");
        if (compiled.MetatileCount > c.MaxSecondaryMetatiles)
            errors.Add($"{compiled.MetatileCount} metatiles exceed the capacity of a secondary tileset ({c.MaxSecondaryMetatiles}).");
        if (compiled.TilesPerMetatile != c.NumTilesPerMetatile)
            errors.Add($"Tiles per metatile ({compiled.TilesPerMetatile}) do not match the project ({c.NumTilesPerMetatile}).");
        if (compiled.Attributes.Count != compiled.MetatileCount)
            errors.Add($"Number of attributes ({compiled.Attributes.Count}) does not match the number of metatiles ({compiled.MetatileCount}).");
        if (compiled.Palettes.Count != 16 || compiled.Palettes.Any(p => p.Length < 16))
            errors.Add("Expected 16 palettes of 16 colors each.");

        int badTile = 0, badPal = 0;
        for (int i = 0; i < compiled.MetatileEntries.Count; i++)
        {
            var e = compiled.MetatileEntries[i];
            bool tileOk = e.TileId < c.NumTilesInPrimary
                ? e.TileId < primary.Tiles.Count
                : e.TileId - c.NumTilesInPrimary < compiled.Tiles.Count;
            if (!tileOk && badTile++ < 5)
                errors.Add($"Metatile {i / compiled.TilesPerMetatile}, entry {i % compiled.TilesPerMetatile}: tile ID {e.TileId} does not exist.");
            if (e.Palette >= c.NumPalsTotal && badPal++ < 5)
                errors.Add($"Metatile {i / compiled.TilesPerMetatile}, entry {i % compiled.TilesPerMetatile}: palette {e.Palette} is outside 0–{c.NumPalsTotal - 1}.");
        }

        var layout = project.Attributes;
        for (int i = 0; i < compiled.Attributes.Count; i++)
        {
            int lt = layout.LayerType(compiled.Attributes[i]);
            if (c.LayerMode == LayerMode.Dual && lt > 2)
            {
                errors.Add($"Metatile {i}: invalid layer type {lt}.");
                break;
            }
        }
        return errors;
    }

    /// <summary>Loads the project as it will look after the plan is applied and checks that the new tileset resolves.</summary>
    public static List<string> ValidateOverlay(ProjectInfo project, ExportPlan plan)
    {
        var errors = new List<string>();
        var texts = plan.Modified.ToDictionary(f => ProjectPaths.Normalize(f.RelativePath), f => (string?)Encoding.UTF8.GetString(f.Content));
        var overlay = new OverlayFileSource(new DiskFileSource(project.Root), texts, plan.Created.Select(f => f.RelativePath));
        ProjectInfo after;
        try
        {
            after = ProjectLoader.Load(project.Root, overlay);
        }
        catch (ProjectLoadException ex)
        {
            return [$"After the export the project would no longer be readable: {ex.Message}"];
        }

        var t = after.FindTileset(plan.TilesetName);
        if (t == null) return [$"The new tileset {plan.TilesetName} would not be found in {project.HeadersPath}."];
        if (!t.IsSecondary) errors.Add("The new tileset would not be marked as secondary.");
        if (!t.IsFullyResolved) errors.Add("The symbols of the new tileset could not be fully resolved.");
        var created = new HashSet<string>(plan.Created.Select(f => ProjectPaths.Normalize(f.RelativePath)), StringComparer.Ordinal);
        foreach (var p in new[] { t.TilesImagePath, t.MetatilesPath, t.AttributesPath }.Concat(t.PalettePaths))
            if (p != null && !created.Contains(p))
                errors.Add($"The registration refers to {p}, but this file is not created.");
        if (after.Tilesets.Count != project.Tilesets.Count + 1)
            errors.Add($"After the export {after.Tilesets.Count} tilesets would be registered instead of {project.Tilesets.Count + 1}.");
        foreach (var old in project.Tilesets)
        {
            var o = after.FindTileset(old.Name);
            if (o == null || o.TilesImagePath != old.TilesImagePath || o.MetatilesPath != old.MetatilesPath)
            {
                errors.Add($"The existing tileset {old.Name} would be changed.");
                break;
            }
        }
        return errors;
    }
}
