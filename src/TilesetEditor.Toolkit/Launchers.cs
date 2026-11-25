using System.Diagnostics;

namespace TilesetEditor.Toolkit;

public sealed record LaunchResult(bool Started, string Message);

/// <summary>
/// Hand-off to Porymap. Porymap 6.x has no command-line argument for a project path: it reopens the most recent
/// project from its own porymap.cfg ("recent_project"). The toolkit therefore starts Porymap, reports which project
/// Porymap will reopen (read-only) and shows the active path so it can be chosen via File → Open Project.
/// The toolkit never writes Porymap's configuration.
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
