using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using TilesetEditor.Core.Project;

namespace TilesetEditor.Export;

/// <summary>A file of a backed-up change: what PokéPat did with it and the content it wrote (SHA-256).</summary>
public sealed record BackupFile(string Path, PlannedAction Action, string? WrittenSha256);

/// <summary>One change PokéPat made (a folder of the backups): when, what, which project, which files.</summary>
public sealed record BackupEntry(string Folder, DateTime Time, string Title, string? Project, IReadOnlyList<BackupFile> Files, bool HasManifest)
{
    /// <summary>The old content of a modified or deleted file, kept in the backup folder.</summary>
    public string Original(string rel) => System.IO.Path.Combine(Folder, rel.Replace('/', System.IO.Path.DirectorySeparatorChar));
}

/// <summary>A file of an undo plan whose current content is not what the change wrote: later changes to it get lost.</summary>
public sealed record BackupConflict(string Path, string Reason);

/// <summary>
/// The history of changes PokéPat made: every write keeps the old files in %LOCALAPPDATA%\TilesetEditor\backups\&lt;time&gt;-&lt;title&gt;
/// and a description (pokepat-backup.json: project, files, created/modified/deleted, the written content's SHA-256).
/// A change can be undone on its own, or the project can go back to the state before a change (undoing every later one
/// too, newest first). Undoing is itself written with a backup, so it can be undone again.
/// </summary>
public static class BackupHistory
{
    public const string ManifestName = "pokepat-backup.json";

    private sealed record ManifestDto(int Version, string Project, string Title, DateTime Time, List<FileDto> Files);
    private sealed record FileDto(string Path, string Action, string? Written);

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public static byte[] Manifest(ExportPlan plan, IEnumerable<string> backedUp) =>
        JsonSerializer.SerializeToUtf8Bytes(new ManifestDto(1, plan.ProjectRoot, plan.TilesetName, DateTime.Now,
            plan.Files.Select(f => new FileDto(f.RelativePath, f.Action.ToString(), f.Action == PlannedAction.Delete ? null : Hashing.Sha256(f.Content))).ToList()), Json);

    /// <summary>All backups, newest first. Older backups without a description show only the files they kept (as modified).</summary>
    public static List<BackupEntry> List(string? root = null)
    {
        root ??= ExportApplier.DefaultBackupRoot;
        if (!Directory.Exists(root)) return [];
        var list = new List<BackupEntry>();
        foreach (var dir in Directory.GetDirectories(root))
        {
            var name = System.IO.Path.GetFileName(dir);
            var manifest = System.IO.Path.Combine(dir, ManifestName);
            if (File.Exists(manifest))
            {
                try
                {
                    var m = JsonSerializer.Deserialize<ManifestDto>(File.ReadAllBytes(manifest), Json);
                    if (m != null)
                    {
                        list.Add(new BackupEntry(dir, m.Time, m.Title, m.Project,
                            m.Files.Select(f => new BackupFile(f.Path, Enum.TryParse<PlannedAction>(f.Action, out var a) ? a : PlannedAction.Modify, f.Written)).ToList(), true));
                        continue;
                    }
                }
                catch (JsonException) { }
            }
            var time = name.Length >= 15 && DateTime.TryParseExact(name[..15], "yyyyMMdd-HHmmss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var t) ? t : Directory.GetCreationTime(dir);
            var title = name.Length > 16 ? name[16..] : name;
            var files = Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories)
                .Select(f => System.IO.Path.GetRelativePath(dir, f).Replace('\\', '/')).Where(f => f != ManifestName)
                .Select(f => new BackupFile(f, PlannedAction.Modify, null)).ToList();
            list.Add(new BackupEntry(dir, time, title, null, files, false));
        }
        return list.OrderByDescending(e => e.Time).ThenByDescending(e => e.Folder, StringComparer.Ordinal).ToList();
    }

    public static bool SameProject(BackupEntry e, string projectRoot) =>
        e.Project != null && string.Equals(System.IO.Path.GetFullPath(e.Project).TrimEnd('\\', '/'), System.IO.Path.GetFullPath(projectRoot).TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);

    /// <summary>Undoes one change. Files changed again since are listed as conflicts (the undo would replace those later changes).</summary>
    public static (ExportPlan Plan, List<BackupConflict> Conflicts) PlanUndo(BackupEntry entry, string projectRoot) =>
        PlanUndo([entry], projectRoot, $"Undo: {entry.Title}");

    /// <summary>
    /// Back to the state before <paramref name="target"/>: undoes it and every later change of the same project. Each file
    /// gets the content it had before the oldest of these changes that touched it.
    /// </summary>
    public static (ExportPlan Plan, List<BackupConflict> Conflicts, List<BackupEntry> Undone) PlanBackTo(BackupEntry target, IReadOnlyList<BackupEntry> all, string projectRoot)
    {
        var undone = all.Where(e => e.HasManifest && SameProject(e, projectRoot) && e.Time >= target.Time).OrderByDescending(e => e.Time).ToList();
        if (!undone.Contains(target)) undone.Add(target);
        var (plan, conflicts) = PlanUndo(undone, projectRoot, $"Back to before: {target.Title}");
        return (plan, conflicts, undone);
    }

    /// <param name="entries">Newest first.</param>
    private static (ExportPlan, List<BackupConflict>) PlanUndo(IReadOnlyList<BackupEntry> entries, string projectRoot, string title)
    {
        var conflicts = new List<BackupConflict>();
        var files = new List<PlannedFile>();
        var paths = entries.SelectMany(e => e.Files.Select(f => f.Path)).Distinct(StringComparer.Ordinal).ToList();
        foreach (var rel in paths)
        {
            // The newest change that touched the file decides what should be there now; the oldest one what was there before.
            var newest = entries.First(e => e.Files.Any(f => f.Path == rel));
            var oldest = entries.Last(e => e.Files.Any(f => f.Path == rel));
            var newestFile = newest.Files.First(f => f.Path == rel);
            var oldestFile = oldest.Files.First(f => f.Path == rel);
            var full = ProjectPaths.Combine(projectRoot, rel);
            bool exists = File.Exists(full);
            string? currentSha = exists ? Hashing.Sha256(File.ReadAllBytes(full)) : null;
            if (newestFile.Action == PlannedAction.Delete ? exists : newestFile.WrittenSha256 != null && currentSha != newestFile.WrittenSha256)
                conflicts.Add(new BackupConflict(rel, exists ? "changed again since (by hand, git or a later change) – that change is lost" : "missing now"));
            if (oldestFile.Action == PlannedAction.Create)
            {
                if (exists) files.Add(new PlannedFile(rel, PlannedAction.Delete, [], "was added by the change – removed again", currentSha));
                continue;
            }
            var backup = oldest.Original(rel);
            if (!File.Exists(backup)) { conflicts.Add(new BackupConflict(rel, "the backup has no copy of this file – left as it is")); continue; }
            var content = File.ReadAllBytes(backup);
            if (exists && currentSha == Hashing.Sha256(content)) continue;     // already as before
            files.Add(exists
                ? new PlannedFile(rel, PlannedAction.Modify, content, "back to the content before the change", currentSha)
                : new PlannedFile(rel, PlannedAction.Create, content, "restored (was deleted)"));
        }
        return (ExportPlan.ForFiles(projectRoot, title, "", files), conflicts);
    }

    /// <summary>Deletes backups older than <paramref name="days"/> days; returns how many.</summary>
    public static int DeleteOlderThan(int days, string? root = null)
    {
        int n = 0;
        foreach (var e in List(root).Where(e => e.Time < DateTime.Now.AddDays(-days)))
        {
            try { Directory.Delete(e.Folder, true); n++; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        return n;
    }
}
