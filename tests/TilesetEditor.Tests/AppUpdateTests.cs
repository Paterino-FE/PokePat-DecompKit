using TilesetEditor.Toolkit.Install;

namespace TilesetEditor.Tests;

public class AppUpdateTests
{
    [Fact]
    public void ReleaseVersionComesFromTagTitleOrPackage()
    {
        // The published release is tagged "Release"; its version is only in the title and the package name.
        var asset = new ReleaseAsset("PokePat-DecompKit-0.2.0-windows-x64.zip", "u", 10);
        Assert.Equal("0.2.0", new Release("Release", [asset], Name: "PokéPat-DecompKit Version 0.2.0").Version);
        Assert.Equal("0.2.0", new Release("Release", [asset]).Version);
        Assert.Equal("0.3.1", new Release("v0.3.1", [asset], Name: "Version 0.2.0").Version);
        Assert.Null(new Release("Release", []).Version);
        Assert.True(GitHubReleases.CompareVersions("0.1.9", "0.2.0") < 0);
        Assert.Equal(0, GitHubReleases.CompareVersions("0.2.0", "0.2.0"));
    }

    [Fact]
    public void DownloadsAreCheckedAgainstTheRelease()
    {
        var file = Path.Combine(TestPaths.NewTempDir("upd"), "p.zip");
        File.WriteAllBytes(file, [1, 2, 3]);
        var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(new byte[] { 1, 2, 3 }));
        AppSelfUpdate.Verify(new ReleaseAsset("p.zip", "u", 3, "sha256:" + hash.ToLowerInvariant()), file, hash);
        Assert.Throws<InvalidDataException>(() => AppSelfUpdate.Verify(new ReleaseAsset("p.zip", "u", 3, "sha256:" + new string('0', 64)), file, hash));
        AppSelfUpdate.Verify(new ReleaseAsset("p.zip", "u", 3), file, hash);                   // no digest: the size must match
        Assert.Throws<InvalidDataException>(() => AppSelfUpdate.Verify(new ReleaseAsset("p.zip", "u", 99), file, hash));
    }

    [Fact]
    public void SwapScriptWaitsBacksUpAndRestarts()
    {
        var script = AppSelfUpdate.SwapScript(@"C:\t\new", @"C:\Users\o'brien\PokePat", 1234, @"C:\t\update.log");
        Assert.Contains("Wait-Process -Id 1234", script);
        Assert.Contains("'C:\\Users\\o''brien\\PokePat'", script);                       // quotes are escaped
        Assert.Contains(".update-backup", script);
        Assert.Contains("restoring the old version", script);
        Assert.EndsWith("Start-Process -FilePath $exe -WorkingDirectory $dest" + Environment.NewLine, script);
    }
}
