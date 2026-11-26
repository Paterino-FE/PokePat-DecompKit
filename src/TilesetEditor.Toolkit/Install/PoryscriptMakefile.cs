using System.Text.RegularExpressions;

namespace TilesetEditor.Toolkit.Install;

/// <summary>
/// Wires Poryscript into a pokeemerald(-expansion) Makefile the way the Poryscript README describes: every data/**/*.pory is
/// compiled to the .inc next to it before the build. Four small additions; nothing existing is changed.
/// </summary>
public static partial class PoryscriptMakefile
{
    public const string ScriptVariable = "SCRIPT       := $(TOOLS_DIR)/poryscript/poryscript$(EXE)";
    public const string AutoGen = "AUTO_GEN_TARGETS += $(patsubst %.pory,%.inc,$(shell find data/ -type f -name '*.pory'))";
    public const string NoRule = "%.pory: ;";
    public const string Rule = "data/%.inc: data/%.pory; $(SCRIPT) -i $< -o $@ -fc tools/poryscript/font_config.json -cc tools/poryscript/command_config.json";
    public const string GitIgnoreLine = "tools/poryscript/poryscript";

    [GeneratedRegex(@"^[A-Z_]+\s*:=\s*\$\(TOOLS_DIR\)/\S+\$\(EXE\)\s*$")]
    private static partial Regex ToolVariable();

    public static bool IsIntegrated(string makefile) =>
        makefile.Contains("data/%.inc: data/%.pory", StringComparison.Ordinal) && makefile.Contains("poryscript", StringComparison.Ordinal);

    /// <summary>The Makefile with Poryscript wired in, or null with the reason when a place to add a line is missing.</summary>
    public static (string? Text, string? Problem) Integrate(string makefile)
    {
        if (IsIntegrated(makefile)) return (makefile, null);
        string nl = makefile.Contains("\r\n") ? "\r\n" : "\n";
        var lines = makefile.Replace("\r\n", "\n").Split('\n').ToList();

        int lastTool = lines.FindLastIndex(l => ToolVariable().IsMatch(l));
        if (lastTool < 0) return (null, "no tool variables ($(TOOLS_DIR)/…$(EXE)) found");
        bool hasScriptVar = lines.Any(l => Regex.IsMatch(l, @"^SCRIPT\s*:="));

        int generated = lines.FindIndex(l => l.StartsWith("generated:", StringComparison.Ordinal));
        if (generated < 0) return (null, "no \"generated:\" rule found");
        int pal = lines.FindIndex(l => Regex.IsMatch(l, @"^%\.(wav|pal):\s*;"));
        int wav = lines.FindIndex(l => Regex.IsMatch(l, @"^%\.wav:\s*;"));
        if (wav >= 0) pal = wav;
        if (pal < 0) return (null, "no \"%.pal: ;\" line found");
        int rl = lines.FindIndex(l => Regex.IsMatch(l, @"^%\.rl:"));
        if (rl < 0) rl = lines.FindLastIndex(l => Regex.IsMatch(l, @"^%\.4bpp:"));
        if (rl < 0) return (null, "no \"%.4bpp:\" rule found");

        // Insert from the bottom up so the earlier indices stay valid.
        var inserts = new List<(int After, string[] Text)>
        {
            (rl, [Rule]),
            (pal, [NoRule]),
        };
        // The auto-generated targets go before "generated:" and the comment lines right above it.
        int before = generated;
        while (before > 0 && lines[before - 1].TrimStart().StartsWith('#')) before--;
        inserts.Add((before - 1, [AutoGen, ""]));
        if (!hasScriptVar) inserts.Add((lastTool, [ScriptVariable]));
        foreach (var (after, text) in inserts.OrderByDescending(i => i.After))
            lines.InsertRange(after + 1, text);
        return (string.Join(nl, lines), null);
    }

    /// <summary>.gitignore with the Poryscript binary excluded (the config files stay tracked).</summary>
    public static string IgnoreBinary(string? gitignore)
    {
        gitignore ??= "";
        if (gitignore.Replace("\r\n", "\n").Split('\n').Any(l => l.Trim() == GitIgnoreLine)) return gitignore;
        string nl = gitignore.Contains("\r\n") ? "\r\n" : "\n";
        var text = gitignore.TrimEnd('\r', '\n');
        return (text.Length > 0 ? text + nl + nl : "") + "# Poryscript binary (config files are tracked)" + nl + GitIgnoreLine + nl;
    }
}
