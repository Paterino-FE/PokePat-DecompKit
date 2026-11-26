using System.Diagnostics;
using System.Text.RegularExpressions;
using TilesetEditor.Compiler;

namespace TilesetEditor.Toolkit.Install;

/// <summary>Result of an update check for one part. <see cref="CanUpdate"/>: PokéPat can do the update itself.</summary>
public sealed record UpdateInfo(InstallComponent Component, string? Installed, string? Latest, bool Available, bool CanUpdate, string Message);

/// <summary>A newer PokéPat release on GitHub.</summary>
public sealed record AppUpdate(string Version, string PageUrl);

public sealed partial class Installer
{
    /// <summary>Written next to Porymap/mGBA when PokéPat installs them: the release tag.</summary>
    public const string VersionMarker = "pokepat-version.txt";
    public const string AppRepo = "Paterino-FE/PokePat-DecompKit";

    private bool _forcePoryscript;

    /// <summary>Installed and latest version of one part (null when the part is not installed or not offered here).</summary>
    public async Task<UpdateInfo?> CheckUpdateAsync(InstallComponent c, CancellationToken ct = default)
    {
        if (!Applies(c)) return null;
        var status = await CheckAsync(c, ct).ConfigureAwait(false);
        if (!status.IsReady) return null;
        switch (c)
        {
            case InstallComponent.Wsl:
                return new UpdateInfo(c, null, null, false, false, "Updated by Windows (wsl --update).");
            case InstallComponent.BuildTools:
                return new UpdateInfo(c, null, null, false, false, "Updated by Ubuntu (sudo apt upgrade).");
            case InstallComponent.Porytiles:
                return new UpdateInfo(c, PorytilesSettings.PinnedVersion, null, false, false,
                    $"Stays at {PorytilesSettings.PinnedVersion} on purpose – PokéPat's tileset import was tested with exactly this version.");
            case InstallComponent.Expansion when Options.Game == TilesetEditor.Core.Project.GameKind.Platinum:
                return new UpdateInfo(c, null, null, false, false,
                    "pokeplatinum has no releases – new work of the decompilation comes with git (git pull from pret/pokeplatinum, merged into your branch).");
            case InstallComponent.Expansion:
            {
                var r = await Shell.RunAsync($"git -C {LinuxShell.Quote(ProjectLinuxPath!)} describe --tags --abbrev=0 --match 'expansion/*' 2>/dev/null", null, ct).ConfigureAwait(false);
                var installed = r.Ok ? Last(r.Output) : null;
                return Compare(c, installed, await GitHubReleases.LatestAsync("rh-hideout/pokeemerald-expansion", ct).ConfigureAwait(false), true);
            }
            case InstallComponent.Poryscript:
            {
                var r = await Shell.RunAsync(LinuxShell.Quote(ProjectLinuxPath! + "/tools/poryscript/poryscript") + " -v", null, ct).ConfigureAwait(false);
                return Compare(c, r.Ok ? Last(r.Output) : null, await GitHubReleases.LatestAsync("huderlem/poryscript", ct).ConfigureAwait(false), true);
            }
            case InstallComponent.Porymap:
                return Compare(c, ProgramVersion(PorymapExe), await GitHubReleases.LatestAsync("huderlem/porymap", ct).ConfigureAwait(false), true);
            default:
                if (string.Equals(Path.GetFileName(EmulatorExe), "melonDS.exe", StringComparison.OrdinalIgnoreCase))
                    return Compare(c, ProgramVersion(EmulatorExe), await GitHubReleases.LatestAsync(MelonDsRepo, ct).ConfigureAwait(false), true);
                if (!string.Equals(Path.GetFileName(EmulatorExe), "mGBA.exe", StringComparison.OrdinalIgnoreCase))
                    return new UpdateInfo(c, null, null, false, false, "Not mGBA – updates of this emulator are not checked.");
                return Compare(c, ProgramVersion(EmulatorExe), await GitHubReleases.LatestAsync("mgba-emu/mgba", ct).ConfigureAwait(false), true);
        }
    }

    private static UpdateInfo Compare(InstallComponent c, string? installed, Release? latest, bool canUpdate)
    {
        if (latest == null) return new UpdateInfo(c, installed, null, false, false, "GitHub could not be reached – try again later.");
        var newest = GitHubReleases.Short(latest.Tag);
        if (string.IsNullOrWhiteSpace(installed))
            return new UpdateInfo(c, null, newest, false, canUpdate, $"Installed version unknown; the newest is {newest}.");
        var have = GitHubReleases.Short(installed);
        return GitHubReleases.CompareVersions(have, newest) < 0
            ? new UpdateInfo(c, have, newest, true, canUpdate, $"Update available: {have} → {newest}")
            : new UpdateInfo(c, have, newest, false, false, $"Up to date ({have}).");
    }

    /// <summary>Version of a Windows program: PokéPat's marker, mGBA's CHANGES.txt or the exe's version resource.</summary>
    public static string? ProgramVersion(string? exe)
    {
        if (string.IsNullOrWhiteSpace(exe) || !File.Exists(exe)) return null;
        var dir = Path.GetDirectoryName(exe)!;
        try
        {
            var marker = Path.Combine(dir, VersionMarker);
            if (File.Exists(marker)) return File.ReadAllText(marker).Trim();
            var changes = Path.Combine(dir, "CHANGES.txt");
            if (File.Exists(changes) && Regex.Match(File.ReadLines(changes).FirstOrDefault() ?? "", @"^\d+\.\d+(\.\d+)?") is { Success: true } m) return m.Value;
            var info = FileVersionInfo.GetVersionInfo(exe);
            foreach (var v in new[] { info.ProductVersion, info.FileVersion })
                if (v != null && Regex.IsMatch(v, @"^\d+\.\d+")) return v;
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        return null;
    }

    /// <summary>Brings one part to the latest release (see <see cref="CheckUpdateAsync"/>).</summary>
    public async Task<ComponentStatus> UpdateAsync(InstallComponent c, Action<string> log, CancellationToken ct = default)
    {
        try
        {
            switch (c)
            {
                case InstallComponent.Expansion when Options.Game == TilesetEditor.Core.Project.GameKind.Platinum:
                    return await CheckAsync(c, ct).ConfigureAwait(false);
                case InstallComponent.Expansion:
                    return await UpdateExpansionAsync(log, ct).ConfigureAwait(false);
                case InstallComponent.Poryscript:
                    _forcePoryscript = true;
                    try { return await InstallAsync(c, log, newWindow: true, ct).ConfigureAwait(false); }
                    finally { _forcePoryscript = false; }
                case InstallComponent.Porymap:
                case InstallComponent.Emulator:
                    return await InstallAsync(c, log, newWindow: true, ct).ConfigureAwait(false);
                default:
                    return await CheckAsync(c, ct).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or HttpRequestException)
        {
            log("Error: " + ex.Message);
            return new ComponentStatus(InstallState.Failed, ex.Message);
        }
    }

    /// <summary>
    /// Merges the latest pokeemerald-expansion release into the project's branch – only when everything is committed, and
    /// if the release clashes with own changes the merge is undone completely, so nothing is ever left half-done.
    /// </summary>
    private async Task<ComponentStatus> UpdateExpansionAsync(Action<string> log, CancellationToken ct)
    {
        if (!(await CheckExpansionAsync(ct).ConfigureAwait(false)).IsReady) return new ComponentStatus(InstallState.Missing, "Needs the project first.");
        var tag = Options.ExpansionTag
                  ?? (await GitHubReleases.LatestAsync("rh-hideout/pokeemerald-expansion", ct).ConfigureAwait(false))?.Tag
                  ?? throw new HttpRequestException("GitHub could not be reached.");
        log($"Updating the project to {tag} …");
        var script = $$"""
            cd {{LinuxShell.Quote(ProjectLinuxPath!)}} || exit 9
            if [ -n "$(git status --porcelain --untracked-files=no)" ]; then echo "RESULT:DIRTY"; exit 4; fi
            url="$(git remote get-url origin 2>/dev/null || true)"
            case "$url" in *pokeemerald-expansion*) src=origin;; *) src={{LinuxShell.Quote(Options.ExpansionRepo)}};; esac
            git fetch --no-tags "$src" {{LinuxShell.Quote("refs/tags/" + tag + ":refs/tags/" + tag)}} || { echo "RESULT:FETCH"; exit 6; }
            ident=""
            git config user.email >/dev/null 2>&1 || ident="-c user.name=PokePat -c user.email=pokepat@localhost"
            if git $ident merge --no-edit -m {{LinuxShell.Quote("Update to pokeemerald-expansion " + tag)}} {{LinuxShell.Quote(tag)}}; then
              echo "RESULT:MERGED"
            else
              git merge --abort >/dev/null 2>&1 || true
              echo "RESULT:CONFLICT"; exit 5
            fi
            """;
        var r = await Shell.RunAsync(script, log, ct).ConfigureAwait(false);
        var result = r.Output.Split('\n').Select(l => l.Trim()).LastOrDefault(l => l.StartsWith("RESULT:", StringComparison.Ordinal));
        return result switch
        {
            "RESULT:MERGED" => new ComponentStatus(InstallState.Ready, $"Updated to {GitHubReleases.Short(tag)}. Build the ROM to check everything still works."),
            "RESULT:DIRTY" => new ComponentStatus(InstallState.Failed,
                "Not updated: your project has changes that are not committed yet. Commit them first (e.g. git commit -am \"my changes\"), then try again."),
            "RESULT:CONFLICT" => new ComponentStatus(InstallState.Failed,
                $"Not updated: {GitHubReleases.Short(tag)} changes the same lines as your own changes. Nothing was changed – merge {tag} by hand with git."),
            "RESULT:FETCH" => new ComponentStatus(InstallState.Failed, "The release could not be downloaded: " + Last(r.Output)),
            _ => new ComponentStatus(InstallState.Failed, "The update failed: " + Last(r.Output)),
        };
    }

    /// <summary>A newer PokéPat release, or null (none published, no network or already the newest).</summary>
    public static async Task<AppUpdate?> CheckAppUpdateAsync(string currentVersion, CancellationToken ct = default)
    {
        var rel = await GitHubReleases.LatestAsync(AppRepo, ct).ConfigureAwait(false);
        if (rel?.Version is not { } version || GitHubReleases.CompareVersions(currentVersion, version) >= 0) return null;
        return new AppUpdate(version, rel.PageUrl ?? $"https://github.com/{AppRepo}/releases/latest");
    }

    /// <summary>All available updates of the current project and programs (for the check on start).</summary>
    public async Task<List<UpdateInfo>> CheckAllUpdatesAsync(CancellationToken ct = default)
    {
        var list = new List<UpdateInfo>();
        foreach (var c in All)
            if (await CheckUpdateAsync(c, ct).ConfigureAwait(false) is { Available: true } u) list.Add(u);
        return list;
    }
}
