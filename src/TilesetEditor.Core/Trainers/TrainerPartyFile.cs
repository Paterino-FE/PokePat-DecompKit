using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace TilesetEditor.Core.Trainers;

/// <summary>
/// One line of trainers.party. Start..End is the content without the line break, Next the start of the following line,
/// PrevEnd the content end of the line before (used to delete a line together with the break in front of it).
/// For attributes Key is the attribute name ("Level", "Nature", …) and Value* the trimmed value; move lines have Key "-".
/// </summary>
public sealed record PartyLine(int Start, int End, int Next, int PrevEnd, string Key, int ValueStart, int ValueEnd, bool NatureSuffix = false);

/// <summary>A token of a Pokémon's first line ("Nickname (Species) (M) @ Item").</summary>
public sealed record PartyToken(int Start, int End);

/// <summary>One Pokémon block (lines up to the next empty line).</summary>
public sealed class PartyMon
{
    public required PartyLine HeaderLine { get; init; }
    public PartyToken? Nickname { get; init; }
    public required PartyToken Species { get; init; }
    public PartyToken? Gender { get; init; }
    public PartyToken? Item { get; init; }
    /// <summary>Range of the header tokens (without a trailing comment).</summary>
    public int TokensStart { get; init; }
    public int TokensEnd { get; init; }
    public List<PartyLine> Lines { get; } = [];
    public IEnumerable<PartyLine> Attributes => Lines.Skip(1).Where(l => l.Key.Length > 0 && l.Key != "-");
    public IEnumerable<PartyLine> Moves => Lines.Where(l => l.Key == "-");
    public int Start => HeaderLine.Start;
    public int End => Lines[^1].End;
}

/// <summary>One "=== TRAINER_X ===" block with positions.</summary>
public sealed class PartyTrainer
{
    public required string Id { get; init; }
    public required PartyLine Section { get; init; }
    public List<PartyLine> Header { get; } = [];
    public List<PartyMon> Party { get; } = [];
    /// <summary>Start of the next section (or end of file).</summary>
    public int BlockEnd { get; set; }
    /// <summary>Set when the block could not be understood completely; such a trainer is shown but not written.</summary>
    public string? Problem { get; set; }
    public int Start => Section.Start;
}

/// <summary>A parsed trainers.party with the file-wide settings needed to convert names.</summary>
public sealed class PartyDocument
{
    public required string Text { get; init; }
    /// <summary>Text with comments blanked out (same positions).</summary>
    public string Masked { get; init; } = "";
    public List<PartyTrainer> Trainers { get; } = [];
    public int DefaultLevel { get; set; } = 100;
    /// <summary>HP, Atk, Def, SpA, SpD, Spe.</summary>
    public int[] DefaultIvs { get; set; } = [31, 31, 31, 31, 31, 31];
    public string Newline { get; init; } = "\n";
    /// <summary>Constant → the readable name the file uses for it (first occurrence).</summary>
    internal Dictionary<string, string> Names { get; } = new(StringComparer.Ordinal);
    /// <summary>Per prefix: (readable names used, constants used).</summary>
    internal Dictionary<string, (int Readable, int Constant)> Style { get; } = new(StringComparer.Ordinal);
}

/// <summary>
/// Reads and edits src/data/trainers.party (competitive syntax, COMPETITIVE_PARTY_SYNTAX TRUE), following the rules of
/// tools/trainerproc. Values are converted to the same constants the C format uses (TrainerData / TrainerMonData).
/// Writing changes only the lines of the edited trainer whose values changed; everything else keeps its exact text.
/// </summary>
public static partial class TrainerPartyFile
{
    public const string FemaleFlag = "F_TRAINER_FEMALE";
    public const int MaxNickname = 12;

    private static readonly string[] HeaderOrder = ["Name", "Class", "Pic", "Gender", "Music", "Items", "Double Battle", "AI", "Mugshot", "Starting Status"];
    private static readonly string[] MonOrder = ["Level", "Ability", "IVs", "EVs", "Nature", "Shiny", "Ball", "Happiness", "Dynamax Level", "Gigantamax", "Tera Type"];
    private static readonly string[] MonModelled = ["Level", "Ability", "IVs", "EVs", "Nature", "Shiny"];
    private static readonly string[] GenderedSpecies = ["Basculegion", "Indeedee", "Oinkologne", "Meowstic", "Nidoran"];

    // Forms that trainerproc turns into "base species @ item" when no item is given.
    private static readonly Dictionary<string, (string Species, string Item)> ItemedSpecies = BuildItemed();

    private static Dictionary<string, (string, string)> BuildItemed()
    {
        var d = new Dictionary<string, (string, string)>(StringComparer.Ordinal);
        foreach (var (t, item) in new[] { ("Bug", "Insect Plate"), ("Dark", "Dread Plate"), ("Dragon", "Draco Plate"), ("Electric", "Zap Plate"), ("Fairy", "Pixie Plate"),
                     ("Fighting", "Fist Plate"), ("Fire", "Flame Plate"), ("Flying", "Sky Plate"), ("Ghost", "Spooky Plate"), ("Grass", "Meadow Plate"), ("Ground", "Earth Plate"),
                     ("Ice", "Icicle Plate"), ("Poison", "Toxic Plate"), ("Psychic", "Mind Plate"), ("Rock", "Stone Plate"), ("Steel", "Iron Plate"), ("Water", "Splash Plate") })
        {
            d["Arceus-" + t] = ("Arceus", item);
            d["Silvally-" + t] = ("Silvally", t + " Memory");
        }
        foreach (var t in new[] { "Burn", "Chill", "Douse", "Shock" }) d["Genesect-" + t] = ("Genesect", t + " Drive");
        foreach (var t in new[] { "Cornerstone", "Hearthflame", "Wellspring" }) d["Ogerpon-" + t] = ("Ogerpon", t + " Mask");
        return d;
    }

    [GeneratedRegex(@"^===[ \t]*([A-Za-z0-9_]+)[ \t]*===[ \t]*$")]
    private static partial Regex SectionLine();

    [GeneratedRegex(@"^([A-Za-z_][A-Za-z0-9_ \-]*?)[ \t]*:[ \t]*(.*?)[ \t]*$")]
    private static partial Regex AttributeLine();

    [GeneratedRegex(@"^([A-Za-z0-9_]+)[ \t]*Nature[ \t]*$")]
    private static partial Regex NatureSuffixLine();

    [GeneratedRegex(@"^-[ \t]*(.*?)[ \t]*$")]
    private static partial Regex MoveLine();

    [GeneratedRegex(@"^#pragma[ \t]+trainerproc[ \t]+(ivs|level)[ \t]+(.*?)[ \t]*$")]
    private static partial Regex Pragma();

    // ---------------------------------------------------------------- reading

    /// <summary>Replaces /* */ comments by spaces (line breaks kept), so positions stay the same.</summary>
    public static string Mask(string t)
    {
        var c = t.ToCharArray();
        for (int i = 0; i + 1 < c.Length; i++)
        {
            if (c[i] != '/' || c[i + 1] != '*') continue;
            int e = t.IndexOf("*/", i + 2, StringComparison.Ordinal);
            int stop = e < 0 ? c.Length : e + 2;
            for (int k = i; k < stop; k++) if (c[k] is not ('\n' or '\r')) c[k] = ' ';
            i = stop - 1;
        }
        return new string(c);
    }

    private readonly record struct RawLine(int Start, int End, int Next);

    private static List<RawLine> Lines(string t)
    {
        var lines = new List<RawLine>();
        int i = 0;
        while (i < t.Length)
        {
            int nl = t.IndexOf('\n', i);
            if (nl < 0) { lines.Add(new RawLine(i, t.Length, t.Length)); break; }
            int end = nl > i && t[nl - 1] == '\r' ? nl - 1 : nl;
            lines.Add(new RawLine(i, end, nl + 1));
            i = nl + 1;
        }
        return lines;
    }

    public static PartyDocument Parse(string text)
    {
        var masked = Mask(text);
        var doc = new PartyDocument { Text = text, Masked = masked, Newline = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n" };
        var lines = Lines(masked);
        string Content(RawLine l) => masked[l.Start..l.End];
        bool Blank(RawLine l) { var s = Content(l).Trim(); return s.Length == 0 || (s.StartsWith('#') && !s.StartsWith("#pragma", StringComparison.Ordinal)); }

        int n = 0;
        // Pragmas before the first trainer.
        for (; n < lines.Count && !SectionLine().IsMatch(Content(lines[n])); n++)
        {
            var pm = Pragma().Match(Content(lines[n]).Trim());
            if (!pm.Success) continue;
            if (pm.Groups[1].Value == "level" && int.TryParse(pm.Groups[2].Value, out var lvl)) doc.DefaultLevel = lvl;
            if (pm.Groups[1].Value == "ivs" && ParseStats(pm.Groups[2].Value, [.. doc.DefaultIvs]) is { } ivs) doc.DefaultIvs = ivs;
        }

        PartyLine Make(int index, string key = "", int vs = 0, int ve = 0, bool suffix = false)
        {
            var l = lines[index];
            int prevEnd = index > 0 ? lines[index - 1].End : l.Start;
            return new PartyLine(l.Start, l.End, l.Next, prevEnd, key, vs, ve, suffix);
        }

        PartyLine? Attribute(int index, bool mon)
        {
            var l = lines[index];
            var s = Content(l);
            if (mon)
            {
                var mv = MoveLine().Match(s);
                if (mv.Success) return Make(index, "-", l.Start + mv.Groups[1].Index, l.Start + mv.Groups[1].Index + mv.Groups[1].Length);
                var ns = NatureSuffixLine().Match(s);
                if (ns.Success) return Make(index, "Nature", l.Start + ns.Groups[1].Index, l.Start + ns.Groups[1].Index + ns.Groups[1].Length, true);
            }
            var m = AttributeLine().Match(s);
            if (!m.Success) return null;
            return Make(index, m.Groups[1].Value, l.Start + m.Groups[2].Index, l.Start + m.Groups[2].Index + m.Groups[2].Length);
        }

        while (n < lines.Count)
        {
            var sm = SectionLine().Match(Content(lines[n]));
            if (!sm.Success) { n++; continue; }
            var tr = new PartyTrainer { Id = sm.Groups[1].Value, Section = Make(n) };
            doc.Trainers.Add(tr);
            n++;
            int blockEnd = n;
            while (blockEnd < lines.Count && !SectionLine().IsMatch(Content(lines[blockEnd]))) blockEnd++;
            tr.BlockEnd = blockEnd < lines.Count ? lines[blockEnd].Start : text.Length;

            for (; n < blockEnd && !Blank(lines[n]); n++)
            {
                var a = Attribute(n, false);
                if (a == null) break;
                tr.Header.Add(a);
            }
            while (n < blockEnd)
            {
                if (Blank(lines[n])) { n++; continue; }
                int groupEnd = n;
                while (groupEnd < blockEnd && !Blank(lines[groupEnd])) groupEnd++;
                var header = ParseMonHeader(masked, lines[n].Start, lines[n].End);
                if (header == null)
                {
                    tr.Problem ??= $"{tr.Id}: line \"{text[lines[n].Start..lines[n].End].Trim()}\" is not a Pokémon.";
                    n = groupEnd;
                    continue;
                }
                var mon = new PartyMon
                {
                    HeaderLine = Make(n), Nickname = header.Value.Nick, Species = header.Value.Species, Gender = header.Value.Gender, Item = header.Value.Item,
                    TokensStart = header.Value.TokensStart, TokensEnd = header.Value.TokensEnd,
                };
                mon.Lines.Add(mon.HeaderLine);
                for (int k = n + 1; k < groupEnd; k++)
                {
                    var a = Attribute(k, true);
                    if (a == null) tr.Problem ??= $"{tr.Id}: line \"{text[lines[k].Start..lines[k].End].Trim()}\" is not understood.";
                    mon.Lines.Add(a ?? Make(k));
                }
                tr.Party.Add(mon);
                n = groupEnd;
            }
            if (tr.Party.Count > TrainerFile.PartySize) tr.Problem ??= $"{tr.Id}: more than {TrainerFile.PartySize} Pokémon.";
        }

        // Names the file uses, for writing changed values in the same style.
        foreach (var t in doc.Trainers)
        {
            foreach (var h in t.Header)
            {
                var v = masked[h.ValueStart..h.ValueEnd];
                switch (h.Key)
                {
                    case "Class": Note(doc, "TRAINER_CLASS", v); break;
                    case "Pic": Note(doc, PicPrefix(t.Id), v); break;
                    case "Music": Note(doc, "TRAINER_ENCOUNTER_MUSIC", v); break;
                    case "Items": foreach (var x in v.Split('/', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)) Note(doc, "ITEM", x); break;
                    case "AI": foreach (var x in v.Split('/', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)) Note(doc, "AI_FLAG", x); break;
                }
            }
            foreach (var m in t.Party)
            {
                var sp = Tok(masked, m.Species);
                if (!ItemedSpecies.ContainsKey(sp) && !(m.Gender != null && GenderedSpecies.Contains(sp))) Note(doc, "SPECIES", sp);
                if (m.Item != null) Note(doc, "ITEM", Tok(masked, m.Item));
                foreach (var a in m.Lines.Skip(1))
                {
                    var v = masked[a.ValueStart..a.ValueEnd];
                    switch (a.Key)
                    {
                        case "-": Note(doc, "MOVE", v); break;
                        case "Ability": Note(doc, "ABILITY", v); break;
                        case "Nature": Note(doc, "NATURE", v); break;
                    }
                }
            }
        }
        return doc;
    }

    private static string Tok(string t, PartyToken? k) => k == null ? "" : t[k.Start..k.End];

    private static void Note(PartyDocument doc, string prefix, string text)
    {
        text = text.Trim();
        if (text.Length == 0) return;
        bool constant = IsConstant(text, prefix);
        var (r, c) = doc.Style.GetValueOrDefault(prefix);
        doc.Style[prefix] = constant ? (r, c + 1) : (r + 1, c);
        if (!constant) doc.Names.TryAdd(prefix == "SPECIES" ? SpeciesConstant(text) : Constant(prefix, text), text);
    }

    private readonly record struct MonHeader(PartyToken? Nick, PartyToken Species, PartyToken? Gender, PartyToken? Item, int TokensStart, int TokensEnd);

    private static bool SpeciesChar(char c) => c is (>= 'A' and <= 'Z') or (>= 'a' and <= 'z') or (>= '0' and <= '9') or '_' or '-' or ' ' or ':' or '.' or '\'' || c >= 0x80;
    private static bool HumanChar(char c, bool first) => c is (>= 'A' and <= 'Z') or (>= 'a' and <= 'z') or '_' or '-' or ' ' || (!first && c is >= '0' and <= '9');

    /// <summary>Like trainerproc's match_*_identifier: greedy, trailing spaces not part of the token.</summary>
    private static PartyToken? Ident(string t, ref int i, int end, bool species)
    {
        int begin = i, j = i, last = i;
        while (j < end && (species ? SpeciesChar(t[j]) : HumanChar(t[j], j == begin)))
        {
            j++;
            if (t[j - 1] != ' ') last = j;
        }
        if (last == begin) return null;
        i = last;
        return new PartyToken(begin, last);
    }

    private static void Ws(string t, ref int i, int end) { while (i < end && t[i] is ' ' or '\t') i++; }

    private static MonHeader? ParseMonHeader(string t, int start, int end)
    {
        int i = start;
        var first = Ident(t, ref i, end, true);
        if (first == null) return null;
        PartyToken? nick = null, gender = null, item = null;
        PartyToken species = first;
        Ws(t, ref i, end);
        if (i < end && t[i] == '(')
        {
            i++;
            var second = Ident(t, ref i, end, true);
            if (second == null || i >= end || t[i] != ')') return null;
            i++;
            Ws(t, ref i, end);
            if (i < end && t[i] == '(')
            {
                i++;
                var third = Ident(t, ref i, end, false);
                if (third == null || i >= end || t[i] != ')') return null;
                i++;
                (nick, species, gender) = (first, second, third);
            }
            else if (second.End - second.Start == 1) gender = second;
            else (nick, species) = (first, second);
        }
        Ws(t, ref i, end);
        if (i < end && t[i] == '@')
        {
            i++;
            Ws(t, ref i, end);
            item = Ident(t, ref i, end, false);
            if (item == null) return null;
        }
        int tokensEnd = i;
        Ws(t, ref i, end);
        if (i != end) return null;
        return new MonHeader(nick, species, gender, item, start, tokensEnd);
    }

    /// <summary>"0 HP / 31 Atk / …" applied to <paramref name="values"/> (HP, Atk, Def, SpA, SpD, Spe); null if invalid.</summary>
    public static int[]? ParseStats(string text, int[] values)
    {
        foreach (var part in text.Split('/'))
        {
            var m = Regex.Match(part.Trim(), @"^(\d+)[ \t]*([A-Za-z]+)$");
            if (!m.Success) return null;
            int idx = Array.IndexOf(StatNames, m.Groups[2].Value);
            if (idx < 0) return null;
            values[idx] = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
        }
        return values;
    }

    private static readonly string[] StatNames = ["HP", "Atk", "Def", "SpA", "SpD", "Spe"];

    /// <summary>HP, Atk, Def, SpA, SpD, Spe → the model's "hp,atk,def,speed,spatk,spdef".</summary>
    private static string ToModelStats(int[] s) => string.Join(",", new[] { s[0], s[1], s[2], s[5], s[3], s[4] }.Select(v => v.ToString(CultureInfo.InvariantCulture)));

    private static int[]? FromModelStats(string model)
    {
        var p = model.Split(',', StringSplitOptions.TrimEntries);
        if (p.Length != 6 || p.Any(x => !int.TryParse(x, NumberStyles.None, CultureInfo.InvariantCulture, out _))) return null;
        var v = p.Select(x => int.Parse(x, CultureInfo.InvariantCulture)).ToArray();
        return [v[0], v[1], v[2], v[4], v[5], v[3]];
    }

    private static string FormatStats(int[] s, bool skipZero) =>
        string.Join(" / ", Enumerable.Range(0, 6).Where(i => !skipZero || s[i] != 0).Select(i => $"{s[i].ToString(CultureInfo.InvariantCulture)} {StatNames[i]}"));

    // ---------------------------------------------------------------- name ↔ constant (trainerproc rules)

    private static bool IsConstant(string s, string prefix) => s.Length > prefix.Length && s.StartsWith(prefix, StringComparison.Ordinal) && s[prefix.Length] == '_';

    /// <summary>trainerproc's fprint_constant: "Poke Ball" → ITEM_POKE_BALL; constants pass through.</summary>
    public static string Constant(string prefix, string s)
    {
        s = s.Trim();
        var sb = new StringBuilder();
        if (!IsConstant(s, prefix)) sb.Append(prefix).Append('_');
        if (s.Length == 0) return sb.Append("NONE").ToString();
        foreach (var c in s)
            sb.Append(c is (>= 'A' and <= 'Z') or (>= '0' and <= '9') ? c : c is >= 'a' and <= 'z' ? char.ToUpperInvariant(c) : '_');
        return sb.ToString();
    }

    /// <summary>trainerproc's fprint_species: "Mr. Mime" → SPECIES_MR_MIME, "Farfetch’d" → SPECIES_FARFETCHD, "Nidoran♀" → SPECIES_NIDORAN_F.</summary>
    public static string SpeciesConstant(string s)
    {
        s = s.Trim();
        var sb = new StringBuilder();
        if (!IsConstant(s, "SPECIES")) sb.Append("SPECIES_");
        if (s.Length == 0) return sb.Append("NONE").ToString();
        bool underscore = false;
        foreach (var c in s)
        {
            if (c is (>= 'A' and <= 'Z') or (>= '0' and <= '9') or (>= 'a' and <= 'z') or 'é')
            {
                if (underscore) sb.Append('_');
                underscore = false;
                sb.Append(c == 'é' ? 'E' : char.ToUpperInvariant(c));
            }
            else if (c is '\'' or '%' or '’') { }
            else if (c == '♂') { underscore = false; sb.Append("_M"); }
            else if (c == '♀') { underscore = false; sb.Append("_F"); }
            else underscore = true;
        }
        return sb.ToString();
    }

    private static string PicPrefix(string id) => id.StartsWith("PARTNER_", StringComparison.Ordinal) ? "TRAINER_BACK_PIC" : "TRAINER_PIC";

    private enum Ctx { Line, Human, Species, Move }

    private static bool ValidIn(string s, Ctx ctx) => ctx switch
    {
        Ctx.Human => Regex.IsMatch(s, @"^[A-Za-z_\-][A-Za-z0-9_\- ]*$") && !s.EndsWith(' '),
        Ctx.Species => s.Length > 1 && s.All(SpeciesChar) && !s.StartsWith(' ') && !s.EndsWith(' '),
        Ctx.Move => Regex.IsMatch(s, @"^[A-Za-z0-9_\-,][A-Za-z0-9_\-, ]*$") && !s.EndsWith(' '),
        _ => s.Length > 0 && !s.Contains('\n') && !s.Contains("/*", StringComparison.Ordinal),
    };

    /// <summary>
    /// The text to write for a constant: the readable name the file already uses for it, otherwise the title-cased
    /// constant ("ITEM_POKE_BALL" → "Poke Ball") — only if trainerproc turns it back into the same constant; else the constant.
    /// </summary>
    private static string Readable(PartyDocument doc, string prefix, string constant, Ctx ctx)
    {
        constant = constant.Trim();
        if (!IsConstant(constant, prefix)) return constant;
        var (r, c) = doc.Style.GetValueOrDefault(prefix);
        if (c > r) return constant;
        string Back(string s) => prefix == "SPECIES" ? SpeciesConstant(s) : Constant(prefix, s);
        foreach (var candidate in new[] { doc.Names.GetValueOrDefault(constant), TitleCase(constant[(prefix.Length + 1)..]) })
            if (candidate != null && Back(candidate) == constant && ValidIn(candidate, ctx)) return candidate;
        return constant;
    }

    private static string? TitleCase(string tail)
    {
        var words = tail.Split('_');
        if (words.Any(w => w.Length == 0 || !w.All(ch => ch is (>= 'A' and <= 'Z') or (>= '0' and <= '9')))) return null;
        return string.Join(" ", words.Select(w => w[0] + w[1..].ToLowerInvariant()));
    }

    // ---------------------------------------------------------------- to the model

    /// <summary>Encounter music in the model's form: "[F_TRAINER_FEMALE | ]TRAINER_ENCOUNTER_MUSIC_X" ("" when neither is set).</summary>
    private static string ModelMusic(bool female, string music) =>
        female ? FemaleFlag + " | " + (music.Length > 0 ? music : "0") : music;

    private static (bool Female, string Music) SplitMusic(string model)
    {
        bool female = false;
        string music = "";
        foreach (var part in model.Split('|', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            if (part == FemaleFlag) female = true;
            else if (part == "0") { }
            else if (music.Length == 0) music = part;
            else throw new InvalidOperationException($"Encounter music \"{model}\" cannot be written in the competitive syntax (one music constant, optionally with {FemaleFlag}).");
        }
        return (female, music);
    }

    public static TrainerData ToData(PartyDocument doc, PartyTrainer p)
    {
        var t = doc.Masked;
        string? H(string key) => p.Header.FirstOrDefault(h => h.Key == key) is { } l ? t[l.ValueStart..l.ValueEnd] : null;
        List<string> List(string? v, string prefix) => v == null ? [] :
            v.Split('/', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Select(x => Constant(prefix, x)).ToList();
        var music = H("Music") is { Length: > 0 } mu ? Constant("TRAINER_ENCOUNTER_MUSIC", mu) : "";
        var d = new TrainerData
        {
            Id = p.Id,
            Name = H("Name") ?? "",
            TrainerClass = H("Class") is { Length: > 0 } c ? Constant("TRAINER_CLASS", c) : "TRAINER_CLASS_PKMN_TRAINER_1",
            Pic = H("Pic") is { Length: > 0 } pic ? Constant(PicPrefix(p.Id), pic) : "",
            Music = ModelMusic(H("Gender") is "F" or "Female", music),
            DoubleBattle = H("Double Battle") == "Yes",
            AiFlags = string.Join(" | ", List(H("AI"), "AI_FLAG")),
            Items = List(H("Items"), "ITEM").Where(i => i != "ITEM_NONE").ToList(),
        };
        for (int i = 0; i < p.Party.Count; i++) d.Party.Add(ToMon(doc, t, p.Party[i], i));
        return d;
    }

    private static TrainerMonData ToMon(PartyDocument doc, string t, PartyMon pm, int index)
    {
        var speciesText = Tok(t, pm.Species);
        var genderText = Tok(t, pm.Gender);
        var itemText = Tok(t, pm.Item);
        if (genderText.Length > 0 && GenderedSpecies.Contains(speciesText))
        {
            speciesText += genderText is "M" or "Male" ? "-M" : genderText is "F" or "Female" ? "-F" : "";
            genderText = "";
        }
        if (itemText.Length == 0 && ItemedSpecies.TryGetValue(speciesText, out var form)) (speciesText, itemText) = form;

        string? A(string key) => pm.Attributes.FirstOrDefault(a => a.Key == key) is { } l ? t[l.ValueStart..l.ValueEnd] : null;
        var m = new TrainerMonData
        {
            Species = SpeciesConstant(speciesText),
            Nickname = Tok(t, pm.Nickname),
            HeldItem = itemText.Length > 0 && Constant("ITEM", itemText) is var it && it != "ITEM_NONE" ? it : "",
            Level = A("Level") is { } lv ? (int.TryParse(lv, NumberStyles.Integer, CultureInfo.InvariantCulture, out var l) ? l : 0) : doc.DefaultLevel,
            Ability = A("Ability") is { Length: > 0 } ab ? Constant("ABILITY", ab) : "",
            Nature = A("Nature") is { Length: > 0 } na ? Constant("NATURE", na) : "",
            IsShiny = A("Shiny") == "Yes",
            Ivs = A("IVs") is { } iv ? (ParseStats(iv, [.. doc.DefaultIvs]) is { } ivs ? ToModelStats(ivs) : iv) : "",
            Evs = A("EVs") is { } ev ? (ParseStats(ev, new int[6]) is { } evs ? ToModelStats(evs) : ev) : "",
            Moves = pm.Moves.Select(mv => Constant("MOVE", t[mv.ValueStart..mv.ValueEnd])).Where(x => x != "MOVE_NONE").ToList(),
            FieldOrder = pm.Lines.Skip(1).Select(a => a.Key).ToList(),
            OriginalIndex = index,
        };
        if (pm.Gender != null) m.Extra.Add(new("Gender", Tok(t, pm.Gender)));
        foreach (var a in pm.Attributes.Where(a => !MonModelled.Contains(a.Key))) m.Extra.Add(new(a.Key, t[a.ValueStart..a.ValueEnd]));
        m.Raw["#species"] = Tok(t, pm.Species);
        m.Raw["#gender"] = Tok(t, pm.Gender);
        m.Raw["#item"] = Tok(t, pm.Item);
        m.Raw["#nickname"] = Tok(t, pm.Nickname);
        m.Original = m.Clone();
        return m;
    }

    /// <summary>A new party member: species and level; everything else uses trainerproc's defaults (IVs 31, no EVs, random nature).</summary>
    public static TrainerMonData NewMon(string species, int level) => new() { Species = species, Level = level, FieldOrder = ["Level"] };

    // ---------------------------------------------------------------- writing

    private sealed class Edits
    {
        private readonly List<(int Start, int End, string Text, int Seq)> _list = [];
        public void Add(int start, int end, string text) => _list.Add((start, end, text, _list.Count));
        public bool Any => _list.Count > 0;

        /// <summary>Applies the edits (all inside from..to) to text[from..to].</summary>
        public string Apply(string text, int from, int to)
        {
            var sb = new StringBuilder(text, from, to - from, to - from + 64);
            foreach (var (s, e, t, _) in _list.OrderByDescending(x => x.Start).ThenByDescending(x => x.End).ThenByDescending(x => x.Seq))
            {
                if (s < from || e > to) throw new InvalidOperationException("Internal error: edit outside its block.");
                sb.Remove(s - from, e - s);
                sb.Insert(s - from, t);
            }
            return sb.ToString();
        }
    }

    /// <summary>Sets, removes (value null) or inserts an attribute line among <paramref name="lines"/> in canonical order.</summary>
    private static void SetLine(Edits edits, string text, string nl, List<PartyLine> lines, PartyLine anchorDefault, string[] order, string key, string? value)
    {
        var line = lines.FirstOrDefault(l => l.Key == key);
        if (line != null && value != null)
        {
            string prefix = line.ValueStart == line.ValueEnd && line.ValueStart > 0 && text[line.ValueStart - 1] == ':' ? " " : "";
            edits.Add(line.ValueStart, line.ValueEnd, prefix + value);
        }
        else if (line != null) edits.Add(line.PrevEnd, line.End, "");
        else if (value != null)
        {
            int rank = Array.IndexOf(order, key);
            var anchor = lines.LastOrDefault(l => Array.IndexOf(order, l.Key) is var r && r >= 0 && r < rank) ?? anchorDefault;
            edits.Add(anchor.End, anchor.End, nl + key + ": " + value);
        }
    }

    private static void CheckNickname(string nick)
    {
        if (nick.Length == 0) return;
        if (nick.Length > MaxNickname || !nick.All(SpeciesChar) || nick.StartsWith(' ') || nick.EndsWith(' '))
            throw new InvalidOperationException($"Nickname \"{nick}\" cannot be written in trainers.party (letters, digits, space, - . : ' only; at most {MaxNickname} characters).");
    }

    private static string MonHeaderText(string nick, string species, string gender, string item)
    {
        if (gender.Length > 1 && nick.Length == 0) gender = gender[..1]; // "Species (Male)" would read "Male" as the species
        var sb = new StringBuilder();
        sb.Append(nick.Length > 0 ? $"{nick} ({species})" : species);
        if (gender.Length > 0) sb.Append(" (").Append(gender).Append(')');
        if (item.Length > 0) sb.Append(" @ ").Append(item);
        return sb.ToString();
    }

    private static string IvText(PartyDocument doc, string ivs) => FromModelStats(ivs) is { } v ? FormatStats(v, false)
        : throw new InvalidOperationException($"IVs \"{ivs}\" must be six numbers (HP, Atk, Def, Speed, SpAtk, SpDef).");

    private static string? EvText(string evs)
    {
        if (evs.Trim().Length == 0) return null;
        var v = FromModelStats(evs) ?? throw new InvalidOperationException($"EVs \"{evs}\" must be six numbers (HP, Atk, Def, Speed, SpAtk, SpDef).");
        return v.All(x => x == 0) ? null : FormatStats(v, true);
    }

    private static string NatureText(PartyDocument doc, string nature) => Readable(doc, "NATURE", nature, Ctx.Line);

    /// <summary>Line edits of one existing Pokémon block (all inside pm.Start..pm.End).</summary>
    private static Edits EditMon(PartyDocument doc, PartyMon pm, TrainerMonData o, TrainerMonData u)
    {
        var text = doc.Text;
        var nl = doc.Newline;
        var edits = new Edits();
        string raw(string k) => o.Raw.GetValueOrDefault(k, "");

        bool itemedForm = ItemedSpecies.ContainsKey(raw("#species")) && raw("#item").Length == 0;
        bool speciesChanged = u.Species != o.Species, itemChanged = u.HeldItem != o.HeldItem;
        if (speciesChanged || itemChanged || u.Nickname != o.Nickname)
        {
            CheckNickname(u.Nickname);
            string species = speciesChanged || itemedForm ? Readable(doc, "SPECIES", u.Species, Ctx.Species) : raw("#species");
            string gender = speciesChanged && GenderedSpecies.Contains(raw("#species")) ? "" : raw("#gender");
            string item = itemChanged || itemedForm ? (u.HeldItem.Length > 0 ? Readable(doc, "ITEM", u.HeldItem, Ctx.Human) : "") : raw("#item");
            string nick = u.Nickname == o.Nickname ? raw("#nickname") : u.Nickname;
            edits.Add(pm.TokensStart, pm.TokensEnd, MonHeaderText(nick, species, gender, item));
        }

        var lines = pm.Lines.Skip(1).Where(l => l.Key is not ("" or "-")).ToList();
        void Set(string key, string? value) => SetLine(edits, text, nl, lines, pm.HeaderLine, MonOrder, key, value);
        if (u.Level != o.Level) Set("Level", u.Level.ToString(CultureInfo.InvariantCulture));
        if (u.Ability != o.Ability) Set("Ability", u.Ability.Length > 0 ? Readable(doc, "ABILITY", u.Ability, Ctx.Line) : null);
        if (u.Ivs != o.Ivs) Set("IVs", u.Ivs.Trim().Length > 0 ? IvText(doc, u.Ivs) : null);
        if (u.Evs != o.Evs) Set("EVs", EvText(u.Evs));
        if (u.Nature != o.Nature)
        {
            var nat = lines.FirstOrDefault(l => l.Key == "Nature");
            var value = u.Nature.Length > 0 ? NatureText(doc, u.Nature) : null;
            if (nat is { NatureSuffix: true } && value != null && !Regex.IsMatch(value, "^[A-Za-z0-9_]+$")) value = u.Nature; // "X Nature" needs one word
            Set("Nature", value);
        }
        if (u.IsShiny != o.IsShiny) Set("Shiny", u.IsShiny ? "Yes" : null);

        if (!u.Moves.SequenceEqual(o.Moves))
        {
            var moves = pm.Moves.ToList();
            var newText = string.Join(nl, u.Moves.Select(m => "- " + Readable(doc, "MOVE", m, Ctx.Move)));
            if (moves.Count > 0 && u.Moves.Count > 0) edits.Add(moves[0].Start, moves[^1].End, newText);
            else if (moves.Count > 0) edits.Add(moves[0].PrevEnd, moves[^1].End, "");
            else if (u.Moves.Count > 0) edits.Add(pm.End, pm.End, nl + newText);
        }
        return edits;
    }

    /// <summary>A complete new Pokémon block (no trailing line break).</summary>
    public static string FormatMon(PartyDocument doc, TrainerMonData m)
    {
        CheckNickname(m.Nickname);
        var nl = doc.Newline;
        var extra = m.Extra.ToDictionary(e => e.Key, e => e.Value);
        var sb = new StringBuilder(MonHeaderText(m.Nickname, Readable(doc, "SPECIES", m.Species, Ctx.Species), extra.GetValueOrDefault("Gender", ""),
            m.HeldItem.Length > 0 ? Readable(doc, "ITEM", m.HeldItem, Ctx.Human) : ""));
        void Line(string key, string? value) { if (value != null) sb.Append(nl).Append(key).Append(": ").Append(value); }
        foreach (var key in MonOrder)
        {
            switch (key)
            {
                case "Level": Line(key, m.Level.ToString(CultureInfo.InvariantCulture)); break;
                case "Ability": Line(key, m.Ability.Length > 0 ? Readable(doc, "ABILITY", m.Ability, Ctx.Line) : null); break;
                case "IVs": Line(key, m.Ivs.Trim().Length > 0 ? IvText(doc, m.Ivs) : null); break;
                case "EVs": Line(key, EvText(m.Evs)); break;
                case "Nature": Line(key, m.Nature.Length > 0 ? NatureText(doc, m.Nature) : null); break;
                case "Shiny": Line(key, m.IsShiny ? "Yes" : null); break;
                default: Line(key, extra.GetValueOrDefault(key)); break;
            }
        }
        foreach (var mv in m.Moves) sb.Append(nl).Append("- ").Append(Readable(doc, "MOVE", mv, Ctx.Move));
        return sb.ToString();
    }

    private static string? ItemsText(PartyDocument doc, IReadOnlyList<string> items) =>
        items.Count == 0 ? null : string.Join(" / ", items.Select(i => Readable(doc, "ITEM", i, Ctx.Human)));

    private static string? AiText(PartyDocument doc, string ai)
    {
        var flags = ai.Split('|', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Where(f => f != "0").ToList();
        if (flags.Count == 0) return null;
        foreach (var f in flags.Where(f => !IsConstant(f, "AI_FLAG")))
            throw new InvalidOperationException($"AI flag \"{f}\" cannot be written in the competitive syntax (only AI_FLAG_… constants joined with |).");
        return string.Join(" / ", flags.Select(f => Readable(doc, "AI_FLAG", f, Ctx.Human)));
    }

    /// <summary>Returns the file text with <paramref name="updated"/> written into the trainer's existing block.</summary>
    public static string Write(PartyDocument doc, PartyTrainer p, TrainerData original, TrainerData updated)
    {
        if (p.Problem != null) throw new InvalidOperationException(p.Problem + " The trainer cannot be edited by the toolkit.");
        var text = doc.Text;
        var nl = doc.Newline;
        var edits = new Edits();
        var header = p.Header.ToList();
        void Set(string key, string? value) => SetLine(edits, text, nl, header, p.Section, HeaderOrder, key, value);

        if (updated.Name != original.Name)
        {
            if (updated.Name.Contains('\n') || updated.Name.Contains("/*", StringComparison.Ordinal)) throw new InvalidOperationException("The name must be a single line.");
            Set("Name", updated.Name);
        }
        if (updated.TrainerClass != original.TrainerClass) Set("Class", Readable(doc, "TRAINER_CLASS", updated.TrainerClass, Ctx.Line));
        if (updated.Pic != original.Pic) Set("Pic", Readable(doc, PicPrefix(p.Id), updated.Pic, Ctx.Line));
        if (updated.Music != original.Music)
        {
            var (of, om) = SplitMusic(original.Music);
            var (uf, um) = SplitMusic(updated.Music);
            if (uf != of) Set("Gender", uf ? "Female" : "Male");
            if (um != om) Set("Music", um.Length > 0 ? Readable(doc, "TRAINER_ENCOUNTER_MUSIC", um, Ctx.Line) : null);
        }
        if (!updated.Items.SequenceEqual(original.Items)) Set("Items", ItemsText(doc, updated.Items));
        if (updated.DoubleBattle != original.DoubleBattle) Set("Double Battle", updated.DoubleBattle ? "Yes" : "No");
        if (updated.AiFlags != original.AiFlags) Set("AI", AiText(doc, updated.AiFlags));

        bool structural = updated.Party.Count != original.Party.Count || updated.Party.Where((m, i) => m.OriginalIndex != i).Any();
        if (!structural)
        {
            for (int i = 0; i < updated.Party.Count; i++)
            {
                if (updated.Party[i].Key == original.Party[i].Key) continue;
                var pm = p.Party[i];
                edits.Add(pm.Start, pm.End, EditMon(doc, pm, original.Party[i], updated.Party[i]).Apply(text, pm.Start, pm.End));
            }
        }
        else
        {
            var monTexts = updated.Party.Select(m => m.OriginalIndex is { } oi && oi < p.Party.Count
                ? (m.Key == original.Party[oi].Key ? text[p.Party[oi].Start..p.Party[oi].End] : EditMon(doc, p.Party[oi], original.Party[oi], m).Apply(text, p.Party[oi].Start, p.Party[oi].End))
                : FormatMon(doc, m)).ToList();
            var seps = new List<string>();
            for (int i = 0; i + 1 < p.Party.Count; i++) seps.Add(text[p.Party[i].End..p.Party[i + 1].Start]);
            var sb = new StringBuilder();
            for (int i = 0; i < monTexts.Count; i++)
            {
                if (i > 0) sb.Append(i - 1 < seps.Count ? seps[i - 1] : nl + nl);
                sb.Append(monTexts[i]);
            }
            var lastHeader = header.Count > 0 ? header[^1] : p.Section;
            if (p.Party.Count == 0) { if (monTexts.Count > 0) edits.Add(lastHeader.End, lastHeader.End, nl + nl + sb); }
            else if (monTexts.Count == 0) edits.Add(lastHeader.End, p.Party[^1].End, "");
            else edits.Add(p.Party[0].Start, p.Party[^1].End, sb.ToString());
        }

        if (!edits.Any) return text;
        return text[..p.Start] + edits.Apply(text, p.Start, p.BlockEnd) + text[p.BlockEnd..];
    }

    /// <summary>A complete new "=== TRAINER_X ===" block (no trailing line break).</summary>
    public static string FormatTrainer(PartyDocument doc, TrainerData d)
    {
        var nl = doc.Newline;
        var (female, music) = SplitMusic(d.Music);
        var sb = new StringBuilder();
        sb.Append("=== ").Append(d.Id).Append(" ===");
        void Line(string key, string? value) { if (value != null) sb.Append(nl).Append(key).Append(": ").Append(value); }
        Line("Name", d.Name);
        Line("Class", d.TrainerClass.Length > 0 ? Readable(doc, "TRAINER_CLASS", d.TrainerClass, Ctx.Line) : null);
        Line("Pic", Readable(doc, PicPrefix(d.Id), d.Pic, Ctx.Line));
        Line("Gender", female ? "Female" : "Male");
        Line("Music", music.Length > 0 ? Readable(doc, "TRAINER_ENCOUNTER_MUSIC", music, Ctx.Line) : null);
        Line("Items", ItemsText(doc, d.Items));
        Line("Double Battle", d.DoubleBattle ? "Yes" : "No");
        Line("AI", AiText(doc, d.AiFlags));
        foreach (var m in d.Party) sb.Append(nl).Append(nl).Append(FormatMon(doc, m));
        return sb.ToString();
    }

    /// <summary>Appends a new trainer block at the end of the file.</summary>
    public static string Append(PartyDocument doc, TrainerData d)
    {
        var text = doc.Text;
        var nl = doc.Newline;
        var sb = new StringBuilder(text);
        if (text.Length > 0 && !text.EndsWith('\n')) sb.Append(nl);
        if (text.Length > 0 && !text.EndsWith(nl + nl, StringComparison.Ordinal)) sb.Append(nl);
        sb.Append(FormatTrainer(doc, d)).Append(nl);
        return sb.ToString();
    }
}
