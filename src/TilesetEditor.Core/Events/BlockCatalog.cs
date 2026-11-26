namespace TilesetEditor.Core.Events;

/// <summary>What kind of input a block parameter takes (decides the picker in the UI and the checks).</summary>
public enum ParamKind
{
    Text,
    /// <summary>In-game text, laid out like a message (preview, charmap check).</summary>
    Message,
    Number,
    Flag,
    Var,
    Item,
    Species,
    Trainer,
    Map,
    Music,
    Sound,
    /// <summary>Movement list like "walk_up*2, face_down".</summary>
    Movement,
    /// <summary>Object on the map: OBJ_EVENT_ID_PLAYER, a local id number or a LOCALID_ constant.</summary>
    Target,
    Weather,
    Special,
    Choice,
    /// <summary>Script command name from asm/macros/event.inc (generic block).</summary>
    Macro,
    /// <summary>Free assembly argument (label, constant, expression).</summary>
    Free,
    /// <summary>PORTRAIT_ constant of the "Dialog portraits" mod.</summary>
    Portrait,
    /// <summary>PICTURE_ constant of the "Dialog portraits" mod (1.1+).</summary>
    Picture,
}

public sealed record BlockParam(string Key, string Label, ParamKind Kind, string Default = "", bool Optional = false,
    IReadOnlyList<string>? Choices = null, string Help = "");

/// <summary>One thing the preview does for a block: show a text, or write a line to the log.</summary>
public sealed record PreviewAction(string? Text = null, string? Log = null);

/// <summary>A building block of the event builder.</summary>
public sealed class BlockDef
{
    public required string Id { get; init; }
    public required string Category { get; init; }
    public required string Icon { get; init; }
    public required string Title { get; init; }
    public string Help { get; init; } = "";
    public IReadOnlyList<BlockParam> Params { get; init; } = [];
    /// <summary>Poryscript lines (without indentation). For branch blocks: the lines before the if.</summary>
    public required Func<BlockArgs, IEnumerable<string>> Lines { get; init; }
    /// <summary>Branch blocks: Poryscript condition and its negation.</summary>
    public Func<BlockArgs, (string Condition, string Negated)>? Condition { get; init; }
    public string ThenCaption { get; init; } = "Then";
    public string ElseCaption { get; init; } = "Else";
    public required Func<BlockArgs, string> Summary { get; init; }
    public Func<BlockArgs, IEnumerable<PreviewAction>> Preview { get; init; } = a => [];
    /// <summary>Branch blocks: the question the preview asks (it cannot know the game state).</summary>
    public Func<BlockArgs, string>? DecisionPrompt { get; init; }

    /// <summary>Menu blocks: one branch per option (switch on VAR_RESULT). Returns (case value, caption).</summary>
    public Func<BlockArgs, IReadOnlyList<(string Value, string Caption)>>? Cases { get; init; }
    /// <summary>Menu blocks: options shown by the preview, and whether B cancels (last case).</summary>
    public Func<BlockArgs, (IReadOnlyList<string> Options, bool CanCancel)>? Menu { get; init; }
    /// <summary>Text placed at top level of the script file (e.g. a mart list). The generator passes a unique "__name".</summary>
    public Func<BlockArgs, string>? TopLevel { get; init; }

    /// <summary>Id of the PokePat mod the block needs (its commands only exist after installing it), or null.</summary>
    public string? RequiresMod { get; init; }

    public bool IsBranch => Condition != null;
    public bool IsSwitch => Cases != null;
}

/// <summary>Parameter values of a block with defaults applied.</summary>
public sealed class BlockArgs(BlockDef def, IReadOnlyDictionary<string, string> values)
{
    public string this[string key]
    {
        get
        {
            if (values.TryGetValue(key, out var v) && v.Trim().Length > 0) return v.Trim();
            return def.Params.FirstOrDefault(p => p.Key == key)?.Default ?? "";
        }
    }

    public string Raw(string key) => values.TryGetValue(key, out var v) ? v : "";
    public bool Has(string key) => this[key].Length > 0;
}

/// <summary>
/// The blocks of the event builder. Each maps to commands of pokeemerald(-expansion) as written in Poryscript;
/// the parameters use the project's own constants. "Command" offers every macro of asm/macros/event.inc.
/// </summary>
public static class BlockCatalog
{
    public const string MacroBlockId = "macro";

    private static readonly string[] StrVars = ["STR_VAR_1", "STR_VAR_2", "STR_VAR_3"];
    private static readonly string[] PortraitSides = ["PORTRAIT_POS_LEFT", "PORTRAIT_POS_RIGHT", "PORTRAIT_POS_LEFT_MIRRORED", "PORTRAIT_POS_RIGHT_MIRRORED"];
    private static readonly string[] Directions = ["DIR_SOUTH", "DIR_NORTH", "DIR_WEST", "DIR_EAST"];
    private static readonly string[] Fades = ["FADE_TO_BLACK", "FADE_FROM_BLACK", "FADE_TO_WHITE", "FADE_FROM_WHITE"];
    private static readonly string[] Emotes = ["emote_exclamation_mark", "emote_question_mark", "emote_heart", "emote_x", "emote_double_exclamation_mark"];
    private static readonly string[] CompareOps = ["==", "!=", "<", "<=", ">", ">="];

    /// <summary>Menu blocks: whether B may cancel ("erlaubt" is the value stored by older versions).</summary>
    public static bool CancelAllowed(BlockArgs a) => a["cancel"] is "allowed" or "erlaubt";

    private static string Q(string text) => "\"" + text + "\""; // texts are laid out (escaped) by the generator before

    private static string Negate(string op) => op switch
    {
        "==" => "!=",
        "!=" => "==",
        "<" => ">=",
        "<=" => ">",
        ">" => "<=",
        _ => "<",
    };

    /// <summary>Renders "walk_up*2, face_down" as Poryscript moves(walk_up * 2, face_down).</summary>
    public static string Moves(string list)
    {
        var parts = ParseMovement(list).Select(m => m.Count > 1 ? $"{m.Action} * {m.Count}" : m.Action);
        return $"moves({string.Join(", ", parts)})";
    }

    /// <summary>"who: steps" per line (Move together block).</summary>
    public static List<(string Target, string Moves)> Group(string text) =>
        text.Replace("\r", "").Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0)
            .Select(l => { int c = l.IndexOf(':'); return c > 0 ? (l[..c].Trim(), l[(c + 1)..].Trim()) : ("", l); }).ToList();

    public static List<(string Action, int Count)> ParseMovement(string list)
    {
        var result = new List<(string, int)>();
        foreach (var raw in list.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var p = raw.Split('*', StringSplitOptions.TrimEntries);
            int n = p.Length > 1 && int.TryParse(p[1], out var c) ? c : 1;
            if (p[0].Length > 0) result.Add((p[0], n));
        }
        return result;
    }

    /// <summary>Menu options: one per line (empty lines ignored), at most 6.</summary>
    public static List<string> Options(string text) =>
        text.Replace("\r", "").Split('\n').Select(o => o.Trim()).Where(o => o.Length > 0).Take(6).ToList();

    public static List<string> ItemList(string text) =>
        text.Split(['\n', ',', ' ', '\r'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

    public static readonly IReadOnlyList<BlockDef> All = Build();

    public static BlockDef? Find(string id) => All.FirstOrDefault(b => b.Id == id);

    private static List<BlockDef> Build()
    {
        BlockParam Msg(string key, string label, string def) => new(key, label, ParamKind.Message, def);
        BlockParam Target(string key = "target", string label = "Who") => new(key, label, ParamKind.Target, "OBJ_EVENT_ID_PLAYER",
            Help: "OBJ_EVENT_ID_PLAYER = the player; otherwise the number (local ID) or LOCALID_… of the person on the map.");
        var list = new List<BlockDef>
        {
            // ---------- Dialog
            new()
            {
                Id = "buffer_species", Category = "Dialog", Icon = "🔤", Title = "Pokémon name in text",
                Help = "Writes a Pokémon's name into a placeholder such as {STR_VAR_1} that the next message can use.",
                Params = [new("slot", "Placeholder", ParamKind.Choice, "STR_VAR_1", Choices: StrVars), new("species", "Pokémon", ParamKind.Species, "SPECIES_PIKACHU")],
                Lines = a => [$"bufferspeciesname({a["slot"]}, {a["species"]})"],
                Summary = a => $"{{{a["slot"]}}} = {a["species"]}",
                Preview = a => [new(Log: $"{{{a["slot"]}}} = {a["species"]}")],
            },
            new()
            {
                Id = "buffer_item", Category = "Dialog", Icon = "🔤", Title = "Item name in text",
                Help = "Writes an item's name into a placeholder such as {STR_VAR_1}.",
                Params = [new("slot", "Placeholder", ParamKind.Choice, "STR_VAR_1", Choices: StrVars), new("item", "Item", ParamKind.Item, "ITEM_POTION")],
                Lines = a => [$"bufferitemname({a["slot"]}, {a["item"]})"],
                Summary = a => $"{{{a["slot"]}}} = {a["item"]}",
                Preview = a => [new(Log: $"{{{a["slot"]}}} = {a["item"]}")],
            },
            new()
            {
                Id = "menu", Category = "Dialog", Icon = "📋", Title = "Choice menu",
                Help = "Shows a question and a menu with 2–6 answers (one per line). Each answer gets its own branch.",
                Params = [new("question", "Question", ParamKind.Message, "What would you like to do?"),
                    new("options", "Answers (one per line)", ParamKind.Text, "Buy\nSell\nNothing"),
                    new("cancel", "Cancel with B", ParamKind.Choice, "allowed", Choices: ["allowed", "not allowed"]),
                    new("x", "Position X (Tiles)", ParamKind.Number, "0"), new("y", "Position Y (Tiles)", ParamKind.Number, "0")],
                Lines = a =>
                {
                    var opts = string.Join(", ", Options(a.Raw("__options_encoded").Length > 0 ? a.Raw("__options_encoded") : a["options"]).Select(Q));
                    return [$"message({Q(a["question"])})", "waitmessage",
                        $"dynmultichoice({a["x"]}, {a["y"]}, {(CancelAllowed(a) ? "FALSE" : "TRUE")}, 6, 0, DYN_MULTICHOICE_CB_NONE, {opts})"];
                },
                Cases = a =>
                {
                    var list = Options(a["options"]).Select((o, i) => (i.ToString(), $"If \"{o}\"")).ToList();
                    if (CancelAllowed(a)) list.Add(("MULTI_B_PRESSED", "If cancelled (B)"));
                    return list;
                },
                Menu = a => (Options(a["options"]), CancelAllowed(a)),
                Summary = a => string.Join(" / ", Options(a["options"])),
            },
            new()
            {
                Id = "mart", Category = "Items & Money", Icon = "🏪", Title = "Shop (Pokémart)",
                Help = "Opens a shop with the given items (separated by commas or line breaks). Prices come from src/data/items.h.",
                Params = [new("items", "Items for sale", ParamKind.Text, "ITEM_POTION, ITEM_ANTIDOTE, ITEM_POKE_BALL")],
                Lines = a => [$"pokemart({a["__name"]})"],
                TopLevel = a => $"mart {a["__name"]} {{\n" + string.Concat(ItemList(a["items"]).Select(i => $"    {i}\n")) + "}\n",
                Summary = a => string.Join(", ", ItemList(a["items"]).Select(i => i.Replace("ITEM_", ""))),
                Preview = a => [new(Log: $"Shop opens: {string.Join(", ", ItemList(a["items"]))}")],
            },
            new()
            {
                Id = "note", Category = "Dialog", Icon = "📝", Title = "Note (no code)",
                Help = "A note for you and your team. Appears as a comment in the script; nothing happens in-game.",
                Params = [new("text", "Note", ParamKind.Text, "TODO")],
                Lines = a => [$"// {a["text"].Replace("\n", " ")}"],
                Summary = a => a["text"],
            },

            // ---------- Battle
            new()
            {
                Id = "trainer_single", Category = "Battle", Icon = "⚔", Title = "Trainer battle",
                Help = "Starts a battle against a trainer from src/data/trainers. After a win the trainer is remembered as defeated.",
                Params = [new("trainer", "Trainer", ParamKind.Trainer, "TRAINER_CALVIN_1"),
                    Msg("intro", "Text before the battle", "You there! Let's battle!"), Msg("lose", "Text when the trainer loses", "I lost...")],
                Lines = a => [$"trainerbattle_single({a["trainer"]}, {Q(a["intro"])}, {Q(a["lose"])})"],
                Summary = a => a["trainer"],
                Preview = a => [new(Text: a["intro"]), new(Log: $"Battle against {a["trainer"]} (skipped in preview)"), new(Text: a["lose"])],
            },
            new()
            {
                Id = "trainer_double", Category = "Battle", Icon = "⚔", Title = "Double battle (trainer)",
                Params = [new("trainer", "Trainer", ParamKind.Trainer, "TRAINER_CALVIN_1"),
                    Msg("intro", "Text before the battle", "We'll battle you together!"), Msg("lose", "Text on defeat", "We lost..."),
                    Msg("notenough", "Text if the player has only 1 Pokémon", "You need two Pokémon for this.")],
                Lines = a => [$"trainerbattle_double({a["trainer"]}, {Q(a["intro"])}, {Q(a["lose"])}, {Q(a["notenough"])})"],
                Summary = a => a["trainer"],
                Preview = a => [new(Text: a["intro"]), new(Log: $"Double battle against {a["trainer"]} (skipped)"), new(Text: a["lose"])],
            },
            new()
            {
                Id = "wild_battle", Category = "Battle", Icon = "🌿", Title = "Wild battle",
                Help = "Immediately starts a battle against a wild Pokémon.",
                Params = [new("species", "Pokémon", ParamKind.Species, "SPECIES_ZIGZAGOON"), new("level", "Level", ParamKind.Number, "5")],
                Lines = a => [$"setwildbattle({a["species"]}, {a["level"]})", "dowildbattle"],
                Summary = a => $"{a["species"]} Lv. {a["level"]}",
                Preview = a => [new(Log: $"Wild battle: {a["species"]} Lv. {a["level"]} (skipped)")],
            },
            new()
            {
                Id = "if_defeated", Category = "Battle", Icon = "🔀", Title = "If trainer defeated",
                Params = [new("trainer", "Trainer", ParamKind.Trainer, "TRAINER_CALVIN_1")],
                Lines = _ => [],
                Condition = a => ($"defeated({a["trainer"]})", $"!defeated({a["trainer"]})"),
                ThenCaption = "Then (already defeated)", ElseCaption = "Else (not defeated yet)",
                Summary = a => a["trainer"],
                DecisionPrompt = a => $"Has {a["trainer"]} already been defeated?",
            },

            // ---------- Pokémon
            new()
            {
                Id = "give_mon", Category = "Pokémon", Icon = "🐣", Title = "Give Pokémon",
                Help = "Gives the player a Pokémon (to the party or the PC). Tip: add a fanfare and a message before it.",
                Params = [new("species", "Pokémon", ParamKind.Species, "SPECIES_PIKACHU"), new("level", "Level", ParamKind.Number, "5"),
                    new("item", "Held item (optional)", ParamKind.Item, "", Optional: true)],
                Lines = a => [a.Has("item") ? $"givemon({a["species"]}, {a["level"]}, {a["item"]})" : $"givemon({a["species"]}, {a["level"]})"],
                Summary = a => $"{a["species"]} Lv. {a["level"]}",
                Preview = a => [new(Log: $"Pokémon received: {a["species"]} Lv. {a["level"]}")],
            },
            new()
            {
                Id = "if_party_full", Category = "Pokémon", Icon = "🔀", Title = "If party full",
                Lines = _ => ["getpartysize"],
                Condition = _ => ("var(VAR_RESULT) == PARTY_SIZE", "var(VAR_RESULT) != PARTY_SIZE"),
                ThenCaption = "Then (6 Pokémon in party)", ElseCaption = "Else (room left)",
                Summary = _ => "6 Pokémon in party?",
                DecisionPrompt = _ => "Is the party full (6 Pokémon)?",
            },
            new()
            {
                Id = "heal", Category = "Pokémon", Icon = "💖", Title = "Heal party",
                Lines = _ => ["special(HealPlayerParty)"],
                Summary = _ => "all Pokémon fully healed",
                Preview = _ => [new(Log: "Party healed")],
            },
            new()
            {
                Id = "cry", Category = "Pokémon", Icon = "🔊", Title = "Pokémon cry",
                Params = [new("species", "Pokémon", ParamKind.Species, "SPECIES_PIKACHU")],
                Lines = a => [$"playmoncry({a["species"]}, CRY_MODE_NORMAL)", "waitmoncry"],
                Summary = a => a["species"],
                Preview = a => [new(Log: $"Cry of {a["species"]}")],
            },

            // ---------- Items & Money
            new()
            {
                Id = "remove_item", Category = "Items & Money", Icon = "➖", Title = "Take item",
                Params = [new("item", "Item", ParamKind.Item, "ITEM_POTION"), new("count", "Count", ParamKind.Number, "1")],
                Lines = a => [$"removeitem({a["item"]}, {a["count"]})"],
                Summary = a => $"{a["item"]} × {a["count"]}",
                Preview = a => [new(Log: $"Item taken: {a["item"]} × {a["count"]}")],
            },
            new()
            {
                Id = "if_item", Category = "Items & Money", Icon = "🔀", Title = "If item in bag",
                Params = [new("item", "Item", ParamKind.Item, "ITEM_POTION"), new("count", "At least", ParamKind.Number, "1")],
                Lines = a => [$"checkitem({a["item"]}, {a["count"]})"],
                Condition = _ => ("var(VAR_RESULT) == TRUE", "var(VAR_RESULT) == FALSE"),
                ThenCaption = "Then (has the item)", ElseCaption = "Else (does not have it)",
                Summary = a => $"{a["item"]} × {a["count"]}",
                DecisionPrompt = a => $"Does the player have at least {a["count"]} × {a["item"]}?",
            },
            new()
            {
                Id = "add_money", Category = "Items & Money", Icon = "💰", Title = "Give money",
                Params = [new("amount", "Amount", ParamKind.Number, "500")],
                Lines = a => [$"addmoney({a["amount"]})"],
                Summary = a => $"+{a["amount"]} ₽",
                Preview = a => [new(Log: $"+{a["amount"]} money")],
            },
            new()
            {
                Id = "remove_money", Category = "Items & Money", Icon = "💸", Title = "Take money",
                Params = [new("amount", "Amount", ParamKind.Number, "500")],
                Lines = a => [$"removemoney({a["amount"]})"],
                Summary = a => $"−{a["amount"]} ₽",
                Preview = a => [new(Log: $"−{a["amount"]} money")],
            },
            new()
            {
                Id = "if_money", Category = "Items & Money", Icon = "🔀", Title = "If enough money",
                Params = [new("amount", "Amount", ParamKind.Number, "500")],
                Lines = a => [$"checkmoney({a["amount"]})"],
                Condition = _ => ("var(VAR_RESULT) == TRUE", "var(VAR_RESULT) == FALSE"),
                ThenCaption = "Then (enough money)", ElseCaption = "Else (not enough)",
                Summary = a => $"≥ {a["amount"]} ₽",
                DecisionPrompt = a => $"Does the player have at least {a["amount"]} money?",
            },

            // ---------- Flags & Variables
            new()
            {
                Id = "set_var", Category = "Flags & Variables", Icon = "🔢", Title = "Set variable",
                Help = "Variables store numbers in the save file, e.g. the progress of a quest (0, 1, 2, …).",
                Params = [new("var", "Variable", ParamKind.Var, "VAR_TEMP_1"), new("value", "Value", ParamKind.Number, "1")],
                Lines = a => [$"setvar({a["var"]}, {a["value"]})"],
                Summary = a => $"{a["var"]} = {a["value"]}",
                Preview = a => [new(Log: $"{a["var"]} = {a["value"]}")],
            },
            new()
            {
                Id = "add_var", Category = "Flags & Variables", Icon = "➕", Title = "Increase variable",
                Params = [new("var", "Variable", ParamKind.Var, "VAR_TEMP_1"), new("value", "By", ParamKind.Number, "1")],
                Lines = a => [$"addvar({a["var"]}, {a["value"]})"],
                Summary = a => $"{a["var"]} += {a["value"]}",
                Preview = a => [new(Log: $"{a["var"]} += {a["value"]}")],
            },
            new()
            {
                Id = "sub_var", Category = "Flags & Variables", Icon = "➖", Title = "Decrease variable",
                Params = [new("var", "Variable", ParamKind.Var, "VAR_TEMP_1"), new("value", "By", ParamKind.Number, "1")],
                Lines = a => [$"subvar({a["var"]}, {a["value"]})"],
                Summary = a => $"{a["var"]} −= {a["value"]}",
                Preview = a => [new(Log: $"{a["var"]} −= {a["value"]}")],
            },
            new()
            {
                Id = "if_var", Category = "Flags & Variables", Icon = "🔀", Title = "If variable",
                Params = [new("var", "Variable", ParamKind.Var, "VAR_TEMP_1"), new("op", "Comparison", ParamKind.Choice, "==", Choices: CompareOps),
                    new("value", "Value", ParamKind.Number, "1")],
                Lines = _ => [],
                Condition = a => ($"var({a["var"]}) {a["op"]} {a["value"]}", $"var({a["var"]}) {Negate(a["op"])} {a["value"]}"),
                Summary = a => $"{a["var"]} {a["op"]} {a["value"]}",
                DecisionPrompt = a => $"Is {a["var"]} {a["op"]} {a["value"]}?",
            },

            // ---------- People & Movement
            new()
            {
                Id = "move", Category = "People & Movement", Icon = "🚶", Title = "Movement",
                Help = "Makes a person or the player walk, turn, jump … Separate steps with commas, repeat with *2 etc.",
                Params = [Target(), new("moves", "Steps", ParamKind.Movement, "walk_up*2, face_down")],
                Lines = a => [$"applymovement({a["target"]}, {Moves(a["moves"])})", "waitmovement(0)"],
                Summary = a => $"{a["target"]}: {a["moves"]}",
                Preview = a => [new(Log: $"Movement {a["target"]}: {a["moves"]}")],
            },
            new()
            {
                Id = "move_together", Category = "People & Movement", Icon = "👥", Title = "Move together (scene)",
                Help = "Several people – and the player – move at the same time, like the Team Magma scene in Mossdeep. " +
                       "One line per person: who: steps. The event continues when everyone has finished.",
                Params = [new("group", "One line per person (who: steps)", ParamKind.Text, "OBJ_EVENT_ID_PLAYER: face_right\n1: walk_left*2, face_right",
                    Help: "who = OBJ_EVENT_ID_PLAYER, a local ID number or LOCALID_…; steps as in \"Movement\", e.g. walk_down, walk_right*3")],
                Lines = a => [.. Group(a["group"]).Select(g => $"applymovement({g.Target}, {Moves(g.Moves)})"), "waitmovement(0)"],
                Summary = a => string.Join(" | ", Group(a["group"]).Select(g => $"{g.Target}: {g.Moves}")),
                Preview = a => [.. Group(a["group"]).Select(g => new PreviewAction(Log: $"Movement {g.Target}: {g.Moves}")), new(Log: "(all at the same time)")],
            },
            new()
            {
                Id = "emote", Category = "People & Movement", Icon = "❗", Title = "Emote (!, ?, ♥)",
                Params = [Target(), new("emote", "Emote", ParamKind.Choice, "emote_exclamation_mark", Choices: Emotes)],
                Lines = a => [$"applymovement({a["target"]}, moves({a["emote"]}))", "waitmovement(0)"],
                Summary = a => $"{a["target"]}: {a["emote"]}",
                Preview = a => [new(Log: $"{a["target"]} shows {a["emote"]}")],
            },
            new()
            {
                Id = "face", Category = "People & Movement", Icon = "↻", Title = "Face direction",
                Params = [Target(), new("dir", "Direction", ParamKind.Choice, "DIR_SOUTH", Choices: Directions)],
                Lines = a => [$"turnobject({a["target"]}, {a["dir"]})"],
                Summary = a => $"{a["target"]} → {a["dir"]}",
                Preview = a => [new(Log: $"{a["target"]} turns to {a["dir"]}")],
            },
            new()
            {
                Id = "hide_object", Category = "People & Movement", Icon = "👻", Title = "Hide person",
                Help = "Removes the person. To keep them gone permanently, give the person a flag in Porymap – removeobject sets it.",
                Params = [Target("target", "Person")],
                Lines = a => [$"removeobject({a["target"]})"],
                Summary = a => a["target"],
                Preview = a => [new(Log: $"Person {a["target"]} removed")],
            },
            new()
            {
                Id = "show_object", Category = "People & Movement", Icon = "👤", Title = "Show person",
                Params = [Target("target", "Person")],
                Lines = a => [$"addobject({a["target"]})"],
                Summary = a => a["target"],
                Preview = a => [new(Log: $"Person {a["target"]} appears")],
            },

            // ---------- Map & World
            new()
            {
                Id = "warp", Category = "Map & World", Icon = "🚪", Title = "Warp (Teleport)",
                Help = "Takes the player to position X/Y on another map. The event ends afterwards.",
                Params = [new("map", "Target map", ParamKind.Map, "MAP_LITTLEROOT_TOWN"), new("x", "X", ParamKind.Number, "5"), new("y", "Y", ParamKind.Number, "5")],
                Lines = a => [$"warp({a["map"]}, {a["x"]}, {a["y"]})", "waitstate"],
                Summary = a => $"{a["map"]} ({a["x"]},{a["y"]})",
                Preview = a => [new(Log: $"Warp to {a["map"]} ({a["x"]},{a["y"]})")],
            },
            new()
            {
                Id = "weather", Category = "Map & World", Icon = "🌦", Title = "Change weather",
                Params = [new("weather", "Weather", ParamKind.Weather, "WEATHER_RAIN")],
                Lines = a => [$"setweather({a["weather"]})", "doweather"],
                Summary = a => a["weather"],
                Preview = a => [new(Log: $"Weather: {a["weather"]}")],
            },
            new()
            {
                Id = "fade", Category = "Map & World", Icon = "🌗", Title = "Fade screen",
                Params = [new("mode", "Mode", ParamKind.Choice, "FADE_TO_BLACK", Choices: Fades)],
                Lines = a => [$"fadescreen({a["mode"]})"],
                Summary = a => a["mode"],
                Preview = a => [new(Log: $"Fade: {a["mode"]}")],
            },

            // ---------- Mod: Dialog portraits
            new()
            {
                Id = "portrait_show", Category = "Dialog", Icon = "🖼", Title = "Show portrait",
                Help = "Shows a picture of the speaker above the following messages (mod \"Dialog portraits\"). It disappears when the message box closes.",
                RequiresMod = "dialog-portraits",
                Params = [new("portrait", "Portrait", ParamKind.Portrait, ""),
                          new("side", "Where", ParamKind.Choice, "PORTRAIT_POS_LEFT", Choices: PortraitSides,
                              Help: "Left or right of the message box; MIRRORED turns the picture so the person looks the other way. Needs mod version 1.1 for anything but left.")],
                Lines = a => a["side"] == "PORTRAIT_POS_LEFT"
                    ? [$"setvar(VAR_0x8004, {a["portrait"]})", "special(ShowPortrait)"]
                    : [$"setvar(VAR_0x8004, {a["portrait"]})", $"setvar(VAR_0x8005, {a["side"]})", "special(ShowPortraitAt)"],
                Summary = a => a["portrait"].Replace("PORTRAIT_", "") + (a["side"] == "PORTRAIT_POS_LEFT" ? "" : " · " + a["side"].Replace("PORTRAIT_POS_", "").Replace('_', ' ').ToLowerInvariant()),
                Preview = a => [new(Log: $"Portrait {a["portrait"]}")],
            },
            new()
            {
                Id = "portrait_hide", Category = "Dialog", Icon = "🖼", Title = "Hide portrait",
                Help = "Removes the portrait while the message box stays open (mod \"Dialog portraits\").",
                RequiresMod = "dialog-portraits",
                Lines = a => ["special(HidePortrait)"],
                Summary = a => "",
                Preview = a => [new(Log: "Portrait hidden")],
            },
            new()
            {
                Id = "picture_show", Category = "Dialog", Icon = "🗺", Title = "Show picture",
                Help = "Shows a bigger picture (up to 128×96, e.g. a map or a letter) in the middle of the screen (mod \"Dialog portraits\" 1.1). It disappears when the message box closes.",
                RequiresMod = "dialog-portraits",
                Params = [new("picture", "Picture", ParamKind.Picture, "")],
                Lines = a => [$"setvar(VAR_0x8004, {a["picture"]})", "special(ShowPicture)"],
                Summary = a => a["picture"].Replace("PICTURE_", ""),
                Preview = a => [new(Log: $"Picture {a["picture"]}")],
            },
            new()
            {
                Id = "picture_hide", Category = "Dialog", Icon = "🗺", Title = "Hide picture",
                Help = "Removes the picture while the message box stays open (mod \"Dialog portraits\" 1.1).",
                RequiresMod = "dialog-portraits",
                Lines = a => ["special(HidePicture)"],
                Summary = a => "",
                Preview = a => [new(Log: "Picture hidden")],
            },

            // ---------- Sound & Timing
            new()
            {
                Id = "sound", Category = "Sound & Timing", Icon = "🔔", Title = "Sound effect",
                Params = [new("sound", "Sound", ParamKind.Sound, "SE_SELECT")],
                Lines = a => [$"playse({a["sound"]})", "waitse"],
                Summary = a => a["sound"],
                Preview = a => [new(Log: $"Sound {a["sound"]}")],
            },
            new()
            {
                Id = "fanfare", Category = "Sound & Timing", Icon = "🎺", Title = "Fanfare",
                Params = [new("music", "Fanfare", ParamKind.Music, "MUS_OBTAIN_ITEM")],
                Lines = a => [$"playfanfare({a["music"]})", "waitfanfare"],
                Summary = a => a["music"],
                Preview = a => [new(Log: $"Fanfare {a["music"]}")],
            },
            new()
            {
                Id = "music", Category = "Sound & Timing", Icon = "🎵", Title = "Change music",
                Params = [new("music", "Music", ParamKind.Music, "MUS_ROUTE101")],
                Lines = a => [$"playbgm({a["music"]}, FALSE)"],
                Summary = a => a["music"],
                Preview = a => [new(Log: $"Music {a["music"]}")],
            },
            new()
            {
                Id = "music_default", Category = "Sound & Timing", Icon = "🎵", Title = "Restore map music",
                Lines = _ => ["fadedefaultbgm"],
                Summary = _ => "map music",
                Preview = _ => [new(Log: "Map music")],
            },
            new()
            {
                Id = "delay", Category = "Sound & Timing", Icon = "⏱", Title = "Wait",
                Help = "Waits the given number of frames (60 frames = 1 second).",
                Params = [new("frames", "Frames", ParamKind.Number, "30")],
                Lines = a => [$"delay({a["frames"]})"],
                Summary = a => $"{a["frames"]} frames",
                Preview = a => [new(Log: $"Wait {a["frames"]} frames")],
            },

            // ---------- Advanced
            new()
            {
                Id = "special", Category = "Advanced", Icon = "⚙", Title = "Special function",
                Help = "Calls a function from data/specials.inc (e.g. HealPlayerParty).",
                Params = [new("name", "Special", ParamKind.Special, "HealPlayerParty")],
                Lines = a => [$"special({a["name"]})"],
                Summary = a => a["name"],
                Preview = a => [new(Log: $"special {a["name"]}")],
            },
            new()
            {
                Id = "call", Category = "Advanced", Icon = "↪", Title = "Call another script",
                Params = [new("label", "Script name", ParamKind.Free, "Common_EventScript_SaveGame")],
                Lines = a => [$"call({a["label"]})"],
                Summary = a => a["label"],
                Preview = a => [new(Log: $"call {a["label"]}")],
            },
            new()
            {
                Id = MacroBlockId, Category = "Advanced", Icon = "🧩", Title = "Command (all script commands)",
                Help = "Any command from asm/macros/event.inc. The fields match its parameters.",
                Params = [new("command", "Command", ParamKind.Macro, "nop")],
                Lines = a => [MacroLine(a)],
                Summary = a => MacroLine(a),
                Preview = a => [new(Log: MacroLine(a))],
            },
        };
        return list;
    }

    /// <summary>Generic command: "name" or "name(a, b)"; arguments are stored as arg0, arg1, …; trailing empty ones are dropped.</summary>
    public static string MacroLine(BlockArgs a)
    {
        var args = new List<string>();
        for (int i = 0; i < 40; i++) args.Add(a.Raw($"arg{i}").Trim());
        while (args.Count > 0 && args[^1].Length == 0) args.RemoveAt(args.Count - 1);
        return args.Count == 0 ? a["command"] : $"{a["command"]}({string.Join(", ", args)})";
    }

    public static IEnumerable<string> Categories => All.Select(b => b.Category).Distinct();
}
