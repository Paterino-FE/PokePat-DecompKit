using TilesetEditor.Toolkit;

namespace TilesetEditor.Tests;

/// <summary>Build runner behaviour with cmd.exe commands standing in for make.</summary>
public class BuildRunnerTests
{
    private static BuildRequest Req(string root, string command) => new()
    {
        ProjectRoot = root,
        RomRelativePath = "pokeemerald.gba",
        Settings = new ProjectToolSettings { BuildEnvironment = BuildEnvironment.Windows, BuildCommand = command },
    };

    [Fact]
    public async Task Success_ReportsFreshRomThenUpToDate()
    {
        var root = TestPaths.NewTempDir("build");
        var lines = new List<string>();
        var r1 = await new BuildRunner().RunAsync(Req(root, "echo built> pokeemerald.gba && echo done"), lines.Add, CancellationToken.None);
        Assert.True(r1.Success);
        Assert.Equal(RomState.Rebuilt, r1.RomState);
        Assert.Equal(Path.Combine(root, "pokeemerald.gba"), r1.ConfirmedRomPath);
        Assert.Contains(lines, l => l.Contains("done"));

        var r2 = await new BuildRunner().RunAsync(Req(root, "echo nothing to be done"), _ => { }, CancellationToken.None);
        Assert.True(r2.Success);
        Assert.Equal(RomState.UpToDate, r2.RomState);
        Assert.Contains("up to date", r2.Message);
    }

    [Fact]
    public async Task Failure_WithExistingRom_NeverReportsTheOldRom()
    {
        var root = TestPaths.NewTempDir("build");
        File.WriteAllText(Path.Combine(root, "pokeemerald.gba"), "old rom from yesterday");
        var r = await new BuildRunner().RunAsync(Req(root, "echo src/foo.c:1: error: boom 1>&2 && exit /b 2"), _ => { }, CancellationToken.None);
        Assert.False(r.Success);
        Assert.Equal(2, r.ExitCode);
        Assert.Equal(RomState.None, r.RomState);
        Assert.Null(r.ConfirmedRomPath);
        Assert.Contains("NOT the result of this build", r.Message);
        Assert.Contains(r.ErrorLines, l => l.Contains("error: boom"));
    }

    [Fact]
    public async Task SuccessWithoutRom_IsAFailure()
    {
        var root = TestPaths.NewTempDir("build");
        var r = await new BuildRunner().RunAsync(Req(root, "echo ok"), _ => { }, CancellationToken.None);
        Assert.False(r.Success);
        Assert.Null(r.ConfirmedRomPath);
    }

    [Fact]
    public async Task ParallelBuildOfSameProject_IsRejected()
    {
        var root = TestPaths.NewTempDir("build");
        var first = new BuildRunner().RunAsync(Req(root, "ping -n 6 127.0.0.1 >nul && echo x> pokeemerald.gba"), _ => { }, CancellationToken.None);
        // Under load the first build may take a moment to take the lock: wait for it instead of a fixed delay.
        for (int i = 0; i < 50 && !BuildRunner.IsRunning(root); i++) await Task.Delay(100);
        Assert.True(BuildRunner.IsRunning(root));
        await Assert.ThrowsAsync<BuildAlreadyRunningException>(() => new BuildRunner().RunAsync(Req(root, "echo y"), _ => { }, CancellationToken.None));
        Assert.True((await first).Success);
        Assert.False(BuildRunner.IsRunning(root));
    }

    [Fact]
    public async Task Cancel_GivesNoResult()
    {
        var root = TestPaths.NewTempDir("build");
        File.WriteAllText(Path.Combine(root, "pokeemerald.gba"), "old");
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
        var r = await new BuildRunner().RunAsync(Req(root, "ping -n 10 127.0.0.1 >nul"), _ => { }, cts.Token);
        Assert.True(r.Cancelled);
        Assert.Null(r.ConfirmedRomPath);
    }

    [Fact]
    public void WslCommandLine_UsesProjectRootAsWorkingDirectory()
    {
        var (file, args, wd) = BuildRunner.BuildCommandLine(@"\\wsl.localhost\Ubuntu\home\me\my project",
            new ProjectToolSettings { BuildEnvironment = BuildEnvironment.Wsl, BuildWslDistro = "Ubuntu", BuildCommand = "make -j$(nproc)" });
        Assert.Equal("wsl.exe", file);
        Assert.Null(wd);
        Assert.Equal(["-d", "Ubuntu", "--cd", "/home/me/my project", "--exec", "bash", "-lc", "make -j$(nproc)"], args);
    }

    [Fact]
    public void Settings_RoundTrip()
    {
        var path = Path.Combine(TestPaths.NewTempDir("settings"), "settings.json");
        var store = new ToolkitSettingsStore(path);
        var s = new ToolkitSettings();
        var p = s.ForProject(@"C:\proj");
        p.BuildCommand = "make modern";
        p.BuildEnvironment = BuildEnvironment.Windows;
        s.Touch(@"C:\proj");
        store.Save(s);
        var back = store.Load();
        Assert.Equal("make modern", back.ForProject(@"C:\proj\").BuildCommand);
        Assert.Equal(BuildEnvironment.Windows, back.ForProject(@"C:\proj").BuildEnvironment);
        Assert.Equal(@"C:\proj", back.LastProject);
    }
}
