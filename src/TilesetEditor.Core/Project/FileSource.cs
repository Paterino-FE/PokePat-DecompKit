namespace TilesetEditor.Core.Project;

/// <summary>Read access to project files by project-relative path (forward slashes).</summary>
public interface IFileSource
{
    bool FileExists(string relativePath);
    bool DirectoryExists(string relativePath);
    string ReadAllText(string relativePath);
    IEnumerable<string> EnumerateDirectories(string relativePath);
}

public sealed class DiskFileSource(string root) : IFileSource
{
    public string Root { get; } = root;

    public string Full(string rel) => ProjectPaths.Combine(Root, rel);

    public bool FileExists(string relativePath) => File.Exists(Full(relativePath));
    public bool DirectoryExists(string relativePath) => Directory.Exists(Full(relativePath));
    public string ReadAllText(string relativePath) => File.ReadAllText(Full(relativePath));

    public IEnumerable<string> EnumerateDirectories(string relativePath)
    {
        var full = Full(relativePath);
        if (!Directory.Exists(full)) return [];
        return Directory.EnumerateDirectories(full).Select(d => Path.GetFileName(d)!);
    }
}

/// <summary>
/// A view of the project with pending changes applied on top, used to validate an export plan
/// with the same parser before anything is written.
/// </summary>
public sealed class OverlayFileSource(IFileSource inner, IReadOnlyDictionary<string, string?> textOverrides, IEnumerable<string> addedFiles) : IFileSource
{
    private readonly HashSet<string> _added = new(addedFiles.Select(ProjectPaths.Normalize), StringComparer.OrdinalIgnoreCase);

    public bool FileExists(string relativePath)
    {
        var n = ProjectPaths.Normalize(relativePath);
        return _added.Contains(n) || textOverrides.ContainsKey(n) || inner.FileExists(n);
    }

    public bool DirectoryExists(string relativePath)
    {
        var n = ProjectPaths.Normalize(relativePath).TrimEnd('/') + "/";
        return _added.Any(a => a.StartsWith(n, StringComparison.OrdinalIgnoreCase)) || inner.DirectoryExists(relativePath);
    }

    public string ReadAllText(string relativePath)
    {
        var n = ProjectPaths.Normalize(relativePath);
        return textOverrides.TryGetValue(n, out var t) && t != null ? t : inner.ReadAllText(n);
    }

    public IEnumerable<string> EnumerateDirectories(string relativePath) => inner.EnumerateDirectories(relativePath);
}

public static class ProjectPaths
{
    public static string Normalize(string rel) => rel.Replace('\\', '/').TrimStart('/');

    public static string Combine(string root, string rel) =>
        Path.Combine(root, Normalize(rel).Replace('/', Path.DirectorySeparatorChar));
}
