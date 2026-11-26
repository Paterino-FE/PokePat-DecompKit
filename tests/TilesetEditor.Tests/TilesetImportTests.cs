using System.Text;
using TilesetEditor.Core.Gba;
using TilesetEditor.Core.Project;
using TilesetEditor.Core.Tilesets;
using TilesetEditor.Export;

namespace TilesetEditor.Tests;

public class TilesetImportTests
{
    /// <summary>General's metatiles in triple-layer form plus two recoloured palettes – like a shared "snow General".</summary>
    private static string MakeSnowPackage(ProjectInfo project)
    {
        var g = project.FindTileset("gTileset_General")!;
        var dir = TestPaths.NewTempDir("snow-general");
        var entries = TilesetPackage.ReadEntries(File.ReadAllBytes(project.FullPath(g.MetatilesPath!)));
        var attrs = TilesetPackage.ReadAttributes(File.ReadAllBytes(project.FullPath(g.AttributesPath!)), project.Attributes.SizeBytes);
        var types = attrs.Select(a => (TilesetPackage.LayerType)((a & project.Attributes.LayerTypeMask) >> project.Attributes.LayerTypeShift)).ToList();
        File.WriteAllBytes(Path.Combine(dir, "metatiles.bin"), TilesetPackage.WriteEntries(TilesetPackage.DualToTriple(entries, types)));
        File.Copy(project.FullPath(g.TilesImagePath!), Path.Combine(dir, "gTileset_GeneralFrost_Tiles.png"));
        foreach (var i in new[] { 2, 3 })
        {
            var pal = JascPalette.Load(project.FullPath(g.PalettePaths[i]));
            for (int k = 1; k < pal.Length; k++) pal[k] = new TilesetEditor.Core.Imaging.Rgba32(248, 248, (byte)(k * 8));
            File.WriteAllText(Path.Combine(dir, $"{i:00}.pal"), JascPalette.Serialize(pal));
        }
        File.WriteAllText(Path.Combine(dir, "README.md"), "Snow recolour of the general tileset.");
        File.WriteAllBytes(Path.Combine(dir, "SnowDemo.png"), TilesetEditor.Core.Imaging.Png.EncodeRgba(new TilesetEditor.Core.Imaging.RgbaImage(64, 64)));
        return dir;
    }

    [Fact]
    public void Layer_conversion_round_trips()
    {
        var project = ProjectLoader.Load(TestPaths.ReferenceProject);
        var g = project.FindTileset("gTileset_General")!;
        var entries = TilesetPackage.ReadEntries(File.ReadAllBytes(project.FullPath(g.MetatilesPath!)));
        var attrs = TilesetPackage.ReadAttributes(File.ReadAllBytes(project.FullPath(g.AttributesPath!)), project.Attributes.SizeBytes);
        var types = attrs.Select(a => (TilesetPackage.LayerType)((a & project.Attributes.LayerTypeMask) >> project.Attributes.LayerTypeShift)).ToList();
        var triple = TilesetPackage.DualToTriple(entries, types);
        Assert.Equal(entries.Length / 8 * 12, triple.Length);
        var back = TilesetPackage.TripleToDual(triple);
        Assert.Equal(0, back.Lossy);
        // Metatiles whose layers are all empty or that use only one layer may come back with another (equivalent) type.
        int same = Enumerable.Range(0, entries.Length / 8).Count(m => entries.AsSpan(m * 8, 8).SequenceEqual(back.Entries.AsSpan(m * 8, 8)) && types[m] == back.LayerTypes[m]);
        Assert.True(same > entries.Length / 8 * 9 / 10, $"{same} of {entries.Length / 8}");

        // All three layers used → middle + top, counted as lossy.
        ushort[] full = [1, 1, 1, 1, 2, 2, 2, 2, 3, 3, 3, 3];
        var lossy = TilesetPackage.TripleToDual(full);
        Assert.Equal((1, (ushort)2, (ushort)3), (lossy.Lossy, lossy.Entries[0], lossy.Entries[4]));
        ushort[] covered = [1, 1, 1, 1, 2, 2, 2, 2, 0, 0, 0, 0];
        Assert.Equal(TilesetPackage.LayerType.Covered, TilesetPackage.TripleToDual(covered).LayerTypes[0]);
    }

    [Fact]
    public void Reads_a_partial_triple_layer_package_and_imports_it_as_a_new_primary()
    {
        var root = TestPaths.CopyMiniProject();
        var project = ProjectLoader.Load(root);
        var pkg = TilesetPackage.Read(MakeSnowPackage(project), project);
        Assert.Equal((TilesetKind.Primary, "gTileset_General", 12), (pkg.SuggestedKind, pkg.SuggestedBase, pkg.TilesPerMetatile));
        Assert.Equal("gTileset_GeneralFrost", pkg.SuggestedName);
        Assert.Equal([2, 3], pkg.Palettes.Keys);
        Assert.EndsWith("gTileset_GeneralFrost_Tiles.png", pkg.TilesImage); // not the demo screenshot
        Assert.False(pkg.IsArtwork);

        var plan = TilesetImportPlanner.Plan(project, pkg, new TilesetImportOptions
        {
            Kind = pkg.SuggestedKind, Name = pkg.SuggestedName, Base = pkg.SuggestedBase,
        });
        Assert.Contains(plan.Files, f => f.RelativePath == "data/tilesets/primary/general_frost/palettes/02.pal" && f.Description.Contains("from the folder"));
        Assert.Contains(plan.Files, f => f.RelativePath == "data/tilesets/primary/general_frost/palettes/00.pal" && f.Description.Contains("gTileset_General"));
        var headers = Encoding.UTF8.GetString(plan.Files.Single(f => f.RelativePath == ProjectLoader.HeadersFile).Content);
        Assert.Contains("const struct Tileset gTileset_GeneralFrost =", headers);
        Assert.Contains(".isSecondary = FALSE,", headers[headers.IndexOf("gTileset_GeneralFrost", StringComparison.Ordinal)..]);
        Assert.NotNull(TilesetImportPlanner.Preview(project, plan, TilesetKind.Primary, null));

        Assert.True(new ExportApplier(backupRoot: TestPaths.NewTempDir("backup")).Apply(plan).Success);
        var after = ProjectLoader.Load(root);
        var snow = after.FindTileset("gTileset_GeneralFrost")!;
        Assert.False(snow.IsSecondary);
        Assert.True(snow.IsFullyResolved);
        var data = after.LoadTilesetData(snow);
        var general = after.LoadTilesetData(after.FindTileset("gTileset_General")!);
        Assert.Equal(general.MetatileCount, data.MetatileCount);
        Assert.Equal(general.Palettes[0], data.Palettes[0]);
        Assert.NotEqual(general.Palettes[2], data.Palettes[2]);
        // Behaviors come from the base.
        Assert.Equal(general.Attributes.Select(a => a & 0xFF), data.Attributes.Select(a => a & 0xFF));
    }

    [Fact]
    public void Replace_mode_overwrites_only_what_changes_and_layouts_can_be_switched()
    {
        var root = TestPaths.CopyMiniProject();
        var project = ProjectLoader.Load(root);
        var pkg = TilesetPackage.Read(MakeSnowPackage(project), project);
        var plan = TilesetImportPlanner.Plan(project, pkg, new TilesetImportOptions
        {
            Mode = TilesetImportMode.Replace, Kind = TilesetKind.Primary, Base = "gTileset_General", Metatiles = MetatileSource.Base,
        });
        // Only the two recoloured palettes differ; tiles, metatiles and behaviors are identical → not written.
        Assert.Equal(["palettes/02.pal", "palettes/03.pal"], plan.Files.Select(f => f.RelativePath[(f.RelativePath.IndexOf("palettes/", StringComparison.Ordinal))..]).Order());
        Assert.All(plan.Files, f => Assert.Equal(PlannedAction.Modify, f.Action));
        Assert.DoesNotContain(plan.Files, f => f.RelativePath.EndsWith(".h", StringComparison.Ordinal));

        var json = "{\n  \"layouts\": [\n    {\n      \"id\": \"LAYOUT_A\",\n      \"primary_tileset\": \"gTileset_General\",\n      \"secondary_tileset\": \"gTileset_X\"\n    },\n" +
                   "    {\n      \"id\": \"LAYOUT_B\",\n      \"primary_tileset\": \"gTileset_General\",\n      \"secondary_tileset\": \"gTileset_Y\"\n    }\n  ]\n}\n";
        var updated = TilesetImportPlanner.AssignLayoutsInText(json, ["LAYOUT_B"], "primary_tileset", "gTileset_GeneralFrost");
        Assert.Equal(json.Replace("\"LAYOUT_B\",\n      \"primary_tileset\": \"gTileset_General\"", "\"LAYOUT_B\",\n      \"primary_tileset\": \"gTileset_GeneralFrost\""), updated);
        Assert.Throws<ExportValidationException>(() => TilesetImportPlanner.AssignLayoutsInText(json, ["LAYOUT_C"], "primary_tileset", "x"));
    }

    [Fact]
    public void A_folder_of_layer_images_is_recognised_as_artwork()
    {
        var project = ProjectLoader.Load(TestPaths.ReferenceProject);
        var dir = TestPaths.NewTempDir("art");
        File.Copy(TestPaths.Asset("test_middle.png"), Path.Combine(dir, "middle.png"));
        var pkg = TilesetPackage.Read(dir, project);
        Assert.True(pkg.IsArtwork);
        Assert.Throws<ExportValidationException>(() => TilesetImportPlanner.Plan(project, pkg, new TilesetImportOptions { Name = "gTileset_Art", Kind = TilesetKind.Secondary }));
    }
}
