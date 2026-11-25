using System.Text.RegularExpressions;

namespace TilesetEditor.Compiler;

/// <summary>Translates Windows paths into paths a WSL process can open.</summary>
public static partial class WslPath
{
    [GeneratedRegex(@"^([A-Za-z]):[\\/]?(.*)$")]
    private static partial Regex DrivePath();

    [GeneratedRegex(@"^[\\/]{2}(?:wsl\$|wsl\.localhost)[\\/]([^\\/]+)(?:[\\/](.*))?$", RegexOptions.IgnoreCase)]
    private static partial Regex WslShare();

    public static string ToLinux(string windowsPath, string distro)
    {
        var full = windowsPath;
        var share = WslShare().Match(full);
        if (share.Success)
        {
            var d = share.Groups[1].Value;
            if (!string.Equals(d, distro, StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException($"The path '{windowsPath}' is in the WSL distribution '{d}', but Porytiles is set up for '{distro}'.");
            return "/" + share.Groups[2].Value.Replace('\\', '/').TrimEnd('/');
        }
        var drive = DrivePath().Match(full);
        if (drive.Success)
        {
            var rest = drive.Groups[2].Value.Replace('\\', '/').TrimEnd('/');
            return $"/mnt/{char.ToLowerInvariant(drive.Groups[1].Value[0])}" + (rest.Length > 0 ? "/" + rest : "");
        }
        if (full.StartsWith("\\\\") || full.StartsWith("//"))
            throw new ArgumentException($"Network path '{windowsPath}' cannot be passed to WSL. Please use a local folder.");
        throw new ArgumentException($"Path '{windowsPath}' is not absolute.");
    }
}
