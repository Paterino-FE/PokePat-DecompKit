using TilesetEditor.Core.Project;

namespace TilesetEditor.Export;

/// <summary>File operations used by the applier; replaceable in tests to simulate failures.</summary>
public interface IExportFileSystem
{
    bool FileExists(string path);
    bool DirectoryExists(string path);
    byte[] ReadAllBytes(string path);
    void WriteAllBytes(string path, byte[] data);
    void CreateDirectory(string path);
    void DeleteFile(string path);
    void DeleteEmptyDirectory(string path);
    void Move(string source, string destination, bool overwrite);
}

public sealed class PhysicalExportFileSystem : IExportFileSystem
{
    public bool FileExists(string path) => File.Exists(path);
    public bool DirectoryExists(string path) => Directory.Exists(path);
    public byte[] ReadAllBytes(string path) => File.ReadAllBytes(path);
    public void WriteAllBytes(string path, byte[] data) => File.WriteAllBytes(path, data);
    public void CreateDirectory(string path) => Directory.CreateDirectory(path);
    public void DeleteFile(string path) => File.Delete(path);

    public void DeleteEmptyDirectory(string path)
    {
        if (Directory.Exists(path) && !Directory.EnumerateFileSystemEntries(path).Any()) Directory.Delete(path);
    }

    public void Move(string source, string destination, bool overwrite) => File.Move(source, destination, overwrite);
}

public sealed class ExportResult
{
    public required bool Success { get; init; }
    public string? Error { get; init; }
    public bool RolledBack { get; init; }
    public IReadOnlyList<string> RollbackProblems { get; init; } = [];
    public IReadOnlyList<string> Written { get; init; } = [];
    public string? BackupDir { get; init; }
}

/// <summary>
/// Applies an export plan all-or-nothing: preconditions are re-checked, modified files are backed up, every
/// file is written to a temporary name and then moved into place. On any failure, created files and folders
/// are removed and modified files restored from the backup.
/// </summary>
public sealed class ExportApplier(IExportFileSystem? fileSystem = null, string? backupRoot = null)
{
    private const string TempSuffix = ".tileseteditor-tmp";
    private readonly IExportFileSystem _fs = fileSystem ?? new PhysicalExportFileSystem();

    public static string DefaultBackupRoot =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TilesetEditor", "backups");

    /// <param name="postCheck">Optional verification after writing (e.g. reload the project). Returning an error triggers a rollback.</param>
    public ExportResult Apply(ExportPlan plan, Func<string?>? postCheck = null)
    {
        string Full(string rel) => ProjectPaths.Combine(plan.ProjectRoot, rel);

        // 1. Preconditions
        foreach (var f in plan.Files)
        {
            var p = Full(f.RelativePath);
            if (f.Action == PlannedAction.Create && _fs.FileExists(p))
                return new ExportResult { Success = false, Error = $"{f.RelativePath} now exists. Aborted, nothing was changed." };
            if (f.Action == PlannedAction.Modify)
            {
                if (!_fs.FileExists(p))
                    return new ExportResult { Success = false, Error = $"{f.RelativePath} is missing. Aborted, nothing was changed." };
                if (f.ExpectedOriginalSha256 != null && Hashing.Sha256(_fs.ReadAllBytes(p)) != f.ExpectedOriginalSha256)
                    return new ExportResult
                    {
                        Success = false,
                        Error = $"{f.RelativePath} was modified since planning (e.g. by Porymap). Please plan the export again. Nothing was changed.",
                    };
            }
        }

        // 2. Backup of files that will be modified
        string backupDir = Path.Combine(backupRoot ?? DefaultBackupRoot, DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + plan.TilesetName);
        var backups = new Dictionary<string, string>();
        try
        {
            foreach (var f in plan.Modified)
            {
                var b = ProjectPaths.Combine(backupDir, f.RelativePath);
                _fs.CreateDirectory(Path.GetDirectoryName(b)!);
                _fs.WriteAllBytes(b, _fs.ReadAllBytes(Full(f.RelativePath)));
                backups[f.RelativePath] = b;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new ExportResult { Success = false, Error = "Could not create backup: " + ex.Message + " Nothing was changed." };
        }

        // 3. Write
        var createdDirs = new List<string>();
        var createdFiles = new List<string>();
        var modifiedFiles = new List<string>();
        var tempFiles = new List<string>();
        try
        {
            foreach (var f in plan.Files)
            {
                var p = Full(f.RelativePath);
                EnsureDirectory(Path.GetDirectoryName(p)!, createdDirs);
                var tmp = p + TempSuffix;
                tempFiles.Add(tmp);
                _fs.WriteAllBytes(tmp, f.Content);
                _fs.Move(tmp, p, overwrite: f.Action == PlannedAction.Modify);
                tempFiles.Remove(tmp);
                if (f.Action == PlannedAction.Create) createdFiles.Add(p); else modifiedFiles.Add(f.RelativePath);
            }

            var post = postCheck?.Invoke();
            if (post != null) throw new IOException("Verification after writing failed: " + post);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            var problems = Rollback(plan, createdFiles, modifiedFiles, tempFiles, createdDirs, backups);
            return new ExportResult
            {
                Success = false,
                Error = ex.Message,
                RolledBack = true,
                RollbackProblems = problems,
                BackupDir = backupDir,
            };
        }

        return new ExportResult
        {
            Success = true,
            Written = plan.Files.Select(f => f.RelativePath).ToList(),
            BackupDir = backupDir,
        };
    }

    private void EnsureDirectory(string dir, List<string> created)
    {
        var missing = new Stack<string>();
        for (var d = dir; !string.IsNullOrEmpty(d) && !_fs.DirectoryExists(d); d = Path.GetDirectoryName(d))
            missing.Push(d);
        while (missing.Count > 0)
        {
            var d = missing.Pop();
            _fs.CreateDirectory(d);
            created.Add(d);
        }
    }

    private List<string> Rollback(ExportPlan plan, List<string> createdFiles, List<string> modifiedFiles, List<string> tempFiles,
        List<string> createdDirs, Dictionary<string, string> backups)
    {
        var problems = new List<string>();
        void Try(Action a, string what)
        {
            try { a(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { problems.Add($"{what}: {ex.Message}"); }
        }

        foreach (var rel in modifiedFiles)
        {
            var full = ProjectPaths.Combine(plan.ProjectRoot, rel);
            Try(() => _fs.WriteAllBytes(full, _fs.ReadAllBytes(backups[rel])), $"Restoring {rel}");
        }
        foreach (var f in createdFiles) Try(() => _fs.DeleteFile(f), $"Deleting {f}");
        foreach (var t in tempFiles) Try(() => { if (_fs.FileExists(t)) _fs.DeleteFile(t); }, $"Deleting {t}");
        foreach (var d in Enumerable.Reverse(createdDirs)) Try(() => _fs.DeleteEmptyDirectory(d), $"Deleting {d}");
        return problems;
    }
}
