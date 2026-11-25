using System.Text.RegularExpressions;

namespace TilesetEditor.Compiler;

public sealed record PorytilesVersion(string Version, string Raw)
{
    public bool Matches(string expected) => string.Equals(Version, expected, StringComparison.Ordinal);
}

/// <summary>Builds and runs Porytiles 2.x command lines. Knows nothing about tilesets; see <see cref="SandboxCompiler"/>.</summary>
public sealed partial class PorytilesClient(PorytilesSettings settings, IProcessRunner? runner = null)
{
    private readonly IProcessRunner _runner = runner ?? new ProcessRunner();

    public PorytilesSettings Settings => settings;

    [GeneratedRegex(@"porytiles\s+(\d+\.\d+\.\d+(?:[-+][\w.]+)?)")]
    private static partial Regex VersionRegex();

    /// <summary>Path as Porytiles will see it.</summary>
    public string ToToolPath(string windowsPath) =>
        settings.Mode == PorytilesMode.Wsl ? WslPath.ToLinux(Path.GetFullPath(windowsPath), settings.WslDistro) : Path.GetFullPath(windowsPath);

    public async Task<ProcessResult> RunAsync(IReadOnlyList<string> args, CancellationToken ct)
    {
        string file;
        var all = new List<string>();
        if (settings.Mode == PorytilesMode.Wsl)
        {
            file = "wsl.exe";
            all.AddRange(["-d", settings.WslDistro, "--exec", "/usr/bin/env", "COLUMNS=200"]);
            // "~" is expanded by a shell only; resolve it through $HOME via sh -c without passing user data through the shell.
            if (settings.Executable.StartsWith("~/", StringComparison.Ordinal))
            {
                all.AddRange(["sh", "-c", "exec \"$HOME/$0\" \"$@\"", settings.Executable[2..]]);
            }
            else
            {
                all.Add(settings.Executable);
            }
        }
        else
        {
            file = settings.Executable;
        }
        all.AddRange(args);
        // Wide output keeps the location markers on one line for the diagnostics parser.
        return await _runner.RunAsync(file, all, settings.Timeout, ct, new Dictionary<string, string> { ["COLUMNS"] = "200" }).ConfigureAwait(false);
    }

    /// <summary>Runs "porytiles --version". Throws with a readable message if the tool is not usable.</summary>
    public async Task<PorytilesVersion> GetVersionAsync(CancellationToken ct)
    {
        ProcessResult r;
        try
        {
            r = await RunAsync(["--version"], ct).ConfigureAwait(false);
        }
        catch (InvalidOperationException ex)
        {
            throw new PorytilesUnavailableException(
                settings.Mode == PorytilesMode.Wsl
                    ? $"Could not start WSL ({ex.Message}). Is WSL installed with the distribution '{settings.WslDistro}'?"
                    : ex.Message);
        }
        var m = VersionRegex().Match(r.Combined);
        if (r.ExitCode != 0 || !m.Success)
        {
            throw new PorytilesUnavailableException(
                $"Porytiles was not found at '{settings.Executable}' or does not start (exit {r.ExitCode}).\n" +
                $"Output: {Trim(r.Combined)}\n" +
                "Setup: run tools\\setup-porytiles.ps1 or adjust the path in the settings.");
        }
        return new PorytilesVersion(m.Groups[1].Value, r.Combined.Trim());
    }

    private static string Trim(string s) => s.Length > 600 ? s[..600] + " …" : s.Trim();
}

public sealed class PorytilesUnavailableException(string message) : Exception(message);
