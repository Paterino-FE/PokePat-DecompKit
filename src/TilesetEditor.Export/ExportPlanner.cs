using System.Text;
using System.Text.Json;
using TilesetEditor.Core.Artwork;
using TilesetEditor.Core.Gba;
using TilesetEditor.Core.Imaging;
using TilesetEditor.Core.Project;

namespace TilesetEditor.Export;

public sealed class ExportValidationException(IReadOnlyList<string> errors)
    : Exception("Export not possible:\n- " + string.Join("\n- ", errors))
{
    public IReadOnlyList<string> Errors { get; } = errors;
}

/// <summary>
/// Plans the export of a new secondary tileset in the project's native layout:
/// data/tilesets/secondary/&lt;folder&gt;/{tiles.png, metatiles.bin, metatile_attributes.bin, palettes/NN.pal}
/// plus the registration in headers.h, graphics.h and metatiles.h exactly as Porymap's "New Tileset" writes it
/// (Project::createNewTileset → Tileset::appendToHeaders/appendToGraphics/appendToMetatiles), adapted to the
/// conventions found in the project (palette count, field set).
/// </summary>
public static class ExportPlanner
{
    public const string SourcesFolder = "tileset_editor";

    public static ExportPlan Plan(ProjectInfo project, ExportInput input, ExportOptions? options = null)
    {
        options ??= new ExportOptions();
        var errors = new List<string>();
        var fs = new DiskFileSource(project.Root);

        TilesetInfo? existing = null;
        if (options.ReplaceExisting)
        {
            existing = project.FindTileset(input.TilesetName);
            if (existing == null) errors.Add($"{input.TilesetName} does not exist – it cannot be replaced.");
            else if (!existing.IsSecondary) errors.Add($"{input.TilesetName} is a primary tileset; only secondaries can be rebuilt from images.");
            else if (!existing.IsFullyResolved) errors.Add($"The files of {input.TilesetName} could not be resolved.");
        }
        else
        {
            var nameError = TilesetNaming.Validate(input.TilesetName);
            if (nameError != null) throw new ExportValidationException([nameError]);
            errors.AddRange(CheckCollisions(project, fs, input.TilesetName));
        }
        errors.AddRange(ExportValidator.ValidateCompiled(project, input.Compiled, input.Primary));
        if (errors.Count > 0) throw new ExportValidationException(errors);

        string folderName = TilesetNaming.ToFolderName(input.TilesetName);
        string dir = existing != null ? Path.GetDirectoryName(existing.MetatilesPath!)!.Replace('\\', '/') : project.SecondaryTilesetsDir + folderName;
        var files = new List<PlannedFile>();
        PlannedFile File_(string rel, byte[] content, string description) =>
            existing != null && File.Exists(project.FullPath(rel))
                ? new PlannedFile(rel, PlannedAction.Modify, content, description, Hashing.Sha256(File.ReadAllBytes(project.FullPath(rel))))
                : new PlannedFile(rel, PlannedAction.Create, content, description);

        // --- Tileset assets
        files.Add(File_(existing?.TilesImagePath ?? $"{dir}/tiles.png",
            File.ReadAllBytes(Path.Combine(input.CompiledDir, "tiles.png")),
            $"Indexed tile image ({input.Compiled.Tiles.Count} tiles, 4bpp indices with palette bank in the upper nibble)"));
        files.Add(File_(existing?.MetatilesPath ?? $"{dir}/metatiles.bin",
            File.ReadAllBytes(Path.Combine(input.CompiledDir, "metatiles.bin")),
            $"{input.Compiled.MetatileCount} Metatiles × {project.Constants.NumTilesPerMetatile} entries"));
        var attrBytes = File.ReadAllBytes(Path.Combine(input.CompiledDir, "metatile_attributes.bin"));
        string attrNote = "behavior MB_NORMAL, layer type from the layers";
        if (existing != null)
        {
            // Keep grass, water, ledges … of the metatiles that existed; the layer type comes from the new images.
            int size = project.Attributes.SizeBytes;
            var now = Core.Tilesets.TilesetPackage.ReadAttributes(attrBytes, size);
            var old = Core.Tilesets.TilesetPackage.ReadAttributes(File.ReadAllBytes(project.FullPath(existing.AttributesPath!)), size);
            uint layer = project.Attributes.LayerTypeMask;
            for (int i = 0; i < now.Length && i < old.Length; i++) now[i] = (old[i] & ~layer) | (now[i] & layer);
            attrBytes = Core.Tilesets.TilesetPackage.WriteAttributes(now, size);
            attrNote = $"behaviors of the existing {Math.Min(now.Length, old.Length)} metatiles kept, layer type from the layers";
        }
        files.Add(File_(existing?.AttributesPath ?? $"{dir}/metatile_attributes.bin", attrBytes,
            $"Attributes ({project.Attributes.SizeBytes} bytes per metatile; {attrNote})"));
        for (int i = 0; i < 16; i++)
        {
            var src = Path.Combine(input.CompiledDir, "palettes", $"{i:00}.pal");
            var colors = JascPalette.Load(src);
            if (colors.Length > 0 && colors[0].SameRgb(input.CompileTransparency)) colors[0] = options.PaletteSlot0;
            string role = i < project.Constants.NumPalsInPrimary ? "used by the primary, ignored in-game"
                : i < project.Constants.NumPalsTotal ? "secondary palette" : "unused";
            files.Add(File_(existing != null && i < existing.PalettePaths.Count ? existing.PalettePaths[i] : $"{dir}/palettes/{i:00}.pal",
                Encoding.ASCII.GetBytes(JascPalette.Serialize(colors)), $"Palette {i} ({role})"));
        }

        // --- Sources and manifest (not used by the build; lets the tileset be re-compiled later)
        if (options.IncludeSources && input.SourceArtwork != null)
        {
            foreach (var (layer, img) in input.SourceArtwork.PresentLayers())
                files.Add(File_($"{dir}/{SourcesFolder}/{LayerNames.PorytilesFile(layer)}",
                    Png.EncodeRgba(img), $"RGBA source layer {LayerNames.German(layer)} (for later editing only)"));
            var manifest = new
            {
                tileset = input.TilesetName,
                primary = input.PrimaryName,
                compiler = new { name = "porytiles", version = input.CompilerVersion },
                transparent_color = input.SourceArtwork.TransparentColor.ToHex(),
                layers = input.SourceArtwork.PresentLayers().Select(l => LayerNames.PorytilesFile(l.Layer)).ToArray(),
                metatile_columns_in_source = input.SourceArtwork.Columns,
                tool = "TilesetEditor " + typeof(ExportPlanner).Assembly.GetName().Version,
            };
            files.Add(File_($"{dir}/{SourcesFolder}/tileset_editor.json",
                Encoding.UTF8.GetBytes(JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }) + "\n"),
                "Manifest: primary, compiler version, layers"));
        }

        if (existing != null)
        {
            // Same metatile order as the images → maps keep pointing at the same metatiles; nothing to register – except the
            // tile count some projects write next to the image (expansion 1.13+: INCGFX(…, "-num_tiles 493 -Wnum_tiles")).
            if (existing.TilesSymbol != null && existing.GraphicsDeclaredIn != null)
            {
                var gfxRel = existing.GraphicsDeclaredIn;
                var gfxOriginal = File.ReadAllBytes(project.FullPath(gfxRel));
                var gfxText = new UTF8Encoding(false).GetString(gfxOriginal);
                var decl = GraphicsIncludes.Declarations(gfxText).FirstOrDefault(d => d.Symbol == existing.TilesSymbol);
                if (decl?.Include.Options is { } opts && System.Text.RegularExpressions.Regex.IsMatch(opts, @"-num_tiles\s+\d+"))
                {
                    int count = input.Compiled.Tiles.Count;
                    var declText = gfxText.Substring(decl.Index, decl.Length);
                    var newDecl = System.Text.RegularExpressions.Regex.Replace(declText, @"-num_tiles\s+\d+", $"-num_tiles {count}");
                    var updated = gfxText[..decl.Index] + newDecl + gfxText[(decl.Index + decl.Length)..];
                    if (updated != gfxText)
                        files.Add(new PlannedFile(gfxRel, PlannedAction.Modify, new UTF8Encoding(false).GetBytes(updated),
                            $"{existing.TilesSymbol}: -num_tiles {count}", Hashing.Sha256(gfxOriginal)));
                }
            }
            files.RemoveAll(f => f.Action == PlannedAction.Modify && f.ExpectedOriginalSha256 == Hashing.Sha256(f.Content));
            var replaced = new ExportPlan { ProjectRoot = project.Root, TilesetName = input.TilesetName, TilesetFolder = dir, Files = files };
            int oldCount = (int)(new FileInfo(project.FullPath(existing.MetatilesPath!)).Length / (2 * project.Constants.NumTilesPerMetatile));
            replaced.Notes.Add($"{input.TilesetName} is rebuilt from the images in its folder; maps that use it show the change after the next build.");
            if (existing.Callback is { } cb && cb != "NULL")
                replaced.Notes.Add($"⚠ {input.TilesetName} has animations ({cb}). They copy frames into fixed tile positions, which now hold other tiles – " +
                                   "the animated parts will look wrong. Rebuild it as a new tileset instead, or adjust the animation code.");
            if (input.Compiled.MetatileCount < oldCount)
                replaced.Notes.Add($"The images have {input.Compiled.MetatileCount} metatiles, the tileset had {oldCount}: maps that use the missing ones show other blocks.");
            return replaced;
        }

        // --- Registration (Porymap format)
        files.AddRange(RegistrationFiles(project, input.TilesetName, dir, isSecondary: true, callback: null));

        var plan = new ExportPlan { ProjectRoot = project.Root, TilesetName = input.TilesetName, TilesetFolder = dir, Files = files };

        // --- Validate the result with the same parser before anything is written
        var overlayErrors = ExportValidator.ValidateOverlay(project, plan);
        if (overlayErrors.Count > 0) throw new ExportValidationException(overlayErrors);

        plan.Notes.Add("Existing tilesets are not changed; the three registration files are only appended to.");
        plan.Notes.Add("Assign it to a map in Porymap (Layout → Secondary Tileset).");
        plan.Notes.Add("Metatile behavior is MB_NORMAL; set behaviors like grass or water in Porymap's tileset editor.");
        return plan;
    }

    /// <summary>
    /// The three registration appends for a new tileset (struct in headers.h, palettes/tiles in graphics.h, metatiles and
    /// attributes in metatiles.h) in the project's style – INCBIN or INCGFX, field set, palette count.
    /// </summary>
    public static List<PlannedFile> RegistrationFiles(ProjectInfo project, string name, string dir, bool isSecondary, string? callback)
    {
        string friendly = TilesetNaming.StripPrefix(name);
        var style = project.Style;
        var headers = new StringBuilder();
        headers.Append($"const struct Tileset {name} =\n{{\n");
        if (style.HeadersHaveIsCompressed) headers.Append("    .isCompressed = TRUE,\n");
        headers.Append($"    .isSecondary = {(isSecondary ? "TRUE" : "FALSE")},\n");
        headers.Append($"    .tiles = gTilesetTiles_{friendly},\n");
        headers.Append($"    .palettes = gTilesetPalettes_{friendly},\n");
        headers.Append($"    .metatiles = gMetatiles_{friendly},\n");
        headers.Append($"    .metatileAttributes = gMetatileAttributes_{friendly},\n");
        if (style.HeadersHaveCallback || callback != null) headers.Append($"    .callback = {callback ?? "NULL"},\n");
        headers.Append("};\n");

        var graphics = new StringBuilder();
        graphics.Append($"const u16 gTilesetPalettes_{friendly}[][16] =\n{{\n");
        for (int i = 0; i < style.PaletteIncbinCount; i++)
            graphics.Append($"    {GraphicsInclude.Format(style.UsesIncGfx, 16, $"{dir}/palettes/{i:00}.pal", style.PalettesOutputExtension)},\n");
        graphics.Append("};\n");
        graphics.Append($"\nconst u32 gTilesetTiles_{friendly}[] = {GraphicsInclude.Format(style.UsesIncGfx, 32, $"{dir}/tiles.png", style.TilesOutputExtension)};\n");

        int bits = style.AttributeDeclarationBits;
        var metatiles = new StringBuilder();
        metatiles.Append($"const u16 gMetatiles_{friendly}[] = INCBIN_U16(\"{dir}/metatiles.bin\");\n");
        metatiles.Append($"const u{bits} gMetatileAttributes_{friendly}[] = INCBIN_U{bits}(\"{dir}/metatile_attributes.bin\");\n");

        return
        [
            Append(project, project.HeadersPath, headers.ToString(), $"Append tileset struct {name}"),
            Append(project, project.GraphicsPath, graphics.ToString(), $"Append {(style.UsesIncGfx ? "INCGFX" : "INCBIN")} for tiles and {style.PaletteIncbinCount} palettes"),
            Append(project, project.MetatilesPath, metatiles.ToString(), "Append INCBIN for metatiles and attributes"),
        ];
    }

    private static PlannedFile Append(ProjectInfo project, string rel, string block, string description)
    {
        var path = project.FullPath(rel);
        var original = File.ReadAllBytes(path);
        var text = Encoding.UTF8.GetString(original);
        string nl = text.Contains("\r\n") ? "\r\n" : "\n";
        var sb = new StringBuilder(text);
        if (text.Length > 0 && !text.EndsWith('\n')) sb.Append(nl);
        sb.Append(nl).Append(block.Replace("\n", nl));
        // Preserve a UTF-8 BOM if the file had one.
        bool bom = original.Length >= 3 && original[0] == 0xEF && original[1] == 0xBB && original[2] == 0xBF;
        var body = new UTF8Encoding(false).GetBytes(bom ? sb.ToString().TrimStart('﻿') : sb.ToString());
        var content = bom ? [0xEF, 0xBB, 0xBF, .. body] : body;
        return new PlannedFile(rel, PlannedAction.Modify, content, description, Hashing.Sha256(original));
    }

    /// <summary>Name, folder and symbol collisions with what already exists in the project.</summary>
    public static List<string> CheckCollisions(ProjectInfo project, IFileSource fs, string name)
    {
        var errors = new List<string>();
        if (project.FindTileset(name) != null)
            errors.Add($"A tileset named {name} already exists in {project.HeadersPath}.");
        string folder = TilesetNaming.ToFolderName(name);
        foreach (var baseDir in new[] { project.SecondaryTilesetsDir, project.PrimaryTilesetsDir })
        {
            if (fs.EnumerateDirectories(baseDir).Any(d => string.Equals(d, folder, StringComparison.OrdinalIgnoreCase)))
                errors.Add($"The folder {baseDir}{folder} already exists. Please choose a different name.");
        }
        foreach (var sym in new[] { TilesetNaming.TilesSymbol(name), TilesetNaming.PalettesSymbol(name), TilesetNaming.MetatilesSymbol(name), TilesetNaming.AttributesSymbol(name) })
            if (project.KnownSymbols.Contains(sym))
                errors.Add($"The symbol {sym} is already defined in the project.");
        // Porymap matches tileset folders case-insensitively on Windows; also guard against other tilesets mapping to the same folder.
        foreach (var t in project.Tilesets)
            if (t.Name != name && string.Equals(TilesetNaming.ToFolderName(t.Name), folder, StringComparison.OrdinalIgnoreCase))
                errors.Add($"The name {name} results in the same folder name as the existing tileset {t.Name}.");
        return errors;
    }
}
