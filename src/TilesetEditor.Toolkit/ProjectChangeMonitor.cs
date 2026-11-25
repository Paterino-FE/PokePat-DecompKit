namespace TilesetEditor.Toolkit;

/// <summary>
/// Detects changes made outside the toolkit (Porymap saving layouts or tilesets, a git branch switch, a text editor).
/// Polls a small set of key files (size + timestamp) instead of using FileSystemWatcher, because change
/// notifications are unreliable on \\wsl.localhost shares, where most decomp projects live on Windows.
/// </summary>
public sealed class ProjectChangeMonitor : IDisposable
{
    /// <summary>Files whose change makes the toolkit re-read the project.</summary>
    public static readonly string[] WatchedFiles =
    [
        "data/layouts/layouts.json",
        "data/maps/map_groups.json",
        "src/data/tilesets/headers.h",
        "src/data/tilesets/graphics.h",
        "src/data/tilesets/metatiles.h",
        "src/graphics.c",
        "include/fieldmap.h",
        "include/global.fieldmap.h",
        "Makefile",
        ".git/HEAD",
    ];

    private readonly string _root;
    private readonly Timer _timer;
    private readonly object _gate = new();
    private Dictionary<string, (bool Exists, long Length, DateTime Write)> _baseline;
    private bool _disposed;

    /// <summary>Raised on a thread-pool thread with the project-relative paths that changed.</summary>
    public event Action<IReadOnlyList<string>>? Changed;

    public ProjectChangeMonitor(string root, TimeSpan interval)
    {
        _root = root;
        _baseline = Snapshot();
        _timer = new Timer(_ => Poll(), null, interval, interval);
    }

    /// <summary>Accept the current state as known, e.g. after the toolkit itself wrote files.</summary>
    public void Rebaseline()
    {
        lock (_gate) _baseline = Snapshot();
    }

    /// <summary>Forget the known state of these files so the next poll reports them again (retry after a failed reload).</summary>
    public void Invalidate(IEnumerable<string> files)
    {
        lock (_gate)
            foreach (var f in files) _baseline.Remove(f);
    }

    /// <summary>Checks once; returns the changed files (also raises <see cref="Changed"/>). Public for tests.</summary>
    public IReadOnlyList<string> Poll()
    {
        List<string> changed;
        lock (_gate)
        {
            if (_disposed) return [];
            var now = Snapshot();
            changed = now.Where(kv => !_baseline.TryGetValue(kv.Key, out var old) || old != kv.Value).Select(kv => kv.Key).ToList();
            if (changed.Count == 0) return changed;
            _baseline = now;
        }
        Changed?.Invoke(changed);
        return changed;
    }

    private Dictionary<string, (bool, long, DateTime)> Snapshot()
    {
        var d = new Dictionary<string, (bool, long, DateTime)>(StringComparer.Ordinal);
        foreach (var rel in WatchedFiles)
        {
            try
            {
                var fi = new FileInfo(Path.Combine(_root, rel.Replace('/', Path.DirectorySeparatorChar)));
                d[rel] = fi.Exists ? (true, fi.Length, fi.LastWriteTimeUtc) : (false, 0, default);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                d[rel] = (false, -1, default); // temporarily unreadable (e.g. WSL restarting); reported once it is back
            }
        }
        return d;
    }

    public static string Describe(IReadOnlyList<string> changed)
    {
        if (changed.Contains(".git/HEAD")) return "Git branch switched";
        var names = changed.Select(c => Path.GetFileName(c)).Distinct().ToList();
        string who = changed.Any(c => c.StartsWith("data/layouts", StringComparison.Ordinal) || c.StartsWith("data/maps", StringComparison.Ordinal)
                                      || c.StartsWith("src/data/tilesets", StringComparison.Ordinal))
            ? " (e.g. by Porymap)" : "";
        return $"{string.Join(", ", names)} changed{who}";
    }

    public void Dispose()
    {
        lock (_gate) _disposed = true;
        _timer.Dispose();
    }
}
