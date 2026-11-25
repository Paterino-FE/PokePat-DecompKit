using System.Text;
using System.Text.RegularExpressions;
using TilesetEditor.Core.Events;
using TilesetEditor.Core.Imaging;
using TilesetEditor.Core.Project;
using TilesetEditor.Core.SecretBases;
using TilesetEditor.Core.Sprites;
using TilesetEditor.Export;

namespace TilesetEditor.Tests;

public class SecretBaseTests
{
    private static string Ref(string rel) => File.ReadAllText(Path.Combine(TestPaths.ReferenceProject, rel.Replace('/', Path.DirectorySeparatorChar)));

    private static SecretBaseData Sources() =>
        SecretBaseCatalog.ParseSources(Ref(SecretBaseCatalog.ConstantsFile), Ref(SecretBaseCatalog.SourceFile), Ref(SecretBaseCatalog.MetatileLabelsFile));

    [Fact]
    public void ParsesGroupsIdsPositionsAndEntranceMetatiles()
    {
        var d = Sources();
        Assert.Equal(24, d.Groups.Count);
        Assert.Equal(24, d.NumGroups);
        Assert.Equal(6, d.Types.Count);
        Assert.Equal(1, d.Types["RED_CAVE"]);
        Assert.Equal(Regex.Matches(Ref(SecretBaseCatalog.ConstantsFile), @"^#define\s+SECRET_BASE_\w+_\d+\s+\d+", RegexOptions.Multiline).Count, d.Ids.Count);
        var red1 = d.Groups.Single(g => g.Constant == "SECRET_BASE_RED_CAVE1");
        Assert.Equal((0, "RED_CAVE", 1), (red1.Index, red1.Type, red1.Number));
        Assert.Equal(("MAP_SECRET_BASE_RED_CAVE1", 0, 1, 3), (red1.MapConstant, red1.WarpId, red1.PcX, red1.PcY));
        Assert.Contains(red1.Ids, i => i.Name == "SECRET_BASE_RED_CAVE1_1" && i.Value == 1);
        Assert.All(d.Groups, g => Assert.NotNull(g.MapConstant));
        Assert.Equal(7, d.EntranceMetatiles.Count);
        Assert.All(d.EntranceMetatiles, e => { Assert.NotNull(e.Closed); Assert.NotNull(e.Open); Assert.NotNull(e.Type); });
        Assert.Equal("TREE", d.EntranceMetatiles[0].Type);
        Assert.Equal("BROWN_CAVE", d.EntranceMetatiles.Single(e => e.ClosedName.Contains("Fallarbor")).Type);
        Assert.Empty(d.Problems);
    }

    [Fact]
    public void LoadsEveryLocationAndChecksTheOutsideTiles()
    {
        var project = ProjectLoader.Load(TestPaths.ReferenceProject);
        var d = SecretBaseCatalog.Load(project.Root, project.Layouts);
        int expected = Directory.EnumerateFiles(Path.Combine(project.Root, "data", "maps"), "map.json", SearchOption.AllDirectories)
            .Sum(f => Regex.Matches(File.ReadAllText(f), "\"type\": \"secret_base\"").Count);
        Assert.True(expected > 0);
        Assert.Equal(expected, d.Locations.Count);
        Assert.All(d.Groups, g => Assert.NotNull(g.MapFolder));
        Assert.All(d.Locations, l => Assert.NotNull(l.Group));
        Assert.Equal(d.Locations.Count, d.Groups.Sum(g => g.Locations.Count));
        // The vanilla entrances are all on entrance metatiles of the matching type.
        Assert.True(d.Locations.Count(l => l.IsEntranceMetatile) >= d.Locations.Count * 9 / 10);
        var r113 = d.Locations.Single(l => l.IdConstant == "SECRET_BASE_RED_CAVE1_3");
        Assert.Equal(("Route113", "RED_CAVE"), (r113.MapFolder, r113.Type));
        Assert.True(r113.IsEntranceMetatile);
        Assert.Equal("RED_CAVE", r113.MetatileType);
        Assert.Contains("Route113", d.MapFolders);
    }

    [Fact]
    public void InsertsIdConstantAlignedInIdOrder()
    {
        var text = Ref(SecretBaseCatalog.ConstantsFile);
        var d = SecretBaseCatalog.ParseSources(text, Ref(SecretBaseCatalog.SourceFile), "");
        var red1 = d.Groups.Single(g => g.Constant == "SECRET_BASE_RED_CAVE1");
        var free = SecretBaseCatalog.NextFreeId(d, red1)!;
        Assert.Equal(red1.Ids.Max(i => i.Slot) + 1, free.Slot);
        Assert.Equal($"SECRET_BASE_RED_CAVE1_{free.Slot}", free.Name);
        var updated = SecretBaseCatalog.InsertIdConstant(text, free);

        // Exactly one line was added, right after the previous id of the group, value column aligned.
        var nl = text.Contains("\r\n") ? "\r\n" : "\n";
        var prev = Regex.Match(text, @"^#define\s+SECRET_BASE_RED_CAVE1_" + (free.Slot - 1) + @"\s+\d+", RegexOptions.Multiline);
        var newLine = Regex.Match(updated, @"^#define\s+" + free.Name + @"\s+(\d+)", RegexOptions.Multiline);
        Assert.True(newLine.Success);
        Assert.Equal(prev.Index + prev.Length + nl.Length, newLine.Index);
        Assert.Equal(prev.Length, newLine.Length);
        Assert.Equal(text, updated.Remove(newLine.Index, newLine.Length + nl.Length));
        var again = SecretBaseCatalog.ParseSources(updated, Ref(SecretBaseCatalog.SourceFile), "");
        Assert.Contains(again.Groups.Single(g => g.Index == 0).Ids, i => i.Name == free.Name && i.Value == free.Value);
        Assert.Throws<InvalidOperationException>(() => SecretBaseCatalog.InsertIdConstant(updated, free));
    }

    [Fact]
    public void FullGroupFallsBackToAnotherInteriorOfTheSameType()
    {
        var text = "#define SECRET_BASE_TREE 5\n\n" +
                   string.Concat(Enumerable.Range(1, 9).Select(k => $"#define SECRET_BASE_TREE1_{k}  {160 + k}\n")) +
                   "\n#define SECRET_BASE_TREE1 SECRET_BASE_GROUP(16)\n\n#define SECRET_BASE_TREE2 SECRET_BASE_GROUP(17)\n\n#define NUM_SECRET_BASE_GROUPS 2\n";
        var src = "[SECRET_BASE_TREE1] = MAP_NUM(SECRET_BASE_TREE1), 0, 2, 3,\n[SECRET_BASE_TREE2] = MAP_NUM(SECRET_BASE_TREE2), 0, 5, 6,";
        var d = SecretBaseCatalog.ParseSources(text, src, "");
        foreach (var g in d.Groups) g.MapFolder = "SecretBase_" + g.ShortName;
        var tree1 = d.Groups.Single(g => g.Number == 1);
        Assert.Null(SecretBaseCatalog.NextFreeId(d, tree1));
        var free = SecretBaseCatalog.NextFreeId(d, "TREE", tree1)!;
        Assert.Equal(("SECRET_BASE_TREE2_1", 171), (free.Name, free.Value));
        // A group without any id gets its first one before the group's #define.
        var updated = SecretBaseCatalog.InsertIdConstant(text, free);
        Assert.Contains("#define SECRET_BASE_TREE2_1  171\n\n#define SECRET_BASE_TREE2 SECRET_BASE_GROUP(17)", updated);
    }

    [Fact]
    public void AddsSecretBaseEventBytePreserving()
    {
        var json = Ref("data/maps/Route113/map.json");
        var before = SecretBaseCatalog.ReadLocations("Route113", json);
        var updated = MapEventScripts.AddSecretBase(json, 12, 7, 0, "SECRET_BASE_RED_CAVE1_4");
        var after = SecretBaseCatalog.ReadLocations("Route113", updated);
        Assert.Equal(before.Count + 1, after.Count);
        var added = after.Last();
        Assert.Equal((12, 7, 0, "SECRET_BASE_RED_CAVE1_4"), (added.X, added.Y, added.Elevation, added.IdConstant));
        var n = updated.Replace("\r\n", "\n");
        Assert.Contains("\"type\": \"secret_base\",\n      \"x\": 12,\n      \"y\": 7,\n      \"elevation\": 0,\n      \"secret_base_id\": \"SECRET_BASE_RED_CAVE1_4\"\n    }", n);
        // Only an insertion: the original text is a prefix + suffix of the result.
        int prefix = 0;
        while (prefix < json.Length && json[prefix] == updated[prefix]) prefix++;
        int suffix = 0;
        while (suffix < json.Length - prefix && json[^(suffix + 1)] == updated[^(suffix + 1)]) suffix++;
        Assert.Equal(json.Length, prefix + suffix);
        Assert.Equal(MapEventScripts.Read(json).Count, MapEventScripts.Read(updated).Count); // no script slot added
    }

    private static DecorationData RefDecorations() => Decorations.Parse(Ref(Decorations.ConstantsFile), Ref(Decorations.HeaderFile), Ref(Decorations.DescriptionFile),
        Ref(Decorations.TilesFile), Ref(Decorations.IconTableFile), Ref(Decorations.IconGraphicsFile));

    [Fact]
    public void ParsesAllDecorations()
    {
        var d = RefDecorations();
        int constants = Regex.Matches(Ref(Decorations.ConstantsFile), @"^#define\s+DECOR_\w+\s+\d+", RegexOptions.Multiline).Count;
        Assert.Equal(constants - 1, d.Items.Count); // without DECOR_NONE
        Assert.All(d.Items, i => Assert.True(i.HasHeaderEntry));
        Assert.All(d.Items, i => Assert.NotEmpty(i.Description));
        Assert.All(d.Items, i => Assert.NotEmpty(i.Tiles));
        var desk = d.Items.Single(i => i.Constant == "DECOR_SMALL_DESK");
        Assert.Equal(("SMALL DESK", 3000, "DECORPERM_SOLID_FLOOR", "DECORCAT_DESK", "A small desk built\nfor one."),
            (desk.Name, desk.Price, desk.Permission, desk.Category, desk.Description));
        Assert.False(desk.IsSprite);
        Assert.Equal("DECOR_TILE(SmallDesk)", desk.Tiles.Single());
        var pichu = d.Items.Single(i => i.Constant == "DECOR_PICHU_DOLL");
        Assert.True(pichu.IsSprite);
        Assert.Equal("OBJ_EVENT_GFX_PICHU_DOLL", pichu.ObjectGfx);
        Assert.True(pichu.HasIconEntry);
        Assert.Null(pichu.IconPng);
        var snorlax = d.Items.Single(i => i.Constant == "DECOR_SNORLAX_DOLL");
        Assert.Equal("graphics/decorations/snorlax_doll.png", snorlax.IconPng);
        Assert.Equal(46, d.Items.Count(i => i.IsSprite));
        Assert.Equal("DECOR_POKEMON_DESK", d.Items.Single(i => i.Name == "POKéMON DESK").Constant);
        // The branch's extra doll has no icon table entry: reported.
        foreach (var missing in d.Items.Where(i => !i.HasIconEntry))
            Assert.Contains(d.Problems, p => p.StartsWith(missing.Constant, StringComparison.Ordinal));
        var labels = SecretBaseCatalog.ParseMetatileLabels(Ref(SecretBaseCatalog.MetatileLabelsFile));
        Assert.NotNull(Decorations.ResolveTile("DECOR_TILE(SmallDesk)", labels, 512));
    }

    [Fact]
    public void UnchangedDecorationEditsAreByteIdentical()
    {
        var header = Ref(Decorations.HeaderFile);
        var descriptions = Ref(Decorations.DescriptionFile);
        foreach (var d in RefDecorations().Items)
        {
            Assert.Same(header, Decorations.SetPrice(Decorations.SetName(header, d.Constant, d.Name), d.Constant, d.Price));
            Assert.Same(descriptions, Decorations.SetDescription(descriptions, d.DescriptionSymbol, d.Description));
        }
    }

    [Fact]
    public void EditsNamePriceAndDescriptionInPlace()
    {
        var header = Ref(Decorations.HeaderFile);
        var descriptions = Ref(Decorations.DescriptionFile);
        var h2 = Decorations.SetPrice(Decorations.SetName(header, "DECOR_PICHU_DOLL", "PICHU PLUSH"), "DECOR_PICHU_DOLL", 1234);
        var d2 = Decorations.SetDescription(descriptions, "DecorDesc_PICHU_DOLL", "A soft PICHU.\nVery cute.");
        var parsed = Decorations.Parse(Ref(Decorations.ConstantsFile), h2, d2, Ref(Decorations.TilesFile), Ref(Decorations.IconTableFile), Ref(Decorations.IconGraphicsFile));
        var pichu = parsed.Items.Single(i => i.Constant == "DECOR_PICHU_DOLL");
        Assert.Equal(("PICHU PLUSH", 1234, "A soft PICHU.\nVery cute."), (pichu.Name, pichu.Price, pichu.Description));
        var before = RefDecorations();
        foreach (var other in parsed.Items.Where(i => i.Constant != "DECOR_PICHU_DOLL"))
        {
            var o = before.Items.Single(i => i.Constant == other.Constant);
            Assert.Equal((o.Name, o.Price), (other.Name, other.Price));
            if (o.DescriptionSymbol != "DecorDesc_PICHU_DOLL") Assert.Equal(o.Description, other.Description);
        }
        // Only the edited values changed: same number of lines, the rest of the line kept.
        Assert.Equal(header.Split('\n').Length, h2.Split('\n').Length);
        Assert.Contains("        .name = _(\"PICHU PLUSH\"),", h2.Replace("\r\n", "\n"));
        Assert.Contains("        .price = 1234,", h2.Replace("\r\n", "\n"));
        Assert.Contains("const u8 DecorDesc_PICHU_DOLL[] = _(\n    \"A soft PICHU.\\n\"\n        \"Very cute.\");", d2.Replace("\r\n", "\n"));
        Assert.Throws<InvalidOperationException>(() => Decorations.SetName(header, "DECOR_PICHU_DOLL", "A NAME THAT IS FAR TOO LONG"));
        Assert.Throws<InvalidOperationException>(() => Decorations.SetDescription(descriptions, "DecorDesc_PICHU_DOLL", "1\n2\n3\n4"));
    }

    private static string CopyDecorationProject(bool withSprites)
    {
        var src = TestPaths.ReferenceProject;
        var dst = TestPaths.NewTempDir("decor");
        var files = new List<string>
        {
            Decorations.ConstantsFile, Decorations.HeaderFile, Decorations.DescriptionFile, Decorations.TilesFile, Decorations.IconTableFile,
            Decorations.IconGraphicsFile, Decorations.GraphicsHeaderFile, SecretBaseCatalog.MetatileLabelsFile,
        };
        if (withSprites)
            files.AddRange([SpriteImportPlanner.ConstantsFile, SpriteImportPlanner.GraphicsFile, SpriteImportPlanner.PicTablesFile, SpriteImportPlanner.InfoFile,
                SpriteImportPlanner.PointersFile, SpriteImportPlanner.RulesFile, SpriteImportPlanner.MovementFile]);
        foreach (var rel in files)
        {
            var t = Path.Combine(dst, rel.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(t)!);
            File.Copy(Path.Combine(src, rel.Replace('/', Path.DirectorySeparatorChar)), t);
        }
        Directory.CreateDirectory(Path.Combine(dst, "graphics", "decorations"));
        return dst;
    }

    private static string Planned(ExportPlan plan, string root, string rel) =>
        plan.Files.FirstOrDefault(f => f.RelativePath == rel) is { } f ? Encoding.UTF8.GetString(f.Content) : File.ReadAllText(Path.Combine(root, rel));

    private static DecorationData ParsePlanned(ExportPlan plan, string root) => Decorations.Parse(Planned(plan, root, Decorations.ConstantsFile),
        Planned(plan, root, Decorations.HeaderFile), Planned(plan, root, Decorations.DescriptionFile), Planned(plan, root, Decorations.TilesFile),
        Planned(plan, root, Decorations.IconTableFile), Planned(plan, root, Decorations.IconGraphicsFile));

    [Fact]
    public void NewDollWithExistingSpritePlansAllTablesAndParses()
    {
        var root = CopyDecorationProject(withSprites: false);
        var data = Decorations.Load(root);
        var sprites = OverworldSprites.Load(TestPaths.ReferenceProject);
        var req = new NewDecorationRequest("TEST_DOLL", "TEST DOLL", "DECORCAT_DOLL", "DECORSHAPE_1x1", 4000, "A test doll.\nPlace it on a mat.", "OBJ_EVENT_GFX_PICHU_DOLL");
        var result = DecorationPlanner.Plan(root, req, data, sprites, new Dictionary<string, Rgba32[]>());
        Assert.Equal("DECOR_TEST_DOLL", result.Constant);
        Assert.Equal(data.MaxId + 1, result.Id);
        Assert.Equal(5, result.Plan.Files.Count);

        var after = ParsePlanned(result.Plan, root);
        Assert.Equal("DECOR_TEST_DOLL", after.NumDecorationsValue);
        var doll = after.Items.Single(i => i.Constant == "DECOR_TEST_DOLL");
        Assert.Equal((result.Id, "TEST DOLL", 4000, "A test doll.\nPlace it on a mat.", "OBJ_EVENT_GFX_PICHU_DOLL"),
            (doll.Id, doll.Name, doll.Price, doll.Description, doll.ObjectGfx));
        Assert.True(doll.IsSprite);
        Assert.True(doll.HasIconEntry);
        Assert.Null(doll.IconSymbol);
        Assert.All(after.Items, i => Assert.True(i.HasIconEntry)); // missing entries were completed
        Assert.Empty(after.Problems);
        Assert.Equal(data.Items.Count + 1, after.Items.Count);
        // Existing entries keep their values.
        foreach (var old in data.Items)
        {
            var now = after.Items.Single(i => i.Constant == old.Constant);
            Assert.Equal((old.Id, old.Name, old.Price, old.Description, string.Join(",", old.Tiles)), (now.Id, now.Name, now.Price, now.Description, string.Join(",", now.Tiles)));
        }
        // header.h: the previous last entry got its comma, the array still closes.
        var header = Planned(result.Plan, root, Decorations.HeaderFile).Replace("\r\n", "\n");
        Assert.Contains("    },\n\n    [DECOR_TEST_DOLL] =\n    {\n        .id = DECOR_TEST_DOLL,", header);
        Assert.EndsWith("    },\n\n};\n", header.TrimEnd('\n') + "\n");
        Assert.Throws<ExportValidationException>(() => DecorationPlanner.Plan(root, req with { Name = "PICHU_DOLL" }, data, sprites, new Dictionary<string, Rgba32[]>()));
        Assert.Throws<ExportValidationException>(() => DecorationPlanner.Plan(root, req with { GfxConstant = "OBJ_EVENT_GFX_NOPE" }, data, sprites, new Dictionary<string, Rgba32[]>()));
    }

    [Fact]
    public void NewDollWithIconAndNewSpritePlansGraphics()
    {
        var root = CopyDecorationProject(withSprites: true);
        var data = Decorations.Load(root);
        var sprites = OverworldSprites.Load(root);
        // 16×16 doll sprite and 24×24 icon with 3 colors on a transparent background.
        var spriteImg = new RgbaImage(16, 16);
        var iconImg = new RgbaImage(24, 24);
        var colors = new[] { new Rgba32(248, 0, 0), new Rgba32(0, 248, 0), new Rgba32(0, 0, 248) };
        for (int y = 2; y < 14; y++) for (int x = 2; x < 14; x++) spriteImg[x, y] = colors[(x + y) % 3];
        for (int y = 3; y < 21; y++) for (int x = 3; x < 21; x++) iconImg[x, y] = colors[(x * y) % 3];
        var spritePath = Path.Combine(root, "doll.png");
        var iconPath = Path.Combine(root, "icon.png");
        File.WriteAllBytes(spritePath, Png.EncodeRgba(spriteImg));
        File.WriteAllBytes(iconPath, Png.EncodeRgba(iconImg));
        var req = new NewDecorationRequest("NEON_DOLL", "NEON DOLL", "DECORCAT_DOLL", "DECORSHAPE_1x1", 3000, "A shiny doll.", null, iconPath,
            new SpriteImportRequest("NeonDoll", spritePath, 16, 16, "OBJ_EVENT_PAL_TAG_NPC_1", false, "NeonDoll"));
        var result = DecorationPlanner.Plan(root, req, data, sprites, new Dictionary<string, Rgba32[]>());
        var paths = result.Plan.Files.Select(f => f.RelativePath).ToList();
        Assert.Contains("graphics/decorations/neon_doll.png", paths);
        Assert.Contains(SpriteImportPlanner.ConstantsFile, paths);
        Assert.Contains(Decorations.GraphicsHeaderFile, paths);
        Assert.Equal(paths.Count, paths.Distinct().Count());

        var after = ParsePlanned(result.Plan, root);
        var doll = after.Items.Single(i => i.Constant == "DECOR_NEON_DOLL");
        Assert.Equal(("OBJ_EVENT_GFX_NEON_DOLL", "gDecorIcon_NeonDoll", "gDecorIconPalette_NeonDoll", "graphics/decorations/neon_doll.png"),
            (doll.ObjectGfx, doll.IconSymbol, doll.IconPaletteSymbol, doll.IconPng));
        Assert.Contains("extern const u32 gDecorIcon_NeonDoll[];", Planned(result.Plan, root, Decorations.GraphicsHeaderFile));
        Assert.Contains("#define OBJ_EVENT_GFX_NEON_DOLL", Planned(result.Plan, root, SpriteImportPlanner.ConstantsFile));
        var icon = Png.Decode(result.Plan.Files.Single(f => f.RelativePath == "graphics/decorations/neon_doll.png").Content);
        Assert.Equal((24, 24, true, 4), (icon.Width, icon.Height, icon.IsIndexed, icon.BitDepth));
        Assert.Equal(0, icon.Indices![0]);

        // Applying the plan leaves a project that loads again.
        Assert.True(new ExportApplier(backupRoot: TestPaths.NewTempDir("backup")).Apply(result.Plan).Success);
        var reloaded = Decorations.Load(root);
        Assert.Equal("graphics/decorations/neon_doll.png", reloaded.Items.Single(i => i.Constant == "DECOR_NEON_DOLL").IconPng);
        Assert.Contains(OverworldSprites.Load(root), s => s.GfxId == "OBJ_EVENT_GFX_NEON_DOLL");
    }

    [Fact]
    public void WrongIconSizeIsRejected()
    {
        var root = CopyDecorationProject(withSprites: false);
        var path = Path.Combine(root, "big.png");
        File.WriteAllBytes(path, Png.EncodeRgba(new RgbaImage(32, 32)));
        Assert.Throws<ExportValidationException>(() => DecorationPlanner.ConvertIcon(path));
    }
}
