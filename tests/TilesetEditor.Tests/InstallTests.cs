using System.Text.Json;
using TilesetEditor.Toolkit.Install;

namespace TilesetEditor.Tests;

public class InstallTests
{
    private static string Makefile => File.ReadAllText(Path.Combine(TestPaths.ReferenceProject, "Makefile"));

    [Fact]
    public void Poryscript_IsWiredIntoTheMakefile()
    {
        var original = Makefile;
        Assert.False(PoryscriptMakefile.IsIntegrated(original));
        var (text, problem) = PoryscriptMakefile.Integrate(original);
        Assert.Null(problem);
        Assert.NotNull(text);
        Assert.True(PoryscriptMakefile.IsIntegrated(text));
        var lines = text!.Split('\n');
        Assert.Contains(PoryscriptMakefile.ScriptVariable, lines);
        Assert.Contains(PoryscriptMakefile.AutoGen, lines);
        Assert.Contains(PoryscriptMakefile.NoRule, lines);
        Assert.Contains(PoryscriptMakefile.Rule, lines);
        // Only lines were added, nothing existing changed.
        var added = lines.ToList();
        foreach (var l in original.Split('\n')) Assert.True(added.Remove(l), "missing line: " + l);
        Assert.All(added, l => Assert.True(l.Length == 0 || l.Contains("pory") || l.StartsWith("SCRIPT"), l));
        // The variable is defined before it is used, the .inc rule comes after the generic rules.
        Assert.True(Array.IndexOf(lines, PoryscriptMakefile.ScriptVariable) < Array.IndexOf(lines, PoryscriptMakefile.Rule));
        Assert.True(Array.FindIndex(lines, l => l.StartsWith("generated:")) > Array.IndexOf(lines, PoryscriptMakefile.AutoGen));
        // Running it again changes nothing.
        Assert.Equal(text, PoryscriptMakefile.Integrate(text).Text);
    }

    [Fact]
    public void Poryscript_KeepsWindowsLineEndings()
    {
        var crlf = Makefile.Replace("\r\n", "\n").Replace("\n", "\r\n");
        var text = PoryscriptMakefile.Integrate(crlf).Text!;
        Assert.DoesNotContain("\r\r", text);
        Assert.Equal(text.Split('\n').Length - 1, text.Split("\r\n").Length - 1);
    }

    [Fact]
    public void Poryscript_UnknownMakefileIsRefused()
    {
        var (text, problem) = PoryscriptMakefile.Integrate("all:\n\techo hi\n");
        Assert.Null(text);
        Assert.NotNull(problem);
    }

    [Fact]
    public void GitIgnore_GetsTheBinaryOnce()
    {
        var once = PoryscriptMakefile.IgnoreBinary("build/\n*.gba\n");
        Assert.EndsWith("tools/poryscript/poryscript\n", once);
        Assert.Equal(once, PoryscriptMakefile.IgnoreBinary(once));
        Assert.Contains("tools/poryscript/poryscript", PoryscriptMakefile.IgnoreBinary(null));
    }

    [Theory]
    [InlineData("plain", "'plain'")]
    [InlineData("it's", "'it'\\''s'")]
    [InlineData("~/pokeemerald-expansion", "\"$HOME\"/'pokeemerald-expansion'")]
    [InlineData("~", "\"$HOME\"")]
    [InlineData("/home/a b", "'/home/a b'")]
    public void Shell_QuotesPaths(string path, string expected) => Assert.Equal(expected, LinuxShell.QuotePath(path));

    [Fact]
    public void Shell_HostPathOfWsl()
    {
        Assert.Equal(@"\\wsl.localhost\Ubuntu\home\me\game", new LinuxShell(true, "Ubuntu").ToHostPath("/home/me/game"));
        Assert.Equal("/home/me/game", new LinuxShell(false, "Ubuntu").ToHostPath("/home/me/game"));
    }

    [Fact]
    public void PinnedPorytiles_MatchesTheLockFile()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "TilesetEditor.sln"))) dir = dir.Parent;
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(dir!.FullName, "tools", "porytiles.lock.json")));
        Assert.Equal(doc.RootElement.GetProperty("url").GetString(), Installer.PorytilesUrl);
        Assert.Equal(doc.RootElement.GetProperty("sha256").GetString(), Installer.PorytilesSha256, ignoreCase: true);
        Assert.Equal(doc.RootElement.GetProperty("executable").GetString(), TilesetEditor.Compiler.PorytilesSettings.DefaultWslExecutable);
    }

    [UnixFact]
    public async Task Shell_RunsAndResolvesHome()
    {
        var shell = new LinuxShell(false, "Ubuntu");
        var r = await shell.RunAsync("echo one; echo two >&2; exit 3");
        Assert.Equal(3, r.ExitCode);
        Assert.Contains("one", r.Output);
        Assert.Contains("two", r.Output);
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        Assert.Equal(home + "/not yet/there", await shell.ResolveAsync("~/not yet/there"));
    }

    [UnixFact]
    public async Task Installer_RecognisesTheProjectFolder()
    {
        var ready = new Installer(new InstallOptions { UseWsl = false, ProjectPath = TestPaths.ReferenceProject });
        Assert.Equal(InstallState.Ready, (await ready.CheckAsync(InstallComponent.Expansion)).State);
        Assert.Equal(Path.GetFullPath(TestPaths.ReferenceProject).TrimEnd('/'), ready.ProjectHostPath);

        var empty = TestPaths.NewTempDir("install");
        var missing = new Installer(new InstallOptions { UseWsl = false, ProjectPath = Path.Combine(empty, "game") });
        Assert.Equal(InstallState.Missing, (await missing.CheckAsync(InstallComponent.Expansion)).State);
        Assert.Equal(InstallState.Missing, (await missing.CheckAsync(InstallComponent.Poryscript)).State);

        File.WriteAllText(Path.Combine(empty, "notes.txt"), "mine");
        var occupied = new Installer(new InstallOptions { UseWsl = false, ProjectPath = empty });
        Assert.Equal(InstallState.Failed, (await occupied.CheckAsync(InstallComponent.Expansion)).State);

        Assert.Equal(InstallState.NotAvailable, (await ready.CheckAsync(InstallComponent.Wsl)).State);
    }

    [UnixFact]
    public async Task Installer_InstallsPoryscriptIntoACopy()
    {
        // A minimal project: the files the check looks for plus the real Makefile.
        var root = TestPaths.NewTempDir("install-pory");
        Directory.CreateDirectory(Path.Combine(root, "include", "constants"));
        File.Copy(Path.Combine(TestPaths.ReferenceProject, "include", "constants", "flags.h"), Path.Combine(root, "include", "constants", "flags.h"));
        File.WriteAllText(Path.Combine(root, "Makefile"), Makefile);
        var installer = new Installer(new InstallOptions { UseWsl = false, ProjectPath = root });
        var tools = Path.Combine(root, "tools", "poryscript");
        Directory.CreateDirectory(tools);
        // Pretend the binary is there (no download in unit tests): only the Makefile and .gitignore are done.
        File.WriteAllText(Path.Combine(tools, "poryscript"), "#!/bin/sh\necho 3.6.1\n");
        File.SetUnixFileMode(Path.Combine(tools, "poryscript"), (UnixFileMode)0b111_101_101);
        var log = new List<string>();
        var s = await installer.InstallAsync(InstallComponent.Poryscript, log.Add, newWindow: false);
        Assert.Equal(InstallState.Ready, s.State);
        Assert.True(PoryscriptMakefile.IsIntegrated(File.ReadAllText(Path.Combine(root, "Makefile"))));
        Assert.Contains("tools/poryscript/poryscript", File.ReadAllText(Path.Combine(root, ".gitignore")));
    }
}

public class UpdateTests
{
    [Theory]
    [InlineData("expansion/1.17.2", "expansion/1.18.0", -1)]
    [InlineData("6.3.1.0", "6.3.1", 0)]
    [InlineData("v3.6.1", "3.6.0", 1)]
    [InlineData("0.10.5", "0.10.10", -1)]
    [InlineData("unknown", "1.0.0", 0)]
    public void Versions_AreComparedByNumber(string a, string b, int expected) =>
        Assert.Equal(expected, Math.Sign(GitHubReleases.CompareVersions(a, b)));

    [Fact]
    public void Versions_ShortForm()
    {
        Assert.Equal("1.17.2", GitHubReleases.Short("expansion/1.17.2"));
        Assert.Equal("6.3.1", GitHubReleases.Short("6.3.1.0"));
    }

    [Fact]
    public void ProgramVersion_FromMarkerOrChangelog()
    {
        var dir = TestPaths.NewTempDir("progver");
        var exe = Path.Combine(dir, "mGBA.exe");
        File.WriteAllText(exe, "");
        Assert.Null(Installer.ProgramVersion(exe));
        File.WriteAllText(Path.Combine(dir, "CHANGES.txt"), "0.10.5: (2025-03-08)\nBugfixes:\n");
        Assert.Equal("0.10.5", Installer.ProgramVersion(exe));
        File.WriteAllText(Path.Combine(dir, Installer.VersionMarker), "0.11.0\n");
        Assert.Equal("0.11.0", Installer.ProgramVersion(exe));
    }

    private static async Task<string> Git(string dir, string args)
    {
        var r = await new LinuxShell(false, "").RunAsync($"cd {LinuxShell.Quote(dir)} && git -c user.name=T -c user.email=t@t {args}");
        Assert.True(r.Ok, args + ": " + r.Output);
        return r.Output;
    }

    /// <summary>An "upstream" repo with release 1.0.0 and 1.1.0, and a project cloned at 1.0.0 on branch pokepat.</summary>
    private static async Task<(string Upstream, string Project)> Repos(string upstreamChange)
    {
        var root = TestPaths.NewTempDir("update");
        var up = Path.Combine(root, "upstream");
        Directory.CreateDirectory(Path.Combine(up, "include", "constants"));
        File.WriteAllText(Path.Combine(up, "Makefile"), "all:\n");
        File.WriteAllText(Path.Combine(up, "include", "constants", "flags.h"), "#define FLAG_A 1\n");
        File.WriteAllText(Path.Combine(up, "game.c"), "line1\nline2\nline3\n");
        await Git(up, "init -q -b master && git add -A && git -c user.name=T -c user.email=t@t commit -qm r1 && git tag expansion/1.0.0");
        File.WriteAllText(Path.Combine(up, "game.c"), upstreamChange);
        await Git(up, "commit -qam r2 && git tag expansion/1.1.0");
        var project = Path.Combine(root, "game");
        await Git(root, $"clone -q --branch expansion/1.0.0 {LinuxShell.Quote(up)} game 2>/dev/null; cd game && git switch -q -c pokepat");
        return (up, project);
    }

    private static Installer For(string upstream, string project) =>
        new(new InstallOptions { UseWsl = false, ProjectPath = project, ExpansionRepo = upstream, ExpansionTag = "expansion/1.1.0" });

    [UnixFact]
    public async Task Expansion_UpdateMergesTheRelease()
    {
        var (up, project) = await Repos("line1\nline2 new\nline3\n");
        File.WriteAllText(Path.Combine(project, "mine.c"), "my hack\n");
        await Git(project, "add -A && git -c user.name=T -c user.email=t@t commit -qm mine");
        var s = await For(up, project).UpdateAsync(InstallComponent.Expansion, _ => { });
        Assert.True(s.IsReady, s.Message);
        Assert.Contains("line2 new", File.ReadAllText(Path.Combine(project, "game.c")));
        Assert.True(File.Exists(Path.Combine(project, "mine.c")));
        Assert.Contains("expansion/1.1.0", await Git(project, "describe --tags --abbrev=0"));
    }

    [UnixFact]
    public async Task Expansion_UpdateRefusesUncommittedChanges()
    {
        var (up, project) = await Repos("line1\nline2 new\nline3\n");
        File.WriteAllText(Path.Combine(project, "game.c"), "line1\nline2\nline3 mine\n");
        var s = await For(up, project).UpdateAsync(InstallComponent.Expansion, _ => { });
        Assert.Equal(InstallState.Failed, s.State);
        Assert.Contains("not committed", s.Message);
        Assert.Equal("line1\nline2\nline3 mine\n", File.ReadAllText(Path.Combine(project, "game.c")));
    }

    [UnixFact]
    public async Task Expansion_ConflictLeavesTheProjectUnchanged()
    {
        var (up, project) = await Repos("line1\nline2 theirs\nline3\n");
        File.WriteAllText(Path.Combine(project, "game.c"), "line1\nline2 mine\nline3\n");
        await Git(project, "commit -qam mine");
        var head = await Git(project, "rev-parse HEAD");
        var s = await For(up, project).UpdateAsync(InstallComponent.Expansion, _ => { });
        Assert.Equal(InstallState.Failed, s.State);
        Assert.Contains("same lines", s.Message);
        Assert.Equal(head, await Git(project, "rev-parse HEAD"));
        Assert.Equal("", (await Git(project, "status --porcelain")).Trim());
        Assert.Equal("line1\nline2 mine\nline3\n", File.ReadAllText(Path.Combine(project, "game.c")));
    }
}
