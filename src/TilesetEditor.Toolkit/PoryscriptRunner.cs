using System.Diagnostics;
using System.Text;
using TilesetEditor.Compiler;

namespace TilesetEditor.Toolkit;

public sealed record PoryscriptResult(bool Success, string? IncText, string Output, string Version);

/// <summary>
/// Compiles Poryscript with the project's own tools/poryscript (same flags as the Makefile rule
/// data/%.inc: data/%.pory) in a throw-away folder. Runs in WSL or natively, like the ROM build.
/// </summary>
public sealed class PoryscriptRunner(ProjectToolSettings settings)
{
    public const string RelativeInput = "data/scripts/toolkit_events.pory";
    public const string RelativeOutput = "data/scripts/toolkit_events.inc";

    public static bool IsAvailable(string projectRoot) =>
        File.Exists(Path.Combine(projectRoot, "tools", "poryscript", "poryscript")) ||
        File.Exists(Path.Combine(projectRoot, "tools", "poryscript", "poryscript.exe"));

    public async Task<PoryscriptResult> CompileAsync(string projectRoot, string poryText, CancellationToken ct)
    {
        var work = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TilesetEditor", "work",
            "events-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N")[..6]);
        var input = Path.Combine(work, RelativeInput.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(input)!);
        await File.WriteAllTextAsync(input, poryText, new UTF8Encoding(false), ct).ConfigureAwait(false);
        var output = Path.Combine(work, RelativeOutput.Replace('/', Path.DirectorySeparatorChar));

        string file;
        var args = new List<string>();
        string? wd = null;
        if (settings.BuildEnvironment == BuildEnvironment.Wsl)
        {
            var proj = WslPath.ToLinux(Path.GetFullPath(projectRoot), settings.BuildWslDistro);
            file = "wsl.exe";
            args.AddRange(["-d", settings.BuildWslDistro, "--cd", WslPath.ToLinux(work, settings.BuildWslDistro), "--exec",
                proj + "/tools/poryscript/poryscript"]);
            args.AddRange(["-i", RelativeInput, "-o", RelativeOutput,
                "-fc", proj + "/tools/poryscript/font_config.json", "-cc", proj + "/tools/poryscript/command_config.json"]);
        }
        else
        {
            file = Path.Combine(projectRoot, "tools", "poryscript", OperatingSystem.IsWindows() ? "poryscript.exe" : "poryscript");
            wd = work;
            args.AddRange(["-i", RelativeInput, "-o", RelativeOutput,
                "-fc", Path.Combine(projectRoot, "tools", "poryscript", "font_config.json"),
                "-cc", Path.Combine(projectRoot, "tools", "poryscript", "command_config.json")]);
        }

        var (exit, text) = await Run(file, args, wd, ct).ConfigureAwait(false);
        var (_, versionText) = await Run(file, settings.BuildEnvironment == BuildEnvironment.Wsl ? [.. args.Take(6), "-v"] : ["-v"], wd, ct).ConfigureAwait(false);
        if (exit != 0 || !File.Exists(output))
            return new PoryscriptResult(false, null, text.Trim().Length > 0 ? text.Trim() : $"Poryscript exited with code {exit}.", versionText.Trim());
        return new PoryscriptResult(true, await File.ReadAllTextAsync(output, ct).ConfigureAwait(false), text.Trim(), versionText.Trim());
    }

    private static async Task<(int Exit, string Output)> Run(string file, IReadOnlyList<string> args, string? wd, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(file)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        if (wd != null) psi.WorkingDirectory = wd;
        foreach (var a in args) psi.ArgumentList.Add(a);
        try
        {
            using var p = Process.Start(psi)!;
            var o = p.StandardOutput.ReadToEndAsync(ct);
            var e = p.StandardError.ReadToEndAsync(ct);
            await p.WaitForExitAsync(ct).ConfigureAwait(false);
            return (p.ExitCode, await o.ConfigureAwait(false) + await e.ConfigureAwait(false));
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            return (-1, $"Could not start Poryscript ({file}): {ex.Message}");
        }
    }
}
