using System.Text.RegularExpressions;

namespace TilesetEditor.Core.Trainers;

public enum TrainerFormat
{
    /// <summary>src/data/trainers.h (COMPETITIVE_PARTY_SYNTAX FALSE).</summary>
    C,
    /// <summary>src/data/trainers.party (COMPETITIVE_PARTY_SYNTAX TRUE, the expansion's default).</summary>
    Party,
}

/// <summary>
/// The project's trainer definitions in whichever format the branch uses. Picks trainers.party when
/// include/config/general.h sets COMPETITIVE_PARTY_SYNTAX TRUE (and the file is not empty), otherwise trainers.h.
/// Without the setting (expansion 1.13+ removed it) trainers.party is used unless trainers.h is a hand-written source.
/// </summary>
public sealed partial class TrainerSource
{
    public const string GeneralConfigFile = "include/config/general.h";

    private readonly List<ParsedTrainer>? _c;
    private readonly PartyDocument? _party;

    private TrainerSource(TrainerFormat format, string text)
    {
        Format = format;
        Text = text;
        if (format == TrainerFormat.Party)
        {
            _party = TrainerPartyFile.Parse(text);
            Trainers = _party.Trainers.Select(t => TrainerPartyFile.ToData(_party, t)).ToList();
        }
        else
        {
            _c = TrainerFile.Parse(text);
            Trainers = _c.Select(TrainerFile.ToData).ToList();
        }
    }

    public TrainerFormat Format { get; }
    public string Text { get; }
    public IReadOnlyList<TrainerData> Trainers { get; }
    public string RelativePath => PathFor(Format);
    public int MonCount => Trainers.Sum(t => t.Party.Count);
    public string FormatName => Format == TrainerFormat.Party ? "competitive syntax" : "C syntax";

    /// <summary>Trainers that are shown but cannot be written (party format only), with the reason.</summary>
    public IReadOnlyDictionary<string, string> Problems =>
        _party?.Trainers.Where(t => t.Problem != null).GroupBy(t => t.Id).ToDictionary(g => g.Key, g => g.First().Problem!) ?? new Dictionary<string, string>();

    public static string PathFor(TrainerFormat f) => f == TrainerFormat.Party ? TrainerFile.PartyFile : TrainerFile.TrainersFile;

    [GeneratedRegex(@"^[ \t]*#define[ \t]+COMPETITIVE_PARTY_SYNTAX[ \t]+(\w+)", RegexOptions.Multiline)]
    private static partial Regex CompetitiveDefine();

    private static string Full(string root, string rel) => Path.Combine([root, .. rel.Split('/')]);

    /// <summary>Which file the branch uses for its trainers.</summary>
    public static TrainerFormat Detect(string root)
    {
        var party = Full(root, TrainerFile.PartyFile);
        bool hasParty = File.Exists(party) && new FileInfo(party).Length > 0;
        if (!hasParty) return TrainerFormat.C;
        var general = Full(root, GeneralConfigFile);
        if (File.Exists(general))
        {
            var m = CompetitiveDefine().Match(File.ReadAllText(general));
            if (m.Success) return m.Groups[1].Value is "TRUE" or "1" ? TrainerFormat.Party : TrainerFormat.C;
        }
        // No setting (expansion 1.13+ always uses trainers.party): use trainers.h only if it is a real source file,
        // not the one the build generates from trainers.party.
        var c = Full(root, TrainerFile.TrainersFile);
        if (!File.Exists(c)) return TrainerFormat.Party;
        var cText = File.ReadAllText(c);
        if (IsGeneratedFromParty(cText)) return TrainerFormat.Party;
        return TrainerFile.Parse(cText).Count > 0 ? TrainerFormat.C : TrainerFormat.Party;
    }

    /// <summary>True for the trainers.h the build writes from trainers.party ("DO NOT MODIFY THIS FILE! It is auto-generated …").</summary>
    public static bool IsGeneratedFromParty(string trainersH)
    {
        var head = trainersH.Length > 600 ? trainersH[..600] : trainersH;
        return head.Contains("auto-generated from src/data/trainers.party", StringComparison.OrdinalIgnoreCase);
    }

    public static TrainerSource Load(string root)
    {
        var format = Detect(root);
        var path = Full(root, PathFor(format));
        return new TrainerSource(format, File.Exists(path) ? File.ReadAllText(path) : "");
    }

    public static TrainerSource FromText(TrainerFormat format, string text) => new(format, text);

    private TrainerSource For(string text) => text == Text ? this : new TrainerSource(Format, text);

    /// <summary>The file text with the trainer <paramref name="updated"/> (edited copy of <paramref name="original"/>) written into its block.</summary>
    public string Write(string text, TrainerData original, TrainerData updated)
    {
        var s = For(text);
        if (s._party != null)
        {
            var p = s._party.Trainers.FirstOrDefault(t => t.Id == original.Id) ?? throw new InvalidOperationException($"{original.Id} was not found in {RelativePath}.");
            return TrainerPartyFile.Write(s._party, p, original, updated);
        }
        var c = s._c!.FirstOrDefault(t => t.Id == original.Id) ?? throw new InvalidOperationException($"{original.Id} was not found in {RelativePath}.");
        return TrainerFile.Write(text, c, original, updated);
    }

    /// <summary>The file text with a new trainer entry appended.</summary>
    public string Append(string text, TrainerData d)
    {
        var s = For(text);
        return s._party != null ? TrainerPartyFile.Append(s._party, d) : TrainerFile.Append(text, s._c!, d);
    }

    /// <summary>Defaults for a new party member in this format.</summary>
    public TrainerMonData NewMon(string species, int level) =>
        _party != null ? TrainerPartyFile.NewMon(species, level) : TrainerFile.NewMon(_c!, species, level);
}
