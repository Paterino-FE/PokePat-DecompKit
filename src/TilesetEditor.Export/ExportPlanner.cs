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

        var nameError = TilesetNaming.Validate(input.TilesetName);
        if (nameError != null) throw new ExportValidationException([nameError]);
        errors.AddRange(CheckCollisions(project, fs, input.TilesetName));
        errors.AddRange(ExportValidator.ValidateCompiled(project, input.Compiled, input.Primary));
        if (errors.Count > 0) throw new ExportValidationException(errors);

        string folderName = TilesetNaming.ToFolderName(input.TilesetName);
        string dir = project.SecondaryTilesetsDir + folderName;
        string friendly = TilesetNaming.StripPrefix(input.TilesetName);
        var files = new List<PlannedFile>();

        // --- Tileset assets
        files.Add(new PlannedFile($"{dir}/tiles.png", PlannedAction.Create,
            File.ReadAllBytes(Path.Combine(input.CompiledDir, "tiles.png")),
            $"Indexed tile image ({input.Compiled.Tiles.Count} tiles, 4bpp indices with palette bank in the upper nibble)"));
        files.Add(new PlannedFile($"{dir}/metatiles.bin", PlannedAction.Create,
            File.ReadAllBytes(Path.Combine(input.CompiledDir, "metatiles.bin")),
            $"{input.Compiled.MetatileCount} Metatiles × {project.Constants.NumTilesPerMetatile} entries"));
        files.Add(new PlannedFile($"{dir}/metatile_attributes.bin", PlannedAction.Create,
            File.ReadAllBytes(Path.Combine(input.CompiledDir, "metatile_attributes.bin")),
            $"Attributes ({project.Attributes.SizeBytes} bytes per metatile; behavior MB_NORMAL, layer type from the layers)"));
        for (int i = 0; i < 16; i++)
        {
            var src = Path.Combine(input.CompiledDir, "palettes", $"{i:00}.pal");
            var colors = JascPalette.Load(src);
            if (colors.Length > 0 && colors[0].SameRgb(input.CompileTransparency)) colors[0] = options.PaletteSlot0;
            string role = i < project.Constants.NumPalsInPrimary ? "used by the primary, ignored in-game"
                : i < project.Constants.NumPalsTotal ? "secondary palette" : "unused";
            files.Add(new PlannedFile($"{dir}/palettes/{i:00}.pal", PlannedAction.Create,
                Encoding.ASCII.GetBytes(JascPalette.Serialize(colors)), $"Palette {i} ({role})"));
        }

        // --- Sources and manifest (not used by the build; lets the tileset be re-compiled later)
        if (options.IncludeSources && input.SourceArtwork != null)
        {
            foreach (var (layer, img) in input.SourceArtwork.PresentLayers())
                files.Add(new PlannedFile($"{dir}/{SourcesFolder}/{LayerNames.PorytilesFile(layer)}", PlannedAction.Create,
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
            files.Add(new PlannedFile($"{dir}/{SourcesFolder}/tileset_editor.json", PlannedAction.Create,
                Encoding.UTF8.GetBytes(JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }) + "\n"),
                "Manifest: primary, compiler version, layers"));
        }

        // --- Registration (Porymap format)
        var style = project.Style;
        var headers = new StringBuilder();
        headers.Append($"const struct Tileset {input.TilesetName} =\n{{\n");
        if (style.HeadersHaveIsCompressed) headers.Append("    .isCompressed = TRUE,\n");
        headers.Append("    .isSecondary = TRUE,\n");
        headers.Append($"    .tiles = gTilesetTiles_{friendly},\n");
        headers.Append($"    .palettes = gTilesetPalettes_{friendly},\n");
        headers.Append($"    .metatiles = gMetatiles_{friendly},\n");
        headers.Append($"    .metatileAttributes = gMetatileAttributes_{friendly},\n");
        if (style.HeadersHaveCallback) headers.Append("    .callback = NULL,\n");
        headers.Append("};\n");

        var graphics = new StringBuilder();
        graphics.Append($"const u16 gTilesetPalettes_{friendly}[][16] =\n{{\n");
        for (int i = 0; i < style.PaletteIncbinCount; i++)
            graphics.Append($"    INCBIN_U16(\"{dir}/palettes/{i:00}{style.PalettesOutputExtension}\"),\n");
        graphics.Append("};\n");
        graphics.Append($"\nconst u32 gTilesetTiles_{friendly}[] = INCBIN_U32(\"{dir}/tiles{style.TilesOutputExtension}\");\n");

        int bits = style.AttributeDeclarationBits;
        var metatiles = new StringBuilder();
        metatiles.Append($"const u16 gMetatiles_{friendly}[] = INCBIN_U16(\"{dir}/metatiles.bin\");\n");
        metatiles.Append($"const u{bits} gMetatileAttributes_{friendly}[] = INCBIN_U{bits}(\"{dir}/metatile_attributes.bin\");\n");

        files.Add(Append(project, project.HeadersPath, headers.ToString(), $"Append tileset struct {input.TilesetName}"));
        files.Add(Append(project, project.GraphicsPath, graphics.ToString(), $"Append INCBIN for tiles and {style.PaletteIncbinCount} palettes"));
        files.Add(Append(project, project.MetatilesPath, metatiles.ToString(), "Append INCBIN for metatiles and attributes"));

        var plan = new ExportPlan { ProjectRoot = project.Root, TilesetName = input.TilesetName, TilesetFolder = dir, Files = files };

        // --- Validate the result with the same parser before anything is written
        var overlayErrors = ExportValidator.ValidateOverlay(project, plan);
        if (overlayErrors.Count > 0) throw new ExportValidationException(overlayErrors);

        plan.Notes.Add("Existing tilesets are not changed; the three registration files are only appended to.");
        plan.Notes.Add("Assign it to a map in Porymap (Layout → Secondary Tileset).");
        plan.Notes.Add("Metatile behavior is MB_NORMAL; set behaviors like grass or water in Porymap's tileset editor.");
        return plan;
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
