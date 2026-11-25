using System.Text.Json;
using System.Text.Json.Serialization;
using TilesetEditor.Compiler;

namespace TilesetEditor.Toolkit;

public enum BuildEnvironment
{
    /// <summary>Run the build command in bash inside a WSL distribution (the usual setup for decomp projects on Windows).</summary>
    Wsl,
    /// <summary>Run the build command with cmd.exe (e.g. MSYS2/devkitPro on the Windows PATH).</summary>
    Windows,
    /// <summary>Linux/macOS: run the build command with bash in the project folder.</summary>
    Native,
}

/// <summary>Tool configuration for one project folder.</summary>
public sealed class ProjectToolSettings
{
    public string? PorymapExe { get; set; }
    public string? EmulatorExe { get; set; }
    public PorytilesSettings Porytiles { get; set; } = new();
    public BuildEnvironment BuildEnvironment { get; set; } = OperatingSystem.IsWindows() ? BuildEnvironment.Wsl : BuildEnvironment.Native;
    public string BuildWslDistro { get; set; } = "Ubuntu";
    /// <summary>Executed in the project root. Default for pokeemerald(-expansion): make with all cores.</summary>
    public string BuildCommand { get; set; } = DefaultBuildCommand;
    /// <summary>ROM path relative to the project root; null = derived from FILE_NAME in the Makefile.</summary>
    public string? RomPath { get; set; }

    public const string DefaultBuildCommand = "make -j$(nproc)";
}

public sealed class ToolkitSettings
{
    public string? LastProject { get; set; }
    /// <summary>Set once the first-run setup (project folder + optional tools) was completed.</summary>
    public bool SetupCompleted { get; set; }
    public List<string> RecentProjects { get; set; } = [];
    /// <summary>Keyed by the normalized project root.</summary>
    public Dictionary<string, ProjectToolSettings> Projects { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Tool paths that apply to every project unless overridden (Porymap, emulator).</summary>
    public string? DefaultPorymapExe { get; set; }
    public string? DefaultEmulatorExe { get; set; }
    /// <summary>"System" (follow Windows), "Light" or "Dark".</summary>
    public string Theme { get; set; } = "System";
    /// <summary>Where "Get started" put the downloaded programs (Porymap, mGBA) and the project.</summary>
    public string? ToolsFolder { get; set; }
    /// <summary>Look for new versions of the programs, the project's expansion and PokéPat when PokéPat starts (at most once a day).</summary>
    public bool CheckUpdatesOnStart { get; set; } = true;
    public DateTime? LastUpdateCheckUtc { get; set; }

    public ProjectToolSettings ForProject(string root)
    {
        var key = ProjectKey(root);
        if (!Projects.TryGetValue(key, out var s))
        {
            s = new ProjectToolSettings { PorymapExe = DefaultPorymapExe, EmulatorExe = DefaultEmulatorExe };
            Projects[key] = s;
        }
        return s;
    }

    public void Touch(string root)
    {
        LastProject = root;
        RecentProjects.RemoveAll(p => string.Equals(ProjectKey(p), ProjectKey(root), StringComparison.OrdinalIgnoreCase));
        RecentProjects.Insert(0, root);
        if (RecentProjects.Count > 10) RecentProjects.RemoveRange(10, RecentProjects.Count - 10);
    }

    public static string ProjectKey(string root) => Path.GetFullPath(root).TrimEnd('\\', '/');
}

public sealed class ToolkitSettingsStore(string? path = null)
{
    /// <summary>Settings file; TILESET_TOOLKIT_SETTINGS overrides the location (tests, portable use).</summary>
    public string FilePath { get; } = path ?? Environment.GetEnvironmentVariable("TILESET_TOOLKIT_SETTINGS") ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "TilesetEditor", "settings.json");

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public ToolkitSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<ToolkitSettings>(File.ReadAllText(FilePath), Json) ?? new ToolkitSettings();
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            // A broken settings file must not prevent the app from starting; it is rewritten on the next save.
        }
        return new ToolkitSettings();
    }

    public void Save(ToolkitSettings settings)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        var tmp = FilePath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(settings, Json));
        File.Move(tmp, FilePath, overwrite: true);
    }
}
