using System.Text;
using System.Text.RegularExpressions;
using TilesetEditor.Core.Gba;
using TilesetEditor.Core.Imaging;
using TilesetEditor.Core.Project;
using TilesetEditor.Core.Tilesets;
using TilesetEditor.Core.Trainers;

namespace TilesetEditor.Export;

public enum TilesetImportMode
{
    /// <summary>A new tileset next to the existing ones (other maps are not affected).</summary>
    New,
    /// <summary>The files of an existing tileset are overwritten (every map using it changes).</summary>
    Replace,
}

public enum MetatileSource { Package, Base }

public sealed record TilesetImportOptions
{
    public TilesetImportMode Mode { get; init; } = TilesetImportMode.New;
    public TilesetKind Kind { get; init; }
    /// <summary>Name of the new tileset (Mode New).</summary>
    public string Name { get; init; } = "";
    /// <summary>The existing tileset that fills the files the package lacks – and the one replaced in Mode Replace.</summary>
    public string? Base { get; init; }
    /// <summary>Use the package's metatiles.bin (converted to the project's layer count) or keep those of the base.</summary>
    public MetatileSource Metatiles { get; init; } = MetatileSource.Package;
    /// <summary>A new tileset made from a base keeps the base's animation callback (water, flowers …).</summary>
    public bool KeepAnimations { get; init; } = true;
    /// <summary>Layouts (LAYOUT_…) that should use the tileset afterwards.</summary>
    public IReadOnlyList<string> AssignToLayouts { get; init; } = [];
}

/// <summary>
/// Puts a finished tileset (<see cref="TilesetPackage"/>) into the project: as a new primary/secondary tileset registered like
/// Porymap does, or over an existing tileset; missing files come from a base tileset, triple-layer metatiles are converted to
/// the project's layout (and back), and chosen layouts are switched to the tileset in data/layouts/layouts.json.
/// </summary>
public static partial class TilesetImportPlanner
{
    public static ExportPlan Plan(ProjectInfo project, TilesetPackage package, TilesetImportOptions options)
    {
        var errors = new List<string>();
        var c = project.Constants;
        bool secondary = options.Kind == TilesetKind.Secondary;
        TilesetInfo? baseTs = options.Base != null ? project.FindTileset(options.Base) : null;
        if (options.Base != null && baseTs == null) errors.Add($"The base tileset {options.Base} does not exist.");
        if (baseTs != null && !baseTs.IsFullyResolved) errors.Add($"The files of {baseTs.Name} could not be resolved.");
        if (baseTs != null && baseTs.IsSecondary != secondary)
            errors.Add($"{baseTs.Name} is a {(baseTs.IsSecondary ? "secondary" : "primary")} tileset; the import is set to {(secondary ? "secondary" : "primary")}.");
        if (options.Mode == TilesetImportMode.Replace && baseTs == null) errors.Add("Choose the tileset that should be replaced.");
        if (options.Mode == TilesetImportMode.New)
        {
            if (TilesetNaming.Validate(options.Name) is { } nameError) errors.Add(nameError);
            else errors.AddRange(ExportPlanner.CheckCollisions(project, new DiskFileSource(project.Root), options.Name));
        }
        if (!package.HasTiles && baseTs == null) errors.Add("The folder has no tile image and no base tileset is chosen.");
        if (options.Metatiles == MetatileSource.Package && !package.HasMetatiles && baseTs == null)
            errors.Add("The folder has no metatiles.bin and no base tileset is chosen.");
        if (options.Metatiles == MetatileSource.Package && package.HasMetatiles && package.TilesPerMetatile == null)
            errors.AddRange(package.Problems);
        if (errors.Count > 0) throw new ExportValidationException(errors);

        string name = options.Mode == TilesetImportMode.Replace ? baseTs!.Name : options.Name;
        string dir = options.Mode == TilesetImportMode.Replace
            ? Path.GetDirectoryName(baseTs!.MetatilesPath!)!.Replace('\\', '/')
            : (secondary ? project.SecondaryTilesetsDir : project.PrimaryTilesetsDir) + TilesetNaming.ToFolderName(name);
        var notes = new List<string>();
        var files = new List<PlannedFile>();

        // --- Tiles
        byte[] tilesBytes = package.TilesImage != null ? File.ReadAllBytes(package.TilesImage) : File.ReadAllBytes(project.FullPath(baseTs!.TilesImagePath!));
        var tilesPng = Png.Decode(tilesBytes);
        int tileCount = tilesPng.Width / 8 * (tilesPng.Height / 8);
        int maxTiles = secondary ? c.NumTilesTotal - c.NumTilesInPrimary : c.NumTilesInPrimary;
        if (tileCount > maxTiles) errors.Add($"The tile image has {tileCount} tiles; a {(secondary ? "secondary" : "primary")} tileset holds at most {maxTiles}.");

        // --- Palettes: from the folder, else the base, else black
        var palettes = new Rgba32[16][];
        var paletteSource = new string[16];
        for (int i = 0; i < 16; i++)
        {
            if (package.Palettes.TryGetValue(i, out var pf)) { palettes[i] = Pad(JascPalette.Load(pf)); paletteSource[i] = "from the folder"; }
            else if (baseTs != null && i < baseTs.PalettePaths.Count && File.Exists(project.FullPath(baseTs.PalettePaths[i])))
            { palettes[i] = Pad(JascPalette.Load(project.FullPath(baseTs.PalettePaths[i]))); paletteSource[i] = $"from {baseTs.Name}"; }
            else { palettes[i] = new Rgba32[16]; paletteSource[i] = "black (not used)"; }
        }

        // --- Metatiles and attributes in the project's layout
        int tpm = c.NumTilesPerMetatile;
        int attrSize = project.Attributes.SizeBytes;
        ushort[] entries;
        uint[]? attrs = null;
        TilesetPackage.LayerType[]? layerTypes = null;
        if (options.Metatiles == MetatileSource.Package && package.HasMetatiles)
        {
            var raw = TilesetPackage.ReadEntries(File.ReadAllBytes(package.MetatilesFile!));
            int srcTpm = package.TilesPerMetatile!.Value;
            if (srcTpm == 12 && tpm == 8)
            {
                var conv = TilesetPackage.TripleToDual(raw);
                entries = conv.Entries;
                layerTypes = conv.LayerTypes;
                notes.Add($"Metatiles converted from triple to dual layer ({conv.Entries.Length / 8} metatiles" +
                          (conv.Lossy > 0 ? $"; {conv.Lossy} used all three layers and keep only middle + top – check them in Porymap" : "") + ").");
            }
            else if (srcTpm == 8 && tpm == 12)
            {
                var srcAttrs = package.AttributesFile != null ? TilesetPackage.ReadAttributes(File.ReadAllBytes(package.AttributesFile), attrSize) : [];
                var types = Enumerable.Range(0, raw.Length / 8).Select(m => m < srcAttrs.Length
                    ? (TilesetPackage.LayerType)((srcAttrs[m] & project.Attributes.LayerTypeMask) >> project.Attributes.LayerTypeShift) : TilesetPackage.LayerType.Normal).ToList();
                entries = TilesetPackage.DualToTriple(raw, types);
                notes.Add("Metatiles converted from dual to triple layer.");
            }
            else entries = raw;
            if (package.AttributesFile != null)
            {
                var ab = File.ReadAllBytes(package.AttributesFile);
                if (ab.Length % attrSize == 0 && ab.Length / attrSize == entries.Length / tpm) attrs = TilesetPackage.ReadAttributes(ab, attrSize);
                else notes.Add($"metatile_attributes.bin of the folder does not match the project's {attrSize}-byte attributes; " +
                               (baseTs != null ? $"the attributes of {baseTs.Name} are used where possible." : "all behaviors are MB_NORMAL."));
            }
        }
        else
        {
            entries = TilesetPackage.ReadEntries(File.ReadAllBytes(project.FullPath(baseTs!.MetatilesPath!)));
            attrs = TilesetPackage.ReadAttributes(File.ReadAllBytes(project.FullPath(baseTs.AttributesPath!)), attrSize);
            if (package.HasMetatiles) notes.Add($"Metatiles and behaviors are kept from {baseTs.Name}.");
        }
        int metatileCount = entries.Length / tpm;
        if (attrs == null)
        {
            // Behaviors of the base for the metatiles it has (a recolour keeps its grass, water …), MB_NORMAL for the rest.
            var baseAttrs = baseTs != null ? TilesetPackage.ReadAttributes(File.ReadAllBytes(project.FullPath(baseTs.AttributesPath!)), attrSize) : [];
            attrs = Enumerable.Range(0, metatileCount).Select(m => m < baseAttrs.Length ? baseAttrs[m] : 0u).ToArray();
        }
        if (layerTypes != null)
            for (int m = 0; m < metatileCount && m < attrs.Length; m++)
                attrs[m] = (attrs[m] & ~project.Attributes.LayerTypeMask) | (((uint)layerTypes[m] << project.Attributes.LayerTypeShift) & project.Attributes.LayerTypeMask);
        if (attrs.Length != metatileCount) attrs = Enumerable.Range(0, metatileCount).Select(m => m < attrs.Length ? attrs[m] : 0u).ToArray();

        int maxMetatiles = secondary ? c.NumMetatilesTotal - c.NumMetatilesInPrimary : c.NumMetatilesInPrimary;
        if (metatileCount > maxMetatiles) errors.Add($"{metatileCount} metatiles; a {(secondary ? "secondary" : "primary")} tileset holds at most {maxMetatiles}.");
        // Tile references: a primary only reaches its own tiles, a secondary also the primary's.
        int maxTileRef = secondary ? c.NumTilesTotal : c.NumTilesInPrimary;
        int badTiles = entries.Count(e => (e & 0x3FF) >= maxTileRef);
        if (badTiles > 0) errors.Add($"{badTiles} metatile entries point at tiles beyond {maxTileRef - 1} – the metatiles do not belong to a {(secondary ? "secondary" : "primary")} tileset of this project.");
        int maxPal = secondary ? c.NumPalsTotal : c.NumPalsInPrimary;
        int badPals = entries.Count(e => (e >> 12) >= maxPal);
        if (badPals > 0) notes.Add($"{badPals} metatile entries use palettes ≥ {maxPal}; they will look wrong in game.");
        if (errors.Count > 0) throw new ExportValidationException(errors);

        // --- Files
        var tilesRel = options.Mode == TilesetImportMode.Replace ? baseTs!.TilesImagePath! : $"{dir}/tiles.png";
        var metatilesRel = options.Mode == TilesetImportMode.Replace ? baseTs!.MetatilesPath! : $"{dir}/metatiles.bin";
        var attrsRel = options.Mode == TilesetImportMode.Replace ? baseTs!.AttributesPath! : $"{dir}/metatile_attributes.bin";
        AddFile(project, files, tilesRel, tilesBytes, $"Tile image ({tileCount} tiles{(package.TilesImage != null ? "" : $", from {baseTs!.Name}")})");
        for (int i = 0; i < 16; i++)
        {
            var rel = options.Mode == TilesetImportMode.Replace && baseTs != null && i < baseTs.PalettePaths.Count ? baseTs.PalettePaths[i] : $"{dir}/palettes/{i:00}.pal";
            string role = !secondary ? (i < c.NumPalsInPrimary ? "" : ", not used by a primary")
                : (i < c.NumPalsInPrimary ? ", slot of the primary – ignored" : i < c.NumPalsTotal ? "" : ", not used");
            AddFile(project, files, rel, Encoding.ASCII.GetBytes(JascPalette.Serialize(palettes[i])), $"Palette {i} ({paletteSource[i]}{role})");
        }
        AddFile(project, files, metatilesRel, TilesetPackage.WriteEntries(entries), $"{metatileCount} metatiles × {tpm} tiles");
        AddFile(project, files, attrsRel, TilesetPackage.WriteAttributes(attrs, attrSize), $"Behaviors/layer types ({attrSize} bytes each)");
        files.RemoveAll(f => f.Action == PlannedAction.Modify && f.ExpectedOriginalSha256 == Hashing.Sha256(f.Content)); // unchanged files

        if (options.Mode == TilesetImportMode.New)
        {
            string? callback = options.KeepAnimations && baseTs?.Callback is { } cb && cb != "NULL" ? cb : null;
            files.AddRange(ExportPlanner.RegistrationFiles(project, name, dir, secondary, callback));
            if (callback != null) notes.Add($"Animations of {baseTs!.Name} are kept ({callback}); they draw into the same tile positions.");
        }

        // --- Maps
        if (options.AssignToLayouts.Count > 0)
            files.Add(AssignLayouts(project, options.AssignToLayouts, secondary ? "secondary_tileset" : "primary_tileset", name));

        var plan = ExportPlan.ForFiles(project.Root, options.Mode == TilesetImportMode.Replace ? $"Replace {name}" : $"New tileset {name}", dir, files);
        plan.Notes.AddRange(notes);
        if (options.Mode == TilesetImportMode.Replace)
            plan.Notes.Add($"Every map that uses {name} shows the new graphics after the next build.");
        else if (options.AssignToLayouts.Count == 0)
            plan.Notes.Add($"Assign {name} to maps here (\"Use on maps\") or in Porymap (Layout → {(secondary ? "Secondary" : "Primary")} Tileset).");
        return plan;
    }

    /// <summary>
    /// The metatiles of a planned import as the game draws them. A secondary is drawn over <paramref name="contextPrimary"/>
    /// (its tiles and palettes 0–5 come from there), a primary on its own.
    /// </summary>
    public static RgbaImage? Preview(ProjectInfo project, ExportPlan plan, TilesetKind kind, TilesetInfo? contextPrimary, int metatilesPerRow = 8)
    {
        byte[]? Content(Func<string, bool> match) => plan.Files.FirstOrDefault(f => match(f.RelativePath))?.Content;
        var tiles = Content(p => p.EndsWith(".png", StringComparison.OrdinalIgnoreCase) && !p.Contains("/palettes/"));
        var mt = Content(p => p.EndsWith("metatiles.bin", StringComparison.Ordinal));
        var at = Content(p => p.EndsWith("metatile_attributes.bin", StringComparison.Ordinal));
        if (tiles == null || mt == null || at == null) return null;
        var pals = Enumerable.Range(0, 16).Select(i => Content(p => p.EndsWith($"/{i:00}.pal", StringComparison.Ordinal)) is { } b
            ? JascPalette.Parse(Encoding.ASCII.GetString(b), $"{i:00}.pal") : new Rgba32[16]).ToList();
        var data = TilesetData.FromBytes(plan.TilesetName, tiles, pals, mt, at, project.Constants.NumTilesPerMetatile, project.Attributes.SizeBytes);
        var primary = kind == TilesetKind.Primary ? data
            : contextPrimary is { IsFullyResolved: true } ? project.LoadTilesetData(contextPrimary) : data;
        return new TilesetRenderer(project.Constants, primary, data) { MetatilesPerRow = metatilesPerRow }.RenderSheet();
    }

    private static Rgba32[] Pad(Rgba32[] p) => p.Length >= 16 ? p[..16] : [.. p, .. new Rgba32[16 - p.Length]];

    private static void AddFile(ProjectInfo project, List<PlannedFile> files, string rel, byte[] content, string description)
    {
        var full = project.FullPath(rel);
        files.Add(File.Exists(full)
            ? new PlannedFile(rel, PlannedAction.Modify, content, description, Hashing.Sha256(File.ReadAllBytes(full)))
            : new PlannedFile(rel, PlannedAction.Create, content, description));
    }

    /// <summary>Sets "primary_tileset"/"secondary_tileset" of the given layouts in layouts.json; nothing else changes.</summary>
    public static PlannedFile AssignLayouts(ProjectInfo project, IReadOnlyList<string> layoutIds, string key, string tileset)
    {
        var rel = ProjectLoader.LayoutsFile;
        var original = File.ReadAllBytes(project.FullPath(rel));
        var text = AssignLayoutsInText(new UTF8Encoding(false).GetString(original), layoutIds, key, tileset);
        return new PlannedFile(rel, PlannedAction.Modify, new UTF8Encoding(false).GetBytes(text),
            $"{string.Join(", ", layoutIds)}: {key} = {tileset}", Hashing.Sha256(original));
    }

    /// <summary>The layouts.json text with "<paramref name="key"/>" of the given layouts set to <paramref name="tileset"/>.</summary>
    public static string AssignLayoutsInText(string text, IReadOnlyList<string> layoutIds, string key, string tileset)
    {
        var rel = ProjectLoader.LayoutsFile;
        foreach (var id in layoutIds)
        {
            var idMatch = Regex.Match(text, @"""id""\s*:\s*""" + Regex.Escape(id) + @"""");
            if (!idMatch.Success) throw new ExportValidationException([$"Layout {id} was not found in {rel}."]);
            int open = text.LastIndexOf('{', idMatch.Index);
            int close = TrainerFile.Match(text, open);
            var field = new Regex(@"(""" + key + @"""\s*:\s*"")([^""]*)("")").Match(text, open, close - open);
            if (!field.Success) throw new ExportValidationException([$"Layout {id} has no {key}."]);
            text = text[..field.Groups[2].Index] + tileset + text[(field.Groups[2].Index + field.Groups[2].Length)..];
        }
        return text;
    }
}
