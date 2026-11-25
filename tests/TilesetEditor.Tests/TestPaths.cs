namespace TilesetEditor.Tests;

internal static class TestPaths
{
    /// <summary>
    /// A path from the environment variable <paramref name="key"/>, else from "test-paths.local.txt" in the repository root
    /// (KEY=path per line, not committed – see test-paths.example.txt), else a folder of the same name next to the repository.
    /// </summary>
    public static string Setting(string key, string fallbackFolder)
    {
        if (Environment.GetEnvironmentVariable(key) is { Length: > 0 } env) return env;
        var repo = RepoRoot();
        if (repo != null)
        {
            var local = Path.Combine(repo, "test-paths.local.txt");
            if (File.Exists(local))
                foreach (var line in File.ReadAllLines(local))
                {
                    var i = line.IndexOf('=');
                    if (i > 0 && line[..i].Trim() == key) return line[(i + 1)..].Trim().Trim('"');
                }
            return Path.Combine(Path.GetDirectoryName(repo) ?? repo, fallbackFolder);
        }
        return fallbackFolder;
    }

    private static string? RepoRoot()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "TilesetEditor.sln"))) return d.FullName;
        return null;
    }

    /// <summary>Read-only checkout of a pokeemerald-expansion based project (C-format trainers). TILESETEDITOR_REFERENCE_PROJECT.</summary>
    public static string ReferenceProject => Setting("TILESETEDITOR_REFERENCE_PROJECT", "reference-project");

    /// <summary>Optional: a writable, buildable checkout for tests that run Poryscript. TILESETEDITOR_TEST_CHECKOUT.</summary>
    public static string TestCheckout => Setting("TILESETEDITOR_TEST_CHECKOUT", "test-checkout");

    public static string Asset(string name) => Path.Combine(AppContext.BaseDirectory, "assets", name);

    public static string NewTempDir(string prefix)
    {
        var d = Path.Combine(Path.GetTempPath(), "TilesetEditorTests", prefix + "-" + Guid.NewGuid().ToString("N")[..8] + " with spaces");
        Directory.CreateDirectory(d);
        return d;
    }

    /// <summary>Copies the parts of the reference project that the tileset code reads into a writable temp project.</summary>
    public static string CopyMiniProject()
    {
        var src = ReferenceProject;
        var dst = NewTempDir("project");
        string[] files =
        [
            "include/fieldmap.h", "include/global.fieldmap.h", "include/constants/metatile_behaviors.h", "include/constants/expansion.h",
            "src/data/tilesets/headers.h", "src/data/tilesets/graphics.h", "src/data/tilesets/metatiles.h", "src/graphics.c",
            "data/layouts/layouts.json", "Makefile",
        ];
        foreach (var f in files) Copy(src, dst, f);
        foreach (var f in Directory.EnumerateFiles(Path.Combine(src, "data", "tilesets", "primary", "general"), "*", SearchOption.AllDirectories))
            Copy(src, dst, Path.GetRelativePath(src, f));
        // Existing secondary folders matter for collision checks.
        foreach (var d in Directory.EnumerateDirectories(Path.Combine(src, "data", "tilesets", "secondary")))
            Directory.CreateDirectory(Path.Combine(dst, "data", "tilesets", "secondary", Path.GetFileName(d)));
        return dst;
    }

    private static void Copy(string src, string dst, string rel)
    {
        var s = Path.Combine(src, rel.Replace('/', Path.DirectorySeparatorChar));
        if (!File.Exists(s)) return;
        var t = Path.Combine(dst, rel.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(t)!);
        File.Copy(s, t);
    }
}
