using System.Text.RegularExpressions;

namespace TilesetEditor.Toolkit;

/// <summary>Facts about the project folder that are not tileset data: branch, ROM name, where it lives.</summary>
public sealed partial class ProjectEnvironment
{
    public required string Root { get; init; }
    public string? GitBranch { get; init; }
    public string? GitCommit { get; init; }
    public bool IsGitRepository { get; init; }
    /// <summary>FILE_NAME from the Makefile (ROM = FILE_NAME.gba).</summary>
    public string RomFileName { get; init; } = "pokeemerald.gba";
    /// <summary>Distribution name if the folder is on a \\wsl.localhost or \\wsl$ share.</summary>
    public string? WslDistro { get; init; }
    public bool UsesPoryscript { get; init; }
    public bool HasMakefile { get; init; }

    [GeneratedRegex(@"^[\\/]{2}(?:wsl\$|wsl\.localhost)[\\/]([^\\/]+)", RegexOptions.IgnoreCase)]
    private static partial Regex WslShare();

    [GeneratedRegex(@"^\s*FILE_NAME\s*:?=\s*(\S+)", RegexOptions.Multiline)]
    private static partial Regex FileNameRegex();

    public static ProjectEnvironment Detect(string root)
    {
        var (isGit, branch, commit) = ReadGit(root);
        string rom = "pokeemerald.gba";
        var makefile = Path.Combine(root, "Makefile");
        bool hasMake = File.Exists(makefile);
        if (hasMake)
        {
            var m = FileNameRegex().Match(File.ReadAllText(makefile));
            if (m.Success && !m.Groups[1].Value.Contains('$')) rom = m.Groups[1].Value + ".gba";
        }
        var share = WslShare().Match(root);
        bool pory = File.Exists(Path.Combine(root, "tools", "poryscript", "poryscript")) ||
                    File.Exists(Path.Combine(root, "tools", "poryscript", "poryscript.exe")) ||
                    (File.Exists(makefile) && File.ReadAllText(makefile).Contains(".pory", StringComparison.Ordinal));
        return new ProjectEnvironment
        {
            Root = root,
            IsGitRepository = isGit,
            GitBranch = branch,
            GitCommit = commit,
            RomFileName = rom,
            WslDistro = share.Success ? share.Groups[1].Value : null,
            UsesPoryscript = pory,
            HasMakefile = hasMake,
        };
    }

    /// <summary>Reads HEAD without invoking git (works for UNC/WSL paths and worktrees).</summary>
    public static (bool IsGit, string? Branch, string? Commit) ReadGit(string root)
    {
        var dotGit = Path.Combine(root, ".git");
        string? gitDir = null;
        if (Directory.Exists(dotGit)) gitDir = dotGit;
        else if (File.Exists(dotGit))
        {
            var line = File.ReadAllText(dotGit).Trim();
            if (line.StartsWith("gitdir:", StringComparison.Ordinal))
            {
                var p = line[7..].Trim();
                gitDir = Path.IsPathRooted(p) && !p.StartsWith('/') ? p : Path.GetFullPath(Path.Combine(root, p));
            }
        }
        if (gitDir == null || !File.Exists(Path.Combine(gitDir, "HEAD"))) return (false, null, null);

        var head = File.ReadAllText(Path.Combine(gitDir, "HEAD")).Trim();
        if (!head.StartsWith("ref:", StringComparison.Ordinal))
            return (true, "(detached HEAD)", head.Length >= 10 ? head[..10] : head);
        var reference = head[4..].Trim();
        string branch = reference.StartsWith("refs/heads/", StringComparison.Ordinal) ? reference["refs/heads/".Length..] : reference;
        string? commit = null;
        // For worktrees the refs live in the common dir.
        var commonFile = Path.Combine(gitDir, "commondir");
        var refDirs = new List<string> { gitDir };
        if (File.Exists(commonFile)) refDirs.Add(Path.GetFullPath(Path.Combine(gitDir, File.ReadAllText(commonFile).Trim())));
        foreach (var dir in refDirs)
        {
            var refFile = Path.Combine(dir, reference.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(refFile)) { commit = File.ReadAllText(refFile).Trim(); break; }
            var packed = Path.Combine(dir, "packed-refs");
            if (File.Exists(packed))
            {
                var line = File.ReadLines(packed).FirstOrDefault(l => l.EndsWith(" " + reference, StringComparison.Ordinal));
                if (line != null) { commit = line.Split(' ')[0]; break; }
            }
        }
        return (true, branch, commit is { Length: >= 10 } ? commit[..10] : commit);
    }
}
