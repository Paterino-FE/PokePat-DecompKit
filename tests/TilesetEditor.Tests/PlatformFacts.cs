namespace TilesetEditor.Tests;

/// <summary>A test that needs Windows (cmd.exe, cscript, WSL paths seen from Windows). Skipped elsewhere.</summary>
public sealed class WindowsFactAttribute : FactAttribute
{
    public WindowsFactAttribute()
    {
        if (!OperatingSystem.IsWindows()) Skip = "Windows only";
    }
}

/// <summary>A test that runs bash directly (Linux/macOS). Skipped on Windows.</summary>
public sealed class UnixFactAttribute : FactAttribute
{
    public UnixFactAttribute()
    {
        if (OperatingSystem.IsWindows()) Skip = "Unix only";
    }
}
