using TilesetEditor.Compiler;
using TilesetEditor.Core.Project;

namespace TilesetEditor.Toolkit;

/// <summary>Result of checking one setup entry. Ok = usable; Skipped = optional entry left empty.</summary>
public sealed record SetupCheck(bool Ok, string Message, bool Skipped = false);

/// <summary>
/// First-run setup shared by the Windows app (setup window) and the Linux command line (<c>setup</c>):
/// the pokeemerald-expansion project folder is required, every tool is optional and can be skipped.
/// </summary>
public static class ToolSetup
{
    /// <summary>Checks that the folder is a pokeemerald(-expansion) project the toolkit can read.</summary>
    public static SetupCheck CheckProject(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return new SetupCheck(false, "Required: choose the folder of your pokeemerald-expansion project.");
        path = path.Trim().Trim('"');
        if (!Directory.Exists(path)) return new SetupCheck(false, "Folder not found.");
        string[] needed = ["Makefile", Path.Combine("include", "constants", "flags.h"), Path.Combine("data", "maps")];
        var missing = needed.Where(n => !File.Exists(Path.Combine(path, n)) && !Directory.Exists(Path.Combine(path, n))).ToList();
        if (missing.Count > 0) return new SetupCheck(false, "This does not look like a pokeemerald project (missing: " + string.Join(", ", missing) + ").");
        try
        {
            var info = ProjectLoader.Load(Path.GetFullPath(path));
            var env = ProjectEnvironment.Detect(info.Root);
            return new SetupCheck(true, "OK – " + (info.ExpansionVersion != null ? $"pokeemerald-expansion {info.ExpansionVersion}" : "pokeemerald") +
                $", {info.Layouts.Count} layouts" + (env.GitBranch != null ? $", branch {env.GitBranch}" : ""));
        }
        catch (Exception ex) when (ex is ProjectLoadException or IOException or UnauthorizedAccessException or FormatException)
        {
            return new SetupCheck(false, "The project could not be read: " + ex.Message);
        }
    }

    /// <summary>Optional program (Porymap, emulator): empty = skipped, otherwise the file must exist.</summary>
    public static SetupCheck CheckProgram(string? path, string what)
    {
        if (string.IsNullOrWhiteSpace(path)) return new SetupCheck(true, $"Skipped – {what} features stay disabled until you set it later.", Skipped: true);
        path = path.Trim().Trim('"');
        if (File.Exists(path)) return new SetupCheck(true, "OK");
        if (FindInPath(path) is { } found) return new SetupCheck(true, "OK – found on PATH: " + found);
        return new SetupCheck(false, $"{what}: file not found.");
    }

    /// <summary>Porytiles is optional (only the Tilesets module needs it). Native mode: the binary must exist.</summary>
    public static SetupCheck CheckPorytiles(PorytilesSettings s)
    {
        if (string.IsNullOrWhiteSpace(s.Executable)) return new SetupCheck(true, "Skipped – the Tilesets module will ask for it.", Skipped: true);
        if (s.Mode == PorytilesMode.Wsl) return new SetupCheck(true, $"Will run inside WSL ({s.WslDistro}). Use \"Check\" in the Project module to test it.");
        var exe = s.Executable.StartsWith("~/", StringComparison.Ordinal)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), s.Executable[2..])
            : s.Executable;
        if (File.Exists(exe)) return new SetupCheck(true, "OK");
        if (FindInPath(exe) is { } found) return new SetupCheck(true, "OK – found on PATH: " + found);
        return new SetupCheck(false, "Porytiles: file not found (leave empty to skip).");
    }

    /// <summary>Full path of a program on PATH, or null.</summary>
    public static string? FindInPath(string name)
    {
        if (name.Contains(Path.DirectorySeparatorChar) || name.Contains('/')) return null;
        var exts = OperatingSystem.IsWindows() ? new[] { "", ".exe", ".cmd", ".bat" } : new[] { "" };
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            foreach (var ext in exts)
            {
                try
                {
                    var p = Path.Combine(dir.Trim(), name + ext);
                    if (File.Exists(p)) return p;
                }
                catch (ArgumentException)
                {
                }
            }
        return null;
    }

    /// <summary>Writes the choices into the settings (project tools + defaults) and marks the setup as done.</summary>
    public static void Apply(ToolkitSettings settings, string projectRoot, ProjectToolSettings tools)
    {
        var root = Path.GetFullPath(projectRoot.Trim().Trim('"'));
        settings.Projects[ToolkitSettings.ProjectKey(root)] = tools;
        if (!string.IsNullOrWhiteSpace(tools.PorymapExe)) settings.DefaultPorymapExe = tools.PorymapExe;
        if (!string.IsNullOrWhiteSpace(tools.EmulatorExe)) settings.DefaultEmulatorExe = tools.EmulatorExe;
        settings.Touch(root);
        settings.SetupCompleted = true;
    }
}
