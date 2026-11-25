using TilesetEditor.Toolkit;

/// <summary>Interactive first-time setup for the command line (Linux): same settings file and checks as the Windows setup window.</summary>
internal static class Setup
{
    public static int Run(ToolkitSettingsStore store, ToolkitSettings settings, Func<string, string?> opt, bool nonInteractive)
    {
        bool interactive = !nonInteractive && !Console.IsInputRedirected;
        Console.WriteLine("PokéPat DecompKit – setup");
        Console.WriteLine("Only the project folder is required. Press Enter to keep the value in [brackets], or type - to skip an optional tool.");
        Console.WriteLine();

        string? Ask(string question, string? current, Func<string?, SetupCheck> check, bool required)
        {
            while (true)
            {
                string? value = current;
                if (interactive)
                {
                    Console.Write($"{question}{(current is { Length: > 0 } ? $" [{current}]" : "")}: ");
                    var input = Console.ReadLine();
                    if (input == null) return current;
                    input = input.Trim().Trim('"');
                    if (input == "-" && !required) value = null;
                    else if (input.Length > 0) value = input;
                }
                var result = check(value);
                Console.WriteLine((result.Ok ? (result.Skipped ? "  – " : "  ✓ ") : "  ✗ ") + result.Message);
                if (result.Ok) return string.IsNullOrWhiteSpace(value) ? null : value;
                if (!interactive) throw new ArgumentException(question + ": " + result.Message);
                current = null;
            }
        }

        var project = Ask("1. Folder of your pokeemerald-expansion project (required)", opt("project") ?? settings.LastProject, ToolSetup.CheckProject, required: true)!;
        var root = Path.GetFullPath(project);
        var tools = settings.ForProject(root);
        if (!OperatingSystem.IsWindows()) tools.BuildEnvironment = BuildEnvironment.Native;
        var cmd = opt("build-command") ?? tools.BuildCommand;
        if (interactive)
        {
            Console.Write($"2. Build command, run in the project folder [{cmd}]: ");
            var input = Console.ReadLine()?.Trim();
            if (!string.IsNullOrEmpty(input)) cmd = input;
        }
        tools.BuildCommand = cmd;
        Console.WriteLine($"  ✓ {(tools.BuildEnvironment == BuildEnvironment.Native ? "bash -lc" : tools.BuildEnvironment.ToString())}: {cmd}");
        tools.PorymapExe = Ask("3. Porymap (optional)", opt("porymap") ?? tools.PorymapExe ?? ToolSetup.FindInPath("porymap"), p => ToolSetup.CheckProgram(p, "Porymap"), false);
        tools.EmulatorExe = Ask("4. Emulator, e.g. mgba-qt (optional)", opt("emulator") ?? tools.EmulatorExe ?? ToolSetup.FindInPath("mgba-qt") ?? ToolSetup.FindInPath("mgba"),
            p => ToolSetup.CheckProgram(p, "Emulator"), false);
        if (!OperatingSystem.IsWindows()) tools.Porytiles.Mode = TilesetEditor.Compiler.PorytilesMode.Native;
        var porytiles = Ask("5. Porytiles (optional, only for importing tilesets)", opt("porytiles") ?? (tools.Porytiles.Executable is { Length: > 0 } e && ToolSetup.CheckPorytiles(tools.Porytiles).Ok && !ToolSetup.CheckPorytiles(tools.Porytiles).Skipped ? e : ToolSetup.FindInPath("porytiles")),
            p => ToolSetup.CheckPorytiles(new TilesetEditor.Compiler.PorytilesSettings { Mode = tools.Porytiles.Mode, WslDistro = tools.Porytiles.WslDistro, Executable = p ?? "" }), false);
        tools.Porytiles.Executable = porytiles ?? "";
        ToolSetup.Apply(settings, root, tools);
        store.Save(settings);
        Console.WriteLine();
        Console.WriteLine($"Saved to {store.FilePath}. Try: decompkit info   or   decompkit build");
        return 0;
    }
}

