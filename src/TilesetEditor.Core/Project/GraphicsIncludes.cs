using System.Text.RegularExpressions;

namespace TilesetEditor.Core.Project;

/// <summary>
/// One graphics/binary include in C source, in either of the two forms the decomps use:
/// <list type="bullet">
/// <item>INCBIN_U32("graphics/x.4bpp.lz") – the path names the built file (pokeemerald, expansion up to 1.12)</item>
/// <item>INCGFX_U32("graphics/x.png", ".4bpp.smol", "-mwidth 2") – the path names the source file, the second argument
/// the conversion (expansion 1.13+)</item>
/// </list>
/// </summary>
/// <param name="Path">The first string argument as written.</param>
/// <param name="Conversion">INCGFX only: the output extension, e.g. ".4bpp.smol" or ".gbapal".</param>
/// <param name="Options">INCGFX only: the optional third argument (converter flags).</param>
public sealed record GraphicsInclude(string Path, string? Conversion = null, string? Options = null)
{
    public bool IsIncGfx => Conversion != null;

    /// <summary>
    /// The path of the built file, as an INCBIN would name it: graphics/x.png + ".4bpp.smol" → graphics/x.4bpp.smol.
    /// </summary>
    public string BuiltPath => Conversion == null ? Path : StripExtension(Path) + Conversion;

    /// <summary>The file a person edits: the INCGFX source, or the .png/.pal an INCBIN target is built from.</summary>
    public string SourcePath => Conversion != null ? Path : ToSourcePath(Path);

    /// <summary>Output extension of the built file, e.g. ".4bpp.lz" or ".gbapal" (from "tiles.4bpp.lz" or the INCGFX argument).</summary>
    public string OutputExtension
    {
        get
        {
            if (Conversion != null) return Conversion;
            var file = System.IO.Path.GetFileName(Path);
            int dot = file.IndexOf('.');
            return dot < 0 ? "" : file[dot..];
        }
    }

    /// <summary>graphics/x.4bpp.lz → graphics/x.png, graphics/x.gbapal(.lz) → graphics/x.pal; other paths stay.</summary>
    public static string ToSourcePath(string builtPath)
    {
        var m = Regex.Match(builtPath, @"^(.*)\.(?:1|2|4|8)bpp(?:\.\w+)*$");
        if (m.Success) return m.Groups[1].Value + ".png";
        m = Regex.Match(builtPath, @"^(.*)\.gbapal(?:\.\w+)*$");
        return m.Success ? m.Groups[1].Value + ".pal" : builtPath;
    }

    private static string StripExtension(string path)
    {
        var file = System.IO.Path.GetFileName(path);
        int dot = file.IndexOf('.');
        return dot < 0 ? path : path[..(path.Length - file.Length + dot)];
    }

    /// <summary>Formats an include of the given style: INCBIN with the built path, or INCGFX with source path and conversion.</summary>
    public static string Format(bool incGfx, int bits, string sourcePath, string outputExtension, string? options = null)
    {
        if (!incGfx) return $"INCBIN_U{bits}(\"{StripExtension(sourcePath)}{outputExtension}\")";
        return options == null
            ? $"INCGFX_U{bits}(\"{sourcePath}\", \"{outputExtension}\")"
            : $"INCGFX_U{bits}(\"{sourcePath}\", \"{outputExtension}\", \"{options}\")";
    }
}

/// <summary>A C array declared with a single include: <c>const u32 gFoo[] = INCBIN_U32("…");</c>.</summary>
public sealed record GraphicsDeclaration(string Symbol, int Bits, GraphicsInclude Include, int Index, int Length);

public static partial class GraphicsIncludes
{
    // const u32 gX[] = INCBIN_U32("a");   const u16 ALIGNED(4) gX[] = INCGFX_U16("a.pal", ".gbapal");   INCBIN_COMP / INCGFX_COMP too.
    [GeneratedRegex(@"(?:const\s+)?u(8|16|32)\s+(?:ALIGNED\s*\(\s*\d+\s*\)\s+)?(\w+)\s*\[\s*\]\s*=\s*(INCBIN|INCGFX)_(?:U(?:8|16|32)|COMP)\s*\(\s*""([^""]*)""(?:\s*,\s*""([^""]*)"")?(?:\s*,\s*""([^""]*)"")?\s*\)")]
    private static partial Regex DeclarationRegex();

    [GeneratedRegex(@"\b(INCBIN|INCGFX)_(?:U(?:8|16|32)|COMP)\s*\(\s*""([^""]*)""(?:\s*,\s*""([^""]*)"")?(?:\s*,\s*""([^""]*)"")?\s*\)")]
    private static partial Regex IncludeRegex();

    /// <summary>All single-include array declarations in <paramref name="text"/>.</summary>
    public static IEnumerable<GraphicsDeclaration> Declarations(string text)
    {
        foreach (Match m in DeclarationRegex().Matches(text))
            yield return new GraphicsDeclaration(m.Groups[2].Value, int.Parse(m.Groups[1].Value), FromMatch(m, 3), m.Index, m.Length);
    }

    /// <summary>Every include in <paramref name="text"/> in order, e.g. the elements of a palette array body.</summary>
    public static IEnumerable<GraphicsInclude> Includes(string text)
    {
        foreach (Match m in IncludeRegex().Matches(text)) yield return FromMatch(m, 1);
    }

    /// <summary>Symbol → include for every single-include declaration (first one wins).</summary>
    public static Dictionary<string, GraphicsInclude> BySymbol(string text)
    {
        var d = new Dictionary<string, GraphicsInclude>(StringComparer.Ordinal);
        foreach (var decl in Declarations(text)) d.TryAdd(decl.Symbol, decl.Include);
        return d;
    }

    /// <summary>True if the text declares graphics with INCGFX (expansion 1.13+ style).</summary>
    public static bool UsesIncGfx(string text) => text.Contains("INCGFX_", StringComparison.Ordinal);

    private static GraphicsInclude FromMatch(Match m, int macroGroup)
    {
        string path = m.Groups[macroGroup + 1].Value;
        if (m.Groups[macroGroup].Value != "INCGFX") return new GraphicsInclude(path);
        var conv = m.Groups[macroGroup + 2];
        var opts = m.Groups[macroGroup + 3];
        return new GraphicsInclude(path, conv.Success ? conv.Value : "", opts.Success ? opts.Value : null);
    }
}
