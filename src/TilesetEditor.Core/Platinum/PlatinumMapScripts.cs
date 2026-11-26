using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using TilesetEditor.Core.Project;

namespace TilesetEditor.Core.Platinum;

/// <summary>A JSON file read for editing: the parsed document and the text it came from (precondition for writing).</summary>
public sealed class PlatinumJsonFile
{
    public required string Path { get; init; }
    public required JsonObject Root { get; init; }
    public required string OriginalText { get; init; }
    public required int Indent { get; init; }
    /// <summary>The file differs from the usual format in details (spaces); saving tidies those lines too.</summary>
    public bool Reformats { get; init; }

    public static PlatinumJsonFile Load(string root, string rel, int indent)
    {
        var text = File.ReadAllText(ProjectPaths.Combine(root, rel), Encoding.UTF8);
        var obj = JsonText.Parse(text) as JsonObject ?? throw new FormatException($"{rel} is not a JSON object.");
        // Files in the project's usual format keep every unchanged line; others (e.g. a stray space) get tidied when saved.
        bool usual = JsonText.Dump(obj, indent).TrimEnd() == text.TrimEnd();
        return new PlatinumJsonFile { Path = rel, Root = obj, OriginalText = text, Indent = indent, Reformats = !usual };
    }

    public string Serialize()
    {
        // Keep the file's own ending (no newline, one or two).
        return JsonText.Dump(Root, Indent) + OriginalText[OriginalText.TrimEnd().Length..];
    }

    public bool IsChanged => Serialize() != OriginalText;

    /// <summary>The current content, for undo (cheap; not the file format).</summary>
    public string Snapshot() => Root.ToJsonString();

    /// <summary>The same file with an earlier content (undo/redo); the original text stays, so "changed" still works.</summary>
    public PlatinumJsonFile Restored(string snapshot) => new()
    {
        Path = Path, Root = JsonText.Parse(snapshot) as JsonObject ?? throw new FormatException("Not a JSON object."),
        OriginalText = OriginalText, Indent = Indent, Reformats = Reformats,
    };
}

/// <summary>A script of a map script file: its number (position in the ScriptEntry list, 1-based) and label.</summary>
public sealed record PlatinumScriptEntry(int Number, string Label, int Line);

/// <summary>
/// A script the map starts by itself (init.s): when the player enters (OnTransition), when it is loaded (OnLoad), after a
/// menu or battle (OnResume), or – the cutscenes – as soon as a variable has a value (OnFrame table). Number = ScriptEntry.
/// </summary>
public sealed record MapInitScript(string Kind, int Number, string? Var = null, string? Value = null)
{
    public string Describe => Kind switch
    {
        "OnTransition" => "When the player enters the map",
        "OnLoad" => "When the map is loaded",
        "OnResume" => "After a menu or battle on this map",
        "OnFrame" => $"Cutscene: as soon as {Var} = {Value}",
        _ => Kind,
    };
}

/// <summary>
/// The events, script file and text bank of one map header of pokeplatinum: res/field/events/events_x.json,
/// res/field/scripts/scripts_x.s (a person's "script" is the number of its ScriptEntry) and res/text/x.json.
/// </summary>
public sealed partial class PlatinumMapScripts
{
    public required PlatinumMapHeader Header { get; init; }
    public string? EventsPath { get; init; }
    public string? ScriptPath { get; init; }
    public string? TextPath { get; init; }

    public static readonly string[] EventLists = ["object_events", "bg_events", "warp_events", "coord_events"];

    public static PlatinumMapScripts For(string root, PlatinumMapHeader h)
    {
        string? Existing(string? rel) => rel != null && File.Exists(ProjectPaths.Combine(root, rel)) ? rel : null;
        return new PlatinumMapScripts
        {
            Header = h,
            EventsPath = Existing(h.EventsFile),
            ScriptPath = Existing(h.ScriptFile),
            TextPath = Existing(h.TextFile),
        };
    }

    // ------------------------------------------------------------------ script file

    [GeneratedRegex(@"^\s*ScriptEntry\s+(\w+)\s*$", RegexOptions.Multiline)]
    private static partial Regex EntryLine();

    [GeneratedRegex(@"^(\w+):\s*$", RegexOptions.Multiline)]
    private static partial Regex LabelLine();

    /// <summary>What <see cref="AddMugshotBefore"/> did.</summary>
    public sealed record MugshotInsert(string Script, int Added, int Replaced, int SkippedBlockScripts);

    /// <summary>
    /// Shows a mugshot (mod "Dialog mugshots") right before a text: "ShowMugshot X" (", MUGSHOT_POS_LEFT" on the left) goes
    /// on the line before every line that shows <paramref name="textId"/> – or only before line <paramref name="lineIndex"/>.
    /// A ShowMugshot/ClearMugshot right above is replaced instead. Scripts made from blocks are left alone (their blocks
    /// would write them anew): use the Mugshot block there. Adds #include "constants/mugshots.h" when needed.
    /// </summary>
    public static MugshotInsert AddMugshotBefore(string script, string textId, string mugshot, bool left, int? lineIndex = null)
    {
        var nl = script.Contains("\r\n") ? "\r\n" : "\n";
        var lines = script.Replace("\r\n", "\n").Split('\n').ToList();
        var shows = new Regex(@"^\s*\w*Message\w*\s+(?:\w+\s*,\s*)?" + Regex.Escape(textId) + @"\b");
        var targets = Enumerable.Range(0, lines.Count).Where(i => (lineIndex == null || i == lineIndex) && shows.IsMatch(lines[i])).ToList();
        int added = 0, replaced = 0, skipped = 0;
        var command = left ? $"ShowMugshot {mugshot}, MUGSHOT_POS_LEFT" : $"ShowMugshot {mugshot}";
        foreach (var i in Enumerable.Reverse(targets))
        {
            // The script the line belongs to: the nearest label above it.
            int at = i;
            while (at >= 0 && !Regex.IsMatch(lines[at], @"^\w+:\s*$")) at--;
            if (at >= 0 && at > 0 && lines[at - 1].StartsWith(PlatinumScriptBlocks.Marker, StringComparison.Ordinal)) { skipped++; continue; }
            var indent = Regex.Match(lines[i], @"^\s*").Value;
            int prev = i - 1;
            while (prev >= 0 && lines[prev].Trim().Length == 0) prev--;
            if (prev >= 0 && Regex.IsMatch(lines[prev], @"^\s*(ShowMugshot|ClearMugshot)\b")) { lines[prev] = indent + command; replaced++; }
            else { lines.Insert(i, indent + command); added++; }
        }
        var result = string.Join(nl, lines);
        if (added + replaced > 0 && !result.Contains("#include \"constants/mugshots.h\"", StringComparison.Ordinal))
        {
            var lastInclude = Regex.Matches(result, @"^#include[^\n]*\n", RegexOptions.Multiline).LastOrDefault();
            var line = "#include \"constants/mugshots.h\"" + nl;
            result = lastInclude != null ? result.Insert(lastInclude.Index + lastInclude.Length, line) : line + result;
        }
        return new MugshotInsert(result, added, replaced, skipped);
    }

    /// <summary>The map's init script file: res/field/maps/x/init.s (newer pokeplatinum) or the header's initScriptsArchiveID.</summary>
    public static string? InitFile(string root, PlatinumMapHeader h)
    {
        if (h.Folder != null)
        {
            var rel = h.Folder + "/init.s";
            return File.Exists(ProjectPaths.Combine(root, rel)) ? rel : null;
        }
        if (h.Fields.GetValueOrDefault("initScriptsArchiveID") is { } id && id != "scripts_init_empty")
        {
            var rel = $"res/field/scripts/{id}.s";
            return File.Exists(ProjectPaths.Combine(root, rel)) ? rel : null;
        }
        return null;
    }

    /// <summary>The scripts the map starts by itself, in the order of its init file.</summary>
    public static List<MapInitScript> InitScripts(string root, PlatinumMapHeader h) =>
        InitFile(root, h) is { } rel ? ParseInit(File.ReadAllText(ProjectPaths.Combine(root, rel))) : [];

    public static List<MapInitScript> ParseInit(string text)
    {
        var list = new List<MapInitScript>();
        foreach (Match m in Regex.Matches(text, @"^\s*(InitScriptEntry_On(Transition|Load|Resume)\s+(\d+)|InitScriptGoToIfEqual\s+(\w+)\s*,\s*(\w+)\s*,\s*(\d+))", RegexOptions.Multiline))
        {
            if (m.Groups[2].Success) list.Add(new MapInitScript("On" + m.Groups[2].Value, int.Parse(m.Groups[3].Value)));
            else list.Add(new MapInitScript("OnFrame", int.Parse(m.Groups[6].Value), m.Groups[4].Value, m.Groups[5].Value));
        }
        return list;
    }

    public static List<PlatinumScriptEntry> Entries(string script)
    {
        var labels = LabelLine().Matches(script).ToDictionary(m => m.Groups[1].Value, m => LineOf(script, m.Index), StringComparer.Ordinal);
        return EntryLine().Matches(script).Select((m, i) => new PlatinumScriptEntry(i + 1, m.Groups[1].Value, labels.GetValueOrDefault(m.Groups[1].Value, -1))).ToList();
    }

    public static int LineOf(string text, int index) => text.AsSpan(0, Math.Min(index, text.Length)).Count('\n');

    /// <summary>The prefix the map uses for labels and texts ("JubilifeCity" from JubilifeCity_Looker).</summary>
    public static string Prefix(string script, string fallback)
    {
        var first = EntryLine().Matches(script).Select(m => m.Groups[1].Value).FirstOrDefault(l => l.Contains('_'));
        return first != null ? first[..first.IndexOf('_')] : fallback;
    }

    /// <summary>Adds a script (label + body) as the next ScriptEntry; returns the new text and the script's number.</summary>
    public static (string Text, int Number) AddScript(string script, string label, string body, IEnumerable<string>? includes = null)
    {
        if (LabelLine().Matches(script).Any(m => m.Groups[1].Value == label)) throw new InvalidDataException($"The label {label} already exists.");
        var end = Regex.Match(script, @"^([ \t]*)ScriptEntryEnd[ \t]*\r?$", RegexOptions.Multiline);
        if (!end.Success) throw new InvalidDataException("The script file has no ScriptEntryEnd.");
        int number = EntryLine().Matches(script).Count + 1;
        var text = script[..end.Index] + $"{end.Groups[1].Value}ScriptEntry {label}\n" + script[end.Index..];
        // The script goes before the final alignment directive (if any), else at the end.
        var tail = Regex.Match(text, @"\n[ \t]*\.balign[^\n]*\n?\s*$");
        var block = $"{label}:\n{body.TrimEnd('\n')}\n\n";
        text = tail.Success ? text[..(tail.Index + 1)] + block + text[(tail.Index + 1)..] : text.TrimEnd('\n') + "\n\n" + block;
        foreach (var inc in includes ?? [])
        {
            var line = $"#include \"{inc}\"";
            if (text.Contains(line, StringComparison.Ordinal)) continue;
            var lastInclude = Regex.Matches(text, @"^#include[^\n]*\n", RegexOptions.Multiline).LastOrDefault();
            text = lastInclude != null ? text.Insert(lastInclude.Index + lastInclude.Length, line + "\n") : line + "\n" + text;
        }
        return (text, number);
    }

    /// <summary>The standard script of a talking person: sound, lock, face the player, message(s), release.</summary>
    public static string TalkScript(IReadOnlyList<string> messageIds, string? mugshot, string confirmSound = "SEQ_SE_CONFIRM")
    {
        var sb = new StringBuilder();
        sb.Append($"    PlaySE {confirmSound}\n    LockAll\n    FacePlayer\n");
        if (mugshot != null) sb.Append($"    ShowMugshot {mugshot}\n");
        foreach (var id in messageIds) sb.Append($"    Message {id}\n    WaitButton\n");
        sb.Append("    CloseMessage\n    ReleaseAll\n    End\n");
        return sb.ToString();
    }

    /// <summary>
    /// The confirm sound as this version of pokeplatinum names it (SEQ_SE_CONFIRM in older versions, SE_CONFIRM_sseq_3 in
    /// newer ones): taken from the NPCMessage macro of asm/macros/scrcmd.inc.
    /// </summary>
    public static string ConfirmSound(string root)
    {
        var path = ProjectPaths.Combine(root, "asm/macros/scrcmd.inc");
        if (File.Exists(path))
        {
            var m = Regex.Match(File.ReadAllText(path), @"\.macro NPCMessage[^\n]*\n\s*PlaySE\s+(\w+)");
            if (m.Success) return m.Groups[1].Value;
        }
        return "SEQ_SE_CONFIRM";
    }

    // ------------------------------------------------------------------ text bank

    public static JsonArray Messages(PlatinumJsonFile bank) => bank.Root["messages"] as JsonArray ?? throw new FormatException($"{bank.Path} has no messages.");

    public static string MessageText(JsonNode? message) => message?["en_US"] switch
    {
        JsonArray a => string.Concat(a.Select(x => x?.GetValue<string>() ?? "")),
        JsonValue v when v.TryGetValue<string>(out var s) => s,
        _ => "",
    };

    /// <summary>Sets the English text, as one string or as lines like the bank's other multi-line messages.</summary>
    public static void SetMessageText(JsonObject message, string text)
    {
        var lines = JsonText.SplitLines(text);
        message["en_US"] = lines.Count > 1 || message["en_US"] is JsonArray
            ? new JsonArray(lines.Select(l => (JsonNode?)JsonValue.Create(l)).ToArray())
            : JsonValue.Create(text);
    }

    public static JsonObject AddMessage(PlatinumJsonFile bank, string id, string text)
    {
        var msgs = Messages(bank);
        if (msgs.OfType<JsonObject>().Any(m => m["id"]?.GetValue<string>() == id)) throw new InvalidDataException($"The text {id} already exists.");
        var m = new JsonObject { ["id"] = id };
        SetMessageText(m, text);
        msgs.Add(m);
        return m;
    }

    /// <summary>
    /// Adds a person who talks: the text(s) to the bank, a script as next ScriptEntry, and the object event – in the form the
    /// project uses (script label and DIR_ names in newer pokeplatinum, numbers in older versions; the key order of the map's
    /// other people; the height of the nearest person).
    /// </summary>
    public static (string Script, string Label, List<string> TextIds) AddTalkingPerson(
        PlatinumJsonFile events, PlatinumJsonFile texts, string script, string prefix, TalkingPerson spec, string confirmSound)
    {
        var name = PascalName(spec.Name);
        if (name.Length == 0 || char.IsDigit(name[0])) throw new InvalidDataException("The person needs a name that starts with a letter (used for the script label).");
        var textIds = new List<string>();
        for (int i = 0; i < spec.Messages.Count; i++)
        {
            var id = $"{prefix}_Text_{name}{(spec.Messages.Count > 1 ? (i + 1).ToString() : "")}";
            AddMessage(texts, id, spec.Messages[i]);
            textIds.Add(id);
        }
        var label = $"{prefix}_{name}";
        var (newScript, number) = AddScript(script, label, TalkScript(textIds, spec.Mugshot, confirmSound), spec.Mugshot != null ? ["constants/mugshots.h"] : null);

        AddObjectEvent(events, name, spec.Graphics, spec.Movement, spec.Direction, spec.Range, spec.X, spec.Z, label, number);
        return (newScript, label, textIds);
    }

    private static bool IsString(JsonNode? n) => n is JsonValue v && v.TryGetValue<System.Text.Json.JsonElement>(out var e) && e.ValueKind == System.Text.Json.JsonValueKind.String;
    private static int Int(JsonNode? n) => n is JsonValue v && v.TryGetValue<System.Text.Json.JsonElement>(out var e) && e.TryGetInt32(out var i) ? i : n is JsonValue v2 && v2.TryGetValue<int>(out var i2) ? i2 : 0;

    /// <summary>Whether the map's events name their script by label (newer pokeplatinum) or by ScriptEntry number.</summary>
    public static bool ScriptsByLabel(PlatinumJsonFile events) =>
        EventLists.SelectMany(l => (events.Root[l] as JsonArray)?.OfType<JsonObject>() ?? []).Any(o => IsString(o["script"]));

    // No script yet: 0 in both forms (an empty label would stop the build: "unrecognized script-label").
    private static JsonNode ScriptValue(PlatinumJsonFile events, string label, int number) =>
        string.IsNullOrEmpty(label) ? JsonValue.Create(number) : ScriptsByLabel(events) ? JsonValue.Create(label) : JsonValue.Create(number);

    /// <summary>Keys in the order of the list's first entry (so new entries look like the others).</summary>
    private static JsonObject LikeSample(JsonArray list, JsonObject e)
    {
        if (list.OfType<JsonObject>().FirstOrDefault() is not { } sample) return e;
        var ordered = new JsonObject();
        foreach (var (k, _) in sample) if (e.ContainsKey(k)) { var v = e[k]; e.Remove(k); ordered[k] = v; }
        foreach (var (k, v) in e.ToList()) { e.Remove(k); ordered[k] = v; }
        return ordered;
    }

    private static JsonArray List(PlatinumJsonFile events, string key)
    {
        if (events.Root[key] is not JsonArray list) { list = new JsonArray(); events.Root[key] = list; }
        return list;
    }

    private static int NearestHeight(PlatinumJsonFile events, int x, int z) =>
        EventLists.SelectMany(l => (events.Root[l] as JsonArray)?.OfType<JsonObject>() ?? [])
            .OrderBy(o => Math.Abs(Int(o["x"]) - x) + Math.Abs(Int(o["z"]) - z)).Select(o => Int(o["y"])).FirstOrDefault();

    /// <summary>A new person (object event) that runs <paramref name="label"/> (or ScriptEntry <paramref name="number"/>).</summary>
    public static JsonObject AddObjectEvent(PlatinumJsonFile events, string name, string graphics, string movement, int direction, int range, int x, int z, string label, int number)
    {
        bool dirNames = EventLists.SelectMany(l => (events.Root[l] as JsonArray)?.OfType<JsonObject>() ?? []).Any(o => IsString(o["initial_dir"]));
        var people = List(events, "object_events");
        var used = people.OfType<JsonObject>().Select(o => o["id"]?.GetValue<string>()).ToHashSet();
        var localId = "LOCALID_" + string.Concat(PascalName(name).Select((c, i) => i > 0 && char.IsUpper(c) ? "_" + c : c.ToString())).ToUpperInvariant();
        var baseId = localId;
        for (int k = 2; used.Contains(localId); k++) localId = $"{baseId}_{k}";
        var person = new JsonObject
        {
            ["id"] = localId,
            ["graphics_id"] = graphics,
            ["movement_type"] = movement,
            ["trainer_type"] = "TRAINER_TYPE_NONE",
            ["hidden_flag"] = "0",
            ["script"] = ScriptValue(events, label, number),
            ["initial_dir"] = dirNames ? JsonValue.Create(new[] { "DIR_NORTH", "DIR_SOUTH", "DIR_WEST", "DIR_EAST" }[direction]) : JsonValue.Create(direction),
            ["data"] = new JsonArray(),
            ["movement_range_x"] = range,
            ["movement_range_z"] = range,
            ["x"] = x,
            ["z"] = z,
            ["y"] = NearestHeight(events, x, z),
        };
        if (people.OfType<JsonObject>().FirstOrDefault()?["data"] is JsonArray sd) person["data"] = new JsonArray(sd.Select(_ => (JsonNode?)JsonValue.Create(0)).ToArray());
        person = LikeSample(people, person);
        people.Add(person);
        return person;
    }

    /// <summary>
    /// The trainer of a person, in either form: newer pokeplatinum "trainer": { "id", "sight_range" }, older "script":
    /// "TRAINER_X" with the sight range in "data"[0]. Null for people who are no trainers.
    /// </summary>
    public static (string Trainer, int Sight)? TrainerOf(JsonObject person)
    {
        if (person["trainer"] is JsonObject t && t["id"] is JsonValue id && id.TryGetValue<string>(out var tid))
            return (tid, Int(t["sight_range"]));
        if (person["script"] is JsonValue sv && sv.TryGetValue<string>(out var script) && script.StartsWith("TRAINER_", StringComparison.Ordinal))
            return (script, person["data"] is JsonArray d && d.Count > 0 ? Int(d[0]) : 0);
        return null;
    }

    /// <summary>Whether this events file writes trainers as "trainer": {…} (newer pokeplatinum) – else "script" + "data".</summary>
    public static bool TrainersAsObject(PlatinumJsonFile events)
    {
        var people = (events.Root["object_events"] as JsonArray)?.OfType<JsonObject>().ToList() ?? [];
        if (people.Any(o => o["trainer"] is JsonObject)) return true;
        if (people.Any(o => o["script"] is JsonValue v && v.TryGetValue<string>(out var s) && s.StartsWith("TRAINER_", StringComparison.Ordinal))) return false;
        return events.Path.StartsWith("res/field/maps/", StringComparison.Ordinal);
    }

    /// <summary>Sets trainer and sight range of a person (keeps the form the file uses).</summary>
    public static void SetTrainer(PlatinumJsonFile events, JsonObject person, string trainer, int sight)
    {
        sight = Math.Clamp(sight, 1, 15);
        person["trainer_type"] = "TRAINER_TYPE_NORMAL";
        if (TrainersAsObject(events))
        {
            person.Remove("script");
            person.Remove("data");
            if (person["trainer"] is JsonObject t) { t["id"] = trainer; t["sight_range"] = sight; }
            else InsertAfter(person, "hidden_flag", "trainer", new JsonObject { ["id"] = trainer, ["sight_range"] = sight });
        }
        else
        {
            person.Remove("trainer");
            if (person.ContainsKey("script")) person["script"] = trainer; else InsertAfter(person, "hidden_flag", "script", JsonValue.Create(trainer));
            person["data"] = new JsonArray(JsonValue.Create(sight));
        }
    }

    private static void InsertAfter(JsonObject o, string afterKey, string key, JsonNode value)
    {
        var entries = o.ToList();
        foreach (var (k, _) in entries) o.Remove(k);
        bool done = false;
        foreach (var (k, v) in entries)
        {
            o[k] = v;
            if (k == afterKey) { o[key] = value; done = true; }
        }
        if (!done) o[key] = value;
    }

    /// <summary>
    /// A trainer standing on the map: battles the player who walks into its sight. Looks like its class where an overworld
    /// sprite of that name exists (TRAINER_CLASS_YOUNGSTER → OBJ_EVENT_GFX_YOUNGSTER).
    /// </summary>
    public static JsonObject AddTrainer(PlatinumJsonFile events, string trainer, string graphics, int direction, int sight, int x, int z)
    {
        var name = trainer.StartsWith("TRAINER_", StringComparison.Ordinal) ? trainer[8..] : trainer;
        var movement = new[] { "MOVEMENT_TYPE_LOOK_NORTH", "MOVEMENT_TYPE_LOOK_SOUTH", "MOVEMENT_TYPE_LOOK_WEST", "MOVEMENT_TYPE_LOOK_EAST" }[Math.Clamp(direction, 0, 3)];
        var person = AddObjectEvent(events, PascalName(name.ToLowerInvariant().Replace('_', ' ')), graphics, movement, direction, 0, x, z, "", 0);
        // A trainer runs no script of its own; the id follows the trainer's name.
        var people = (JsonArray)events.Root["object_events"]!;
        var used = people.OfType<JsonObject>().Where(o => !ReferenceEquals(o, person)).Select(o => o["id"]?.GetValue<string>()).ToHashSet();
        var localId = "LOCALID_" + name;
        for (int k = 2; used.Contains(localId); k++) localId = $"LOCALID_{name}_{k}";
        person["id"] = localId;
        person.Remove("script");
        SetTrainer(events, person, trainer, sight);
        return person;
    }

    /// <summary>The overworld sprite that fits a trainer class, or null.</summary>
    public static string? GraphicsForClass(string trainerClass, IReadOnlyList<string> graphics)
    {
        var key = trainerClass.StartsWith("TRAINER_CLASS_", StringComparison.Ordinal) ? trainerClass[14..] : trainerClass;
        var set = graphics.ToHashSet(StringComparer.Ordinal);
        foreach (var candidate in new[] { key, key.Replace("_M", "M").Replace("_F", "F"), key.Split('_')[0], key.Replace("_", "") })
            if (set.Contains("OBJ_EVENT_GFX_" + candidate)) return "OBJ_EVENT_GFX_" + candidate;
        return graphics.FirstOrDefault(g => g.StartsWith("OBJ_EVENT_GFX_" + key.Split('_')[0], StringComparison.Ordinal));
    }

    /// <summary>Whether this events file names its warps (newer pokeplatinum: "id": "WARP_X", "dest_warp_id": the target's id).</summary>
    public static bool WarpsByName(PlatinumJsonFile events)
    {
        var warps = (events.Root["warp_events"] as JsonArray)?.OfType<JsonObject>().ToList() ?? [];
        // A map without warps says nothing: the layout decides (per-map folders = newer pokeplatinum = names).
        return warps.Count > 0 ? warps.Any(o => o.ContainsKey("id") || IsString(o["dest_warp_id"])) : events.Path.StartsWith("res/field/maps/", StringComparison.Ordinal);
    }

    /// <summary>The warps of a map as a warp elsewhere names them: their ids (newer pokeplatinum) or numbers (older), with position.</summary>
    public static List<(string Value, string Display)> WarpTargets(string root, PlatinumMapHeader target)
    {
        if (target.EventsFile == null || !File.Exists(ProjectPaths.Combine(root, target.EventsFile))) return [];
        var f = PlatinumJsonFile.Load(root, target.EventsFile, 4);
        var warps = (f.Root["warp_events"] as JsonArray)?.OfType<JsonObject>().ToList() ?? [];
        return warps.Select((w, i) =>
        {
            var id = w["id"] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
            var to = w["dest_header_id"] is JsonValue d && d.TryGetValue<string>(out var ds) ? ds : "";
            return (id ?? i.ToString(), $"{id ?? $"warp {i}"} at {Int(w["x"])}, {Int(w["z"])} (leads to {to})");
        }).ToList();
    }

    /// <summary>A new warp to <paramref name="destHeader"/>, arriving at its warp <paramref name="destWarp"/> (id or number).</summary>
    public static JsonObject AddWarp(PlatinumJsonFile events, int x, int z, string destHeader, string destWarp)
    {
        var list = List(events, "warp_events");
        JsonObject warp;
        if (WarpsByName(events))
        {
            var used = list.OfType<JsonObject>().Select(o => o["id"]?.GetValue<string>()).ToHashSet();
            var baseId = "WARP_" + (destHeader.StartsWith("MAP_HEADER_", StringComparison.Ordinal) ? destHeader[11..] : destHeader);
            var id = baseId;
            for (int k = 2; used.Contains(id); k++) id = $"{baseId}_{k}";
            warp = new JsonObject { ["id"] = id, ["x"] = x, ["z"] = z, ["dest_header_id"] = destHeader, ["dest_warp_id"] = destWarp };
        }
        else
            warp = new JsonObject { ["x"] = x, ["z"] = z, ["dest_header_id"] = destHeader, ["dest_warp_id"] = int.TryParse(destWarp, out var n) ? n : 0 };
        warp = LikeSample(list, warp);
        list.Add(warp);
        return warp;
    }

    /// <summary>
    /// A warp in both directions: one on this map to <paramref name="destHeader"/> and the way back on the destination map
    /// (at <paramref name="backX"/>/<paramref name="backZ"/>), each arriving at the other – by id in newer pokeplatinum, by
    /// number in older. Both files are changed in memory; save both.
    /// </summary>
    public static (JsonObject Here, JsonObject Back) AddTwoWayWarp(PlatinumJsonFile here, string hereHeader, int x, int z,
        PlatinumJsonFile dest, string destHeader, int backX, int backZ)
    {
        if (ReferenceEquals(here, dest)) throw new InvalidDataException("A warp in both directions needs another map – for this map, add two warps.");
        bool names = WarpsByName(here);
        int hereIndex = (here.Root["warp_events"] as JsonArray)?.Count ?? 0;
        var a = AddWarp(here, x, z, destHeader, "0");
        string hereRef = names ? a["id"]!.GetValue<string>() : hereIndex.ToString();
        int backIndex = (dest.Root["warp_events"] as JsonArray)?.Count ?? 0;
        var b = AddWarp(dest, backX, backZ, hereHeader, hereRef);
        string backRef = WarpsByName(dest) ? b["id"]!.GetValue<string>() : backIndex.ToString();
        a["dest_warp_id"] = names ? JsonValue.Create(backRef) : JsonValue.Create(int.Parse(backRef));
        return (a, b);
    }

    /// <summary>
    /// A copy of an event at <paramref name="x"/>/<paramref name="z"/> in <paramref name="list"/> of <paramref name="events"/>
    /// (copy and paste, duplicate). Its id gets a free number (LOCALID_OLD_MAN_2, WARP_X_2), so ids stay unique.
    /// </summary>
    public static JsonObject CopyEvent(PlatinumJsonFile events, string list, JsonObject source, int x, int z)
    {
        var copy = (JsonObject)JsonNode.Parse(source.ToJsonString())!;
        copy["x"] = x;
        copy["z"] = z;
        var arr = List(events, list);
        if (copy["id"] is JsonValue v && v.TryGetValue<string>(out var id) && id.Length > 0)
        {
            var used = arr.OfType<JsonObject>().Select(o => o["id"]?.GetValue<string>()).ToHashSet();
            var baseId = System.Text.RegularExpressions.Regex.Replace(id, @"_\d+$", "");
            var nid = baseId;
            for (int k = 2; used.Contains(nid); k++) nid = $"{baseId}_{k}";
            copy["id"] = nid;
        }
        arr.Add(copy);
        return copy;
    }

    /// <summary>A person, sign or trigger tile without script (0) – give it one in Events & Dialogs.</summary>
    public static JsonObject AddPersonWithoutScript(PlatinumJsonFile events, string graphics, int x, int z) =>
        AddObjectEvent(events, PascalName(graphics.Replace("OBJ_EVENT_GFX_", "").ToLowerInvariant().Replace('_', ' ')), graphics, "MOVEMENT_TYPE_LOOK_SOUTH", 1, 0, x, z, "", 0);

    /// <summary>A new sign (bg event the player reads facing it).</summary>
    public static JsonObject AddSign(PlatinumJsonFile events, int x, int z, string label, int number)
    {
        var list = List(events, "bg_events");
        var sample = list.OfType<JsonObject>().FirstOrDefault(o => o.ContainsKey("script"));
        bool typeNames = sample?["type"] is { } t && IsString(t);
        bool dirNames = sample?["player_facing_dir"] is { } d ? IsString(d) : list.OfType<JsonObject>().Any(o => IsString(o["player_facing_dir"]));
        var sign = new JsonObject
        {
            ["script"] = ScriptValue(events, label, number),
            ["type"] = typeNames || sample == null ? JsonValue.Create("BG_EVENT_TYPE_FACING") : JsonValue.Create(0),
            ["x"] = x,
            ["z"] = z,
            ["y"] = NearestHeight(events, x, z),
            ["player_facing_dir"] = dirNames || sample == null ? JsonValue.Create("BG_EVENT_DIR_NORTH") : JsonValue.Create(1),
        };
        if (sample != null) sign = LikeSampleOf(sample, sign);
        list.Add(sign);
        return sign;
    }

    /// <summary>A new trigger tile (coord event): runs when the player steps on it while <paramref name="var"/> has <paramref name="value"/>.</summary>
    public static JsonObject AddTrigger(PlatinumJsonFile events, int x, int z, string var, int value, string label, int number)
    {
        var list = List(events, "coord_events");
        var trigger = new JsonObject
        {
            ["script"] = ScriptValue(events, label, number),
            ["x"] = x,
            ["z"] = z,
            ["y"] = NearestHeight(events, x, z),
            ["width"] = 1,
            ["length"] = 1,
            ["var"] = var,
            ["value"] = value,
        };
        trigger = LikeSample(list, trigger);
        list.Add(trigger);
        return trigger;
    }

    private static JsonObject LikeSampleOf(JsonObject sample, JsonObject e)
    {
        var ordered = new JsonObject();
        foreach (var (k, _) in sample) if (e.ContainsKey(k)) { var v = e[k]; e.Remove(k); ordered[k] = v; }
        foreach (var (k, v) in e.ToList()) { e.Remove(k); ordered[k] = v; }
        return ordered;
    }

    /// <summary>A label-friendly name: "old man 2" → "OldMan2".</summary>
    public static string PascalName(string s) =>
        string.Concat(Regex.Split(s.Trim(), @"[^A-Za-z0-9]+").Where(w => w.Length > 0).Select(w => char.ToUpperInvariant(w[0]) + w[1..]));
}

/// <summary>What a new talking person looks like and says.</summary>
public sealed record TalkingPerson(string Name, string Graphics, string Movement, int Direction, int Range, int X, int Z, IReadOnlyList<string> Messages, string? Mugshot);
