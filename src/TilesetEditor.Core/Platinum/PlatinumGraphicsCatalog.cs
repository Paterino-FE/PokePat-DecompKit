using TilesetEditor.Core.Gba;
using TilesetEditor.Core.Imaging;
using TilesetEditor.Core.Project;

namespace TilesetEditor.Core.Platinum;

/// <summary>A group of pictures the player sees together (one screen or one kind of graphic).</summary>
public sealed record GraphicsCategory(string Id, string Title, string Description, string[] Folders);

/// <summary>One battle field: the platforms of player and opponent, with a palette per time of day.</summary>
public sealed record BattleTerrain(string Name, string? PlayerPng, string? EnemyPng, IReadOnlyList<string> Palettes);

/// <summary>The pictures of res/graphics sorted the way a hacker looks for them, with what each group is for.</summary>
public static class PlatinumGraphicsCatalog
{
    public static readonly GraphicsCategory[] Categories =
    [
        new("terrain", "Battle fields (platforms)", "The ground the Pokémon stand on in battle, for every terrain (grass, cave, water, snow, the Elite Four rooms, Battle Frontier …). " +
            "Outdoor fields have a palette for day, evening and night; all palettes use the same picture. The large battle backgrounds behind them are still a prebuilt archive in pokeplatinum (res/prebuilt/battle/graphic/batt_bg.narc) and cannot be edited as pictures yet.",
            ["res/graphics/battle/terrain"]),
        new("trainer_backs", "Trainer back sprites", "Back sprites of trainers who fight next to the player (multi battles, Battle Frontier partners, Diamond/Pearl player outfits).", ["res/graphics/battle/trainer_backs"]),
        new("battle_ui", "Battle interface", "Cursor, level-up window, party balls and other parts of the battle screen.", ["res/graphics/battle/interface", "res/graphics/battle/misc"]),
        new("type_icons", "Type & category icons", "The type and contest category icons shown in battle, summary and Pokédex.", ["res/graphics/battle/type_icons"]),
        new("ball_throws", "Poké Ball throws", "The Poké Balls flying and opening in battle.", ["res/graphics/battle/ball_throws"]),
        new("title", "Title screen & main menu", "The title screen (Giratina, logo) and the main menu.", ["res/graphics/title_screen", "res/graphics/main_menu"]),
        new("windows", "Text boxes & signposts", "Message box frames the player can choose in the options, signposts and the map name popups.", ["res/graphics/windows", "res/graphics/signposts", "res/graphics/map_popups"]),
        new("menus", "Start menu, party & summary", "The start menu, the party screen and the Pokémon summary.", ["res/graphics/start_menu", "res/graphics/party_menu", "res/graphics/pokemon_summary_screen"]),
        new("bag", "Bag & shops", "The bag screen and the Poké Mart.", ["res/graphics/bag", "res/graphics/shop_menu"]),
        new("pokedex", "Pokédex", "Every screen of the Pokédex.", ["res/graphics/pokedex"]),
        new("trainer_case", "Trainer card & badges", "The trainer card, its trainer pictures and the badge case.", ["res/graphics/trainer_case"]),
        new("town_map", "Town map", "The town map with its icons.", ["res/graphics/town_map"]),
        new("poketch", "Pokétch", "All Pokétch apps on the bottom screen.", ["res/graphics/poketch"]),
        new("options", "Options, naming, mail", "Options menu, naming screen, mail and signature screens.", ["res/graphics/options_menu", "res/graphics/naming_screen", "res/graphics/mail", "res/graphics/signature_interface"]),
        new("underground", "Underground", "The Underground: top screen, map transition and traps.", ["res/graphics/underground_top_screen", "res/graphics/underground_map_transition", "res/graphics/trap_effects", "res/graphics/mining"]),
        new("frontier", "Battle Frontier", "Screens of the Battle Frontier facilities.", ["res/graphics/frontier"]),
        new("contests", "Contests, Poffins & accessories", "Poffin cooking and case, berry tags, the fashion case.", ["res/graphics/poffin_cooking", "res/graphics/poffin_case", "res/graphics/berry_tag", "res/graphics/fashion_case"]),
        new("events", "Egg hatching, credits & more", "Egg hatching, end credits, diploma, move tutor, scroll prompts, the Wi-Fi icon.", ["res/graphics/egg_hatch", "res/graphics/end_credits", "res/graphics/diploma", "res/graphics/move_tutor", "res/graphics/scroll_prompts", "res/graphics/network_icon", "res/graphics/yes_no_touch_menu"]),
        new("other", "Other screens", "Everything else: Underground, contests, Poffin, mining, frontier, end credits …", []),
    ];

    /// <summary>Pictures of a category; "other" gets what no other category has.</summary>
    public static List<string> Pictures(string root, GraphicsCategory c, IReadOnlyList<string>? all = null)
    {
        all ??= PlatinumGraphics.List(root);
        if (c.Id == "other")
        {
            var taken = Categories.Where(x => x.Id != "other").SelectMany(x => x.Folders).Append(PlatinumOverworld.Folder).ToList();
            return all.Where(p => !taken.Any(f => p.StartsWith(f + "/", StringComparison.Ordinal))).ToList();
        }
        return all.Where(p => c.Folders.Any(f => p.StartsWith(f + "/", StringComparison.Ordinal))).ToList();
    }

    public static List<BattleTerrain> Terrains(string root)
    {
        var dir = ProjectPaths.Combine(root, "res/graphics/battle/terrain");
        if (!Directory.Exists(dir)) return [];
        string? Existing(string rel) => File.Exists(ProjectPaths.Combine(root, rel)) ? rel : null;
        string[] order = ["day", "evening", "night", "all"];
        return Directory.GetDirectories(dir).OrderBy(d => d, StringComparer.Ordinal).Select(d =>
        {
            var name = Path.GetFileName(d);
            var rel = $"res/graphics/battle/terrain/{name}";
            var pals = Directory.GetFiles(d, "*.pal").Select(Path.GetFileNameWithoutExtension)
                .OrderBy(p => Array.IndexOf(order, p) is var i && i < 0 ? 99 : i).Select(p => $"{rel}/{p}.pal").ToList();
            return new BattleTerrain(name, Existing($"{rel}/player.png"), Existing($"{rel}/enemy.png"), pals);
        }).Where(t => t.PlayerPng != null || t.EnemyPng != null).ToList();
    }

    /// <summary>A picture drawn with another palette file of the same set (index 0 transparent).</summary>
    public static RgbaImage Render(string root, string pngRel, string? palRel)
    {
        var png = Png.Load(ProjectPaths.Combine(root, pngRel));
        var pal = palRel != null ? JascPalette.Load(ProjectPaths.Combine(root, palRel)) : png.Palette;
        var img = new RgbaImage(png.Width, png.Height);
        for (int i = 0; i < img.Pixels.Length; i++)
        {
            int idx = png.Indices?[i] ?? -1;
            img.Pixels[i] = idx < 0 ? png.Rgba.Pixels[i] : idx == 0 || pal == null || idx >= pal.Length ? Rgba32.Transparent : pal[idx] with { A = 255 };
        }
        return img;
    }
}
