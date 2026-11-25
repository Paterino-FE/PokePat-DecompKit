using System.Text;
using System.Text.RegularExpressions;

namespace TilesetEditor.Core.Trainers;

/// <summary>A designated initializer ".name = value," with absolute positions in the source text.</summary>
public sealed record CField(string Name, int LineStart, int ValueStart, int ValueEnd, int End, string Raw);

/// <summary>One "{ .species = …, … }" of a party, as found in the file.</summary>
public sealed record ParsedMon(int Open, int Close, IReadOnlyList<CField> Fields);

/// <summary>One "[TRAINER_X] = { … }," of src/data/trainers.h with positions.</summary>
public sealed record ParsedTrainer(string Id, int Start, int Open, int Close, IReadOnlyList<CField> Fields, IReadOnlyList<ParsedMon> Party, int PartyOpen, int PartyClose);

/// <summary>Editable party member. Unknown fields are kept verbatim in <see cref="Extra"/>.</summary>
public sealed class TrainerMonData
{
    public string Species { get; set; } = "SPECIES_NONE";
    public int Level { get; set; } = 5;
    public string HeldItem { get; set; } = "";
    public string Ability { get; set; } = "";
    public string Nature { get; set; } = "";
    public string Nickname { get; set; } = "";
    public bool IsShiny { get; set; }
    /// <summary>"31,31,31,31,31,31" (HP, Atk, Def, Speed, SpAtk, SpDef) or the raw expression.</summary>
    public string Ivs { get; set; } = "";
    public string Evs { get; set; } = "";
    public List<string> Moves { get; set; } = [];
    /// <summary>Fields this editor does not model, in file order (name → raw value).</summary>
    public List<KeyValuePair<string, string>> Extra { get; set; } = [];
    /// <summary>Order of the fields as found (for regeneration in the same order).</summary>
    public List<string> FieldOrder { get; set; } = [];
    public int? OriginalIndex { get; set; }
    /// <summary>Raw text of the fields as read, used again for fields whose value did not change.</summary>
    public Dictionary<string, string> Raw { get; set; } = [];
    /// <summary>The values as read (null for new members).</summary>
    public TrainerMonData? Original { get; set; }

    public TrainerMonData Clone()
    {
        var c = (TrainerMonData)MemberwiseClone();
        c.Moves = [.. Moves];
        c.Extra = [.. Extra];
        c.FieldOrder = [.. FieldOrder];
        c.Raw = new(Raw);
        return c;
    }

    public string Key => string.Join("|", Species, Level, HeldItem, Ability, Nature, Nickname, IsShiny, Ivs, Evs, string.Join(",", Moves),
        string.Join(";", Extra.Select(e => e.Key + "=" + e.Value)), string.Join(",", FieldOrder));
}

/// <summary>Editable trainer. Fields not modelled here (e.g. mugshots) stay untouched in the file.</summary>
public sealed class TrainerData
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string TrainerClass { get; set; } = "";
    public string Pic { get; set; } = "";
    public string Music { get; set; } = "";
    public bool DoubleBattle { get; set; }
    public string AiFlags { get; set; } = "";
    public List<string> Items { get; set; } = [];
    public List<TrainerMonData> Party { get; set; } = [];

    public TrainerData Clone()
    {
        var c = (TrainerData)MemberwiseClone();
        c.Items = [.. Items];
        c.Party = Party.Select(m => m.Clone()).ToList();
        return c;
    }

    public string PartySummary => Party.Count == 0 ? "(no Pokémon)" :
        string.Join(", ", Party.Select(m => $"{Pretty(m.Species, "SPECIES_")} {m.Level}"));

    public static string Pretty(string constant, string prefix)
    {
        var s = constant.StartsWith(prefix, StringComparison.Ordinal) ? constant[prefix.Length..] : constant;
        return string.Join(" ", s.Split('_', StringSplitOptions.RemoveEmptyEntries).Select(w => w.Length > 1 ? w[0] + w[1..].ToLowerInvariant() : w));
    }
}

/// <summary>
/// Reads and edits src/data/trainers.h (C syntax, COMPETITIVE_PARTY_SYNTAX FALSE). Edits replace only the block of the
/// changed trainer; inside it, unchanged fields and unchanged party members keep their exact text.
/// </summary>
public static partial class TrainerFile
{
    public const string TrainersFile = "src/data/trainers.h";
    public const string PartyFile = "src/data/trainers.party";
    public const string OpponentsFile = "include/constants/opponents.h";
    public const int PartySize = 6;
    public const int NameLength = 10;

    [GeneratedRegex(@"^[ \t]*\[(TRAINER_\w+)\][ \t]*=[ \t]*(?://[^\r\n]*)?\r?\n?[ \t]*\{", RegexOptions.Multiline)]
    private static partial Regex EntryStart();

    [GeneratedRegex(@"^(?:_|COMPOUND_STRING)\(""(.*)""\)$", RegexOptions.Singleline)]
    private static partial Regex StringValue();

    [GeneratedRegex(@"^TRAINER_PARTY_(?:IVS|EVS)\(([^)]*)\)$")]
    private static partial Regex StatsValue();

    /// <summary>Index of the brace/paren closing the one at <paramref name="open"/>, skipping strings and comments.</summary>
    public static int Match(string t, int open)
    {
        int depth = 0;
        for (int i = open; i < t.Length; i++)
        {
            char c = t[i];
            if (c == '"' || c == '\'')
            {
                for (i++; i < t.Length && t[i] != c; i++) if (t[i] == '\\') i++;
                continue;
            }
            if (c == '/' && i + 1 < t.Length && t[i + 1] == '/') { while (i < t.Length && t[i] != '\n') i++; continue; }
            if (c == '/' && i + 1 < t.Length && t[i + 1] == '*') { i = t.IndexOf("*/", i + 2, StringComparison.Ordinal); if (i < 0) return -1; i++; continue; }
            if (c is '{' or '(' or '[') depth++;
            else if (c is '}' or ')' or ']')
            {
                depth--;
                if (depth == 0) return i;
            }
        }
        return -1;
    }

    /// <summary>Fields ".name = value," directly inside the braces open..close (nested braces belong to the value).</summary>
    public static List<CField> Fields(string t, int open, int close)
    {
        var result = new List<CField>();
        int i = open + 1;
        while (i < close)
        {
            char c = t[i];
            if (char.IsWhiteSpace(c) || c == ',') { i++; continue; }
            if (c == '/' && t[i + 1] == '/') { while (i < close && t[i] != '\n') i++; continue; }
            if (c == '/' && t[i + 1] == '*') { i = t.IndexOf("*/", i + 2, StringComparison.Ordinal) + 2; continue; }
            if (c != '.') { i++; continue; }
            int nameStart = i + 1, j = nameStart;
            while (j < close && (char.IsLetterOrDigit(t[j]) || t[j] == '_')) j++;
            var name = t[nameStart..j];
            int eq = t.IndexOf('=', j);
            if (eq < 0 || eq > close) break;
            int v = eq + 1, k = v;
            while (k < close)
            {
                char d = t[k];
                if (d is '{' or '(' or '[' or '"' or '\'')
                {
                    if (d is '"' or '\'') { for (k++; k < close && t[k] != d; k++) if (t[k] == '\\') k++; k++; continue; }
                    k = Match(t, k) + 1;
                    continue;
                }
                if (d == ',') break;
                k++;
            }
            int lineStart = t.LastIndexOf('\n', i) + 1;
            int end = k < close ? k + 1 : k;
            result.Add(new CField(name, lineStart, v, k, end, t[v..k].Trim()));
            i = end;
        }
        return result;
    }

    public static List<ParsedTrainer> Parse(string text)
    {
        var result = new List<ParsedTrainer>();
        foreach (Match m in EntryStart().Matches(text))
        {
            int open = m.Index + m.Length - 1;
            int close = Match(text, open);
            if (close < 0) continue;
            var fields = Fields(text, open, close);
            var party = new List<ParsedMon>();
            int partyOpen = -1, partyClose = -1;
            var pf = fields.FirstOrDefault(f => f.Name == "party");
            if (pf != null)
            {
                // "(const struct TrainerMon[]) { … }": the array braces come after the cast.
                int castEnd = text.IndexOf(')', pf.ValueStart);
                partyOpen = text.IndexOf('{', castEnd < 0 || castEnd > pf.ValueEnd ? pf.ValueStart : castEnd);
                if (partyOpen >= 0 && partyOpen < pf.ValueEnd)
                {
                    partyClose = Match(text, partyOpen);
                    for (int i = partyOpen + 1; i < partyClose; i++)
                    {
                        if (text[i] != '{') continue;
                        int mc = Match(text, i);
                        party.Add(new ParsedMon(i, mc, Fields(text, i, mc)));
                        i = mc;
                    }
                }
            }
            result.Add(new ParsedTrainer(m.Groups[1].Value, m.Index, open, close, fields, party, partyOpen, partyClose));
        }
        return result;
    }

    private static string Unstring(string raw)
    {
        var m = StringValue().Match(raw);
        return m.Success ? m.Groups[1].Value : raw;
    }

    private static List<string> BraceList(string raw)
    {
        var t = raw.Trim();
        if (t.StartsWith('{') && t.EndsWith('}')) t = t[1..^1];
        return t.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Where(s => s.Length > 0).ToList();
    }

    private static string Stats(string raw)
    {
        var m = StatsValue().Match(raw);
        return m.Success ? string.Join(",", m.Groups[1].Value.Split(',').Select(s => s.Trim())) : raw;
    }

    public static TrainerData ToData(ParsedTrainer p)
    {
        string F(string n) => p.Fields.FirstOrDefault(f => f.Name == n)?.Raw ?? "";
        var d = new TrainerData
        {
            Id = p.Id,
            Name = Unstring(F("trainerName")),
            TrainerClass = F("trainerClass"),
            Pic = F("trainerPic"),
            Music = Regex.Replace(F("encounterMusic_gender"), @"\s+", " "),
            DoubleBattle = F("doubleBattle") is "TRUE" or "1",
            AiFlags = Regex.Replace(F("aiFlags"), @"\s+", " "),
            Items = BraceList(F("items")).Where(i => i != "ITEM_NONE").ToList(),
        };
        for (int i = 0; i < p.Party.Count; i++) d.Party.Add(ToMon(p.Party[i].Fields, i));
        return d;
    }

    private static readonly string[] MonModelled = ["species", "lvl", "heldItem", "ability", "nature", "nickname", "isShiny", "iv", "ev", "moves"];

    private static TrainerMonData ToMon(IReadOnlyList<CField> fields, int index)
    {
        string F(string n) => fields.FirstOrDefault(f => f.Name == n)?.Raw ?? "";
        var m = new TrainerMonData
        {
            Species = F("species"),
            Level = int.TryParse(F("lvl"), out var l) ? l : 0,
            HeldItem = F("heldItem") is "ITEM_NONE" ? "" : F("heldItem"),
            Ability = F("ability"),
            Nature = F("nature"),
            Nickname = Unstring(F("nickname")),
            IsShiny = F("isShiny") is "TRUE" or "1",
            Ivs = Stats(F("iv")),
            Evs = Stats(F("ev")),
            Moves = BraceList(F("moves")).Where(m => m != "MOVE_NONE").ToList(),
            Extra = fields.Where(f => !MonModelled.Contains(f.Name)).Select(f => new KeyValuePair<string, string>(f.Name, f.Raw)).ToList(),
            FieldOrder = fields.Select(f => f.Name).ToList(),
            OriginalIndex = index,
            Raw = fields.GroupBy(f => f.Name).ToDictionary(g => g.Key, g => g.First().Raw),
        };
        m.Original = m.Clone();
        return m;
    }

    // ---------------------------------------------------------------- writing

    private static readonly string[] MonOrder = ["species", "nickname", "gender", "heldItem", "ability", "ev", "iv", "lvl", "nature", "isShiny", "friendship", "dynamaxLevel", "moves"];

    private static string Stats(string text, string macro) =>
        Regex.IsMatch(text.Trim(), @"^\d+(\s*,\s*\d+){5}$") ? $"{macro}({string.Join(", ", text.Split(',').Select(s => s.Trim()))})" : text.Trim();

    /// <summary>Raw values of the modelled fields (null = field absent).</summary>
    private static Dictionary<string, string?> MonValues(TrainerMonData m, string indent)
    {
        var values = NewValues(m, indent);
        var o = m.Original;
        if (o == null) return values;
        // Unchanged fields keep their original notation (e.g. isShiny = 1, moves on one line).
        var fresh = NewValues(o, indent);
        foreach (var k in values.Keys.ToList())
            if (values[k] != null && values[k] == fresh[k] && m.Raw.TryGetValue(k, out var raw)) values[k] = raw;
        return values;
    }

    private static Dictionary<string, string?> NewValues(TrainerMonData m, string indent) => new()
    {
        ["species"] = m.Species,
        ["lvl"] = m.Level.ToString(System.Globalization.CultureInfo.InvariantCulture),
        ["heldItem"] = m.HeldItem.Length > 0 && m.HeldItem != "ITEM_NONE" ? m.HeldItem : null,
        ["ability"] = m.Ability.Length > 0 ? m.Ability : null,
        ["nature"] = m.Nature.Length > 0 ? m.Nature : null,
        ["nickname"] = m.Nickname.Length > 0 ? $"COMPOUND_STRING(\"{m.Nickname}\")" : null,
        ["isShiny"] = m.IsShiny ? "TRUE" : null,
        ["iv"] = m.Ivs.Length > 0 ? Stats(m.Ivs, "TRAINER_PARTY_IVS") : null,
        ["ev"] = m.Evs.Length > 0 ? Stats(m.Evs, "TRAINER_PARTY_EVS") : null,
        ["moves"] = m.Moves.Count > 0 ? "{\n" + string.Concat(m.Moves.Select(x => indent + "    " + x + ",\n")) + indent + "}" : null,
    };

    /// <summary>A party member in the file's layout (12-space fields inside a 12-space brace).</summary>
    public static string FormatMon(TrainerMonData m, string indent = "            ")
    {
        var values = MonValues(m, indent);
        var extra = m.Extra.ToDictionary(e => e.Key, e => e.Value);
        var order = new List<string>(m.FieldOrder);
        foreach (var name in values.Keys.Concat(extra.Keys))
        {
            if (order.Contains(name)) continue;
            int rank = Array.IndexOf(MonOrder, name);
            int at = order.FindIndex(o => Array.IndexOf(MonOrder, o) > rank);
            if (rank < 0 || at < 0) order.Add(name); else order.Insert(at, name);
        }
        var sb = new StringBuilder("{\n");
        foreach (var name in order)
        {
            string? v = values.TryGetValue(name, out var mv) ? mv : extra.GetValueOrDefault(name);
            if (v == null) continue;
            sb.Append(indent).Append('.').Append(name).Append(" = ").Append(v).Append(",\n");
        }
        sb.Append(indent).Append('}');
        return sb.ToString();
    }

    private static string FormatItems(IReadOnlyList<string> items) => "{ " + string.Join(", ", items) + " }";

    /// <summary>Returns the file text with the trainer <paramref name="updated"/> written into its existing block.</summary>
    public static string Write(string text, ParsedTrainer parsed, TrainerData original, TrainerData updated)
    {
        var edits = new List<(int Start, int End, string Text)>();
        CField? Field(string n) => parsed.Fields.FirstOrDefault(f => f.Name == n);
        string LineIndent(CField f) => text[f.LineStart..(f.LineStart + text[f.LineStart..].TakeWhile(c => c is ' ' or '\t').Count())];

        void SetField(string name, string? value, string? before)
        {
            var f = Field(name);
            if (f != null && value != null) edits.Add((f.ValueStart, f.ValueEnd, " " + value));
            else if (f != null) edits.Add((f.LineStart, NextLine(text, f.End), ""));
            else if (value != null)
            {
                var anchor = (before != null ? Field(before) : null) ?? Field("partySize") ?? parsed.Fields.Last();
                edits.Add((anchor.LineStart, anchor.LineStart, $"{LineIndent(anchor)}.{name} = {value},\n"));
            }
        }

        if (updated.Name != original.Name) SetField("trainerName", $"_(\"{updated.Name}\")", "trainerClass");
        if (updated.TrainerClass != original.TrainerClass) SetField("trainerClass", updated.TrainerClass, "trainerPic");
        if (updated.Pic != original.Pic) SetField("trainerPic", updated.Pic, "encounterMusic_gender");
        if (updated.Music != original.Music) SetField("encounterMusic_gender", updated.Music, "doubleBattle");
        if (!updated.Items.SequenceEqual(original.Items)) SetField("items", updated.Items.Count > 0 ? FormatItems(updated.Items) : null, "doubleBattle");
        if (updated.DoubleBattle != original.DoubleBattle) SetField("doubleBattle", updated.DoubleBattle ? "TRUE" : "FALSE", "aiFlags");
        if (updated.AiFlags != original.AiFlags) SetField("aiFlags", updated.AiFlags.Length > 0 ? updated.AiFlags : null, "partySize");
        if (updated.Party.Count != original.Party.Count) SetField("partySize", updated.Party.Count.ToString(System.Globalization.CultureInfo.InvariantCulture), "party");

        bool partyChanged = updated.Party.Count != original.Party.Count
                            || updated.Party.Where((m, i) => m.OriginalIndex != i || m.Key != original.Party[i].Key).Any();
        if (partyChanged)
        {
            if (parsed.PartyOpen < 0) throw new InvalidOperationException($"{parsed.Id}: the party array was not found.");
            var sb = new StringBuilder("\n");
            foreach (var m in updated.Party)
            {
                bool unchanged = m.OriginalIndex is { } oi && oi < original.Party.Count && original.Party[oi].Key == m.Key;
                var monText = unchanged ? text[parsed.Party[m.OriginalIndex!.Value].Open..(parsed.Party[m.OriginalIndex!.Value].Close + 1)] : FormatMon(m);
                sb.Append("            ").Append(monText).Append(",\n");
            }
            sb.Append("        ");
            edits.Add((parsed.PartyOpen + 1, parsed.PartyClose, sb.ToString()));
        }

        var result = new StringBuilder(text);
        foreach (var (start, end, t) in edits.OrderByDescending(e => e.Start).ThenByDescending(e => e.End))
        {
            result.Remove(start, end - start);
            result.Insert(start, t);
        }
        return result.ToString();
    }

    private static int NextLine(string t, int i)
    {
        int n = t.IndexOf('\n', i);
        return n < 0 ? t.Length : n + 1;
    }

    /// <summary>A complete new "[TRAINER_X] = { … }," block in the file's layout.</summary>
    public static string FormatTrainer(TrainerData d)
    {
        var sb = new StringBuilder();
        sb.Append("    [").Append(d.Id).Append("] =\n    {\n");
        sb.Append("        .trainerName = _(\"").Append(d.Name).Append("\"),\n");
        sb.Append("        .trainerClass = ").Append(d.TrainerClass).Append(",\n");
        sb.Append("        .trainerPic = ").Append(d.Pic).Append(",\n");
        sb.Append("        .encounterMusic_gender = \n            ").Append(d.Music.Length > 0 ? d.Music : "TRAINER_ENCOUNTER_MUSIC_MALE").Append(",\n");
        if (d.Items.Count > 0) sb.Append("        .items = ").Append(FormatItems(d.Items)).Append(",\n");
        sb.Append("        .doubleBattle = ").Append(d.DoubleBattle ? "TRUE" : "FALSE").Append(",\n");
        if (d.AiFlags.Length > 0) sb.Append("        .aiFlags = ").Append(d.AiFlags).Append(",\n");
        sb.Append("        .partySize = ").Append(d.Party.Count).Append(",\n");
        sb.Append("        .party = (const struct TrainerMon[])\n        {\n");
        foreach (var m in d.Party) sb.Append("            ").Append(FormatMon(m)).Append(",\n");
        sb.Append("        },\n    },");
        return sb.ToString();
    }

    /// <summary>Default fields for a new party member, copied from the first member in the file (gender, IVs, nature, dynamax level).</summary>
    public static TrainerMonData NewMon(IReadOnlyList<ParsedTrainer> all, string species, int level)
    {
        var template = all.SelectMany(t => t.Party).FirstOrDefault();
        var m = template != null ? ToMon(template.Fields, 0) : new TrainerMonData { FieldOrder = ["species", "lvl"] };
        m.Species = species;
        m.Level = level;
        m.HeldItem = "";
        m.Ability = "";
        m.Nickname = "";
        m.IsShiny = false;
        m.Moves = [];
        m.Evs = "";
        m.OriginalIndex = null;
        return m;
    }

    /// <summary>Appends a trainer block after the last entry (keeps the file's final newline state).</summary>
    public static string Append(string text, IReadOnlyList<ParsedTrainer> all, TrainerData d)
    {
        int insertAt = all.Count > 0 ? all.Max(t => t.Close) + 1 : text.Length;
        if (insertAt < text.Length && text[insertAt] == ',') insertAt++;
        return text[..insertAt] + "\n" + FormatTrainer(d) + text[insertAt..];
    }

    // ---------------------------------------------------------------- constants

    [GeneratedRegex(@"^[ \t]*#define[ \t]+(TRAINER_\w+)([ \t]+)(\d+)[ \t]*(?://.*)?$", RegexOptions.Multiline)]
    private static partial Regex TrainerDefine();


    public sealed record IdSlot(int Id, bool RaisesCount, string? Problem);

    /// <summary>Free id for a new trainer: a gap below TRAINERS_COUNT first, otherwise TRAINERS_COUNT (if below MAX_TRAINERS_COUNT).</summary>
    public static IdSlot FindFreeId(string opponents, IReadOnlyCollection<string> trainersInFile)
    {
        var used = TrainerDefine().Matches(opponents).Select(m => int.Parse(m.Groups[3].Value)).ToHashSet();
        var countDefine = NumericDefine(opponents, "TRAINERS_COUNT");
        if (countDefine == null) return new IdSlot(0, false, "TRAINERS_COUNT was not found in " + OpponentsFile + ".");
        int count = int.Parse(countDefine.Groups[2].Value);
        for (int id = 1; id < count; id++) if (!used.Contains(id)) return new IdSlot(id, false, null);
        int max = NumericDefine(opponents, "MAX_TRAINERS_COUNT") is { } maxDefine ? int.Parse(maxDefine.Groups[2].Value) : int.MaxValue;
        if (count >= max)
            return new IdSlot(0, false, $"All {max} trainer slots are in use (MAX_TRAINERS_COUNT). \"More trainer slots…\" raises the limit (old save files stop working).");
        return new IdSlot(count, true, null);
    }

    /// <summary>Adds "#define TRAINER_X n" after the define with the next lower id, aligned like its neighbours; raises TRAINERS_COUNT if needed.</summary>
    public static string AddConstant(string opponents, string name, IdSlot slot)
    {
        if (Regex.IsMatch(opponents, @"\b" + Regex.Escape(name) + @"\b")) throw new InvalidOperationException($"{name} already exists.");
        var defines = TrainerDefine().Matches(opponents).Where(m => !m.Groups[1].Value.Contains("COUNT")).ToList();
        var before = defines.Where(m => int.Parse(m.Groups[3].Value) < slot.Id).OrderBy(m => int.Parse(m.Groups[3].Value)).ThenBy(m => m.Index).LastOrDefault()
                     ?? throw new InvalidOperationException("No trainer constant found to insert after.");
        int column = before.Groups[1].Length + before.Groups[2].Length;
        var head = "#define " + name;
        var nameCol = ("#define " + before.Groups[1].Value).Length + before.Groups[2].Length;
        string line = (head.Length < nameCol ? head.PadRight(nameCol) : head + " ") + slot.Id;
        _ = column;
        int eol = opponents.IndexOf('\n', before.Index);
        string nl = eol > 0 && opponents[eol - 1] == '\r' ? "\r\n" : "\n";
        int insertAt = eol < 0 ? opponents.Length : eol + 1;
        var result = opponents[..insertAt] + line + nl + opponents[insertAt..];
        if (slot.RaisesCount)
        {
            var m = NumericDefine(result, "TRAINERS_COUNT") ?? throw new InvalidOperationException("TRAINERS_COUNT was not found in " + OpponentsFile + ".");
            result = result[..m.Groups[2].Index] + (slot.Id + 1) + result[(m.Groups[2].Index + m.Groups[2].Length)..];
        }
        return result;
    }

    [GeneratedRegex(@"^([ \t]*#define[ \t]+(\w+)[ \t]+)(\w+)[ \t]*(?://[^\n]*)?\r?$", RegexOptions.Multiline)]
    private static partial Regex AnyDefine();

    /// <summary>MAX_TRAINERS_COUNT (the number behind it, e.g. MAX_TRAINERS_COUNT_EMERALD in expansion 1.13+), or null.</summary>
    public static int? MaxTrainers(string opponents) => NumericDefine(opponents, "MAX_TRAINERS_COUNT") is { } m ? int.Parse(m.Groups[2].Value) : null;

    /// <summary>
    /// Raises MAX_TRAINERS_COUNT by <paramref name="by"/> (rounded up to a multiple of 8 so the flag bytes stay whole). Every
    /// trainer has a "defeated" flag right after TRAINER_FLAGS_START, and the system and daily flags follow relative to it,
    /// so they move along and FLAGS_COUNT grows: the save block gets by/8 bytes bigger and old save files no longer match.
    /// </summary>
    public static (string Text, int OldMax, int NewMax) RaiseMaxTrainers(string opponents, int by)
    {
        var m = NumericDefine(opponents, "MAX_TRAINERS_COUNT") ?? throw new InvalidOperationException($"MAX_TRAINERS_COUNT was not found in {OpponentsFile}.");
        int old = int.Parse(m.Groups[2].Value);
        int add = (by + 7) / 8 * 8;
        int now = old + add;
        return (opponents[..m.Groups[2].Index] + now + opponents[(m.Groups[2].Index + m.Groups[2].Length)..], old, now);
    }

    [GeneratedRegex(@"\G([ \t]*#define[ \t]+\w+[ \t]+)(\d+)")]
    private static partial Regex NumberAt();

    /// <summary>
    /// The "#define NAME number" line that <paramref name="name"/> stands for (group 2 = the number). expansion 1.13+ writes
    /// "#define TRAINERS_COUNT TRAINERS_COUNT_EMERALD" inside "#if IS_FRLG … #else" with "#define TRAINERS_COUNT_EMERALD 855"
    /// further up: aliases are followed, and of several definitions the first one that leads to a number is used.
    /// </summary>
    private static Match? NumericDefine(string text, string name, int depth = 0)
    {
        if (depth > 8) return null;
        foreach (Match m in AnyDefine().Matches(text))
        {
            if (m.Groups[2].Value != name) continue;
            var value = m.Groups[3].Value;
            if (Regex.IsMatch(value, @"^\d+$")) return NumberAt().Match(text, m.Index); // group 2 = the number
            if (NumericDefine(text, value, depth + 1) is { } target) return target;
        }
        return null;
    }
}
