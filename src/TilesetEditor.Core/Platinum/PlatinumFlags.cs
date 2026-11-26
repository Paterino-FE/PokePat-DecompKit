using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using TilesetEditor.Core.Project;

namespace TilesetEditor.Core.Platinum;

/// <summary>A flag or variable of generated/vars_flags.txt with its number and group.</summary>
public sealed record PlatinumVarFlag(string Name, int Value, string Group, int Line)
{
    public bool IsFlag => Name.StartsWith("FLAG_", StringComparison.Ordinal);
    public bool IsFree => Name.StartsWith("FLAG_UNUSED_", StringComparison.Ordinal) || Name.StartsWith("VAR_UNUSED_", StringComparison.Ordinal);
    public bool IsUnknown => Name.StartsWith("FLAG_UNK_", StringComparison.Ordinal) || Name.StartsWith("VAR_UNK_", StringComparison.Ordinal);
    public string Hex => $"0x{Value:X4}";
}

/// <summary>Where a flag or variable is used.</summary>
public sealed record PlatinumVarFlagUse(string File, int Line, string Text, string How);

/// <summary>A trainer and the flag the game sets when the player beats it (TRAINER_DEFEATED_FLAGS_START + trainer number).</summary>
public sealed record PlatinumTrainerFlag(string Trainer, int Number, string? Flag, int FlagValue, bool Missing);

/// <summary>
/// Flags and variables of pokeplatinum: generated/vars_flags.txt numbers them like a C enum (the build makes a header of
/// it). Groups follow the ranges of the file (map-local, hidden items, trainers, daily …). Defeated-trainer flags are not
/// named in scripts: the game uses TRAINER_DEFEATED_FLAGS_START + the trainer's number, so every trainer of
/// generated/trainers.txt needs its place in that range.
/// </summary>
public static partial class PlatinumFlags
{
    public const string File = "generated/vars_flags.txt";
    public const string TrainersFile = "generated/trainers.txt";

    public static readonly (string Group, string Description)[] Groups =
    [
        ("Story & events", "Flags of the story and of events: set when something happened."),
        ("Hide people", "FLAG_HIDE_…: a person whose hidden_flag is this flag is not on the map while it is set."),
        ("Items & gifts received", "Set when the player got an item or gift from someone (so it is given only once)."),
        ("Hidden items", "One flag per hidden item: set when it was found."),
        ("Trainers defeated", "One flag per trainer of generated/trainers.txt, in the same order: set when the trainer was beaten."),
        ("First arrival", "Set when the player first reaches a town (Fly destinations)."),
        ("Daily", "Cleared every day (daily gifts, berries …)."),
        ("Map-local flags", "FLAG_MAP_LOCAL_…: cleared when the player leaves the map – for things inside one map."),
        ("Free flags", "FLAG_UNUSED_… and FLAG_UNK_…: not used by the game – free for your own events."),
        ("System", "Flags the game's code uses (menus, features, villa …)."),
        ("Story variables", "Variables of the story and events (numbers, e.g. the state of a quest)."),
        ("Map-local variables", "VAR_MAP_LOCAL_…: reset when the player leaves the map."),
        ("Looks variables", "VAR_OBJ_GFX_ID_…: the overworld sprite of special people (e.g. the counterpart)."),
        ("Script temporaries", "VAR_0x8000 … VAR_RESULT: values while a script runs – not saved."),
        ("Free variables", "VAR_UNUSED_…: free for your own events."),
    ];

    [GeneratedRegex(@"\b(?:FLAG|VAR)_[A-Za-z0-9_]+\b")]
    private static partial Regex Token();

    public static bool Exists(string root) => System.IO.File.Exists(ProjectPaths.Combine(root, File));

    /// <summary>All flags and variables with their numbers (the file's markers like HIDDEN_ITEM_FLAGS_START are left out).</summary>
    public static List<PlatinumVarFlag> Load(string root) => Parse(System.IO.File.ReadAllText(ProjectPaths.Combine(root, File)));

    public static List<PlatinumVarFlag> Parse(string text) => ParseAll(text).List;

    /// <summary>The number of any name of the file, markers included (e.g. TRAINER_DEFEATED_FLAGS_START), or -1.</summary>
    public static int ValueOf(string text, string name) => ParseAll(text).Values.TryGetValue(name, out var v) ? v : -1;

    private static (List<PlatinumVarFlag> List, Dictionary<string, int> Values) ParseAll(string text)
    {
        var values = new Dictionary<string, int>(StringComparer.Ordinal);
        var list = new List<PlatinumVarFlag>();
        int next = 0, lineNo = -1;
        string? range = null;
        foreach (var raw in text.Replace("\r\n", "\n").Split('\n'))
        {
            lineNo++;
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#') || line.StartsWith("//", StringComparison.Ordinal)) continue;
            var parts = line.Split('=', 2, StringSplitOptions.TrimEntries);
            var name = parts[0];
            int value = next;
            if (parts.Length == 2)
            {
                var v = parts[1];
                if (v.StartsWith("0x", StringComparison.OrdinalIgnoreCase) && int.TryParse(v[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var h)) value = h;
                else if (int.TryParse(v, out var d)) value = d;
                else if (values.TryGetValue(v, out var r)) value = r;
            }
            values[name] = value;
            next = value + 1;
            if (name.EndsWith("_START", StringComparison.Ordinal) || name.StartsWith("SYSTEM_FLAGS_", StringComparison.Ordinal)) range = name;
            if (!name.StartsWith("FLAG_", StringComparison.Ordinal) && !name.StartsWith("VAR_", StringComparison.Ordinal)) continue;
            list.Add(new PlatinumVarFlag(name, value, GroupOf(name, range), lineNo));
        }
        return (list, values);
    }

    private static string GroupOf(string name, string? range)
    {
        if (name.StartsWith("VAR_", StringComparison.Ordinal))
        {
            if (name.StartsWith("VAR_UNUSED_", StringComparison.Ordinal)) return "Free variables";
            if (name.StartsWith("VAR_MAP_LOCAL_", StringComparison.Ordinal)) return "Map-local variables";
            if (name.StartsWith("VAR_OBJ_GFX_ID_", StringComparison.Ordinal)) return "Looks variables";
            if (range == "SCRIPT_LOCAL_VARS_START") return "Script temporaries";
            return "Story variables";
        }
        if (name.StartsWith("FLAG_UNUSED_", StringComparison.Ordinal) || name.StartsWith("FLAG_UNK_", StringComparison.Ordinal)) return "Free flags";
        if (name.StartsWith("FLAG_MAP_LOCAL_", StringComparison.Ordinal)) return "Map-local flags";
        if (range == "TRAINER_DEFEATED_FLAGS_START" || name.StartsWith("FLAG_DEFEATED_TRAINER_", StringComparison.Ordinal)) return "Trainers defeated";
        if (range == "HIDDEN_ITEM_FLAGS_START" || name.StartsWith("FLAG_OBTAINED_HIDDEN_", StringComparison.Ordinal)) return "Hidden items";
        if (name.StartsWith("FLAG_DAILY_", StringComparison.Ordinal) || range == "DAILY_FLAGS_START") return "Daily";
        if (name.StartsWith("FLAG_FIRST_ARRIVAL_", StringComparison.Ordinal)) return "First arrival";
        if (name.StartsWith("FLAG_HIDE_", StringComparison.Ordinal)) return "Hide people";
        if (name.StartsWith("FLAG_RECEIVED_", StringComparison.Ordinal) || name.StartsWith("FLAG_OBTAINED_", StringComparison.Ordinal) || name.StartsWith("FLAG_GOT_", StringComparison.Ordinal))
            return "Items & gifts received";
        if (range is "FLAG_VILLA_FURNITURE_START" || (range?.StartsWith("SYSTEM_FLAGS", StringComparison.Ordinal) ?? false) || name.StartsWith("FLAG_SYS_", StringComparison.Ordinal)) return "System";
        return "Story & events";
    }

    /// <summary>
    /// Every place a flag or variable name appears: scripts (res/field), events (hidden_flag, var …), text-free C code (src,
    /// include). <paramref name="progress"/> gets the share done (0…1).
    /// </summary>
    public static Dictionary<string, List<PlatinumVarFlagUse>> FindUses(string root, CancellationToken cancel = default, IProgress<double>? progress = null)
    {
        var result = new Dictionary<string, List<PlatinumVarFlagUse>>(StringComparer.Ordinal);
        var files = new List<string>();
        foreach (var (dir, patterns) in new[] { ("res", new[] { "*.s", "*.json" }), ("src", new[] { "*.c", "*.h" }), ("include", new[] { "*.h" }), ("asm", new[] { "*.s", "*.inc" }) })
        {
            var full = ProjectPaths.Combine(root, dir);
            if (!Directory.Exists(full)) continue;
            foreach (var p in patterns) files.AddRange(Directory.EnumerateFiles(full, p, SearchOption.AllDirectories));
        }
        for (int i = 0; i < files.Count; i++)
        {
            cancel.ThrowIfCancellationRequested();
            if (i % 50 == 0) progress?.Report((double)i / files.Count);
            var rel = Path.GetRelativePath(root, files[i]).Replace('\\', '/');
            int n = 0;
            foreach (var line in System.IO.File.ReadLines(files[i]))
            {
                n++;
                if (!line.Contains("FLAG_", StringComparison.Ordinal) && !line.Contains("VAR_", StringComparison.Ordinal)) continue;
                foreach (Match m in Token().Matches(line))
                {
                    var how = How(line, m.Value, rel);
                    if (!result.TryGetValue(m.Value, out var uses)) result[m.Value] = uses = [];
                    if (uses.Count < 400) uses.Add(new PlatinumVarFlagUse(rel, n, line.Trim(), how));
                }
            }
        }
        progress?.Report(1);
        return result;
    }

    private static string How(string line, string name, string file)
    {
        var t = line.Trim();
        if (file.EndsWith(".json", StringComparison.Ordinal))
            return t.Contains("\"hidden_flag\"", StringComparison.Ordinal) ? "hides a person" : t.Contains("\"var\"", StringComparison.Ordinal) ? "starts a trigger tile"
                : t.Contains("\"hidden_item\"", StringComparison.Ordinal) ? "hidden item" : "event data";
        if (file.EndsWith(".c", StringComparison.Ordinal) || file.EndsWith(".inc", StringComparison.Ordinal)) return "game code";
        var cmd = t.Split(' ', 2)[0];
        return cmd switch
        {
            "SetFlag" => "sets",
            "ClearFlag" => "clears",
            "SetVar" or "AddVar" or "SubVar" or "CopyVar" when t.Split(' ', 2).ElementAtOrDefault(1)?.StartsWith(name, StringComparison.Ordinal) == true => "changes",
            _ when cmd.StartsWith("GoToIf", StringComparison.Ordinal) || cmd.StartsWith("CallIf", StringComparison.Ordinal) || cmd.StartsWith("CheckFlag", StringComparison.Ordinal) => "checks",
            _ => "uses",
        };
    }

    /// <summary>Each trainer with the flag the game uses for it; Missing = the trainer is beyond the named trainer flags.</summary>
    public static List<PlatinumTrainerFlag> TrainerFlags(string root, List<PlatinumVarFlag> all)
    {
        var path = ProjectPaths.Combine(root, TrainersFile);
        if (!System.IO.File.Exists(path)) return [];
        var trainers = System.IO.File.ReadLines(path).Select(l => l.Split('=')[0].Trim()).Where(l => l.StartsWith("TRAINER_", StringComparison.Ordinal)).ToList();
        var text = System.IO.File.ReadAllText(ProjectPaths.Combine(root, File));
        int start = ValueOf(text, "TRAINER_DEFEATED_FLAGS_START");
        if (start < 0) return [];
        var byValue = all.Where(f => f.IsFlag).GroupBy(f => f.Value).ToDictionary(g => g.Key, g => g.First());
        var list = new List<PlatinumTrainerFlag>();
        for (int i = 0; i < trainers.Count; i++)
        {
            byValue.TryGetValue(start + i, out var f);
            bool own = f != null && f.Name == "FLAG_DEFEATED_" + trainers[i];
            list.Add(new PlatinumTrainerFlag(trainers[i], i, f?.Name, start + i, !own));
        }
        return list;
    }

    /// <summary>
    /// Gives trainers without their own flag one: the free flag (FLAG_UNK_/FLAG_UNUSED_, not used anywhere) at the trainer's
    /// number is renamed to FLAG_DEFEATED_TRAINER_…, and TRAINER_DEFEATED_FLAGS_END moves along. No other flag changes
    /// its number. Throws if a needed place is taken by a flag that is used.
    /// </summary>
    public static string FixTrainerFlags(string text, List<PlatinumTrainerFlag> trainers, Func<string, bool> isUsed)
    {
        var lines = text.Replace("\r\n", "\n").Split('\n').ToList();
        var all = Parse(text);
        var missing = trainers.Where(t => t.Missing).ToList();
        if (missing.Count == 0) return text;
        foreach (var t in missing)
        {
            var f = all.FirstOrDefault(x => x.Value == t.FlagValue && x.IsFlag);
            if (f == null) throw new InvalidDataException($"{t.Trainer} needs flag number {t.FlagValue}, which is beyond the end of the flags.");
            bool free = f.IsFree || f.IsUnknown;
            if (!free || isUsed(f.Name))
                throw new InvalidDataException($"{t.Trainer} needs flag number {t.FlagValue} ({f.Hex}), but that is {f.Name}, which is in use. Move that flag's uses to another free flag first.");
            var line = lines[f.Line];
            lines[f.Line] = line.Replace(f.Name, "FLAG_DEFEATED_" + t.Trainer);
        }
        int endLine = lines.FindIndex(l => l.TrimStart().StartsWith("TRAINER_DEFEATED_FLAGS_END", StringComparison.Ordinal));
        if (endLine >= 0)
        {
            var last = "FLAG_DEFEATED_" + trainers[^1].Trainer;
            int lastLine = lines.FindIndex(l => l.Split('=')[0].Trim() == last);
            lines[endLine] = $"TRAINER_DEFEATED_FLAGS_END = {last}";
            // The END marker must come right after the last trainer flag (it is an enumerator, too).
            if (lastLine > endLine)
            {
                var end = lines[endLine];
                lines.RemoveAt(endLine);
                lines.Insert(lastLine, end);
            }
        }
        return string.Join("\n", lines);
    }

    /// <summary>Renames a free flag or variable (e.g. FLAG_UNUSED_0x006D → FLAG_MY_QUEST_DONE); its number stays.</summary>
    public static string Rename(string text, PlatinumVarFlag f, string newName)
    {
        var prefix = f.IsFlag ? "FLAG_" : "VAR_";
        if (!Regex.IsMatch(newName, "^" + prefix + "[A-Z][A-Z0-9_]*$")) throw new InvalidDataException($"The name must start with {prefix} and use capital letters, digits and _.");
        if (Parse(text).Any(x => x.Name == newName)) throw new InvalidDataException($"{newName} already exists.");
        var lines = text.Replace("\r\n", "\n").Split('\n');
        if (f.Line >= lines.Length || lines[f.Line].Split('=')[0].Trim() != f.Name) throw new InvalidDataException($"{f.Name} was not found in {File} – reload the project.");
        lines[f.Line] = lines[f.Line].Replace(f.Name, newName);
        return string.Join("\n", lines);
    }

    /// <summary>Groups whose flags the game uses by number or only inside one script – "never named" is normal there.</summary>
    public static bool UsedByNumber(PlatinumVarFlag f) =>
        f.Group is "Trainers defeated" or "Hidden items" or "Map-local flags" or "Map-local variables" or "Script temporaries" or "Looks variables" or "First arrival" or "Daily";

    /// <summary>
    /// Named flags and variables that nothing names (no script, event or code): candidates for cleanup or mistakes (a flag
    /// that is set but never checked shows up with only "sets"). Free and by-number groups are left out.
    /// </summary>
    public static List<PlatinumVarFlag> NeverUsed(List<PlatinumVarFlag> all, Dictionary<string, List<PlatinumVarFlagUse>> uses) =>
        all.Where(f => !f.IsFree && !f.IsUnknown && !UsedByNumber(f) && !(uses.TryGetValue(f.Name, out var u) && u.Any(x => x.File != File))).ToList();

    /// <summary>Flags that are set somewhere but never checked (and not used by code or events otherwise).</summary>
    public static List<PlatinumVarFlag> SetButNeverChecked(List<PlatinumVarFlag> all, Dictionary<string, List<PlatinumVarFlagUse>> uses) =>
        all.Where(f => f.IsFlag && !UsedByNumber(f) && uses.TryGetValue(f.Name, out var u)
                       && u.Where(x => x.File != File).All(x => x.How is "sets" or "clears") && u.Any(x => x.How == "sets")).ToList();

    /// <summary>
    /// The next free place for a new flag or variable: an unused FLAG_UNUSED_/VAR_UNUSED_ (outside the places reserved for
    /// new trainers right after the trainer flags), else an unused FLAG_UNK_. Null if none is left.
    /// </summary>
    public static PlatinumVarFlag? NextFree(string text, bool flag, Func<string, bool> isUsed)
    {
        var all = Parse(text);
        int trainerEnd = ValueOf(text, "TRAINER_DEFEATED_FLAGS_END");
        bool Reserved(PlatinumVarFlag f) => trainerEnd >= 0 && f.Value > trainerEnd && f.Value <= trainerEnd + 64;
        // Flag 0 means "no flag" (e.g. hidden_flag 0 = always there) – never hand it out.
        var candidates = all.Where(f => f.IsFlag == flag && f.Value != 0 && (f.IsFree || f.IsUnknown) && f.Group is not ("Map-local flags" or "Daily")).ToList();
        return candidates.Where(f => f.IsFree && !Reserved(f)).Concat(candidates.Where(f => f.IsUnknown && !Reserved(f)))
            .FirstOrDefault(f => !isUsed(f.Name));
    }

    public static byte[] Encode(string text) => new UTF8Encoding(false).GetBytes(text);
}

/// <summary>
/// A new trainer for pokeplatinum: its file res/trainers/data/&lt;name&gt;.json (a copy of a similar trainer), its constant
/// in generated/trainers.txt (before MAX_TRAINERS) and its defeated flag – the build stops when there are more trainers
/// than trainer flags, so the free flag at the trainer's place is named FLAG_DEFEATED_TRAINER_….
/// </summary>
public static class PlatinumNewTrainer
{
    public static string ConstantFor(string trainerClass, string name)
    {
        var cls = trainerClass.StartsWith("TRAINER_CLASS_", StringComparison.Ordinal) ? trainerClass[14..] : trainerClass;
        var key = Regex.Replace(name.Trim().ToUpperInvariant(), "[^A-Z0-9]+", "_").Trim('_');
        return $"TRAINER_{cls}_{key}";
    }

    /// <param name="isUsed">Whether a flag name is used anywhere (then its place cannot be taken).</param>
    public static (List<PlatinumFileChange> Files, string Constant) Plan(string root, PlatinumTrainer template, string trainerClass, string name, Func<string, bool> isUsed)
    {
        name = name.Trim();
        if (name.Length == 0) throw new InvalidDataException("The trainer needs a name.");
        if (name.Length > 7) throw new InvalidDataException("Trainer names have at most 7 characters in Platinum.");
        var constant = ConstantFor(trainerClass, name);
        var listPath = ProjectPaths.Combine(root, PlatinumFlags.TrainersFile);
        var list = System.IO.File.ReadAllText(listPath).Replace("\r\n", "\n");
        var lines = list.Split('\n').ToList();
        if (lines.Any(l => l.Split('=')[0].Trim() == constant)) throw new InvalidDataException($"{constant} already exists – choose another name.");
        var stem = constant[8..].ToLowerInvariant();
        var file = $"{PlatinumProject.TrainersDir}/{stem}.json";
        if (System.IO.File.Exists(ProjectPaths.Combine(root, file))) throw new InvalidDataException($"{file} already exists.");

        int max = lines.FindIndex(l => l.Trim() == "MAX_TRAINERS");
        if (max < 0) max = lines.FindLastIndex(l => l.StartsWith("TRAINER_", StringComparison.Ordinal)) + 1;
        lines.Insert(max, constant);
        var newList = string.Join("\n", lines);

        // The defeated flag at the new trainer's place.
        var flagsText = System.IO.File.ReadAllText(ProjectPaths.Combine(root, PlatinumFlags.File));
        var all = PlatinumFlags.Parse(flagsText);
        var trainers = newList.Split('\n').Select(l => l.Split('=')[0].Trim()).Where(l => l.StartsWith("TRAINER_", StringComparison.Ordinal)).ToList();
        int start = PlatinumFlags.ValueOf(flagsText, "TRAINER_DEFEATED_FLAGS_START");
        if (start < 0) throw new InvalidDataException($"{PlatinumFlags.File} has no TRAINER_DEFEATED_FLAGS_START.");
        var byValue = all.Where(f => f.IsFlag).GroupBy(f => f.Value).ToDictionary(g => g.Key, g => g.First());
        var flagList = trainers.Select((t, i) => new PlatinumTrainerFlag(t, i, byValue.GetValueOrDefault(start + i)?.Name, start + i,
            byValue.GetValueOrDefault(start + i)?.Name != "FLAG_DEFEATED_" + t)).ToList();
        var newFlags = PlatinumFlags.FixTrainerFlags(flagsText, flagList, isUsed);

        var t = new PlatinumTrainer { File = file, Raw = template.Raw, OriginalText = "", Name = name, Class = trainerClass,
            DoubleBattle = template.DoubleBattle, Items = template.Items.ToList(), AiFlags = template.AiFlags.ToList(),
            Party = template.Party.Select(m => m.Clone()).ToList(), Messages = template.Messages.ToList() };
        var utf8 = new UTF8Encoding(false);
        return ([
            new PlatinumFileChange(file, utf8.GetBytes(PlatinumTrainers.Serialize(t)), $"new trainer {name} (copy of {template.Id})"),
            new PlatinumFileChange(PlatinumFlags.TrainersFile, utf8.GetBytes(newList), $"{constant} before MAX_TRAINERS"),
            new PlatinumFileChange(PlatinumFlags.File, utf8.GetBytes(newFlags), $"FLAG_DEFEATED_{constant} (its defeated flag)"),
        ], constant);
    }

    /// <summary>Whether a name appears in the project's scripts, events or code (a quick search for one name).</summary>
    public static bool IsReferenced(string root, string name)
    {
        foreach (var (dir, pattern) in new[] { ("res/field", "*.s"), ("res/field", "*.json"), ("src", "*.c"), ("include", "*.h") })
        {
            var full = ProjectPaths.Combine(root, dir);
            if (!Directory.Exists(full)) continue;
            foreach (var f in Directory.EnumerateFiles(full, pattern, SearchOption.AllDirectories))
                if (System.IO.File.ReadAllText(f).Contains(name, StringComparison.Ordinal)) return true;
        }
        return false;
    }
}
