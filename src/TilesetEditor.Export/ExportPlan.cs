using System.Security.Cryptography;
using TilesetEditor.Core.Artwork;
using TilesetEditor.Core.Gba;
using TilesetEditor.Core.Imaging;

namespace TilesetEditor.Export;

public enum PlannedAction
{
    Create,
    Modify,
    /// <summary>The file is removed (backed up first; restored on rollback). Content is ignored.</summary>
    Delete,
}

public sealed record PlannedFile(string RelativePath, PlannedAction Action, byte[] Content, string Description, string? ExpectedOriginalSha256 = null)
{
    public string ActionText => Action switch { PlannedAction.Create => "Create", PlannedAction.Delete => "Delete", _ => "Modify" };
    public string SizeText => Content.Length < 1024 ? $"{Content.Length} B" : $"{Content.Length / 1024.0:0.0} KB";
}

public sealed class ExportPlan
{
    public required string ProjectRoot { get; init; }
    public required string TilesetName { get; init; }
    public required string TilesetFolder { get; init; }
    public required IReadOnlyList<PlannedFile> Files { get; init; }
    public List<string> Notes { get; } = [];

    public IEnumerable<PlannedFile> Created => Files.Where(f => f.Action == PlannedAction.Create);
    public IEnumerable<PlannedFile> Modified => Files.Where(f => f.Action == PlannedAction.Modify);

    /// <summary>A plan that is not a tileset export (e.g. events); <paramref name="title"/> is shown as heading.</summary>
    public static ExportPlan ForFiles(string projectRoot, string title, string folder, IReadOnlyList<PlannedFile> files) =>
        new() { ProjectRoot = projectRoot, TilesetName = title, TilesetFolder = folder, Files = files };

    /// <summary>Create the file, or modify it (with the current content's hash as precondition). Null if nothing changes.</summary>
    public static PlannedFile? CreateOrModify(string projectRoot, string rel, byte[] content, string description)
    {
        var full = TilesetEditor.Core.Project.ProjectPaths.Combine(projectRoot, rel);
        if (!File.Exists(full)) return new PlannedFile(rel, PlannedAction.Create, content, description);
        var current = File.ReadAllBytes(full);
        if (current.AsSpan().SequenceEqual(content)) return null;
        return new PlannedFile(rel, PlannedAction.Modify, content, description, Hashing.Sha256(current)) { };
    }
}

/// <summary>What the compiler produced, independent of how it was produced.</summary>
public sealed class ExportInput
{
    public required string TilesetName { get; init; }
    public required string PrimaryName { get; init; }
    /// <summary>Directory with tiles.png, metatiles.bin, metatile_attributes.bin and palettes/NN.pal (Porytiles porytiles_bin).</summary>
    public required string CompiledDir { get; init; }
    public required TilesetData Compiled { get; init; }
    public required TilesetData Primary { get; init; }
    public required string CompilerVersion { get; init; }
    /// <summary>Transparency color used during compilation; palette slot 0 entries with this value are normalized.</summary>
    public required Rgba32 CompileTransparency { get; init; }
    /// <summary>Optional: the imported artwork, stored next to the tileset for later re-compilation.</summary>
    public Artwork? SourceArtwork { get; init; }
}

public sealed class ExportOptions
{
    /// <summary>Store the RGBA source layers and a manifest in &lt;tileset&gt;/tileset_editor/.</summary>
    public bool IncludeSources { get; set; } = true;
    /// <summary>Value written to palette slot 0 (transparent on hardware) instead of the sandbox transparency color.</summary>
    public Rgba32 PaletteSlot0 { get; set; } = Rgba32.Magenta;
    /// <summary>
    /// Overwrite the existing secondary tileset <see cref="ExportInput.TilesetName"/> in its folder instead of adding a new one
    /// (edited images of an existing tileset). Behaviors of the metatiles that already existed are kept.
    /// </summary>
    public bool ReplaceExisting { get; set; }
}

public static class Hashing
{
    public static string Sha256(byte[] data) => Convert.ToHexString(SHA256.HashData(data));
}
