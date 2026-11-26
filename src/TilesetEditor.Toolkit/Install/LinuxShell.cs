using System.Diagnostics;
using System.Text;

namespace TilesetEditor.Toolkit.Install;

public sealed record ShellResult(int ExitCode, string Output)
{
    public bool Ok => ExitCode == 0;
}

/// <summary>
/// Runs bash scripts where the project lives: inside a WSL distribution (Windows) or directly (Linux). Used by "Get started"
/// for checks, git and chmod; downloads and unpacking happen in .NET so no curl/unzip is needed.
/// </summary>
public sealed class LinuxShell(bool useWsl, string distro)
{
    public bool UseWsl { get; } = useWsl;
    public string Distro { get; } = distro;

    /// <summary>Single-quotes a value for bash.</summary>
    public static string Quote(string s) => "'" + s.Replace("'", "'\\''") + "'";

    /// <summary>A path for bash: "~/x" keeps the home directory expandable, everything else is quoted.</summary>
    public static string QuotePath(string path) =>
        path == "~" ? "\"$HOME\"" : path.StartsWith("~/", StringComparison.Ordinal) ? "\"$HOME\"/" + Quote(path[2..]) : Quote(path);

    private ProcessStartInfo Start(string script, bool redirect)
    {
        var psi = new ProcessStartInfo(UseWsl ? "wsl.exe" : "bash")
        {
            UseShellExecute = false,
            RedirectStandardOutput = redirect,
            RedirectStandardError = redirect,
            CreateNoWindow = redirect,
        };
        if (redirect)
        {
            psi.StandardOutputEncoding = Encoding.UTF8;
            psi.StandardErrorEncoding = Encoding.UTF8;
        }
        if (UseWsl) foreach (var a in new[] { "-d", Distro, "--cd", "~", "--exec", "bash" }) psi.ArgumentList.Add(a);
        psi.ArgumentList.Add("-lc");
        psi.ArgumentList.Add(script);
        psi.Environment["WSL_UTF8"] = "1";
        psi.Environment["GIT_TERMINAL_PROMPT"] = "0";
        psi.Environment["LC_ALL"] = "C.UTF-8";
        return psi;
    }

    /// <summary>Runs <paramref name="script"/> and collects stdout+stderr; every line (also git's \r progress lines) goes to <paramref name="onLine"/>.</summary>
    public async Task<ShellResult> RunAsync(string script, Action<string>? onLine = null, CancellationToken ct = default)
    {
        var sb = new StringBuilder();
        using var p = new Process { StartInfo = Start(script, redirect: true) };
        try { p.Start(); }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return new ShellResult(-1, (UseWsl ? "WSL could not be started: " : "bash could not be started: ") + ex.Message);
        }
        async Task Pump(StreamReader r)
        {
            var line = new StringBuilder();
            var buf = new char[1024];
            int n;
            while ((n = await r.ReadAsync(buf, ct).ConfigureAwait(false)) > 0)
                for (int i = 0; i < n; i++)
                {
                    char c = buf[i];
                    if (c is '\n' or '\r')
                    {
                        if (line.Length == 0) continue;
                        var s = line.ToString();
                        line.Clear();
                        if (c == '\n') lock (sb) sb.AppendLine(s);
                        onLine?.Invoke(s);
                    }
                    else line.Append(c);
                }
            if (line.Length > 0)
            {
                lock (sb) sb.AppendLine(line.ToString());
                onLine?.Invoke(line.ToString());
            }
        }
        using var reg = ct.Register(() => { try { p.Kill(entireProcessTree: true); } catch (InvalidOperationException) { } });
        await Task.WhenAll(Pump(p.StandardOutput), Pump(p.StandardError)).ConfigureAwait(false);
        await p.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        return new ShellResult(p.ExitCode, sb.ToString().Replace("\0", ""));
    }

    /// <summary>
    /// Runs a script the user has to interact with (sudo password). In the Windows app it opens its own console window;
    /// from a terminal (<paramref name="newWindow"/> false) it uses that terminal. Returns the exit code.
    /// </summary>
    public async Task<int> RunInteractiveAsync(string script, bool newWindow, CancellationToken ct = default)
    {
        var psi = Start(script, redirect: false);
        if (newWindow && OperatingSystem.IsWindows())
        {
            // A console program started from a GUI program without redirection gets its own console window.
            psi.CreateNoWindow = false;
        }
        using var p = Process.Start(psi) ?? throw new InvalidOperationException("The console could not be opened.");
        await p.WaitForExitAsync(ct).ConfigureAwait(false);
        return p.ExitCode;
    }

    /// <summary>Absolute Linux path ("~" expanded) – also for folders that do not exist yet.</summary>
    public async Task<string?> ResolveAsync(string linuxPath, CancellationToken ct = default)
    {
        var r = await RunAsync("readlink -m -- " + QuotePath(linuxPath.Trim()), null, ct).ConfigureAwait(false);
        var line = r.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).LastOrDefault();
        return r.Ok && line != null && line.StartsWith('/') ? line : null;
    }

    /// <summary>How this program reaches an absolute Linux path: \\wsl.localhost\Distro\… on Windows, unchanged on Linux.</summary>
    public string ToHostPath(string absoluteLinuxPath) =>
        UseWsl ? @"\\wsl.localhost\" + Distro + absoluteLinuxPath.Replace('/', '\\') : absoluteLinuxPath;
}
