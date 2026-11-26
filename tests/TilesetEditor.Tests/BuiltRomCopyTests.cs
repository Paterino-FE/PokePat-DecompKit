using TilesetEditor.Toolkit;
using Xunit;

namespace TilesetEditor.Tests;

public class BuiltRomCopyTests
{
    [Fact]
    public void TheBuiltRomIsCopiedNextToTheAppAndReplacedByTheNextBuild()
    {
        var project = Path.Combine(TestPaths.NewTempDir("romproject"), "pokeplatinum");
        Directory.CreateDirectory(Path.Combine(project, "build"));
        var rom = Path.Combine(project, "build", "pokeplatinum.us.nds");
        var app = TestPaths.NewTempDir("app");
        File.WriteAllText(rom, "first");
        var (copy, error) = BuiltRomCopy.Copy(rom, project, app);
        Assert.Null(error);
        Assert.Equal(Path.Combine(app, "ROM", "pokeplatinum.nds"), copy);
        File.WriteAllText(rom, "second build");
        Assert.Equal(copy, BuiltRomCopy.Copy(rom, project + "/", app).Path);   // same name, replaced
        Assert.Equal("second build", File.ReadAllText(copy!));
        Assert.Single(Directory.GetFiles(Path.Combine(app, "ROM")));             // no temporary file left
    }
}
