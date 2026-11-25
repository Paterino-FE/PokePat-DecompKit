using TilesetEditor.Toolkit;

namespace TilesetEditor.Tests;

public class ProjectChangeMonitorTests
{
    private static string NewProject()
    {
        var root = TestPaths.NewTempDir("watch");
        Directory.CreateDirectory(Path.Combine(root, "data", "layouts"));
        Directory.CreateDirectory(Path.Combine(root, ".git"));
        File.WriteAllText(Path.Combine(root, "data", "layouts", "layouts.json"), "{}");
        File.WriteAllText(Path.Combine(root, ".git", "HEAD"), "ref: refs/heads/main\n");
        return root;
    }

    // A long interval: the tests call Poll() themselves.
    private static ProjectChangeMonitor Monitor(string root) => new(root, TimeSpan.FromHours(1));

    [Fact]
    public void ReportsOutsideChangeOnce()
    {
        var root = NewProject();
        using var m = Monitor(root);
        Assert.Empty(m.Poll());
        File.WriteAllText(Path.Combine(root, "data", "layouts", "layouts.json"), "{\"layouts\": []}");
        Assert.Equal(["data/layouts/layouts.json"], m.Poll());
        Assert.Empty(m.Poll());
    }

    [Fact]
    public void RebaselineHidesOwnWrites()
    {
        var root = NewProject();
        using var m = Monitor(root);
        File.WriteAllText(Path.Combine(root, "data", "layouts", "layouts.json"), "{\"own\": 1}");
        m.Rebaseline();
        Assert.Empty(m.Poll());
    }

    [Fact]
    public void InvalidateReportsAgainForRetry()
    {
        var root = NewProject();
        using var m = Monitor(root);
        File.WriteAllText(Path.Combine(root, ".git", "HEAD"), "ref: refs/heads/feature\n");
        var changed = m.Poll();
        Assert.Equal("Git branch switched", ProjectChangeMonitor.Describe(changed));
        m.Invalidate(changed);
        Assert.Equal(changed, m.Poll());
    }

    [Fact]
    public void RaisesEventWithDescription()
    {
        var root = NewProject();
        using var m = Monitor(root);
        IReadOnlyList<string>? seen = null;
        m.Changed += c => seen = c;
        Directory.CreateDirectory(Path.Combine(root, "src", "data", "tilesets"));
        File.WriteAllText(Path.Combine(root, "src", "data", "tilesets", "headers.h"), "// new");
        m.Poll();
        Assert.NotNull(seen);
        Assert.Equal("headers.h changed (e.g. by Porymap)", ProjectChangeMonitor.Describe(seen!));
    }
}
