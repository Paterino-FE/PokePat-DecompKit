using System.Diagnostics;

namespace TilesetEditor.Toolkit;

public sealed record LaunchResult(bool Started, string Message);

/// <summary>
/// Hand-off to Porymap. Porymap 6.x has no command-line argument for a project path: it reopens the most recent
/// project from its own porymap.cfg ("recent_project") and the last map of a project from the project's porymap.user.cfg
/// ("recent_map_or_layout"). <see cref="OpenProject"/> therefore points both at the active project (and map) before
/// starting Porymap – only while Porymap is closed, because Porymap writes its configuration back when it exits.
/// Every changed file keeps its previous content as *.toolkit-backup.
/// </summary>
public static class PorymapLauncher
{
    public static IEnumerable<string> CandidateConfigFiles()
    {
        foreach (var baseDir in new[]
                 {
                     Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                     Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                 })
        {
            yield return Path.Combine(baseDir, "pret", "porymap", "porymap.cfg");
            yield return Path.Combine(baseDir, "porymap", "porymap.cfg");
        }
    }

    /// <summary>The project Porymap will reopen on launch, if its config can be read.</summary>
    public static (string? RecentProject, bool ReopenOnLaunch, string? ConfigFile) ReadPorymapState()
    {
        foreach (var f in CandidateConfigFiles())
        {
            if (!File.Exists(f)) continue;
            string? recent = null;
            bool reopen = true;
            foreach (var line in File.ReadLines(f))
            {
                var i = line.IndexOf('=');
                if (i < 0) continue;
                var k = line[..i].Trim();
                var v = line[(i + 1)..].Trim();
                if (k == "recent_project") recent = v.Split(',', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
                else if (k == "reopen_on_launch") reopen = v == "1";
            }
            return (recent, reopen, f);
        }
        return (null, false, null);
    }

    public static bool SamePath(string? a, string? b)
    {
        if (a == null || b == null) return false;
        static string N(string p) => p.Replace('\\', '/').TrimEnd('/').ToLowerInvariant();
        return N(a) == N(b);
    }

    public const string GlobalConfigFile = "porymap.cfg";
    public const string ProjectConfigFile = "porymap.project.cfg";

    /// <summary>True while a Porymap process runs (it would overwrite the configuration on exit).</summary>
    public static bool IsRunning()
    {
        try { return Process.GetProcessesByName("porymap").Length > 0; }
        catch (InvalidOperationException) { return false; }
    }

    /// <summary>The path as Porymap (Qt) writes it: forward slashes, e.g. //wsl.localhost/Ubuntu/home/me/hack.</summary>
    public static string ToPorymapPath(string path) => Path.GetFullPath(path).Replace('\\', '/').TrimEnd('/');

    /// <summary>Sets "key=value" in a Porymap .cfg text (keeps every other line and the line endings; adds the key if missing).</summary>
    public static string SetKey(string cfg, string key, string value)
    {
        string nl = cfg.Contains("\r\n") ? "\r\n" : "\n";
        var lines = cfg.Length == 0 ? new List<string>() : cfg.Replace("\r\n", "\n").Split('\n').ToList();
        if (lines.Count > 0 && lines[^1].Length == 0) lines.RemoveAt(lines.Count - 1);
        int idx = lines.FindIndex(l => l.IndexOf('=') is var eq && eq > 0 && l[..eq].Trim() == key);
        if (idx >= 0) lines[idx] = $"{key}={value}";
        else lines.Add($"{key}={value}");
        return string.Join(nl, lines) + nl;
    }

    /// <summary>
    /// Points Porymap at <paramref name="projectRoot"/> (and <paramref name="mapName"/>, if given): recent_project and
    /// reopen_on_launch in the global porymap.cfg, recent_map_or_layout in the project's porymap.user.cfg, and a
    /// porymap.project.cfg with base_game_version=pokeemerald if the project has none (otherwise Porymap asks on first open).
    /// Returns the files that were changed.
    /// </summary>
    public static List<string> PrepareProject(string projectRoot, string? mapName, string? globalConfigOverride = null)
    {
        var changed = new List<string>();
        string? global = globalConfigOverride ?? CandidateConfigFiles().FirstOrDefault(File.Exists)
                         ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "pret", "porymap", GlobalConfigFile);
        var before = File.Exists(global) ? File.ReadAllText(global) : "";
        var after = SetKey(SetKey(SetKey(before, "recent_project", ToPorymapPath(projectRoot)), "reopen_on_launch", "1"), "project_manually_closed", "0");
        if (WriteIfChanged(global, before, after)) changed.Add(global);

        if (!string.IsNullOrWhiteSpace(mapName))
        {
            var user = Path.Combine(projectRoot, PorymapBridgeInstaller.UserConfigFile);
            var ub = File.Exists(user) ? File.ReadAllText(user) : "";
            if (WriteIfChanged(user, ub, SetKey(ub, "recent_map_or_layout", mapName))) changed.Add(user);
        }

        var project = Path.Combine(projectRoot, ProjectConfigFile);
        if (!File.Exists(project))
        {
            File.WriteAllText(project, "base_game_version=pokeemerald\n");
            changed.Add(project);
        }
        return changed;
    }

    private static bool WriteIfChanged(string path, string before, string after)
    {
        if (before == after) return false;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        if (before.Length > 0) File.WriteAllText(path + ".toolkit-backup", before);
        var tmp = path + ".tileseteditor-tmp";
        File.WriteAllText(tmp, after, new System.Text.UTF8Encoding(false));
        File.Move(tmp, path, overwrite: true);
        return true;
    }

    /// <summary>
    /// Opens the project (and a map) in Porymap: prepares Porymap's configuration and starts it. If Porymap is already
    /// running nothing is changed – it would overwrite the settings when it exits – and the result says how to switch there.
    /// </summary>
    public static LaunchResult OpenProject(string? porymapExe, string projectRoot, string? mapName = null)
    {
        if (string.IsNullOrWhiteSpace(porymapExe) || !File.Exists(porymapExe))
            return new LaunchResult(false,
                "Porymap is not configured. Enter the path to porymap.exe in the project view " +
                "(Download: https://github.com/huderlem/porymap/releases).");
        if (IsRunning())
            return new LaunchResult(false,
                "Porymap is already open. Close it and try again, so PokePat can open " +
                (mapName != null ? $"{mapName} of this project" : "this project") +
                $" – or pick it in Porymap yourself (File → Open Project…: {projectRoot}" + (mapName != null ? $", map list: {mapName}" : "") + ").");
        try
        {
            PrepareProject(projectRoot, mapName);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new LaunchResult(false, "Could not prepare Porymap's settings: " + ex.Message);
        }
        try
        {
            Process.Start(new ProcessStartInfo(porymapExe) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(porymapExe)! });
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            return new LaunchResult(false, $"Could not start Porymap: {ex.Message}");
        }
        return new LaunchResult(true, mapName != null ? $"Porymap opens {mapName}." : "Porymap opens this project.");
    }

    public static LaunchResult Launch(string? porymapExe, string projectRoot)
    {
        if (string.IsNullOrWhiteSpace(porymapExe) || !File.Exists(porymapExe))
            return new LaunchResult(false,
                "Porymap is not configured. Enter the path to porymap.exe in the project view " +
                "(Download: https://github.com/huderlem/porymap/releases).");
        try
        {
            Process.Start(new ProcessStartInfo(porymapExe)
            {
                UseShellExecute = false,
                WorkingDirectory = Path.GetDirectoryName(porymapExe)!,
            });
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            return new LaunchResult(false, $"Could not start Porymap: {ex.Message}");
        }

        var (recent, reopen, _) = ReadPorymapState();
        string msg;
        if (reopen && SamePath(recent, projectRoot))
            msg = "Porymap was started and opens the last used project – that is the active project folder.";
        else
            msg = "Porymap was started. This Porymap version cannot take a project path at startup. " +
                  $"Choose \"File → Open Project…\" there and open this folder:\n{projectRoot}" +
                  (recent != null ? $"\n\n(Porymap last opened: {recent})" : "");
        return new LaunchResult(true, msg);
    }
}

public static class EmulatorLauncher
{
    public static LaunchResult Launch(string? emulatorExe, string romPath)
    {
        if (string.IsNullOrWhiteSpace(emulatorExe) || !File.Exists(emulatorExe))
            return new LaunchResult(false, "No emulator configured.");
        if (!File.Exists(romPath)) return new LaunchResult(false, $"ROM not found: {romPath}");
        try
        {
            var psi = new ProcessStartInfo(emulatorExe) { UseShellExecute = false };
            psi.ArgumentList.Add(romPath);
            Process.Start(psi);
            return new LaunchResult(true, $"Emulator started: {Path.GetFileName(emulatorExe)} {romPath}");
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            return new LaunchResult(false, $"Could not start emulator: {ex.Message}");
        }
    }
}
