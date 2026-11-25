using System.Diagnostics;
using System.IO.Compression;
using TilesetEditor.Compiler;

namespace TilesetEditor.Toolkit.Install;

/// <summary>What "Get started" can set up, in the order it is installed.</summary>
public enum InstallComponent { Wsl, BuildTools, Expansion, Poryscript, Porytiles, Porymap, Emulator }

public enum InstallState
{
    Unknown,
    Ready,
    Missing,
    Failed,
    /// <summary>The user has to finish something (WSL first start, restart, password window).</summary>
    NeedsUser,
    /// <summary>Not offered on this system (e.g. WSL on Linux).</summary>
    NotAvailable,
}

public sealed record ComponentStatus(InstallState State, string Message)
{
    public bool IsReady => State == InstallState.Ready;
}

public sealed class InstallOptions
{
    /// <summary>Windows: everything Linux-side happens in this WSL distribution. Linux: directly.</summary>
    public bool UseWsl { get; set; } = OperatingSystem.IsWindows();
    public string Distro { get; set; } = "Ubuntu";
    /// <summary>Linux path of the project ("~" allowed). Inside WSL the build is much faster than on C:\.</summary>
    public string ProjectPath { get; set; } = "~/pokeemerald-expansion";
    /// <summary>Folder for the Windows programs (Porymap, mGBA).</summary>
    public string ToolsFolder { get; set; } = DefaultToolsFolder;
    public string ExpansionRepo { get; set; } = "https://github.com/rh-hideout/pokeemerald-expansion";
    /// <summary>null = the latest release.</summary>
    public string? ExpansionTag { get; set; }
    public string Branch { get; set; } = "pokepat";
    /// <summary>Already configured programs count as installed.</summary>
    public string? ExistingPorymap { get; set; }
    public string? ExistingEmulator { get; set; }

    /// <summary>Options for the current project of the settings (WSL distribution and Linux path when it lives in WSL).</summary>
    public static InstallOptions FromSettings(ToolkitSettings s)
    {
        var o = new InstallOptions
        {
            ToolsFolder = s.ToolsFolder ?? DefaultToolsFolder,
            ExistingPorymap = s.DefaultPorymapExe,
            ExistingEmulator = s.DefaultEmulatorExe,
        };
        var last = s.LastProject;
        if (last != null && s.Projects.TryGetValue(ToolkitSettings.ProjectKey(last), out var t))
        {
            o.Distro = t.BuildWslDistro;
            if (!string.IsNullOrWhiteSpace(t.PorymapExe)) o.ExistingPorymap = t.PorymapExe;
            if (!string.IsNullOrWhiteSpace(t.EmulatorExe)) o.ExistingEmulator = t.EmulatorExe;
        }
        if (string.IsNullOrWhiteSpace(last)) return o;
        if (!o.UseWsl) { o.ProjectPath = last; return o; }
        var m = System.Text.RegularExpressions.Regex.Match(last, @"^[\\/]{2}(?:wsl\$|wsl\.localhost)[\\/]([^\\/]+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (!m.Success) return o;
        try
        {
            o.Distro = m.Groups[1].Value;
            o.ProjectPath = WslPath.ToLinux(last, o.Distro);
        }
        catch (ArgumentException)
        {
        }
        return o;
    }

    public static string DefaultToolsFolder => OperatingSystem.IsWindows()
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PokePat", "tools")
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share", "pokepat", "tools");
}

/// <summary>
/// One-click setup for new hackers: checks and installs WSL, the build tools, pokeemerald-expansion (latest release, own
/// branch), Poryscript (wired into the Makefile), the pinned Porytiles, Porymap and mGBA – each step can also be run alone.
/// </summary>
public sealed partial class Installer(InstallOptions options)
{
    public const string PorytilesUrl = "https://github.com/grunt-lucas/porytiles/releases/download/v2.0.0/porytiles-linux-amd64.zip";
    public const string PorytilesSha256 = "68C904C3FF707ED2D6DE0372073F165E627EC3EE7855EEDBB3363184CC9212A4";
    public const string PoryscriptFallbackUrl = "https://github.com/huderlem/poryscript/releases/download/3.6.1/poryscript-linux.zip";
    public const string AptPackages = "build-essential binutils-arm-none-eabi gcc-arm-none-eabi libnewlib-arm-none-eabi libpng-dev git python3";

    public InstallOptions Options { get; } = options;
    public LinuxShell Shell { get; } = new(options.UseWsl, options.Distro);

    /// <summary>Absolute Linux path of the project once resolved.</summary>
    public string? ProjectLinuxPath { get; private set; }
    /// <summary>The project as this program opens it (\\wsl.localhost\… on Windows).</summary>
    public string? ProjectHostPath => ProjectLinuxPath != null ? Shell.ToHostPath(ProjectLinuxPath) : null;
    public string? PorymapExe { get; private set; }
    public string? EmulatorExe { get; private set; }
    public bool PorytilesReady { get; private set; }

    public static IReadOnlyList<InstallComponent> All { get; } = Enum.GetValues<InstallComponent>();

    public static string Title(InstallComponent c) => c switch
    {
        InstallComponent.Wsl => "WSL with Ubuntu",
        InstallComponent.BuildTools => "Build tools",
        InstallComponent.Expansion => "pokeemerald-expansion",
        InstallComponent.Poryscript => "Poryscript",
        InstallComponent.Porytiles => "Porytiles " + PorytilesSettings.PinnedVersion,
        InstallComponent.Porymap => "Porymap",
        _ => "Emulator (mGBA)",
    };

    public static string Description(InstallComponent c) => c switch
    {
        InstallComponent.Wsl => "Linux inside Windows – the game is built there.",
        InstallComponent.BuildTools => "make, the GBA compiler (arm-none-eabi), libpng, git, python3. Asks for your Linux password once.",
        InstallComponent.Expansion => "Your game: the latest pokeemerald-expansion release, on your own branch \"pokepat\".",
        InstallComponent.Poryscript => "Script language for events and dialogs; added to the project's Makefile.",
        InstallComponent.Porytiles => "Turns pictures into tilesets (Tilesets module).",
        InstallComponent.Porymap => "Map editor; PokéPat opens it with your project and map.",
        _ => "Plays the ROM after \"Build ROM\".",
    };

    public bool Applies(InstallComponent c) => c switch
    {
        InstallComponent.Wsl => Options.UseWsl,
        InstallComponent.Porymap or InstallComponent.Emulator => OperatingSystem.IsWindows(),
        _ => true,
    };

    private string HostTools(string sub) => Path.Combine(Options.ToolsFolder, sub);

    // ------------------------------------------------------------------ checks

    public async Task<ComponentStatus> CheckAsync(InstallComponent c, CancellationToken ct = default)
    {
        if (!Applies(c))
            return new ComponentStatus(InstallState.NotAvailable, c switch
            {
                InstallComponent.Wsl => "Not needed – this is Linux.",
                InstallComponent.Porymap => "Install Porymap yourself on this system (github.com/huderlem/porymap).",
                _ => "Install mGBA with your package manager (e.g. mgba-qt).",
            });
        try
        {
            if (c != InstallComponent.Wsl && Options.UseWsl && !(await CheckAsync(InstallComponent.Wsl, ct).ConfigureAwait(false)).IsReady)
                return new ComponentStatus(InstallState.Missing, "Needs WSL first.");
            return c switch
            {
                InstallComponent.Wsl => await CheckWslAsync(ct).ConfigureAwait(false),
                InstallComponent.BuildTools => await CheckBuildToolsAsync(ct).ConfigureAwait(false),
                InstallComponent.Expansion => await CheckExpansionAsync(ct).ConfigureAwait(false),
                InstallComponent.Poryscript => await CheckPoryscriptAsync(ct).ConfigureAwait(false),
                InstallComponent.Porytiles => await CheckPorytilesAsync(ct).ConfigureAwait(false),
                InstallComponent.Porymap => CheckPorymap(),
                _ => CheckEmulator(),
            };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new ComponentStatus(InstallState.Failed, ex.Message);
        }
    }

    /// <summary>The installed WSL distributions (empty if WSL is missing). Windows only.</summary>
    public static async Task<List<string>> ListDistrosAsync(CancellationToken ct = default)
    {
        if (!OperatingSystem.IsWindows()) return [];
        try
        {
            var psi = new ProcessStartInfo("wsl.exe") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
            psi.ArgumentList.Add("--list");
            psi.ArgumentList.Add("--quiet");
            psi.Environment["WSL_UTF8"] = "1";
            using var p = Process.Start(psi)!;
            var output = await p.StandardOutput.ReadToEndAsync(ct).ConfigureAwait(false);
            await p.WaitForExitAsync(ct).ConfigureAwait(false);
            if (p.ExitCode != 0) return [];
            return output.Replace("\0", "").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(d => d.Length > 0 && !d.StartsWith("docker-desktop", StringComparison.OrdinalIgnoreCase)).ToList();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return [];
        }
    }

    private bool? _wslOk;

    private async Task<ComponentStatus> CheckWslAsync(CancellationToken ct)
    {
        if (_wslOk == true) return new ComponentStatus(InstallState.Ready, $"Ready ({Options.Distro}).");
        var distros = await ListDistrosAsync(ct).ConfigureAwait(false);
        if (distros.Count == 0) { _wslOk = false; return new ComponentStatus(InstallState.Missing, "WSL is not installed yet."); }
        if (!distros.Contains(Options.Distro, StringComparer.OrdinalIgnoreCase))
        {
            _wslOk = false;
            return new ComponentStatus(InstallState.Missing, $"\"{Options.Distro}\" is not installed (found: {string.Join(", ", distros)}).");
        }
        var r = await Shell.RunAsync("echo ok", null, ct).ConfigureAwait(false);
        _wslOk = r.Ok && r.Output.Contains("ok");
        return _wslOk == true
            ? new ComponentStatus(InstallState.Ready, $"Ready ({Options.Distro}).")
            : new ComponentStatus(InstallState.NeedsUser, $"{Options.Distro} does not start yet – open it once from the Start menu and choose a user name and password.");
    }

    private async Task<ComponentStatus> CheckBuildToolsAsync(CancellationToken ct)
    {
        const string script = """
            m=""
            for c in make gcc g++ git python3 arm-none-eabi-gcc arm-none-eabi-as; do command -v "$c" >/dev/null 2>&1 || m="$m $c"; done
            [ -e /usr/include/png.h ] || m="$m libpng-dev"
            if command -v arm-none-eabi-gcc >/dev/null 2>&1; then
              case "$(arm-none-eabi-gcc -print-file-name=libc.a)" in /*) ;; *) m="$m libnewlib-arm-none-eabi";; esac
            fi
            echo "MISSING:$m"
            """;
        var r = await Shell.RunAsync(script, null, ct).ConfigureAwait(false);
        var line = r.Output.Split('\n').Select(l => l.Trim()).LastOrDefault(l => l.StartsWith("MISSING:", StringComparison.Ordinal));
        if (line == null) return new ComponentStatus(InstallState.Failed, "Could not check: " + Last(r.Output));
        var missing = line["MISSING:".Length..].Trim();
        return missing.Length == 0
            ? new ComponentStatus(InstallState.Ready, "Ready.")
            : new ComponentStatus(InstallState.Missing, "Missing: " + missing.Replace(" ", ", "));
    }

    private async Task<string?> ResolveProjectAsync(CancellationToken ct)
    {
        ProjectLinuxPath ??= await Shell.ResolveAsync(Options.ProjectPath, ct).ConfigureAwait(false);
        return ProjectLinuxPath;
    }

    /// <summary>Forget the resolved project path (after the user changed the folder).</summary>
    public void ResetProject() => ProjectLinuxPath = null;

    private async Task<ComponentStatus> CheckExpansionAsync(CancellationToken ct)
    {
        var p = await ResolveProjectAsync(ct).ConfigureAwait(false);
        if (p == null) return new ComponentStatus(InstallState.Failed, $"\"{Options.ProjectPath}\" is not a valid Linux path.");
        var q = LinuxShell.Quote(p);
        var script = $$"""
            p={{q}}
            if [ -f "$p/Makefile" ] && [ -f "$p/include/constants/flags.h" ]; then
              echo "STATE:READY"
              echo "VERSION:$(git -C "$p" describe --tags --always 2>/dev/null)"
              echo "BRANCH:$(git -C "$p" branch --show-current 2>/dev/null)"
            elif [ -e "$p" ] && [ -n "$(ls -A "$p" 2>/dev/null)" ]; then echo "STATE:NOTEMPTY"
            else echo "STATE:MISSING"; fi
            """;
        var r = await Shell.RunAsync(script, null, ct).ConfigureAwait(false);
        string Value(string key) => r.Output.Split('\n').Select(l => l.Trim()).LastOrDefault(l => l.StartsWith(key + ":", StringComparison.Ordinal))?[(key.Length + 1)..] ?? "";
        return Value("STATE") switch
        {
            "READY" => new ComponentStatus(InstallState.Ready, $"Ready: {p}" +
                (Value("VERSION").Length > 0 ? $" ({Value("VERSION")}" + (Value("BRANCH").Length > 0 ? $", branch {Value("BRANCH")})" : ")") : "")),
            "NOTEMPTY" => new ComponentStatus(InstallState.Failed, $"{p} exists but is not a pokeemerald project – choose an empty or new folder."),
            "MISSING" => new ComponentStatus(InstallState.Missing, $"Will be downloaded to {p}."),
            _ => new ComponentStatus(InstallState.Failed, "Could not check: " + Last(r.Output)),
        };
    }

    private async Task<ComponentStatus> CheckPoryscriptAsync(CancellationToken ct)
    {
        if (!(await CheckExpansionAsync(ct).ConfigureAwait(false)).IsReady) return new ComponentStatus(InstallState.Missing, "Needs the project first.");
        var host = ProjectHostPath!;
        bool binary = File.Exists(Path.Combine(host, "tools", "poryscript", "poryscript"));
        var makefile = Path.Combine(host, "Makefile");
        bool wired = File.Exists(makefile) && PoryscriptMakefile.IsIntegrated(await File.ReadAllTextAsync(makefile, ct).ConfigureAwait(false));
        if (binary && wired)
        {
            var v = await Shell.RunAsync(LinuxShell.Quote(ProjectLinuxPath! + "/tools/poryscript/poryscript") + " -v", null, ct).ConfigureAwait(false);
            return new ComponentStatus(InstallState.Ready, "Ready" + (v.Ok && Last(v.Output).Length > 0 ? $" ({Last(v.Output)})." : "."));
        }
        return new ComponentStatus(InstallState.Missing, binary ? "Installed, but not yet in the Makefile." : "Not in the project yet.");
    }

    private async Task<ComponentStatus> CheckPorytilesAsync(CancellationToken ct)
    {
        var exe = LinuxShell.QuotePath(PorytilesSettings.DefaultWslExecutable);
        var r = await Shell.RunAsync($"[ -x {exe} ] && {exe} --version", null, ct).ConfigureAwait(false);
        PorytilesReady = r.Ok && r.Output.Contains(PorytilesSettings.PinnedVersion);
        return PorytilesReady
            ? new ComponentStatus(InstallState.Ready, $"Ready ({PorytilesSettings.PinnedVersion}).")
            : new ComponentStatus(InstallState.Missing, "Not installed.");
    }

    private ComponentStatus CheckPorymap()
    {
        foreach (var exe in new[] { Options.ExistingPorymap, Path.Combine(HostTools("porymap"), "porymap.exe") })
            if (!string.IsNullOrWhiteSpace(exe) && File.Exists(exe))
            {
                PorymapExe = exe;
                return new ComponentStatus(InstallState.Ready, "Ready: " + exe);
            }
        return new ComponentStatus(InstallState.Missing, $"Will be installed to {HostTools("porymap")}.");
    }

    private ComponentStatus CheckEmulator()
    {
        foreach (var exe in new[] { Options.ExistingEmulator, Path.Combine(HostTools("mgba"), "mGBA.exe") })
            if (!string.IsNullOrWhiteSpace(exe) && File.Exists(exe))
            {
                EmulatorExe = exe;
                return new ComponentStatus(InstallState.Ready, "Ready: " + exe);
            }
        return new ComponentStatus(InstallState.Missing, $"Will be installed to {HostTools("mgba")}.");
    }

    private static string Last(string output) =>
        output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).LastOrDefault() ?? "";

    // ------------------------------------------------------------------ installs

    /// <summary>
    /// Installs one component and checks it again. <paramref name="newWindow"/>: steps that need the user's password open their
    /// own console window (app) instead of using the current terminal (command line).
    /// </summary>
    public async Task<ComponentStatus> InstallAsync(InstallComponent c, Action<string> log, bool newWindow, CancellationToken ct = default)
    {
        if (!Applies(c)) return await CheckAsync(c, ct).ConfigureAwait(false);
        try
        {
            var result = c switch
            {
                InstallComponent.Wsl => await InstallWslAsync(log, ct).ConfigureAwait(false),
                InstallComponent.BuildTools => await InstallBuildToolsAsync(log, newWindow, ct).ConfigureAwait(false),
                InstallComponent.Expansion => await InstallExpansionAsync(log, ct).ConfigureAwait(false),
                InstallComponent.Poryscript => await InstallPoryscriptAsync(log, ct).ConfigureAwait(false),
                InstallComponent.Porytiles => await InstallPorytilesAsync(log, ct).ConfigureAwait(false),
                InstallComponent.Porymap => await InstallPorymapAsync(log, ct).ConfigureAwait(false),
                _ => await InstallEmulatorAsync(log, ct).ConfigureAwait(false),
            };
            if (result != null) return result;
            return await CheckAsync(c, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or HttpRequestException or InvalidDataException
                                       or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            log("Error: " + ex.Message);
            return new ComponentStatus(InstallState.Failed, ex.Message);
        }
    }

    /// <summary>Installs everything that is missing, in order; stops at the first step that needs the user or fails.</summary>
    public async Task<bool> InstallAllAsync(Action<string> log, Action<InstallComponent, ComponentStatus> status, bool newWindow, CancellationToken ct = default)
    {
        foreach (var c in All)
        {
            var s = await CheckAsync(c, ct).ConfigureAwait(false);
            status(c, s);
            if (s.State is InstallState.Ready or InstallState.NotAvailable) continue;
            log($"== {Title(c)}");
            s = await InstallAsync(c, log, newWindow, ct).ConfigureAwait(false);
            status(c, s);
            log(s.IsReady ? $"   {s.Message}" : $"   Stopped: {s.Message}");
            if (!s.IsReady) return false;
        }
        return true;
    }

    private static async Task<ComponentStatus?> InstallWslAsync(Action<string> log, CancellationToken ct)
    {
        log("Installing WSL with Ubuntu (Windows asks for permission) …");
        var psi = new ProcessStartInfo("wsl.exe", "--install -d Ubuntu") { UseShellExecute = true, Verb = "runas" };
        try
        {
            using var p = Process.Start(psi);
            if (p != null) await p.WaitForExitAsync(ct).ConfigureAwait(false);
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            return new ComponentStatus(InstallState.Missing, "Cancelled – WSL needs administrator permission once.");
        }
        return new ComponentStatus(InstallState.NeedsUser,
            "If Windows asks for a restart: restart, then open PokéPat again. Ubuntu asks for a user name and password once – choose any (remember the password). Then click \"Check again\".");
    }

    private async Task<ComponentStatus?> InstallBuildToolsAsync(Action<string> log, bool newWindow, CancellationToken ct)
    {
        var pause = newWindow ? "read -r -p 'Press Enter to close this window.' _" : "true";
        var script = $"""
            echo '== PokePat DecompKit: installing the build tools =='
            if ! command -v apt-get >/dev/null 2>&1; then
              echo 'This Linux has no apt-get. Install with your package manager: make, gcc, arm-none-eabi-gcc + newlib, libpng (dev), git, python3.'
              {pause}; exit 2
            fi
            echo 'Enter your Linux password if asked (nothing is shown while typing).'
            if sudo apt-get update && sudo apt-get install -y {AptPackages}; then
              echo; echo 'Done – you can go back to PokePat.'; sleep 2
            else
              echo; echo 'The installation failed – see the messages above.'; {pause}; exit 1
            fi
            """;
        log(newWindow ? "A console window opens – enter your Linux password there." : "Running apt-get (sudo asks for your password) …");
        int code = await Shell.RunInteractiveAsync(script, newWindow, ct).ConfigureAwait(false);
        log($"apt-get finished (exit code {code}).");
        return null;
    }

    private async Task<ComponentStatus?> InstallExpansionAsync(Action<string> log, CancellationToken ct)
    {
        var state = await CheckExpansionAsync(ct).ConfigureAwait(false);
        if (state.State != InstallState.Missing) return state;
        var tag = Options.ExpansionTag;
        if (tag == null && Options.ExpansionRepo.Contains("github.com/rh-hideout/pokeemerald-expansion", StringComparison.OrdinalIgnoreCase))
        {
            log("Looking up the latest pokeemerald-expansion release …");
            tag = (await GitHubReleases.LatestAsync("rh-hideout/pokeemerald-expansion", ct).ConfigureAwait(false))?.Tag;
            log(tag != null ? $"Latest release: {tag}" : "GitHub could not be asked – using the default branch.");
        }
        var p = LinuxShell.Quote(ProjectLinuxPath!);
        var script = $"""
            set -e
            command -v git >/dev/null 2>&1 || {"{"} echo 'git is missing – install the build tools first.'; exit 3; {"}"}
            mkdir -p "$(dirname {p})"
            git -c advice.detachedHead=false clone --progress --filter=blob:none {(tag != null ? "--branch " + LinuxShell.Quote(tag) + " " : "")}{LinuxShell.Quote(Options.ExpansionRepo)} {p}
            cd {p}
            git switch -c {LinuxShell.Quote(Options.Branch)}
            echo "Project ready on branch {Options.Branch.Replace("\"", "")}."
            """;
        log($"Downloading pokeemerald-expansion to {ProjectLinuxPath} (a few hundred MB, takes a few minutes) …");
        var r = await Shell.RunAsync(script, log, ct).ConfigureAwait(false);
        return r.Ok ? null : new ComponentStatus(InstallState.Failed, "git clone failed: " + Last(r.Output));
    }

    private static string TempFile(string name)
    {
        var dir = Path.Combine(Path.GetTempPath(), "PokePat-install", Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, name);
    }

    private static void DeleteTemp(string file)
    {
        try { Directory.Delete(Path.GetDirectoryName(file)!, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    /// <summary>Unpacks the entries below <paramref name="prefix"/> into <paramref name="target"/>; <paramref name="keep"/> decides which existing files stay.</summary>
    private static int Extract(string zip, string prefix, string target, Func<string, bool>? keep = null)
    {
        int n = 0;
        using var archive = ZipFile.OpenRead(zip);
        var root = Path.GetFullPath(target);
        foreach (var e in archive.Entries)
        {
            if (e.FullName.EndsWith('/') || !e.FullName.StartsWith(prefix, StringComparison.Ordinal)) continue;
            var rel = e.FullName[prefix.Length..];
            var dest = Path.GetFullPath(Path.Combine(root, rel.Replace('/', Path.DirectorySeparatorChar)));
            if (!dest.StartsWith(root, StringComparison.Ordinal)) throw new InvalidDataException("Unsafe path in archive: " + e.FullName);
            if (File.Exists(dest) && keep?.Invoke(rel) == true) continue;
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            e.ExtractToFile(dest, overwrite: true);
            n++;
        }
        return n;
    }

    private async Task<ComponentStatus?> InstallPoryscriptAsync(Action<string> log, CancellationToken ct)
    {
        if (!(await CheckExpansionAsync(ct).ConfigureAwait(false)).IsReady) return new ComponentStatus(InstallState.Missing, "Needs the project first.");
        var host = ProjectHostPath!;
        var target = Path.Combine(host, "tools", "poryscript");
        if (_forcePoryscript || !File.Exists(Path.Combine(target, "poryscript")))
        {
            var rel = await GitHubReleases.LatestAsync("huderlem/poryscript", ct).ConfigureAwait(false);
            var url = rel?.Assets.FirstOrDefault(a => a.Name == "poryscript-linux.zip")?.Url ?? PoryscriptFallbackUrl;
            log($"Downloading Poryscript {rel?.Tag ?? "3.6.1"} …");
            var zip = TempFile("poryscript-linux.zip");
            try
            {
                await GitHubReleases.DownloadAsync(url, zip, log, ct).ConfigureAwait(false);
                // The project's own command/font configs stay if they already exist.
                int n = Extract(zip, "poryscript-linux/", target, rel => rel.EndsWith(".json", StringComparison.OrdinalIgnoreCase));
                log($"  {n} file(s) written to tools/poryscript.");
            }
            finally { DeleteTemp(zip); }
            var chmod = await Shell.RunAsync("chmod +x " + LinuxShell.Quote(ProjectLinuxPath! + "/tools/poryscript/poryscript"), log, ct).ConfigureAwait(false);
            if (!chmod.Ok) return new ComponentStatus(InstallState.Failed, "chmod failed: " + Last(chmod.Output));
        }
        var makefile = Path.Combine(host, "Makefile");
        var text = await File.ReadAllTextAsync(makefile, ct).ConfigureAwait(false);
        if (!PoryscriptMakefile.IsIntegrated(text))
        {
            var (patched, problem) = PoryscriptMakefile.Integrate(text);
            if (patched == null) return new ComponentStatus(InstallState.Failed, "The Makefile could not be extended: " + problem);
            await File.WriteAllTextAsync(makefile, patched, new System.Text.UTF8Encoding(false), ct).ConfigureAwait(false);
            log("  Makefile: .pory files are now compiled during the build.");
        }
        var gitignore = Path.Combine(host, ".gitignore");
        var gi = File.Exists(gitignore) ? await File.ReadAllTextAsync(gitignore, ct).ConfigureAwait(false) : null;
        var newGi = PoryscriptMakefile.IgnoreBinary(gi);
        if (newGi != gi) await File.WriteAllTextAsync(gitignore, newGi, new System.Text.UTF8Encoding(false), ct).ConfigureAwait(false);
        return null;
    }

    private async Task<ComponentStatus?> InstallPorytilesAsync(Action<string> log, CancellationToken ct)
    {
        var exe = PorytilesSettings.DefaultWslExecutable;
        var folder = exe[..exe.IndexOf("/porytiles-linux-amd64/", StringComparison.Ordinal)];
        var abs = await Shell.ResolveAsync(folder, ct).ConfigureAwait(false) ?? throw new IOException("The home folder could not be found.");
        log($"Downloading Porytiles {PorytilesSettings.PinnedVersion} …");
        var zip = TempFile("porytiles-linux-amd64.zip");
        try
        {
            var sha = await GitHubReleases.DownloadAsync(PorytilesUrl, zip, log, ct).ConfigureAwait(false);
            if (!string.Equals(sha, PorytilesSha256, StringComparison.OrdinalIgnoreCase))
                return new ComponentStatus(InstallState.Failed, $"Checksum mismatch – the download was not used (expected {PorytilesSha256}, got {sha}).");
            log("  Checksum OK.");
            Extract(zip, "", Shell.ToHostPath(abs));
        }
        finally { DeleteTemp(zip); }
        var chmod = await Shell.RunAsync("chmod +x " + LinuxShell.QuotePath(exe), log, ct).ConfigureAwait(false);
        return chmod.Ok ? null : new ComponentStatus(InstallState.Failed, "chmod failed: " + Last(chmod.Output));
    }

    private async Task<ComponentStatus?> InstallPorymapAsync(Action<string> log, CancellationToken ct)
    {
        var rel = await GitHubReleases.LatestAsync("huderlem/porymap", ct).ConfigureAwait(false)
                  ?? throw new HttpRequestException("GitHub could not be reached.");
        var asset = rel.Assets.FirstOrDefault(a => a.Name.Contains("windows", StringComparison.OrdinalIgnoreCase) && a.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                    ?? throw new InvalidDataException($"Porymap {rel.Tag} has no Windows download.");
        log($"Downloading Porymap {rel.Tag} …");
        var zip = TempFile(asset.Name);
        try
        {
            await GitHubReleases.DownloadAsync(asset.Url, zip, log, ct).ConfigureAwait(false);
            var target = HostTools("porymap");
            if (PorymapLauncher.IsRunning()) return new ComponentStatus(InstallState.Failed, "Close Porymap first, then try again.");
            Extract(zip, "", target);
            await File.WriteAllTextAsync(Path.Combine(target, VersionMarker), rel.Tag, ct).ConfigureAwait(false);
            Options.ExistingPorymap = Path.Combine(target, "porymap.exe");
            log($"  Installed to {target}");
        }
        finally { DeleteTemp(zip); }
        return null;
    }

    private async Task<ComponentStatus?> InstallEmulatorAsync(Action<string> log, CancellationToken ct)
    {
        var rel = await GitHubReleases.LatestAsync("mgba-emu/mgba", ct).ConfigureAwait(false)
                  ?? throw new HttpRequestException("GitHub could not be reached.");
        var asset = rel.Assets.FirstOrDefault(a => a.Name.EndsWith("-win64.7z", StringComparison.OrdinalIgnoreCase))
                    ?? throw new InvalidDataException($"mGBA {rel.Tag} has no 64-bit Windows download.");
        log($"Downloading mGBA {rel.Tag} …");
        var archive = TempFile(asset.Name);
        try
        {
            await GitHubReleases.DownloadAsync(asset.Url, archive, log, ct).ConfigureAwait(false);
            var unpacked = Path.Combine(Path.GetDirectoryName(archive)!, "out");
            Directory.CreateDirectory(unpacked);
            // Windows' own tar (libarchive) reads 7z.
            var psi = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "tar.exe")) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true };
            foreach (var a in new[] { "-xf", archive, "-C", unpacked }) psi.ArgumentList.Add(a);
            using (var p = Process.Start(psi)!)
            {
                var err = await p.StandardError.ReadToEndAsync(ct).ConfigureAwait(false);
                await p.WaitForExitAsync(ct).ConfigureAwait(false);
                if (p.ExitCode != 0)
                    return new ComponentStatus(InstallState.Failed, "This Windows cannot unpack .7z files – install mGBA from mgba.io and choose it under Setup. " + err.Trim());
            }
            var exe = Directory.EnumerateFiles(unpacked, "mGBA.exe", SearchOption.AllDirectories).FirstOrDefault()
                      ?? throw new InvalidDataException("mGBA.exe is not in the download.");
            var target = HostTools("mgba");
            CopyDirectory(Path.GetDirectoryName(exe)!, target);
            await File.WriteAllTextAsync(Path.Combine(target, VersionMarker), rel.Tag, ct).ConfigureAwait(false);
            Options.ExistingEmulator = Path.Combine(target, "mGBA.exe");
            log($"  Installed to {target}");
        }
        finally { DeleteTemp(archive); }
        return null;
    }

    private static void CopyDirectory(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (var dir in Directory.EnumerateDirectories(from, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(Path.Combine(to, Path.GetRelativePath(from, dir)));
        foreach (var file in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
            File.Copy(file, Path.Combine(to, Path.GetRelativePath(from, file)), overwrite: true);
    }

    // ------------------------------------------------------------------ result

    /// <summary>Stores the project and tools in the settings like the setup window does (call after the checks).</summary>
    public void ApplyTo(ToolkitSettings settings)
    {
        var root = ProjectHostPath ?? throw new InvalidOperationException("The project folder is not known yet.");
        var tools = settings.ForProject(root);
        tools.BuildEnvironment = Options.UseWsl ? BuildEnvironment.Wsl : BuildEnvironment.Native;
        tools.BuildWslDistro = Options.Distro;
        if (PorymapExe != null) tools.PorymapExe = PorymapExe;
        if (EmulatorExe != null) tools.EmulatorExe = EmulatorExe;
        if (PorytilesReady)
        {
            tools.Porytiles.Mode = Options.UseWsl ? PorytilesMode.Wsl : PorytilesMode.Native;
            tools.Porytiles.WslDistro = Options.Distro;
            tools.Porytiles.Executable = PorytilesSettings.DefaultWslExecutable;
        }
        settings.ToolsFolder = Options.ToolsFolder;
        ToolSetup.Apply(settings, root, tools);
    }
}
