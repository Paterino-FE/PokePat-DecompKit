using TilesetEditor.Core.Project;
using Xunit;

namespace TilesetEditor.Tests;

public class BuildProblemsTests
{
    [Fact]
    public void ProblemsOfRealBuildLogsPointAtProjectFiles()
    {
        const string root = "/home/u/pokeplatinum";
        var log = """
            [12/80] Generating 'res/field/maps/scripts.p/scripts_jubilife_city'
            /tmp/ccdYHHbl.s: Assembler messages:
            /home/u/pokeplatinum/res/field/maps/jubilife_city/scripts.s:1251: Error: bad instruction `notacommand 5'
            /home/u/pokeplatinum/res/field/maps/jubilife_city/events.json:5:18: error: JSON parse error: unexpected character, expected a JSON value
                5 |             "x": FOO_UNKNOWN, "y0": 168,
                  |                  ^
            /home/u/pokeplatinum/res/field/maps/jubilife_city/events.json:5:18: error: JSON parse error: unexpected character, expected a JSON value
            ../src/overworld.c:44:9: warning: unused variable 'x' [-Wunused-variable]
            data/maps/LittlerootTown/scripts.inc:12: Error: unknown pseudo-op: `.fooo'
            ninja: build stopped: subcommand failed.
            make: *** [Makefile:109: rom] Error 1
            """;
        var p = BuildProblems.Parse(log, root);
        Assert.Equal(4, p.Count);   // the repeated JSON error once, nothing for make/ninja
        Assert.Equal(("res/field/maps/jubilife_city/scripts.s", 1251), (p[0].File, p[0].Line!.Value));
        Assert.True(p[0].IsError);
        Assert.Equal("res/field/maps/jubilife_city/events.json:5", p[1].Where);
        Assert.False(p[2].IsError);
        Assert.Equal("src/overworld.c", p[2].File);
        Assert.Equal("data/maps/LittlerootTown/scripts.inc", p[3].File);

        // Windows path of a WSL project: the build prints the Linux path.
        Assert.Equal("res/a.json", BuildProblems.Rel("/home/u/pokeplatinum/res/a.json", @"\\wsl.localhost\Ubuntu\home\u\pokeplatinum"));
    }

    [Fact]
    public void LinkerErrorsNameTheMissingSymbol()
    {
        var p = BuildProblems.Parse("""
            ld: build/modern/src/foo.o: in function `DoThing':
            foo.c:(.text+0x12): undefined reference to `Missing_Func'
            # Link Error: Undefined : "ov5_Missing"
            # Referenced from "ov5_Caller" in ov5.o
            """);
        Assert.Equal(2, p.Count);
        Assert.Contains("Missing_Func", p[0].Message);
        Assert.Contains("DoThing", p[0].Message);
        Assert.Contains("ov5_Missing", p[1].Message);
    }
}
