using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using TilesetEditor.Compiler;

namespace TilesetEditor.Toolkit;

public enum RomState
{
    /// <summary>The build succeeded and wrote a ROM whose content differs from the one before the build.</summary>
    Rebuilt,
    /// <summary>The build succeeded; make had nothing to do and the ROM is byte-identical to before.</summary>
    UpToDate,
    /// <summary>No confirmed ROM (failed build, missing file, cancelled).</summary>
    None,
}

public sealed record RomFingerprint(bool Exists, long Length, DateTime LastWriteUtc, string? Sha256)
{
    public static RomFingerprint Of(string path)
    {
        var fi = new FileInfo(path);
        if (!fi.Exists) return new RomFingerprint(false, 0, default, null);
        using var s = File.OpenRead(path);
        return new RomFingerprint(true, fi.Length, fi.LastWriteTimeUtc, Convert.ToHexString(SHA256.HashData(s)));
    }
}

public sealed class BuildRequest
{
    public required string ProjectRoot { get; init; }
    public required ProjectToolSettings Settings { get; init; }
    /// <summary>ROM path relative to the project root (e.g. pokeemerald.gba).</summary>
    public required string RomRelativePath { get; init; }
}

public sealed class BuildResult
{
    public required bool Success { get; init; }
    public required int ExitCode { get; init; }
    public required TimeSpan Duration { get; init; }
    public required RomState RomState { get; init; }
    /// <summary>Only set when the build succeeded and the ROM was verified after the build. Never set on failure.</summary>
    public string? ConfirmedRomPath { get; init; }
    public string? Message { get; init; }
    public IReadOnlyList<string> ErrorLines { get; init; } = [];
    public bool Cancelled { get; init; }
}

public sealed class BuildAlreadyRunningException(string root)
    : Exception($"A build is already running for '{root}'. Please wait until it has finished.");

/// <summary>
/// Runs the project's build command in its root (inside WSL or with cmd.exe), streams the output, prevents
/// concurrent builds of the same folder (also across app instances) and confirms the ROM by fingerprinting it
/// before and after. A ROM is only ever reported for a successful build.
/// </summary>
public sealed partial class BuildRunner
{
    private static readonly ConcurrentDictionary<string, byte> Running = new(StringComparer.OrdinalIgnoreCase);

    public static string LockDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TilesetEditor", "locks");

    public static bool IsRunning(string root) => Running.ContainsKey(ToolkitSettings.ProjectKey(root));

    [GeneratedRegex(@"(\berror\b|Error \d+|\*\*\*|undefined reference|No such file|Failed to|fatal)", RegexOptions.IgnoreCase)]
    private static partial Regex ErrorLine();

    /// <summary>The process invocation for the configured environment.</summary>
    public static (string File, List<string> Args, string? WorkingDirectory) BuildCommandLine(string root, ProjectToolSettings s)
    {
        if (s.BuildEnvironment == BuildEnvironment.Wsl)
        {
            var linuxRoot = WslPath.ToLinux(Path.GetFullPath(root), s.BuildWslDistro);
            // bash -lc loads the login profile (PATH to devkitARM/arm-none-eabi etc.) like an interactive terminal.
            return ("wsl.exe", ["-d", s.BuildWslDistro, "--cd", linuxRoot, "--exec", "bash", "-lc", s.BuildCommand], null);
        }
        if (s.BuildEnvironment == BuildEnvironment.Native)
            return ("bash", ["-lc", s.BuildCommand], root);
        return ("cmd.exe", ["/d", "/s", "/c", s.BuildCommand], root);
    }

    public async Task<BuildResult> RunAsync(BuildRequest req, Action<string> onOutput, CancellationToken ct)
    {
        var key = ToolkitSettings.ProjectKey(req.ProjectRoot);
        if (!Running.TryAdd(key, 0)) throw new BuildAlreadyRunningException(req.ProjectRoot);
        FileStream? lockFile = null;
        try
        {
            Directory.CreateDirectory(LockDirectory);
            var lockPath = Path.Combine(LockDirectory, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key.ToLowerInvariant())))[..16] + ".lock");
            try
            {
                lockFile = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose);
            }
            catch (IOException)
            {
                throw new BuildAlreadyRunningException(req.ProjectRoot);
            }
            return await RunLockedAsync(req, onOutput, ct).ConfigureAwait(false);
        }
        finally
        {
            lockFile?.Dispose();
            Running.TryRemove(key, out _);
        }
    }

    private async Task<BuildResult> RunLockedAsync(BuildRequest req, Action<string> onOutput, CancellationToken ct)
    {
        var romPath = Path.Combine(req.ProjectRoot, req.RomRelativePath.Replace('/', Path.DirectorySeparatorChar));
        var before = RomFingerprint.Of(romPath);
        var (file, args, wd) = BuildCommandLine(req.ProjectRoot, req.Settings);
        onOutput($"> {file} {string.Join(' ', args.Select(a => a.Contains(' ') ? $"\"{a}\"" : a))}");
        if (before.Exists) onOutput($"(existing ROM before the build: {before.LastWriteUtc.ToLocalTime():g}, SHA-256 {before.Sha256![..12]}…)");

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

        var errors = new List<string>();
        var sw = Stopwatch.StartNew();
        using var p = new Process { StartInfo = psi };
        void Line(string? l)
        {
            if (l == null) return;
            if (ErrorLine().IsMatch(l)) lock (errors) errors.Add(l);
            onOutput(l);
        }
        p.OutputDataReceived += (_, e) => Line(e.Data);
        p.ErrorDataReceived += (_, e) => Line(e.Data);
        try
        {
            p.Start();
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            return new BuildResult
            {
                Success = false, ExitCode = -1, Duration = sw.Elapsed, RomState = RomState.None,
                Message = $"Could not start the build environment ({file}): {ex.Message}",
            };
        }
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        try
        {
            await p.WaitForExitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try { p.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            p.WaitForExit();
            return new BuildResult
            {
                Success = false, ExitCode = -1, Duration = sw.Elapsed, RomState = RomState.None, Cancelled = true,
                Message = "Build cancelled. There is no new result; an existing ROM may be incomplete or outdated.",
            };
        }
        p.WaitForExit();

        if (p.ExitCode != 0)
        {
            return new BuildResult
            {
                Success = false, ExitCode = p.ExitCode, Duration = sw.Elapsed, RomState = RomState.None,
                ErrorLines = errors.TakeLast(30).ToList(),
                Message = $"Build failed (exit code {p.ExitCode})." +
                          (before.Exists ? " The existing ROM comes from an earlier build and is NOT the result of this build." : ""),
            };
        }

        var after = RomFingerprint.Of(romPath);
        if (!after.Exists)
        {
            return new BuildResult
            {
                Success = false, ExitCode = 0, Duration = sw.Elapsed, RomState = RomState.None,
                Message = $"The build command reports success, but {req.RomRelativePath} does not exist. Please check the ROM path or build command.",
            };
        }
        bool changed = !before.Exists || before.Sha256 != after.Sha256;
        return new BuildResult
        {
            Success = true,
            ExitCode = 0,
            Duration = sw.Elapsed,
            RomState = changed ? RomState.Rebuilt : RomState.UpToDate,
            ConfirmedRomPath = romPath,
            Message = changed
                ? $"Build succeeded, ROM rebuilt: {romPath}"
                : $"Build succeeded, nothing to rebuild – the ROM is up to date (content unchanged): {romPath}",
        };
    }
}
