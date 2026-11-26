using System.Text;
using System.Text.RegularExpressions;
using TilesetEditor.Core.GameData;
using TilesetEditor.Core.Project;
using TilesetEditor.Core.Trainers;

namespace TilesetEditor.Core.SecretBases;

/// <summary>One entry of gDecorations (src/data/decoration/header.h) with its description, graphics and icon.</summary>
public sealed class Decoration
{
    public required string Constant { get; init; }
    public required int Id { get; init; }
    public string Name { get; set; } = "";
    public string Permission { get; set; } = "";
    public string Shape { get; set; } = "";
    public string Category { get; set; } = "";
    public int Price { get; set; }
    public string PriceRaw { get; set; } = "";
    /// <summary>DecorDesc_X in description.h, or "" when the text is written in the header entry (expansion 1.16+).</summary>
    public string DescriptionSymbol { get; set; } = "";
    public string Description { get; set; } = "";
    /// <summary>.description = COMPOUND_STRING("…") directly in gDecorations (expansion 1.16+).</summary>
    public bool DescriptionInline { get; set; }
    public string TilesSymbol { get; set; } = "";
    public List<string> Tiles { get; } = [];
    public bool HasHeaderEntry { get; set; }
    public bool HasIconEntry { get; set; }
    public string? IconSymbol { get; set; }
    public string? IconPaletteSymbol { get; set; }
    /// <summary>Project-relative PNG of the 24×24 menu icon (null: the game builds the icon from the sprite/metatiles).</summary>
    public string? IconPng { get; set; }

    public bool IsSprite => Permission == "DECORPERM_SPRITE";
    public string GraphicsKind => IsSprite ? "Object sprite" : "Metatiles";
    /// <summary>OBJ_EVENT_GFX_… shown for a sprite decoration (tiles[0]).</summary>
    public string? ObjectGfx => IsSprite ? Tiles.FirstOrDefault() : null;
    public string ShortName => Constant.Replace("DECOR_", "");
    public string CategoryText => Category.Replace("DECORCAT_", "");
    public string ShapeText => Shape.Replace("DECORSHAPE_", "");
    public string PermissionText => Permission.Replace("DECORPERM_", "");
    public string DescriptionOneLine => Description.Replace("\n", " ");
    public string GraphicsText => IsSprite ? ObjectGfx?.Replace("OBJ_EVENT_GFX_", "") ?? "?" : $"{Tiles.Count} metatile(s)";

    /// <summary>Width × height in blocks from DECORSHAPE_WxH.</summary>
    public (int W, int H) ShapeSize => Decorations.ShapeSize(Shape);
}

public sealed class DecorationData
{
    public List<Decoration> Items { get; } = [];
    public string? NumDecorationsValue { get; set; }
    public List<string> Problems { get; } = [];
    public Dictionary<string, int> MetatileLabels { get; set; } = [];
    public int MaxId => Items.Count == 0 ? 0 : Items.Max(i => i.Id);
    /// <summary>
    /// expansion 1.16+ ("consolidated"): description and icon are fields of the gDecorations entry; description.h and
    /// icon.h (gDecorIconTable) no longer exist.
    /// </summary>
    public bool Consolidated { get; set; }
}

/// <summary>
/// Reads and edits the decoration tables: include/constants/decorations.h, src/data/decoration/{header,description,tiles,icon}.h and
/// src/data/graphics/decorations.h. Edits change only the touched value; unchanged values give byte-identical text.
/// expansion 1.16+ keeps description and icon in the header entry (.description = COMPOUND_STRING(…), .icon = {pic, pal}).
/// </summary>
public static partial class Decorations
{
    public const string ConstantsFile = "include/constants/decorations.h";
    public const string HeaderFile = "src/data/decoration/header.h";
    public const string DescriptionFile = "src/data/decoration/description.h";
    public const string TilesFile = "src/data/decoration/tiles.h";
    public const string IconTableFile = "src/data/decoration/icon.h";
    public const string IconGraphicsFile = "src/data/graphics/decorations.h";
    public const string GraphicsHeaderFile = "include/graphics.h";
    public const string IconDir = "graphics/decorations";
    /// <summary>struct Decoration: u8 name[16] → 15 characters + end marker.</summary>
    public const int MaxNameLength = 15;
    /// <summary>Decoration ids are stored as u8 in the save file.</summary>
    public const int MaxId = 255;

    [GeneratedRegex(@"^#define[ \t]+(DECOR_\w+)[ \t]+(\d+)", RegexOptions.Multiline)]
    private static partial Regex ConstantDefine();

    [GeneratedRegex(@"^#define[ \t]+NUM_DECORATIONS[ \t]+(\w+)", RegexOptions.Multiline)]
    private static partial Regex NumDefine();

    [GeneratedRegex(@"^[ \t]*\[(DECOR_\w+)\][ \t]*=\s*\{", RegexOptions.Multiline)]
    private static partial Regex EntryStart();

    [GeneratedRegex(@"const\s+u8\s+(DecorDesc_\w+)\[\]\s*=\s*_\(((?:\s*""(?:[^""\\]|\\.)*"")+)\s*\);")]
    private static partial Regex DescriptionBlock();

    [GeneratedRegex(@"""((?:[^""\\]|\\.)*)""")]
    private static partial Regex StringLiteral();

    [GeneratedRegex(@"const\s+u16\s+(DecorGfx_\w+)\[\]\s*=\s*\{(.*?)\};", RegexOptions.Singleline)]
    private static partial Regex TilesBlock();

    [GeneratedRegex(@"\[(DECOR_\w+)\]\s*=\s*\{\s*(\w+)\s*,\s*(\w+)\s*\}")]
    private static partial Regex IconEntry();

    [GeneratedRegex(@"^\{\s*(\w+)\s*,\s*(\w+)\s*\}$")]
    private static partial Regex IconField();

    [GeneratedRegex(@"^_\(""(.*)""\)$", RegexOptions.Singleline)]
    private static partial Regex NameValue();

    public static (int W, int H) ShapeSize(string shape)
    {
        var m = Regex.Match(shape, @"(\d+)x(\d+)$");
        return m.Success ? (int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value)) : (1, 1);
    }

    public static DecorationData Load(string root)
    {
        string Read(string rel)
        {
            var p = ProjectPaths.Combine(root, rel);
            return File.Exists(p) ? File.ReadAllText(p) : "";
        }
        var data = Parse(Read(ConstantsFile), Read(HeaderFile), Read(DescriptionFile), Read(TilesFile), Read(IconTableFile), Read(IconGraphicsFile));
        data.Consolidated |= !File.Exists(ProjectPaths.Combine(root, DescriptionFile)) && !File.Exists(ProjectPaths.Combine(root, IconTableFile));
        data.MetatileLabels = SecretBaseCatalog.ParseMetatileLabels(Read(SecretBaseCatalog.MetatileLabelsFile));
        foreach (var d in data.Items)
            if (d.IconPng != null && !File.Exists(ProjectPaths.Combine(root, d.IconPng))) d.IconPng = null;
        return data;
    }

    public static DecorationData Parse(string constants, string header, string descriptions, string tiles, string icons, string iconGraphics)
    {
        var data = new DecorationData();
        if (NumDefine().Match(constants) is { Success: true } num) data.NumDecorationsValue = num.Groups[1].Value;
        var descs = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (Match m in DescriptionBlock().Matches(descriptions)) descs.TryAdd(m.Groups[1].Value, DecodeLiterals(m.Groups[2].Value));
        var gfx = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (Match m in TilesBlock().Matches(tiles))
            gfx.TryAdd(m.Groups[1].Value, StripComments(m.Groups[2].Value).Split(',').Select(s => s.Trim()).Where(s => s.Length > 0).ToList());
        var iconEntries = new Dictionary<string, (string Pic, string Pal)>(StringComparer.Ordinal);
        foreach (Match m in IconEntry().Matches(icons)) iconEntries.TryAdd(m.Groups[1].Value, (m.Groups[2].Value, m.Groups[3].Value));
        var iconFiles = GraphicsIncludes.BySymbol(iconGraphics);
        var entries = ParseHeader(header);
        bool iconTable = icons.Trim().Length > 0;
        data.Consolidated = !iconTable && descriptions.Trim().Length == 0 && entries.Values.Any(f => f.Any(x => x.Name == "icon"));

        foreach (Match c in ConstantDefine().Matches(constants))
        {
            var constant = c.Groups[1].Value;
            if (constant == "DECOR_NONE") continue;
            var d = new Decoration { Constant = constant, Id = int.Parse(c.Groups[2].Value) };
            if (entries.TryGetValue(constant, out var fields))
            {
                d.HasHeaderEntry = true;
                string F(string name) => fields.FirstOrDefault(f => f.Name == name)?.Raw ?? "";
                d.Name = NameValue().Match(F("name")) is { Success: true } nm ? Unescape(nm.Groups[1].Value) : F("name");
                d.Permission = F("permission");
                d.Shape = F("shape");
                d.Category = F("category");
                d.PriceRaw = F("price");
                d.Price = int.TryParse(d.PriceRaw, out var p) ? p : 0;
                var descRaw = F("description");
                if (CString.TryRead(descRaw) is { } inline)
                {
                    d.DescriptionInline = true;
                    d.Description = Unescape(inline);
                }
                else
                {
                    d.DescriptionSymbol = descRaw;
                    d.Description = descs.GetValueOrDefault(d.DescriptionSymbol, "");
                }
                if (IconField().Match(F("icon")) is { Success: true } iconField) iconEntries.TryAdd(constant, (iconField.Groups[1].Value, iconField.Groups[2].Value));
                d.TilesSymbol = F("tiles");
                if (gfx.TryGetValue(d.TilesSymbol, out var t)) d.Tiles.AddRange(t);
            }
            else data.Problems.Add($"{constant} has no entry in gDecorations ({HeaderFile}).");
            if (iconEntries.TryGetValue(constant, out var icon))
            {
                d.HasIconEntry = true;
                if (icon.Pic != "NULL")
                {
                    d.IconSymbol = icon.Pic;
                    d.IconPaletteSymbol = icon.Pal;
                    if (iconFiles.TryGetValue(icon.Pic, out var inc) && inc.SourcePath.EndsWith(".png", StringComparison.Ordinal)) d.IconPng = inc.SourcePath;
                }
            }
            else if (!iconTable) d.HasIconEntry = true; // no gDecorIconTable: a missing .icon is {NULL, NULL}
            else
                data.Problems.Add($"{constant} has no entry in gDecorIconTable ({IconTableFile}) – the game reads past the end of the table for its menu icon. " +
                                  "\"Add missing icon entries\" fixes it.");
            data.Items.Add(d);
        }
        data.Items.Sort((a, b) => a.Id.CompareTo(b.Id));
        if (data.MaxId > MaxId) data.Problems.Add($"Decoration ids above {MaxId} do not fit into the save file (u8).");
        return data;
    }

    /// <summary>Header entries: constant → fields (positions refer to <paramref name="header"/>).</summary>
    public static Dictionary<string, List<CField>> ParseHeader(string header)
    {
        var result = new Dictionary<string, List<CField>>(StringComparer.Ordinal);
        foreach (Match m in EntryStart().Matches(header))
        {
            int open = m.Index + m.Length - 1;
            int close = TrainerFile.Match(header, open);
            if (close < 0) continue;
            result.TryAdd(m.Groups[1].Value, TrainerFile.Fields(header, open, close));
        }
        return result;
    }

    private static string StripComments(string s) => Regex.Replace(Regex.Replace(s, @"/\*.*?\*/", "", RegexOptions.Singleline), @"//[^\n]*", "");

    /// <summary>Concatenates C string literals and turns "\n" escapes into line breaks.</summary>
    public static string DecodeLiterals(string literals) =>
        Unescape(string.Concat(StringLiteral().Matches(literals).Select(m => m.Groups[1].Value)));

    private static string Unescape(string s) => s.Replace("\\n", "\n").Replace("\\\"", "\"").Replace("\\\\", "\\");

    private static string Escape(string s) => s.Replace("\\", "\\\\").Replace("\"", "\\\"");

    // ------------------------------------------------------------------ in-place edits

    /// <summary>Validation of a decoration name; null if fine.</summary>
    public static string? CheckName(string name)
    {
        if (name.Length == 0) return "The name is empty.";
        if (name.Length > MaxNameLength) return $"The name has {name.Length} characters; at most {MaxNameLength} fit (u8 name[16]).";
        if (name.IndexOfAny(['"', '\\', '\n', '\r']) >= 0) return "The name must not contain quotes, backslashes or line breaks.";
        return null;
    }

    /// <summary>Validation of a description; null if fine.</summary>
    public static string? CheckDescription(string text)
    {
        if (text.Contains('"') || text.Contains('\\')) return "The description must not contain quotes or backslashes.";
        var lines = text.Replace("\r\n", "\n").Split('\n');
        if (lines.Length > 3) return "The decoration menu shows 3 lines – please use at most 3 lines.";
        return null;
    }

    /// <summary>Sets ".name = _("…")" of one decoration. Returns the text unchanged when the name is the same.</summary>
    public static string SetName(string header, string constant, string name)
    {
        return SetField(header, constant, "name", $"_(\"{Escape(name)}\")", raw => NameValue().Match(raw) is { Success: true } m && Unescape(m.Groups[1].Value) == name,
            () => CheckName(name));
    }

    /// <summary>Sets ".price = n" of one decoration (u16).</summary>
    public static string SetPrice(string header, string constant, int price)
    {
        return SetField(header, constant, "price", price.ToString(), raw => int.TryParse(raw, out var p) && p == price,
            () => price is < 0 or > 65535 ? "The price must be between 0 and 65535 (u16)." : null);
    }

    private static string SetField(string header, string constant, string field, string value, Func<string, bool> same, Func<string?> check)
    {
        var entry = EntryStart().Matches(header).FirstOrDefault(m => m.Groups[1].Value == constant)
                    ?? throw new InvalidOperationException($"{constant} was not found in {HeaderFile}.");
        int open = entry.Index + entry.Length - 1;
        int close = TrainerFile.Match(header, open);
        var f = TrainerFile.Fields(header, open, close).FirstOrDefault(x => x.Name == field)
                ?? throw new InvalidOperationException($".{field} of {constant} was not found.");
        if (same(f.Raw)) return header;
        if (check() is { } problem) throw new InvalidOperationException(problem);
        int start = f.ValueStart;
        while (start < f.ValueEnd && char.IsWhiteSpace(header[start])) start++;
        int end = start + f.Raw.Length;
        return header[..start] + value + header[end..];
    }

    /// <summary>Rewrites the string of DecorDesc_X in the style of the existing block. Unchanged text → unchanged file.</summary>
    public static string SetDescription(string descriptions, string symbol, string text)
    {
        text = text.Replace("\r\n", "\n").TrimEnd('\n');
        var m = DescriptionBlock().Matches(descriptions).FirstOrDefault(x => x.Groups[1].Value == symbol)
                ?? throw new InvalidOperationException($"{symbol} was not found in {DescriptionFile}.");
        if (DecodeLiterals(m.Groups[2].Value) == text) return descriptions;
        if (CheckDescription(text) is { } problem) throw new InvalidOperationException(problem);
        var block = m.Groups[2];
        var literals = StringLiteral().Matches(block.Value).ToList();
        string nl = descriptions.Contains("\r\n") ? "\r\n" : "\n";
        string Indent(int literalIndex)
        {
            int abs = block.Index + literals[literalIndex].Index;
            int lineStart = descriptions.LastIndexOf('\n', abs - 1) + 1;
            var lead = descriptions[lineStart..abs];
            return lead.Trim().Length == 0 ? lead : " ";
        }
        string first = literals.Count > 0 ? Indent(0) : "    ";
        string next = literals.Count > 1 ? Indent(1) : first + "    ";
        // First literal keeps its position (normally on the line after "_("); the block ends right after the last literal.
        int literalsStart = block.Index + (literals.Count > 0 ? literals[0].Index : block.Length);
        int lineStartOfFirst = descriptions.LastIndexOf('\n', literalsStart - 1) + 1;
        bool firstOnOwnLine = descriptions[lineStartOfFirst..literalsStart].Trim().Length == 0;
        int replaceFrom = firstOnOwnLine ? lineStartOfFirst : literalsStart;
        int replaceTo = block.Index + block.Length;
        return descriptions[..replaceFrom] + FormatLiterals(text, firstOnOwnLine ? first : "", next, nl) + descriptions[replaceTo..];
    }

    /// <summary>
    /// Sets ".description = COMPOUND_STRING(…)" of one decoration in the header (expansion 1.16+), in the layout of the
    /// existing value. Unchanged text → unchanged file.
    /// </summary>
    public static string SetInlineDescription(string header, string constant, string text)
    {
        text = text.Replace("\r\n", "\n").TrimEnd('\n');
        var entry = EntryStart().Matches(header).FirstOrDefault(m => m.Groups[1].Value == constant)
                    ?? throw new InvalidOperationException($"{constant} was not found in {HeaderFile}.");
        int open = entry.Index + entry.Length - 1;
        int close = TrainerFile.Match(header, open);
        var f = TrainerFile.Fields(header, open, close).FirstOrDefault(x => x.Name == "description")
                ?? throw new InvalidOperationException($".description of {constant} was not found.");
        if (CString.TryRead(f.Raw) is not { } current) throw new InvalidOperationException($".description of {constant} is not a plain text.");
        if (Unescape(current) == text) return header;
        if (CheckDescription(text) is { } problem) throw new InvalidOperationException(problem);
        int start = f.ValueStart;
        while (start < f.ValueEnd && char.IsWhiteSpace(header[start])) start++;
        int end = start + f.Raw.Length;
        return header[..start] + CString.Format(text.Replace("\n", "\\n"), f.Raw, header, start) + header[end..];
    }

    /// <summary>"line1\n" / "line2" as C literals, one per line.</summary>
    public static string FormatLiterals(string text, string firstIndent, string nextIndent, string nl)
    {
        var lines = text.Replace("\r\n", "\n").Split('\n');
        var sb = new StringBuilder();
        for (int i = 0; i < lines.Length; i++)
        {
            if (i > 0) sb.Append(nl).Append(nextIndent);
            else sb.Append(firstIndent);
            sb.Append('"').Append(Escape(lines[i])).Append(i < lines.Length - 1 ? "\\n" : "").Append('"');
        }
        return sb.ToString();
    }

    // ------------------------------------------------------------------ table insertions (shared with the planner)

    /// <summary>
    /// Inserts <paramref name="entry"/> as the last element of the brace-initialized array <paramref name="arraySymbol"/>
    /// (adds the missing comma after the current last element). The entry should end with "," and contain no trailing newline.
    /// </summary>
    public static string AppendToArray(string text, string arraySymbol, string entry, bool blankLineBefore)
    {
        var head = Regex.Match(text, @"\b" + Regex.Escape(arraySymbol) + @"\s*\[[^\]]*\](?:\s*\[[^\]]*\])*\s*=\s*\{");
        if (!head.Success) throw new InvalidOperationException($"The table {arraySymbol} was not found.");
        int open = head.Index + head.Length - 1;
        int close = TrainerFile.Match(text, open);
        if (close < 0) throw new InvalidOperationException($"The end of {arraySymbol} was not found.");
        string nl = text.Contains("\r\n") ? "\r\n" : "\n";
        int last = close - 1;
        while (last > open && char.IsWhiteSpace(text[last])) last--;
        string insertComma = last > open && text[last] != ',' && text[last] != '{' ? "," : "";
        return text[..(last + 1)] + insertComma + nl + (blankLineBefore ? nl : "") + entry + text[(last + 1)..];
    }

    /// <summary>Adds "[DECOR_X] = {NULL, NULL}," to gDecorIconTable for every decoration that has no entry.</summary>
    public static string AddMissingIconEntries(string iconTable, IEnumerable<Decoration> missing)
    {
        foreach (var d in missing.Where(d => !d.HasIconEntry).OrderBy(d => d.Id))
            iconTable = AppendToArray(iconTable, "gDecorIconTable", IconEntryLine(iconTable, d.Constant, "NULL", "NULL"), false);
        return iconTable;
    }

    /// <summary>"    [DECOR_X]   = {pic, pal}," aligned like the existing entries.</summary>
    public static string IconEntryLine(string iconTable, string constant, string pic, string pal)
    {
        var sample = Regex.Matches(iconTable, @"^([ \t]*)(\[DECOR_\w+\][ \t]*)=", RegexOptions.Multiline).LastOrDefault();
        string indent = sample?.Groups[1].Value ?? "    ";
        int width = sample?.Groups[2].Value.Length ?? 0;
        string key = $"[{constant}]";
        key = key.Length + 1 <= width ? key.PadRight(width) : key + " ";
        return $"{indent}{key}= {{{pic}, {pal}}},";
    }

    /// <summary>Absolute metatile id of a DecorGfx item: DECOR_TILE(Name) → METATILE_SecretBase_Name; plain numbers are secondary-relative.</summary>
    public static int? ResolveTile(string item, IReadOnlyDictionary<string, int> labels, int numMetatilesInPrimary)
    {
        var m = Regex.Match(item, @"^DECOR_TILE\((\w+)\)$");
        if (m.Success) return labels.TryGetValue("METATILE_SecretBase_" + m.Groups[1].Value, out var v) ? v : null;
        if (item.StartsWith("0x", StringComparison.OrdinalIgnoreCase) && int.TryParse(item[2..], System.Globalization.NumberStyles.HexNumber, null, out var hex))
            return hex + numMetatilesInPrimary;
        return int.TryParse(item, out var n) ? n + numMetatilesInPrimary : null;
    }
}
