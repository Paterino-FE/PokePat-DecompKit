using System.Text.RegularExpressions;

namespace TilesetEditor.Core.Project;

public enum ConfigValueKind
{
    /// <summary>TRUE / FALSE – the branch has an on/off switch for this option.</summary>
    Switch,
    /// <summary>GEN_3 … GEN_LATEST: selects the behaviour of a game generation.</summary>
    Generation,
    Number,
    /// <summary>Another constant (e.g. ITEM_NONE, OW_ITEM_DESCRIPTIONS_OFF).</summary>
    Symbol,
    /// <summary>Anything else (expressions, macros, multi-line definitions).</summary>
    Expression,
}

/// <summary>One #define from include/config/*.h, as found in the branch.</summary>
public sealed record ConfigOption(
    string Name,
    string Value,
    ConfigValueKind Kind,
    string File,
    int Line,
    string Section,
    string Description,
    /// <summary>Defined inside #if/#ifdef (other than the include guard): the effective value may differ.</summary>
    bool Conditional,
    /// <summary>Numeric value if it can be resolved from the config headers, else null.</summary>
    long? Resolved)
{
    public string KindText => Kind switch
    {
        ConfigValueKind.Switch => "Switch",
        ConfigValueKind.Generation => "Generation",
        ConfigValueKind.Number => "Number",
        ConfigValueKind.Symbol => "Constant",
        _ => "Expression",
    };

    /// <summary>A plain on/off switch: only offered where the branch defines the option as TRUE/FALSE unconditionally.</summary>
    public bool IsToggle => Kind == ConfigValueKind.Switch && !Conditional;
}

/// <summary>Reads the feature configuration of a pokeemerald-expansion branch (read-only).</summary>
public static partial class ConfigCatalog
{
    public const string ConfigDir = "include/config";

    [GeneratedRegex(@"^\s*#\s*define\s+(\w+)(\([^)]*\))?\s*(.*?)\s*$")]
    private static partial Regex DefineRegex();

    [GeneratedRegex(@"^\s*#\s*(if|ifdef|ifndef)\b(.*)$")]
    private static partial Regex IfRegex();

    [GeneratedRegex(@"^\s*#\s*endif\b")]
    private static partial Regex EndifRegex();

    [GeneratedRegex(@"^GEN_(\d+|LATEST)\w*$")]
    private static partial Regex GenRegex();

    [GeneratedRegex(@"^-?(0x[0-9A-Fa-f]+|\d+)[uUlL]*$")]
    private static partial Regex NumberRegex();

    [GeneratedRegex(@"^[A-Za-z_]\w*$")]
    private static partial Regex SymbolRegex();

    /// <summary>Short comment lines without sentence punctuation act as section titles ("// PC settings").</summary>
    internal static bool LooksLikeTitle(string c) =>
        c.Length is > 0 and <= 45 && !c.TrimEnd().EndsWith('.') && !c.EndsWith(':') && !c.Contains(". ", StringComparison.Ordinal)
        && !c.StartsWith("Eg", StringComparison.OrdinalIgnoreCase) && !c.StartsWith("Note", StringComparison.OrdinalIgnoreCase);

    /// <summary>German category name for a config file (include/config/battle.h → "Kampf").</summary>
    public static string CategoryOf(string file) => Path.GetFileNameWithoutExtension(file) switch
    {
        "battle" => "Battle",
        "pokemon" => "Pokémon",
        "overworld" => "Overworld",
        "item" => "Items",
        "general" => "General",
        "caps" => "Level and EV caps",
        "save" => "Save data",
        "debug" => "Debug",
        "species_enabled" => "Pokémon species (on/off)",
        "test" => "Tests",
        var other => char.ToUpperInvariant(other[0]) + other[1..],
    };

    public static List<ConfigOption> Load(string projectRoot)
    {
        var dir = Path.Combine(projectRoot, ConfigDir.Replace('/', Path.DirectorySeparatorChar));
        if (!Directory.Exists(dir)) return [];
        var files = Directory.EnumerateFiles(dir, "*.h").OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
            .Select(f => (Rel: ConfigDir + "/" + Path.GetFileName(f), Text: File.ReadAllText(f))).ToList();
        return Parse(files);
    }

    public static List<ConfigOption> Parse(IReadOnlyList<(string Rel, string Text)> files)
    {
        var defines = new CDefines();
        foreach (var f in files) defines.AddFromText(f.Text);
        var result = new List<ConfigOption>();
        foreach (var (rel, text) in files) ParseFile(rel, text, defines, result);
        return result;
    }

    private static void ParseFile(string rel, string text, CDefines defines, List<ConfigOption> result)
    {
        var lines = text.Replace("\r\n", "\n").Split('\n');
        var ifStack = new Stack<bool>(); // true = include guard
        string section = "";
        var pendingComment = new List<string>();
        for (int i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            var trimmed = line.Trim();
            if (trimmed.Length == 0)
            {
                pendingComment.Clear();
                continue;
            }
            var ifm = IfRegex().Match(line);
            if (ifm.Success)
            {
                bool guard = ifStack.Count == 0 && ifm.Groups[1].Value == "ifndef" && ifm.Groups[2].Value.Trim().StartsWith("GUARD_", StringComparison.Ordinal);
                ifStack.Push(guard);
                pendingComment.Clear();
                continue;
            }
            if (EndifRegex().IsMatch(line))
            {
                if (ifStack.Count > 0) ifStack.Pop();
                continue;
            }
            if (trimmed.StartsWith("//", StringComparison.Ordinal))
            {
                var c = trimmed.TrimStart('/').Trim();
                pendingComment.Add(c);
                // A comment block after a blank line acts as section title for the following defines.
                if (pendingComment.Count == 1 && LooksLikeTitle(c)) section = c;
                continue;
            }
            var dm = DefineRegex().Match(line);
            if (!dm.Success)
            {
                pendingComment.Clear();
                continue;
            }
            var name = dm.Groups[1].Value;
            if (name.StartsWith("GUARD_", StringComparison.Ordinal)) continue;
            bool functionLike = dm.Groups[2].Success;
            var rest = dm.Groups[3].Value;
            string description = "";
            int commentAt = rest.IndexOf("//", StringComparison.Ordinal);
            if (commentAt >= 0)
            {
                description = rest[(commentAt + 2)..].Trim();
                rest = rest[..commentAt].Trim();
            }
            bool multiLine = rest.EndsWith('\\');
            if (description.Length == 0 && pendingComment.Count > 0) description = string.Join(" ", pendingComment.Where(p => p.Length > 0));
            pendingComment.Clear();

            var value = rest.Trim();
            var kind = functionLike || multiLine ? ConfigValueKind.Expression
                : value is "TRUE" or "FALSE" ? ConfigValueKind.Switch
                : GenRegex().IsMatch(value) ? ConfigValueKind.Generation
                : NumberRegex().IsMatch(value) ? ConfigValueKind.Number
                : SymbolRegex().IsMatch(value) ? ConfigValueKind.Symbol
                : ConfigValueKind.Expression;
            bool conditional = ifStack.Any(g => !g);
            long? resolved = kind is ConfigValueKind.Switch ? (value == "TRUE" ? 1 : 0) : functionLike ? null : defines.TryEvaluate(name);
            result.Add(new ConfigOption(name, functionLike ? dm.Groups[2].Value + " " + value : value, kind, rel, i + 1, section, description, conditional, resolved));
        }
    }
}
