using System.Text.Json.Nodes;
using TilesetEditor.Core.Project;

namespace TilesetEditor.Core.Platinum;

/// <summary>One kind of encounter table of a map (grass, surfing, a rod) with the game's fixed chance per slot.</summary>
public sealed record EncounterTable(string Key, string RateKey, string Title, int[] Chances, bool HasLevelRange);

/// <summary>Slots that replace grass slots at certain times (time of day, swarm, Poké Radar, GBA cartridge).</summary>
public sealed record EncounterReplacement(string Key, string Title, int[] ReplacesSlots, string Explanation);

/// <summary>
/// Wild Pokémon of a Platinum map: res/field/maps/&lt;map&gt;/encounters.json (newer pokeplatinum) or
/// res/field/encounters/&lt;name&gt;.json (older; the map header names it). Grass has 12 slots, water and the rods 5 – each
/// slot with a fixed chance (Gen 4). Some lists replace grass slots: morning/day and night (slots 3–4), swarms (1–2),
/// the Poké Radar (5, 6, 11, 12) and a GBA game in the DS slot (9–10).
/// </summary>
public static class PlatinumEncounters
{
    public static readonly EncounterTable Land = new("land_encounters", "land_rate", "Grass & caves", [20, 20, 10, 10, 10, 10, 5, 5, 4, 4, 1, 1], false);

    public static readonly EncounterTable[] Water =
    [
        new("surf_encounters", "surf_rate", "Surfing", [60, 30, 5, 4, 1], true),
        new("old_rod_encounters", "old_rod_rate", "Old Rod", [60, 30, 5, 4, 1], true),
        new("good_rod_encounters", "good_rod_rate", "Good Rod", [60, 30, 5, 4, 1], true),
        new("super_rod_encounters", "super_rod_rate", "Super Rod", [60, 30, 5, 4, 1], true),
    ];

    public static readonly EncounterReplacement[] Replacements =
    [
        new("day", "Morning & day", [2, 3], "Replace grass slots 3 and 4 (10 % each) in the morning and during the day."),
        new("night", "Night", [2, 3], "Replace grass slots 3 and 4 (10 % each) at night."),
        new("swarms", "Swarm", [0, 1], "Replace grass slots 1 and 2 (20 % each) on days with a swarm on this route."),
        new("radar", "Poké Radar", [4, 5, 10, 11], "Replace grass slots 5, 6, 11 and 12 while the Poké Radar is used."),
        new("ruby", "Ruby in the GBA slot", [8, 9], "Replace grass slots 9 and 10 (4 % each) while Ruby is in the DS's GBA slot."),
        new("sapphire", "Sapphire in the GBA slot", [8, 9], "The same with Sapphire."),
        new("emerald", "Emerald in the GBA slot", [8, 9], "The same with Emerald."),
        new("firered", "FireRed in the GBA slot", [8, 9], "The same with FireRed."),
        new("leafgreen", "LeafGreen in the GBA slot", [8, 9], "The same with LeafGreen."),
    ];

    /// <summary>The project-relative encounters file of a map header, or null if the map has none.</summary>
    public static string? FileOf(string root, PlatinumMapHeader h)
    {
        string? rel = null;
        if (h.Folder != null && h.Encounters == "encounters.json") rel = $"{h.Folder}/encounters.json";
        else if (h.Encounters is { Length: > 0 } e && !e.Contains("NONE", StringComparison.OrdinalIgnoreCase) && !e.Contains('/'))
            rel = $"res/field/encounters/{e}.json";
        return rel != null && File.Exists(ProjectPaths.Combine(root, rel)) ? rel : null;
    }

    public static PlatinumJsonFile Load(string root, string rel) => PlatinumJsonFile.Load(root, rel, 4);

    public static int Int(JsonNode? n) => n is JsonValue v && v.TryGetValue<int>(out var i) ? i : n is JsonValue v2 && v2.TryGetValue<System.Text.Json.JsonElement>(out var e) && e.TryGetInt32(out var j) ? j : 0;
    public static string Str(JsonNode? n) => n is JsonValue v && v.TryGetValue<string>(out var s) ? s : "";

    /// <summary>Whether the map has any wild Pokémon (a rate above 0 with a species).</summary>
    public static bool HasAny(JsonObject root) =>
        new[] { Land }.Concat(Water).Any(t => Int(root[t.RateKey]) > 0 && root[t.Key] is JsonArray a && a.OfType<JsonObject>().Any(s => Str(s["species"]) is { Length: > 0 } sp && sp != "SPECIES_NONE"));

    /// <summary>How likely each species is in the grass (base table, without time of day or swarms), highest first.</summary>
    public static List<(string Species, int Percent, int MinLevel, int MaxLevel)> GrassChances(JsonObject root)
    {
        if (root[Land.Key] is not JsonArray slots) return [];
        var result = new Dictionary<string, (int P, int Min, int Max)>(StringComparer.Ordinal);
        for (int i = 0; i < Math.Min(slots.Count, Land.Chances.Length); i++)
        {
            if (slots[i] is not JsonObject s) continue;
            var sp = Str(s["species"]);
            if (sp.Length == 0 || sp == "SPECIES_NONE") continue;
            int lv = Int(s["level"]);
            result[sp] = result.TryGetValue(sp, out var x) ? (x.P + Land.Chances[i], Math.Min(x.Min, lv), Math.Max(x.Max, lv)) : (Land.Chances[i], lv, lv);
        }
        return result.Select(kv => (kv.Key, kv.Value.P, kv.Value.Min, kv.Value.Max)).OrderByDescending(x => x.P).ToList();
    }

    /// <summary>Problems that would make the game behave oddly (level 0, min above max, a rate without Pokémon …).</summary>
    public static List<string> Check(JsonObject root, ISet<string> species)
    {
        var problems = new List<string>();
        void Sp(string where, string sp)
        {
            if (sp.Length > 0 && sp != "SPECIES_NONE" && species.Count > 0 && !species.Contains(sp)) problems.Add($"{where}: {sp} is not a species of this project.");
        }
        if (root[Land.Key] is JsonArray land)
            for (int i = 0; i < land.Count; i++)
                if (land[i] is JsonObject s)
                {
                    Sp($"Grass slot {i + 1}", Str(s["species"]));
                    int lv = Int(s["level"]);
                    if (Str(s["species"]) is { Length: > 0 } sp && sp != "SPECIES_NONE" && (lv < 1 || lv > 100)) problems.Add($"Grass slot {i + 1}: level {lv} (1–100).");
                }
        if (Int(root[Land.RateKey]) > 0 && root[Land.Key] is JsonArray l2 && l2.OfType<JsonObject>().All(s => Str(s["species"]) is "" or "SPECIES_NONE"))
            problems.Add("Grass: the rate is above 0 but no Pokémon is set.");
        foreach (var t in Water)
        {
            if (root[t.Key] is not JsonArray slots) continue;
            for (int i = 0; i < slots.Count; i++)
                if (slots[i] is JsonObject s && Str(s["species"]) is { Length: > 0 } sp && sp != "SPECIES_NONE")
                {
                    Sp($"{t.Title} slot {i + 1}", sp);
                    int min = Int(s["level_min"]), max = Int(s["level_max"]);
                    if (min < 1 || max > 100 || min > max) problems.Add($"{t.Title} slot {i + 1}: levels {min}–{max} (1–100, min ≤ max).");
                }
            if (Int(root[t.RateKey]) > 0 && slots.OfType<JsonObject>().All(s => Str(s["species"]) is "" or "SPECIES_NONE"))
                problems.Add($"{t.Title}: the rate is above 0 but no Pokémon is set.");
        }
        foreach (var r in Replacements)
            if (root[r.Key] is JsonArray a)
                for (int i = 0; i < a.Count; i++) Sp($"{r.Title} {i + 1}", Str(a[i]));
        return problems;
    }
}
