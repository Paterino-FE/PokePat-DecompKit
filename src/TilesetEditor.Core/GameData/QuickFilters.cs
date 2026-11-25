using System.Text.RegularExpressions;
using TilesetEditor.Core.Gba;
using TilesetEditor.Core.Imaging;
using TilesetEditor.Core.Project;
using TilesetEditor.Core.Sprites;

namespace TilesetEditor.Core.GameData;

/// <summary>A column value set: a row matches if the column holds one of the values.</summary>
public sealed record QuickFilterCondition(string Column, IReadOnlySet<string> Values);

/// <summary>A one-click filter of the game data view ("Poké Balls", "Healing", "Fire" …) with an icon from the game.</summary>
public sealed class QuickFilter
{
    public required string Label { get; init; }
    public required IReadOnlyList<QuickFilterCondition> AnyOf { get; init; }
    /// <summary>The icon as the game shows it (item icon, type or category icon), or null.</summary>
    public RgbaImage? Icon { get; set; }
    public int Count { get; set; }

    public bool Matches(Func<string, string?> valueOf) => AnyOf.Any(c => valueOf(c.Column) is { } v && c.Values.Contains(v));
}

/// <summary>
/// Builds the one-click categories of a game data table from what the project defines: item pockets and kinds (sortType,
/// expansion 1.13+) grouped the way a player thinks of them, move types and damage categories, Pokémon types.
/// Only categories with entries are returned.
/// </summary>
public static partial class QuickFilters
{
    private sealed record ItemGroup(string Label, string IconItem, string[] Pockets, string[] Kinds);

    // Grouping of POCKET_ and ITEM_TYPE_ values; the icon item stands for the group (first matching entry if it is missing).
    private static readonly ItemGroup[] ItemGroups =
    [
        new("Poké Balls", "ITEM_POKE_BALL", ["POCKET_POKE_BALLS"], []),
        new("Healing", "ITEM_POTION", [], ["ITEM_TYPE_HEALTH_RECOVERY", "ITEM_TYPE_STATUS_RECOVERY", "ITEM_TYPE_PP_RECOVERY"]),
        new("Battle items", "ITEM_X_ATTACK", [], ["ITEM_TYPE_X_ITEM", "ITEM_TYPE_BATTLE_ITEM", "ITEM_TYPE_FLUTE", "ITEM_TYPE_AUX_ITEM"]),
        new("Vitamins & boosts", "ITEM_RARE_CANDY", [], ["ITEM_TYPE_LEVEL_UP_ITEM", "ITEM_TYPE_STAT_BOOST_DRINK", "ITEM_TYPE_STAT_BOOST_FEATHER", "ITEM_TYPE_STAT_BOOST_MOCHI", "ITEM_TYPE_NATURE_MINT"]),
        new("Evolution", "ITEM_FIRE_STONE", [], ["ITEM_TYPE_EVOLUTION_STONE", "ITEM_TYPE_EVOLUTION_ITEM"]),
        new("Held items", "ITEM_LEFTOVERS", [], ["ITEM_TYPE_HELD_ITEM", "ITEM_TYPE_SPECIAL_HELD_ITEM", "ITEM_TYPE_TYPE_BOOST_HELD_ITEM", "ITEM_TYPE_CONTEST_HELD_ITEM",
            "ITEM_TYPE_EV_BOOST_HELD_ITEM", "ITEM_TYPE_INCENSE", "ITEM_TYPE_GEM", "ITEM_TYPE_PLATE", "ITEM_TYPE_MEMORY", "ITEM_TYPE_DRIVE"]),
        new("Mega, Z & Tera", "ITEM_GENGARITE", [], ["ITEM_TYPE_MEGA_STONE", "ITEM_TYPE_Z_CRYSTAL", "ITEM_TYPE_TERA_SHARD"]),
        new("Berries", "ITEM_ORAN_BERRY", ["POCKET_BERRIES"], []),
        new("TMs & HMs", "ITEM_TM_FOCUS_PUNCH", ["POCKET_TM_HM"], []),
        new("Key items", "ITEM_MACH_BIKE", ["POCKET_KEY_ITEMS"], []),
        new("Field", "ITEM_REPEL", [], ["ITEM_TYPE_FIELD_USE"]),
        new("Treasures & fossils", "ITEM_NUGGET", [], ["ITEM_TYPE_SELLABLE", "ITEM_TYPE_RELIC", "ITEM_TYPE_FOSSIL", "ITEM_TYPE_SHARD", "ITEM_TYPE_NECTAR", "ITEM_TYPE_GROWTH"]),
        new("Mail", "ITEM_ORANGE_MAIL", [], ["ITEM_TYPE_MAIL"]),
        // Without sortType (expansion up to 1.12) the remaining items are grouped by their pocket.
        new("Items", "ITEM_POTION", ["POCKET_ITEMS"], []),
    ];

    /// <summary>The quick filters for <paramref name="table"/>; icons are loaded from <paramref name="root"/>.</summary>
    public static List<QuickFilter> For(GameDataTable table, string root)
    {
        var result = table.Def.Id switch
        {
            "items" => Items(table, root),
            "moves" => Types(table, root, "Type").Concat(Categories(table, root)).ToList(),
            "species" => Types(table, root, "Type 1", "Type 2"),
            _ => [],
        };
        foreach (var f in result) f.Count = table.Rows.Count(r => f.Matches(c => Cell(table, r, c)));
        return result.Where(f => f.Count > 0).ToList();
    }

    public static string? Cell(GameDataTable table, GameDataRow row, string column)
    {
        int i = Column(table, column);
        return i < 0 ? null : row.Cells[i].Value;
    }

    private static int Column(GameDataTable table, string header) => table.Def.Columns.ToList().FindIndex(c => c.Header == header);

    private static List<QuickFilter> Items(GameDataTable table, string root)
    {
        bool hasKinds = table.Rows.Any(r => Cell(table, r, "Kind") is { Length: > 0 });
        var icons = new Lazy<ItemIcons>(() => ItemIcons.Load(root));
        var list = new List<QuickFilter>();
        foreach (var g in ItemGroups)
        {
            // With kinds the "Items" pocket is split into the groups above; without them only the pockets exist.
            if (hasKinds && g.Label == "Items") continue;
            if (!hasKinds && g.Pockets.Length == 0) continue;
            var conds = new List<QuickFilterCondition>();
            if (g.Pockets.Length > 0) conds.Add(new("Pocket", g.Pockets.ToHashSet()));
            if (g.Kinds.Length > 0) conds.Add(new("Kind", g.Kinds.ToHashSet()));
            var f = new QuickFilter { Label = g.Label, AnyOf = conds };
            var iconItem = table.Rows.Any(r => r.Key == g.IconItem && f.Matches(c => Cell(table, r, c))) ? g.IconItem
                : table.Rows.FirstOrDefault(r => f.Matches(c => Cell(table, r, c)))?.Key;
            if (iconItem != null) f.Icon = icons.Value.Icon(iconItem);
            list.Add(f);
        }
        return list;
    }

    private static List<QuickFilter> Types(GameDataTable table, string root, params string[] columns)
    {
        var types = columns.SelectMany(c => table.Rows.Select(r => Cell(table, r, c))).OfType<string>()
            .Where(v => Regex.IsMatch(v, @"^TYPE_\w+$") && v is not ("TYPE_NONE" or "TYPE_MYSTERY")).Distinct()
            .OrderBy(TypeOrder).ToList();
        return types.Select(t => new QuickFilter
        {
            Label = Pretty(t, "TYPE_"),
            AnyOf = columns.Select(c => new QuickFilterCondition(c, new HashSet<string> { t })).ToList(),
            Icon = TypeIcon(root, t),
        }).ToList();
    }

    private static List<QuickFilter> Categories(GameDataTable table, string root)
    {
        string[] order = ["DAMAGE_CATEGORY_PHYSICAL", "DAMAGE_CATEGORY_SPECIAL", "DAMAGE_CATEGORY_STATUS"];
        var sheet = LoadIndexed(root, "graphics/interface/category_icons.png", null);
        return order.Select((c, i) => new QuickFilter
        {
            Label = Pretty(c, "DAMAGE_CATEGORY_"),
            AnyOf = [new QuickFilterCondition("Category", new HashSet<string> { c })],
            Icon = sheet != null && sheet.Height >= (i + 1) * 16 ? Crop(sheet, 0, i * 16, Math.Min(16, sheet.Width), 16) : null,
        }).ToList();
    }

    private static readonly string[] TypeSequence =
        ["NORMAL", "FIGHTING", "FLYING", "POISON", "GROUND", "ROCK", "BUG", "GHOST", "STEEL", "FIRE", "WATER", "GRASS", "ELECTRIC", "PSYCHIC", "ICE", "DRAGON", "DARK", "FAIRY", "STELLAR"];

    private static int TypeOrder(string t)
    {
        int i = Array.IndexOf(TypeSequence, t["TYPE_".Length..]);
        return i < 0 ? 100 : i;
    }

    /// <summary>graphics/types/fire.png (32×16, own palette); FIGHTING is called fight.png.</summary>
    private static RgbaImage? TypeIcon(string root, string type)
    {
        var name = type["TYPE_".Length..].ToLowerInvariant();
        foreach (var file in new[] { name, name == "fighting" ? "fight" : name })
            if (LoadIndexed(root, $"graphics/types/{file}.png", null) is { } img) return img;
        return null;
    }

    /// <summary>POCKET_POKE_BALLS → "Poke Balls", TYPE_FIRE → "Fire".</summary>
    public static string Pretty(string constant, string prefix)
    {
        var s = constant.StartsWith(prefix, StringComparison.Ordinal) ? constant[prefix.Length..] : constant;
        return string.Join(' ', s.Split('_', StringSplitOptions.RemoveEmptyEntries)
            .Select(w => w.All(char.IsDigit) ? w : char.ToUpperInvariant(w[0]) + w[1..].ToLowerInvariant()));
    }

    /// <summary>An indexed PNG drawn with <paramref name="palettePath"/> (or its own palette); index 0 transparent.</summary>
    internal static RgbaImage? LoadIndexed(string root, string pngRel, string? palettePath)
    {
        var p = ProjectPaths.Combine(root, pngRel);
        if (!File.Exists(p)) return null;
        try
        {
            var png = Png.Load(p);
            Rgba32[]? pal = palettePath != null && File.Exists(ProjectPaths.Combine(root, palettePath)) ? JascPalette.Load(ProjectPaths.Combine(root, palettePath)) : null;
            if (!png.IsIndexed) return png.Rgba;
            return OverworldSprites.Render(png, pal);
        }
        catch (Exception ex) when (ex is IOException or FormatException or InvalidDataException) { return null; }
    }

    private static RgbaImage Crop(RgbaImage src, int x, int y, int w, int h)
    {
        var img = new RgbaImage(w, h);
        img.Blit(src, x, y, 0, 0, w, h);
        return img;
    }

    /// <summary>Item → its 24×24 bag icon (iconPic/iconPalette of gItemsInfo, files from src/data/graphics/items.h).</summary>
    private sealed class ItemIcons
    {
        private readonly string _root;
        private readonly Dictionary<string, (string Pic, string Pal)> _symbols = new(StringComparer.Ordinal);
        private readonly Dictionary<string, GraphicsInclude> _files;

        private ItemIcons(string root, string itemsText, string graphicsText)
        {
            _root = root;
            foreach (var e in CTable.Parse(itemsText, "gItemsInfo", "ITEM_").Entries)
                if (e.Field("iconPic")?.Raw is { } pic && e.Field("iconPalette")?.Raw is { } pal) _symbols.TryAdd(e.Key, (pic, pal));
            _files = GraphicsIncludes.BySymbol(graphicsText);
        }

        public static ItemIcons Load(string root)
        {
            string Read(string rel) => File.Exists(ProjectPaths.Combine(root, rel)) ? File.ReadAllText(ProjectPaths.Combine(root, rel)) : "";
            return new ItemIcons(root, Read("src/data/items.h"), Read("src/data/graphics/items.h"));
        }

        public RgbaImage? Icon(string item)
        {
            // TMs/HMs get their disc icon at run time (colored by the move's type): the normal-type disc stands for them.
            if (!_symbols.TryGetValue(item, out var s) && Regex.IsMatch(item, @"^ITEM_(TM|HM)")) s = ("gItemIcon_TM", "gItemIconPalette_NormalTMHM");
            if (s.Pic == null || !_files.TryGetValue(s.Pic, out var pic)) return null;
            string? pal = _files.TryGetValue(s.Pal, out var p) && p.SourcePath.EndsWith(".pal", StringComparison.Ordinal) ? p.SourcePath : null;
            return LoadIndexed(_root, pic.SourcePath, pal);
        }
    }
}
