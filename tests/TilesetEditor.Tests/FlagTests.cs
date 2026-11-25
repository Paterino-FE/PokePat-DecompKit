using TilesetEditor.Core.Events;

namespace TilesetEditor.Tests;

public class FlagTests
{
    [Fact]
    public void ClaimsAFreeSlotKeepingValueAndAlignment()
    {
        var text = "#define FLAG_A               0x1F\r\n#define FLAG_UNUSED_0x020    0x20 // Unused Flag\r\n#define FLAG_B    0x21\r\n";
        var r = FlagEditor.Rewrite(text, "FLAG_UNUSED_0x020", "FLAG_RIVAL_DEFEATED", "Rival beaten on Route 103");
        Assert.Equal("#define FLAG_A               0x1F\r\n#define FLAG_RIVAL_DEFEATED  0x20 // Rival beaten on Route 103\r\n#define FLAG_B    0x21\r\n", r);
        // Comment only
        Assert.Equal("#define FLAG_B    0x21 // x\r\n", FlagEditor.Rewrite("#define FLAG_B    0x21\r\n", "FLAG_B", "FLAG_B", "x"));
        Assert.Throws<InvalidOperationException>(() => FlagEditor.Rewrite(text, "FLAG_UNUSED_0x020", "FLAG_A", null));
    }

    [Theory]
    [InlineData("Rival besiegt", "FLAG_RIVAL_BESIEGT")]
    [InlineData("FLAG_my flag", "FLAG_MY_FLAG")]
    [InlineData("metGuardTwice", "FLAG_MET_GUARD_TWICE")]
    public void BuildsNames(string input, string expected) => Assert.Equal(expected, FlagEditor.ToName(input, FlagKind.Flag));

    [Fact]
    public void ReadsDefinitionsUsagesAndVisibilityFlags()
    {
        var cat = FlagCatalog.Load(TestPaths.ReferenceProject);
        var temp = Assert.Single(cat.Entries, e => e.Name == "FLAG_TEMP_1");
        Assert.Equal((1L, "Temporary (per map)"), (temp.Value!.Value, temp.Category));
        var free = cat.Entries.First(e => e.Name.StartsWith("FLAG_UNUSED_", StringComparison.Ordinal));
        Assert.Equal(0x863, cat.Entries.Single(e => e.Name == "FLAG_UNUSED_0x863").Value); // needs MAX_TRAINERS_COUNT from opponents.h
        Assert.True(free.IsClaimable);
        Assert.Contains(cat.Entries, e => e.Name == "VAR_TEMP_0" && e.Value == 0x4000 && e.Kind == FlagKind.Var);
        Assert.DoesNotContain(cat.Entries, e => e.Name == "TEMP_FLAGS_START" || e.Name.EndsWith("_COUNT", StringComparison.Ordinal));

        // A story flag is set somewhere and checked somewhere; hide flags are used as visibility flags in map.json.
        var badge = cat.UsagesOf("FLAG_BADGE01_GET");
        Assert.Contains(badge, u => u.Kind == FlagUsageKind.Sets);
        Assert.Contains(badge, u => u.Kind == FlagUsageKind.Checks);
        Assert.Contains(cat.Usages.Values.SelectMany(l => l), u => u.Kind == FlagUsageKind.Visibility && u.Map.Length > 0);
        Assert.Contains(cat.Objects, o => o.Flag.StartsWith("FLAG_HIDE_", StringComparison.Ordinal));
    }

    [Fact]
    public void AddsMoreSavedFlagsAndVariables()
    {
        var flags = File.ReadAllText(Path.Combine(TestPaths.ReferenceProject, "include", "constants", "flags.h"));
        var vars = File.ReadAllText(Path.Combine(TestPaths.ReferenceProject, "include", "constants", "vars.h"));
        var opp = File.ReadAllText(Path.Combine(TestPaths.ReferenceProject, "include", "constants", "opponents.h"));
        var before = FlagCatalog.ParseDefinitions(flags, vars, opp);
        long oldCount = EvalCount(flags);

        var (f1, n1) = FlagEditor.AddFlags(flags, 64, opp);
        Assert.Equal(64, n1.Count);
        Assert.Equal(oldCount + 64, EvalCount(f1));
        var (f2, n2) = FlagEditor.AddFlags(f1, 64, opp); // second block extends the first
        Assert.Equal(oldCount + 128, EvalCount(f2));
        Assert.Equal(n1[^1], $"FLAG_UNUSED_0x{oldCount + 63:X3}");
        Assert.Equal(n2[0], $"FLAG_UNUSED_0x{oldCount + 64:X3}");
        var (v1, vn) = FlagEditor.AddVars(vars);
        Assert.Equal(16, vn.Count);
        Assert.Contains("#define VARS_END                                         0x410F", v1);

        var after = FlagCatalog.ParseDefinitions(f2, v1, opp);
        Assert.Equal(before.Count + 128 + 16, after.Count);
        var newFlag = after.Single(e => e.Name == n2[^1]);
        Assert.Equal(oldCount + 127, newFlag.Value);
        Assert.True(newFlag.IsClaimable);
        Assert.True(after.Single(e => e.Name == vn[0]).IsClaimable);
        // Claiming works on the new entries like on any free one.
        Assert.Contains("#define FLAG_TK_NEW", FlagEditor.Rewrite(f2, n1[0], "FLAG_TK_NEW", null));
        // The original text before the block is unchanged.
        int at = flags.IndexOf("#define FLAGS_COUNT", StringComparison.Ordinal);
        Assert.Equal(flags[..at], f2[..at]);
    }

    private static long EvalCount(string flagsText)
    {
        var d = new TilesetEditor.Core.Project.CDefines();
        d.AddFromText(File.ReadAllText(Path.Combine(TestPaths.ReferenceProject, "include", "constants", "opponents.h")));
        d.AddFromText(flagsText);
        return d.TryEvaluate("FLAGS_COUNT")!.Value;
    }

    [Fact]
    public void ClassifiesCommands()
    {
        Assert.Equal(FlagUsageKind.Sets, FlagCatalog.Classify("\tsetflag FLAG_X", "FLAG_X"));
        Assert.Equal(FlagUsageKind.Clears, FlagCatalog.Classify("clearflag(FLAG_X)", "FLAG_X"));
        Assert.Equal(FlagUsageKind.Checks, FlagCatalog.Classify("\tgoto_if_set FLAG_X, Label", "FLAG_X"));
        Assert.Equal(FlagUsageKind.Checks, FlagCatalog.Classify("if (flag(FLAG_X)) {", "FLAG_X"));
        Assert.Equal(FlagUsageKind.Sets, FlagCatalog.Classify("\tsetvar VAR_Y, 2", "VAR_Y"));
        Assert.Equal(FlagUsageKind.Other, FlagCatalog.Classify("\tsetvar VAR_RESULT, VAR_Y", "VAR_Y"));
        Assert.Equal(FlagUsageKind.Checks, FlagCatalog.Classify("    if (FlagGet(FLAG_X))", "FLAG_X"));
    }

    [Fact]
    public void ChangesTheVisibilityFlagOfOnePerson()
    {
        var json = "{\n  \"object_events\": [\n    {\n      \"graphics_id\": \"OBJ_EVENT_GFX_BOY_1\",\n      \"flag\": \"0\",\n      \"script\": \"A\"\n    }\n  ]\n}\n";
        var r = MapEventScripts.SetStringField(json, MapEventKind.Object, 0, "flag", "FLAG_TEMP_1");
        Assert.Equal(json.Replace("\"flag\": \"0\"", "\"flag\": \"FLAG_TEMP_1\""), r);
    }
}
