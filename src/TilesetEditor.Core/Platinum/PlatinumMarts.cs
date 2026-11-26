using System.Text;
using System.Text.RegularExpressions;
using TilesetEditor.Core.Project;

namespace TilesetEditor.Core.Platinum;

/// <summary>What a shop list sells: items, Underground goods (decorations) or seals.</summary>
public enum MartKind { Items, Goods, Seals }

/// <summary>One shop list of include/data/mart_items.h; Level is only used by the common Poké Mart list (1–6).</summary>
public sealed class MartList
{
    public required string Name { get; init; }
    public required bool Common { get; init; }
    public required MartKind Kind { get; init; }
    public required List<(string Item, int Level)> Entries { get; set; }
    public required int BodyStart { get; init; }
    public required int BodyEnd { get; init; }
    /// <summary>The MART_… constant scripts use for it (not for the common list).</summary>
    public string? Id { get; set; }
}

/// <summary>
/// The shops of pokeplatinum (include/data/mart_items.h): the common list of every Poké Mart – each item with the badge
/// level from which it is sold (0 badges → 1, 1–2 → 2, 3–4 → 3, 5–6 → 4, 7 → 5, 8 → 6; see ScrCmd_PokeMartCommon) – and
/// the lists of the special shops (city specialties, the Veilstone store, decorations, the Sunyshore seal market).
/// Saving rewrites only the lists that changed; everything else of the file stays as it is.
/// </summary>
public static partial class PlatinumMarts
{
    public const string File = "include/data/mart_items.h";
    public const int MaxCommon = 63;   // u16 shopItems[64] in ScrCmd_PokeMartCommon, one place for SHOP_ITEM_END

    public static readonly string[] Levels = ["From the start (0 badges)", "From 1 badge", "From 3 badges", "From 5 badges", "From 7 badges", "From 8 badges"];

    [GeneratedRegex(@"const\s+PokeMartCommonItem\s+(\w+)\[\]\s*=\s*\{(.*?)\};", RegexOptions.Singleline)]
    private static partial Regex CommonArray();

    [GeneratedRegex(@"const\s+u16\s+(\w+)\[\]\s*=\s*\{(.*?)\};", RegexOptions.Singleline)]
    private static partial Regex ListArray();

    [GeneratedRegex(@"\{\s*(\w+)\s*,\s*(0x[0-9A-Fa-f]+|\d+)\s*\}")]
    private static partial Regex CommonEntry();

    [GeneratedRegex(@"\[\s*(MART_\w+)\s*\]\s*=\s*(\w+)")]
    private static partial Regex PointerEntry();

    public static bool Exists(string root) => System.IO.File.Exists(ProjectPaths.Combine(root, File));

    public static List<MartList> Load(string root) => Parse(System.IO.File.ReadAllText(ProjectPaths.Combine(root, File), Encoding.UTF8));

    public static List<MartList> Parse(string text)
    {
        var lists = new List<MartList>();
        foreach (Match m in CommonArray().Matches(text))
        {
            var body = m.Groups[2];
            lists.Add(new MartList
            {
                Name = m.Groups[1].Value, Common = true, Kind = MartKind.Items, BodyStart = body.Index, BodyEnd = body.Index + body.Length,
                Entries = CommonEntry().Matches(body.Value).Select(e => (e.Groups[1].Value, Convert.ToInt32(e.Groups[2].Value, e.Groups[2].Value.StartsWith("0x") ? 16 : 10))).ToList(),
            });
        }
        foreach (Match m in ListArray().Matches(text))
        {
            var body = m.Groups[2];
            var items = Regex.Split(body.Value, @"[\s,]+").Where(t => t.Length > 0 && t != "SHOP_ITEM_END").ToList();
            if (items.Any(t => !Regex.IsMatch(t, @"^[A-Z_0-9]+$"))) continue;   // not a plain list of constants
            var kind = items.All(t => t.StartsWith("UG_GOOD_", StringComparison.Ordinal)) && items.Count > 0 ? MartKind.Goods
                : items.All(t => t.Contains("_SEAL_", StringComparison.Ordinal)) && items.Count > 0 ? MartKind.Seals : MartKind.Items;
            lists.Add(new MartList
            {
                Name = m.Groups[1].Value, Common = false, Kind = kind, BodyStart = body.Index, BodyEnd = body.Index + body.Length,
                Entries = items.Select(t => (t, 0)).ToList(),
            });
        }
        // Which MART_… id names which list (the pointer tables of the file).
        foreach (Match p in PointerEntry().Matches(text))
            if (lists.FirstOrDefault(l => l.Name == p.Groups[2].Value) is { } l) l.Id = p.Groups[1].Value;
        return lists.OrderBy(l => l.BodyStart).ToList();
    }

    /// <summary>The file with the lists' bodies written anew (only for lists given here; positions from the parsed text).</summary>
    public static string Write(string text, IEnumerable<MartList> changed)
    {
        foreach (var l in changed.OrderByDescending(l => l.BodyStart))
        {
            string body = l.Common
                ? "\n" + string.Join(",\n", l.Entries.Select(e => $"    {{ {e.Item}, 0x{Math.Clamp(e.Level, 1, 6):X} }}")) + "\n"
                : "\n" + string.Concat(l.Entries.Select(e => $"    {e.Item},\n")) + "    SHOP_ITEM_END\n";
            text = text[..l.BodyStart] + body + text[l.BodyEnd..];
        }
        return text;
    }

    /// <summary>Problems before saving: empty lists, too many items for the common list, unknown constants, doubles.</summary>
    public static List<string> Check(MartList l, ISet<string> known)
    {
        var problems = new List<string>();
        if (l.Entries.Count == 0) problems.Add($"{Title(l)}: the shop sells nothing.");
        if (l.Common && l.Entries.Count > MaxCommon) problems.Add($"{Title(l)}: at most {MaxCommon} items fit.");
        foreach (var e in l.Entries.Where(e => known.Count > 0 && !known.Contains(e.Item)).Select(e => e.Item).Distinct())
            problems.Add($"{Title(l)}: {e} is not known in this project.");
        foreach (var g in l.Entries.GroupBy(e => e.Item).Where(g => g.Count() > 1)) problems.Add($"{Title(l)}: {g.Key} is in the list {g.Count()} times.");
        return problems;
    }

    /// <summary>"JubilifeMartSpecialties" → "Jubilife Mart Specialties"; the common list → "Every Poké Mart".</summary>
    public static string Title(MartList l)
    {
        if (l.Common) return "Every Poké Mart (left counter)";
        var words = Regex.Replace(l.Name.Replace("_", " "), "(?<=[a-z0-9])(?=[A-Z])", " ");
        return Regex.Replace(words, @"\s+", " ").Trim();
    }

    /// <summary>The maps whose scripts open a list (by its MART_… id), as "map folder" names.</summary>
    public static Dictionary<string, List<string>> UsedBy(string root, IEnumerable<MartList> lists)
    {
        var ids = lists.Where(l => l.Id != null).ToDictionary(l => l.Id!, l => l.Name, StringComparer.Ordinal);
        var result = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var dirs = new[] { "res/field/maps", "res/field/scripts" }.Select(d => ProjectPaths.Combine(root, d)).Where(Directory.Exists);
        foreach (var file in dirs.SelectMany(d => Directory.EnumerateFiles(d, "*.s", SearchOption.AllDirectories)))
        {
            var text = System.IO.File.ReadAllText(file);
            if (!text.Contains("MART_", StringComparison.Ordinal)) continue;
            var where = Path.GetFileName(Path.GetDirectoryName(file)) is "scripts" ? Path.GetFileNameWithoutExtension(file).Replace("scripts_", "") : Path.GetFileName(Path.GetDirectoryName(file))!;
            foreach (Match m in Regex.Matches(text, @"\b(MART_\w+_ID_\w+)\b"))
                if (ids.TryGetValue(m.Groups[1].Value, out var name))
                {
                    if (!result.TryGetValue(name, out var maps)) result[name] = maps = [];
                    if (!maps.Contains(where)) maps.Add(where);
                }
        }
        return result;
    }
}
