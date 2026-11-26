using System.Text.Json;
using TilesetEditor.Core.Project;

namespace TilesetEditor.Core.Platinum;

/// <summary>A text of a bank that matches the search.</summary>
public sealed record TextHit(string Bank, string Id, int Index, string Text, string? MapHeaderId, string? MapName);

/// <summary>
/// Every text of the game (res/text/*.json, ~700 banks): read once in parallel, then searched in memory. Map banks know
/// their map (the header's text bank), so a hit can open the map's texts in Events &amp; Dialogs.
/// </summary>
public sealed class PlatinumTextSearch
{
    private readonly List<(string Bank, string Id, int Index, string Text, string Plain)> _texts;
    private readonly Dictionary<string, (string Id, string Name)> _bankMaps;

    private PlatinumTextSearch(List<(string, string, int, string, string)> texts, Dictionary<string, (string, string)> bankMaps)
    {
        _texts = texts;
        _bankMaps = bankMaps;
    }

    public int Count => _texts.Count;
    public int Banks => _texts.Select(t => t.Bank).Distinct().Count();

    public static PlatinumTextSearch Load(PlatinumProject p)
    {
        var dir = ProjectPaths.Combine(p.Root, "res/text");
        var files = Directory.Exists(dir) ? Directory.GetFiles(dir, "*.json").OrderBy(f => f, StringComparer.Ordinal).ToArray() : [];
        var perFile = new List<(string, string, int, string, string)>[files.Length];
        ParallelIo.For(files.Length, k =>
        {
            var list = new List<(string, string, int, string, string)>();
            perFile[k] = list;
            var bank = "res/text/" + Path.GetFileName(files[k]);
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(files[k]));
                if (!doc.RootElement.TryGetProperty("messages", out var msgs) || msgs.ValueKind != JsonValueKind.Array) return;
                int i = 0;
                foreach (var m in msgs.EnumerateArray())
                {
                    var id = m.TryGetProperty("id", out var idv) ? idv.GetString() ?? "" : "";
                    var text = m.TryGetProperty("en_US", out var t)
                        ? t.ValueKind == JsonValueKind.Array ? string.Concat(t.EnumerateArray().Select(x => x.GetString())) : t.GetString() ?? ""
                        : "";
                    if (text.Length > 0) list.Add((bank, id, i, text, Fold(Plain(text))));
                    i++;
                }
            }
            catch (JsonException) { }
        });
        var bankMaps = new Dictionary<string, (string, string)>(StringComparer.Ordinal);
        // Several maps can share a bank (e.g. parts of a city): the map named like the bank owns it.
        foreach (var g in p.MapHeaders.Where(h => h.TextFile != null && h.EventsFile != null).GroupBy(h => h.TextFile!))
        {
            var stem = Path.GetFileNameWithoutExtension(g.Key).ToUpperInvariant();
            var owner = g.FirstOrDefault(h => h.Id == "MAP_HEADER_" + stem) ?? g.First();
            bankMaps[g.Key] = (owner.Id, owner.FriendlyName);
        }
        return new PlatinumTextSearch(perFile.SelectMany(x => x).ToList(), bankMaps);
    }

    /// <summary>The text as the player reads it, for searching: line codes become spaces, {COMMANDS} are left out.</summary>
    public static string Plain(string text) =>
        System.Text.RegularExpressions.Regex.Replace(text.Replace('\n', ' ').Replace('\r', ' ').Replace('\f', ' '), @"\{[^}]*\}", " ");

    /// <summary>For searching: lower case without accents, so "pokemon" finds "Pokémon".</summary>
    public static string Fold(string s)
    {
        const string from = "éèêëáàâäåãíìîïóòôöõúùûüçñýÿ";
        const string to = "eeeeaaaaaaiiiiooooouuuucnyy";
        var chars = s.ToLowerInvariant().ToCharArray();
        for (int i = 0; i < chars.Length; i++)
        {
            int k = from.IndexOf(chars[i]);
            if (k >= 0) chars[i] = to[k];
        }
        return new string(chars);
    }

    /// <summary>
    /// Texts containing every word of <paramref name="query"/> (case-insensitive), or with that id. Map texts first;
    /// at most <paramref name="max"/> hits.
    /// </summary>
    public List<TextHit> Search(string query, bool onlyMaps = false, int max = 500)
    {
        var words = Fold(query).Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (words.Length == 0) return [];
        var hits = new List<TextHit>();
        foreach (var t in _texts)
        {
            bool map = _bankMaps.TryGetValue(t.Bank, out var owner);
            if (onlyMaps && !map) continue;
            bool match = words.All(w => t.Plain.Contains(w, StringComparison.Ordinal)) || t.Id.Equals(query.Trim(), StringComparison.OrdinalIgnoreCase);
            if (!match) continue;
            hits.Add(new TextHit(t.Bank, t.Id, t.Index, t.Text, map ? owner.Id : null, map ? owner.Name : null));
        }
        return hits.OrderBy(h => h.MapHeaderId == null).Take(max).ToList();
    }
}
