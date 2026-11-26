using System.IO.Compression;
using System.Text;

namespace TilesetEditor.Toolkit.Install;

/// <summary>A newer PokéPat release with its Windows package.</summary>
public sealed record AppRelease(string Version, string PageUrl, string Notes, ReleaseAsset Package);

/// <summary>
/// PokéPat updates itself from its GitHub releases: it finds a newer release, downloads the Windows package, checks it
/// against GitHub's SHA-256 (or at least its size), unpacks it and swaps the files after PokéPat has closed – a running
/// program cannot overwrite its own exe. The swap backs up the old files and puts them back if anything fails; then
/// PokéPat starts again. Your settings (%LOCALAPPDATA%), your own mods (Documents\PokePat Mods) and projects are not touched.
/// </summary>
public static class AppSelfUpdate
{
    public const string ExeName = "PokePatDecompKit.exe";

    public static async Task<AppRelease?> CheckAsync(string currentVersion, CancellationToken ct = default)
    {
        var rel = await GitHubReleases.LatestAsync(Installer.AppRepo, ct).ConfigureAwait(false);
        if (rel?.Version is not { } version || GitHubReleases.CompareVersions(currentVersion, version) >= 0) return null;
        var package = rel.Assets.FirstOrDefault(a => a.Name.EndsWith("-windows-x64.zip", StringComparison.OrdinalIgnoreCase));
        if (package == null) return null;
        return new AppRelease(version, rel.PageUrl ?? $"https://github.com/{Installer.AppRepo}/releases/latest", rel.Notes ?? "", package);
    }

    /// <summary>Whether the program folder can be written (not e.g. Program Files without admin rights).</summary>
    public static bool CanWrite(string dir)
    {
        try
        {
            var probe = Path.Combine(dir, $".pokepat-write-test-{Guid.NewGuid():N}");
            File.WriteAllText(probe, "");
            File.Delete(probe);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }

    /// <summary>Downloads and unpacks the package; returns the folder that holds the new PokePatDecompKit.exe.</summary>
    public static async Task<string> DownloadAsync(AppRelease release, string workDir, Action<string>? progress, CancellationToken ct = default)
    {
        if (Directory.Exists(workDir)) Directory.Delete(workDir, true);
        Directory.CreateDirectory(workDir);
        var zip = Path.Combine(workDir, release.Package.Name);
        progress?.Invoke($"Downloading {release.Package.Name} …");
        var sha = await GitHubReleases.DownloadAsync(release.Package.Url, zip, progress, ct).ConfigureAwait(false);
        Verify(release.Package, zip, sha);
        progress?.Invoke("Unpacking …");
        var target = Path.Combine(workDir, "new");
        ZipFile.ExtractToDirectory(zip, target);
        var exe = Directory.EnumerateFiles(target, ExeName, SearchOption.AllDirectories).OrderBy(f => f.Length).FirstOrDefault()
                  ?? throw new InvalidDataException($"The package has no {ExeName}.");
        return Path.GetDirectoryName(exe)!;
    }

    /// <summary>The download must be exactly the published file: SHA-256 from GitHub if given, else the size.</summary>
    public static void Verify(ReleaseAsset asset, string file, string sha256Hex)
    {
        if (asset.Digest is { } d && d.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
        {
            if (!string.Equals(d[7..], sha256Hex, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"The download does not match the release (SHA-256 {sha256Hex[..12]}… instead of {d[7..19]}…). Nothing was changed – try again later.");
            return;
        }
        if (asset.Size > 0 && new FileInfo(file).Length != asset.Size)
            throw new InvalidDataException($"The download is incomplete ({new FileInfo(file).Length} of {asset.Size} bytes). Nothing was changed – try again.");
    }

    /// <summary>
    /// The PowerShell script that waits for PokéPat (process <paramref name="pid"/>) to close, backs up the files it replaces,
    /// copies the new ones over <paramref name="installDir"/>, starts PokéPat again – or restores the backup if a step fails.
    /// </summary>
    public static string SwapScript(string newDir, string installDir, int pid, string logFile)
    {
        static string Q(string s) => "'" + s.Replace("'", "''") + "'";
        var sb = new StringBuilder();
        sb.AppendLine("$ErrorActionPreference = 'Stop'");
        sb.AppendLine($"$new = {Q(newDir)}; $dest = {Q(installDir)}; $log = {Q(logFile)}; $exe = Join-Path $dest {Q(ExeName)}");
        sb.AppendLine("$backup = Join-Path $dest '.update-backup'");
        sb.AppendLine("function Log($m) { Add-Content -Path $log -Value ((Get-Date -Format 'HH:mm:ss') + '  ' + $m) }");
        sb.AppendLine("Log 'PokePat update: waiting for PokePat to close'");
        sb.AppendLine($"try {{ Wait-Process -Id {pid} -Timeout 120 -ErrorAction SilentlyContinue }} catch {{ }}");
        sb.AppendLine("Start-Sleep -Milliseconds 700");
        sb.AppendLine("$copied = @()");
        sb.AppendLine("try {");
        sb.AppendLine("  if (Test-Path $backup) { Remove-Item $backup -Recurse -Force }");
        sb.AppendLine("  New-Item -ItemType Directory -Path $backup | Out-Null");
        sb.AppendLine("  foreach ($f in Get-ChildItem -Path $new -Recurse -File) {");
        sb.AppendLine("    $rel = $f.FullName.Substring($new.Length).TrimStart('\\')");
        sb.AppendLine("    $target = Join-Path $dest $rel");
        sb.AppendLine("    if (Test-Path $target) { $b = Join-Path $backup $rel; New-Item -ItemType Directory -Force -Path (Split-Path $b) | Out-Null; Copy-Item $target $b -Force }");
        sb.AppendLine("    New-Item -ItemType Directory -Force -Path (Split-Path $target) | Out-Null");
        sb.AppendLine("    Copy-Item $f.FullName $target -Force");
        sb.AppendLine("    $copied += $rel");
        sb.AppendLine("  }");
        sb.AppendLine("  Log ('Updated ' + $copied.Count + ' files')");
        sb.AppendLine("} catch {");
        sb.AppendLine("  Log ('Update failed: ' + $_ + ' - restoring the old version')");
        sb.AppendLine("  foreach ($rel in $copied) { $b = Join-Path $backup $rel; $target = Join-Path $dest $rel; if (Test-Path $b) { Copy-Item $b $target -Force } else { Remove-Item $target -Force -ErrorAction SilentlyContinue } }");
        sb.AppendLine("}");
        sb.AppendLine("Start-Process -FilePath $exe -WorkingDirectory $dest");
        return sb.ToString();
    }
}
