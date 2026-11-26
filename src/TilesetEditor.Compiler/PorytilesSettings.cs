namespace TilesetEditor.Compiler;

public enum PorytilesMode
{
    /// <summary>Run the Linux binary inside WSL (Porytiles 2.x ships no Windows build).</summary>
    Wsl,
    /// <summary>Run a native executable directly.</summary>
    Native,
}

public sealed class PorytilesSettings
{
    /// <summary>The compiler version this app was built and tested against. Pinned for reproducible output.</summary>
    public const string PinnedVersion = "2.0.0";

    /// <summary>Where tools/setup-porytiles.ps1 installs the pinned binary inside WSL.</summary>
    public const string DefaultWslExecutable = "~/.local/share/tileseteditor/porytiles-2.0.0/porytiles-linux-amd64/porytiles";

    public PorytilesMode Mode { get; set; } = OperatingSystem.IsWindows() ? PorytilesMode.Wsl : PorytilesMode.Native;
    public string WslDistro { get; set; } = "Ubuntu";
    /// <summary>Linux path (WSL mode, "~" allowed) or Windows path (native mode).</summary>
    public string Executable { get; set; } = OperatingSystem.IsWindows() ? DefaultWslExecutable : InstalledLinuxExecutable() ?? "porytiles";

    /// <summary>On Linux (CLI, tests inside WSL): the pinned binary where the installer puts it, if it is there.</summary>
    private static string? InstalledLinuxExecutable()
    {
        var home = Environment.GetEnvironmentVariable("HOME");
        if (string.IsNullOrEmpty(home)) return null;
        var path = home + DefaultWslExecutable[1..];
        return File.Exists(path) ? path : null;
    }
    public string ExpectedVersion { get; set; } = PinnedVersion;
    /// <summary>Upper bound for a single Porytiles invocation.</summary>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromMinutes(5);
}
