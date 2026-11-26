using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using TilesetEditor.Core.Project;

namespace TilesetEditor.Core.Platinum;

/// <summary>One Pokémon of a trainer's party.</summary>
public sealed class PlatinumTrainerMon
{
    public string Species { get; set; } = "SPECIES_NONE";
    public int Form { get; set; }
    public int Level { get; set; } = 5;
    /// <summary>ITEM_… or null (no item).</summary>
    public string? Item { get; set; }
    /// <summary>Up to four MOVE_…; null = the moves it knows at its level.</summary>
    public List<string>? Moves { get; set; }
    /// <summary>0–255: the IVs of the party (higher = stronger).</summary>
    public int IvScale { get; set; }
    public int BallSeal { get; set; }

    public PlatinumTrainerMon Clone() => new()
    {
        Species = Species, Form = Form, Level = Level, Item = Item, Moves = Moves?.ToList(), IvScale = IvScale, BallSeal = BallSeal,
    };
}

/// <summary>A trainer of res/trainers/data/*.json.</summary>
public sealed class PlatinumTrainer
{
    public required string File { get; init; }
    public string Id => Path.GetFileNameWithoutExtension(File);
    public string Name { get; set; } = "";
    public string Class { get; set; } = "";
    public bool DoubleBattle { get; set; }
    public List<string> Items { get; set; } = [];
    public List<string> AiFlags { get; set; } = [];
    public List<PlatinumTrainerMon> Party { get; set; } = [];
    /// <summary>English messages by type (TRMSG_PRE_BATTLE …), real line breaks.</summary>
    public List<(string Type, string Text)> Messages { get; set; } = [];
    /// <summary>The file as read: unknown keys and other languages are kept when saving.</summary>
    public required JsonObject Raw { get; init; }
    /// <summary>The text that was read (precondition for writing).</summary>
    public required string OriginalText { get; init; }

    public override string ToString() => $"{Name} ({Class})";
}

/// <summary>
/// Reads and writes pokeplatinum trainers. The files are exactly what Python's json.dumps(indent=4, ensure_ascii=False)
/// writes, so a trainer is rewritten with <see cref="JsonText.Dump"/> and only edited values change.
/// </summary>
public static class PlatinumTrainers
{
    public const int MaxParty = 6;

    public static List<PlatinumTrainer> LoadAll(string root)
    {
        var dir = ProjectPaths.Combine(root, PlatinumProject.TrainersDir);
        if (!Directory.Exists(dir)) return [];
        // ~900 small files: read in parallel (each read is a round trip over a WSL share), kept in name order.
        var paths = Directory.GetFiles(dir, "*.json").OrderBy(p => p, StringComparer.Ordinal).ToArray();
        var list = new PlatinumTrainer[paths.Length];
        ParallelIo.For(paths.Length, k => list[k] = Parse(PlatinumProject.TrainersDir + "/" + Path.GetFileName(paths[k]), File.ReadAllText(paths[k], Encoding.UTF8)));
        return list.ToList();
    }

    public static PlatinumTrainer Parse(string file, string text)
    {
        var raw = JsonText.Parse(text) as JsonObject ?? throw new FormatException($"{file} is not a JSON object.");
        static string? Str(JsonNode? n) => n is JsonValue v
            ? v.TryGetValue<JsonElement>(out var e) ? (e.ValueKind == JsonValueKind.String ? e.GetString() : null) : v.TryGetValue<string>(out var s) ? s : null
            : null;
        static int Int(JsonNode? n) => n is JsonValue v && v.TryGetValue<JsonElement>(out var e) && e.ValueKind == JsonValueKind.Number && e.TryGetInt32(out var i) ? i : 0;
        static List<string> Strings(JsonNode? n) => n is JsonArray a ? a.Select(x => Str(x) ?? "").ToList() : [];

        var t = new PlatinumTrainer { File = file, Raw = raw, OriginalText = text };
        t.Name = Str(raw["name"]) ?? "";
        t.Class = Str(raw["class"]) ?? "";
        t.DoubleBattle = raw["double_battle"] is JsonValue dv && dv.TryGetValue<JsonElement>(out var de) && de.ValueKind == JsonValueKind.True;
        t.Items = Strings(raw["items"]);
        t.AiFlags = Strings(raw["ai_flags"]);
        if (raw["party"] is JsonArray party)
            foreach (var m in party.OfType<JsonObject>())
                t.Party.Add(new PlatinumTrainerMon
                {
                    Species = Str(m["species"]) ?? "SPECIES_NONE",
                    Form = Int(m["form"]),
                    Level = Int(m["level"]),
                    Item = Str(m["item"]),
                    Moves = m["moves"] is JsonArray mv ? Strings(mv) : null,
                    IvScale = Int(m["iv_scale"]),
                    BallSeal = Int(m["ball_seal"]),
                });
        if (raw["messages"] is JsonArray msgs)
            foreach (var m in msgs.OfType<JsonObject>())
            {
                var en = m["en_US"];
                var textValue = en is JsonArray lines ? string.Concat(lines.Select(Str)) : Str(en) ?? "";
                t.Messages.Add((Str(m["type"]) ?? "", textValue));
            }
        return t;
    }

    /// <summary>The trainer as file text: the read file with the edited values put in.</summary>
    public static string Serialize(PlatinumTrainer t)
    {
        var raw = (JsonObject)JsonText.Parse(t.Raw.ToJsonString())!; // detached copy; keeps unknown keys and order
        raw["name"] = t.Name;
        raw["class"] = t.Class;
        raw["items"] = new JsonArray(t.Items.Select(i => (JsonNode?)JsonValue.Create(i)).ToArray());
        raw["ai_flags"] = new JsonArray(t.AiFlags.Select(i => (JsonNode?)JsonValue.Create(i)).ToArray());
        raw["double_battle"] = t.DoubleBattle;
        var oldParty = raw["party"] as JsonArray;
        var party = new JsonArray();
        for (int i = 0; i < t.Party.Count; i++)
        {
            var m = t.Party[i];
            // Start from the old entry of the same slot so keys this tool does not know stay.
            var o = oldParty != null && i < oldParty.Count && oldParty[i] is JsonObject old ? (JsonObject)JsonText.Parse(old.ToJsonString())! : new JsonObject();
            o["species"] = m.Species;
            o["form"] = m.Form;
            o["level"] = m.Level;
            o["item"] = m.Item == null ? null : JsonValue.Create(m.Item);
            o["moves"] = m.Moves == null ? null : new JsonArray(m.Moves.Select(x => (JsonNode?)JsonValue.Create(x)).ToArray());
            o["iv_scale"] = m.IvScale;
            o["ball_seal"] = m.BallSeal;
            party.Add(Order(o, ["species", "form", "level", "item", "moves", "iv_scale", "ball_seal"]));
        }
        raw["party"] = party;
        if (raw["messages"] is JsonArray msgs)
        {
            foreach (var m in msgs.OfType<JsonObject>())
            {
                var type = m["type"]?.GetValue<string>();
                var edited = t.Messages.FirstOrDefault(x => x.Type == type);
                if (edited.Type == null) continue;
                var old = m["en_US"];
                var oldText = old is JsonArray a ? string.Concat(a.Select(x => x?.GetValue<string>())) : old?.GetValue<string>() ?? "";
                if (oldText == edited.Text) continue;
                m["en_US"] = old is JsonArray
                    ? new JsonArray(JsonText.SplitLines(edited.Text).Select(l => (JsonNode?)JsonValue.Create(l)).ToArray())
                    : JsonValue.Create(edited.Text);
            }
        }
        var text = JsonText.Dump(raw, 4);
        return t.OriginalText.EndsWith('\n') ? text + "\n" : text;
    }

    /// <summary>A copy of the object with the given keys first (in that order), then the others.</summary>
    private static JsonObject Order(JsonObject o, string[] keys)
    {
        var result = new JsonObject();
        foreach (var k in keys)
            if (o.ContainsKey(k)) { var v = o[k]; o.Remove(k); result[k] = v; }
        foreach (var (k, v) in o.ToList()) { o.Remove(k); result[k] = v; }
        return result;
    }

    /// <summary>TRAINER_CLASS_ACE_TRAINER_MALE → res/trainers/classes/ace_trainer_male/front.png (null if missing).</summary>
    public static string? ClassPicture(string root, string trainerClass)
    {
        var name = (trainerClass.StartsWith("TRAINER_CLASS_", StringComparison.Ordinal) ? trainerClass[14..] : trainerClass).ToLowerInvariant();
        var path = ProjectPaths.Combine(root, $"{PlatinumProject.TrainerClassesDir}/{name}/front.png");
        return File.Exists(path) ? path : null;
    }

    /// <summary>Problems that make the trainer unusable in-game (blocks saving).</summary>
    public static IEnumerable<string> Check(PlatinumTrainer t, PlatinumProject p)
    {
        if (t.Name.Trim().Length == 0) yield return "The trainer needs a name.";
        if (t.Party.Count == 0) yield return "The party is empty.";
        if (t.Party.Count > MaxParty) yield return $"At most {MaxParty} Pokémon.";
        if (t.DoubleBattle && t.Party.Count < 2) yield return "A double battle needs at least 2 Pokémon.";
        if (p.TrainerClasses.Count > 0 && !p.TrainerClasses.Contains(t.Class)) yield return $"{t.Class} is not a trainer class.";
        if (t.Items.Count > 4) yield return "At most 4 items.";
        for (int i = 0; i < t.Party.Count; i++)
        {
            var m = t.Party[i];
            var where = $"Pokémon {i + 1}";
            if (p.Species.Count > 0 && !p.Species.Contains(m.Species)) yield return $"{where}: {m.Species} is not a species.";
            if (m.Level is < 1 or > 100) yield return $"{where}: level must be 1–100.";
            if (m.IvScale is < 0 or > 255) yield return $"{where}: IV scale must be 0–255.";
            if (m.Item != null && p.Items.Count > 0 && !p.Items.Contains(m.Item)) yield return $"{where}: {m.Item} is not an item.";
            if (m.Moves != null)
            {
                if (m.Moves.Count > 4) yield return $"{where}: at most 4 moves.";
                foreach (var mv in m.Moves)
                    if (p.Moves.Count > 0 && !p.Moves.Contains(mv)) yield return $"{where}: {mv} is not a move.";
            }
        }
    }
}
