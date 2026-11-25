using TilesetEditor.Toolkit;
using TilesetEditor.Toolkit.Install;

/// <summary>
/// "Get started" for the command line: checks (--check) or installs everything a new project needs and stores it as the
/// current project. Same steps as the app's Get started window.
/// </summary>
internal static class Install
{
    public static async Task<int> RunAsync(ToolkitSettingsStore store, ToolkitSettings settings, Func<string, string?> opt, bool checkOnly)
    {
        var options = new InstallOptions
        {
            ProjectPath = opt("project-path") ?? "~/pokeemerald-expansion",
            ToolsFolder = opt("tools") ?? settings.ToolsFolder ?? InstallOptions.DefaultToolsFolder,
            ExpansionTag = opt("tag"),
            ExistingPorymap = settings.DefaultPorymapExe,
            ExistingEmulator = settings.DefaultEmulatorExe,
        };
        if (opt("distro") is { } d) options.Distro = d;
        if (opt("repo") is { } repo) options.ExpansionRepo = repo;
        if (opt("branch") is { } b) options.Branch = b;
        var installer = new Installer(options);
        var only = opt("only")?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(s => Enum.TryParse<InstallComponent>(s, ignoreCase: true, out var c) ? c : throw new ArgumentException($"Unknown component \"{s}\" ({string.Join(", ", Installer.All)})."))
            .ToHashSet();

        if (opt("updates") != null || opt("update") is { } x && x.Length > 0)
        {
            // install --updates: list what is new;  install --update porymap,expansion: update those parts.
            var wanted = opt("update")?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(s => Enum.Parse<InstallComponent>(s, ignoreCase: true)).ToHashSet();
            var current = InstallOptions.FromSettings(settings);
            if (opt("project-path") == null) options.ProjectPath = current.ProjectPath;
            if (opt("distro") == null) options.Distro = current.Distro;
            options.ExistingPorymap = current.ExistingPorymap;
            options.ExistingEmulator = current.ExistingEmulator;
            var up = new Installer(options);
            int code = 0;
            foreach (var c in Installer.All)
            {
                var u = await up.CheckUpdateAsync(c);
                if (u == null) continue;
                Console.WriteLine($"{(u.Available ? "⬆" : "✓")} {Installer.Title(c),-24} {u.Message}");
                if (wanted?.Contains(c) == true && u.Available)
                {
                    var s = await up.UpdateAsync(c, line => Console.WriteLine("   " + line));
                    Console.WriteLine($"   {s.Message}");
                    if (!s.IsReady) code = 1;
                }
            }
            if (await Installer.CheckAppUpdateAsync(typeof(Install).Assembly.GetName().Version?.ToString(3) ?? "0.0.0") is { } app)
                Console.WriteLine($"⬆ PokéPat {app.Version} is available: {app.PageUrl}");
            settings.LastUpdateCheckUtc = DateTime.UtcNow;
            store.Save(settings);
            return code;
        }

        Console.WriteLine($"PokéPat DecompKit – get started   (project: {options.ProjectPath}, programs: {options.ToolsFolder})");
        bool allReady = true;
        foreach (var c in Installer.All)
        {
            if (only != null && !only.Contains(c)) continue;
            var s = await installer.CheckAsync(c);
            if (!checkOnly && s.State is InstallState.Missing or InstallState.Failed)
            {
                Console.WriteLine($"== {Installer.Title(c)}: {s.Message}");
                s = await installer.InstallAsync(c, line => Console.WriteLine("   " + line), newWindow: false);
            }
            Console.WriteLine($"{Mark(s.State)} {Installer.Title(c),-24} {s.Message}");
            if (s.State is not (InstallState.Ready or InstallState.NotAvailable))
            {
                allReady = false;
                if (!checkOnly && only == null) break;
            }
        }
        if (!checkOnly && allReady && (await installer.CheckAsync(InstallComponent.Expansion)).IsReady)
        {
            foreach (var c in new[] { InstallComponent.Porytiles, InstallComponent.Porymap, InstallComponent.Emulator }) await installer.CheckAsync(c);
            installer.ApplyTo(settings);
            store.Save(settings);
            Console.WriteLine($"Saved as current project: {installer.ProjectHostPath}");
        }
        return allReady ? 0 : 1;
    }

    private static string Mark(InstallState s) => s switch
    {
        InstallState.Ready => "✓",
        InstallState.NotAvailable => "–",
        InstallState.NeedsUser => "!",
        _ => "✗",
    };
}
