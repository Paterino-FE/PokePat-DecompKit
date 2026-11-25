using System.Diagnostics;
using System.Text;
using TilesetEditor.Core.Project;
using TilesetEditor.Toolkit;

namespace TilesetEditor.Tests;

public class MapCatalogTests
{
    [Fact]
    public void LoadsMapsWithLayoutsTilesetsAndLinks()
    {
        var project = ProjectLoader.Load(TestPaths.ReferenceProject);
        var catalog = MapCatalog.Load(project);
        Assert.True(catalog.Maps.Count > 500, $"nur {catalog.Maps.Count} Maps");
        Assert.Empty(catalog.Problems);

        var r101 = catalog.Find("Route101")!;
        Assert.Equal("MAP_ROUTE101", r101.Id);
        Assert.Equal("gMapGroup_TownsAndRoutes", r101.Group);
        Assert.Equal("LAYOUT_ROUTE101", r101.LayoutId);
        Assert.Equal((20, 20), (r101.Layout!.Width, r101.Layout.Height));
        Assert.Equal("gTileset_General", r101.PrimaryTileset);
        Assert.Equal("gTileset_Petalburg", r101.SecondaryTileset);
        Assert.Equal(2, r101.Connections.Count);
        Assert.Contains("data/maps/Route101/scripts.inc", r101.ScriptFiles);
        Assert.Contains("Route101_EventScript_Youngster", r101.EventScripts);
        Assert.Same(r101, catalog.Find("MAP_ROUTE101"));
        Assert.Contains(catalog.UsingTileset("gTileset_Petalburg"), m => m.Name == "PetalburgCity");
    }

    [Fact]
    public void DetectsPoryscriptFiles()
    {
        var catalog = MapCatalog.Load(ProjectLoader.Load(TestPaths.ReferenceProject));
        Assert.Contains("data/maps/LittlerootTown/scripts.pory", catalog.Find("LittlerootTown")!.ScriptFiles);
    }
}

public class ConfigCatalogTests
{
    [Fact]
    public void ReadsBranchConfiguration()
    {
        var options = ConfigCatalog.Load(TestPaths.ReferenceProject);
        var signpost = Assert.Single(options, o => o.Name == "OW_AUTO_SIGNPOST");
        Assert.Equal("FALSE", signpost.Value);
        Assert.Equal(ConfigValueKind.Switch, signpost.Kind);
        Assert.True(signpost.IsToggle);
        Assert.Equal("include/config/overworld.h", signpost.File);
        Assert.Contains("MB_SIGNPOST", signpost.Description);
        Assert.Equal("Movement config", signpost.Section);

        Assert.Equal(ConfigValueKind.Generation, options.First(o => o.Name == "OW_RUNNING_INDOORS").Kind);
        Assert.Equal(25, options.First(o => o.Name == "OW_BERRY_MUTATION_CHANCE").Resolved);
        Assert.DoesNotContain(options, o => o.Name.StartsWith("GUARD_"));
    }

    [Fact]
    public void MarksConditionalAndFunctionLikeDefines()
    {
        var text = """
            #ifndef GUARD_CONFIG_X_H
            #define GUARD_CONFIG_X_H

            // Section A
            #define A_SWITCH TRUE // plain switch
            #if A_SWITCH
            #define B_INNER FALSE
            #endif
            // comment above
            #define C_MACRO(x) ((x) + 1)
            #define D_SUM (A_NUM + 2)
            #define A_NUM 40
            #endif
            """;
        var o = ConfigCatalog.Parse([("include/config/x.h", text)]);
        var a = o.Single(x => x.Name == "A_SWITCH");
        Assert.True(a.IsToggle);
        Assert.False(a.Conditional);
        Assert.Equal("plain switch", a.Description);
        Assert.Equal("Section A", a.Section);
        var b = o.Single(x => x.Name == "B_INNER");
        Assert.True(b.Conditional);
        Assert.False(b.IsToggle); // no toggle for conditionally defined options
        var c = o.Single(x => x.Name == "C_MACRO");
        Assert.Equal(ConfigValueKind.Expression, c.Kind);
        Assert.Equal("comment above", c.Description);
        Assert.Equal(42, o.Single(x => x.Name == "D_SUM").Resolved);
    }
}

public class ConfigEditorTests
{
    [Fact]
    public void ReplacesOnlyTheValueAndKeepsCommentColumn()
    {
        var text = "#define A_SWITCH            FALSE       // comment\r\n#define B_NUM 25 // x\r\n#define C_GEN GEN_LATEST // gen\r\n";
        var result = ConfigEditor.Apply(text, [new ConfigChange("A_SWITCH", 1, "FALSE", "TRUE"), new ConfigChange("B_NUM", 2, "25", "100"), new ConfigChange("C_GEN", 3, "GEN_LATEST", "GEN_3")]);
        Assert.Equal("#define A_SWITCH            TRUE        // comment\r\n#define B_NUM 100 // x\r\n#define C_GEN GEN_3      // gen\r\n", result);
    }

    [Fact]
    public void ReportsConflictWhenTheFileChanged()
    {
        var text = "#define A TRUE // x\n";
        Assert.Throws<ConfigConflictException>(() => ConfigEditor.Apply(text, [new ConfigChange("A", 1, "FALSE", "TRUE")]));
        Assert.Throws<ConfigConflictException>(() => ConfigEditor.Apply(text, [new ConfigChange("A", 5, "TRUE", "FALSE")]));
        Assert.Throws<ConfigConflictException>(() => ConfigEditor.Apply(text, [new ConfigChange("A", 1, "TRUE", "1; rm -rf")]));
    }

    [Fact]
    public void WorksOnTheBranchConfig()
    {
        var file = Path.Combine(TestPaths.ReferenceProject, "include", "config", "overworld.h");
        var opt = ConfigCatalog.Load(TestPaths.ReferenceProject).Single(o => o.Name == "OW_AUTO_SIGNPOST");
        var changed = ConfigEditor.Apply(File.ReadAllText(file), [new ConfigChange(opt.Name, opt.Line, opt.Value, "TRUE")]);
        var reparsed = ConfigCatalog.Parse([("include/config/overworld.h", changed)]).Single(o => o.Name == "OW_AUTO_SIGNPOST");
        Assert.Equal("TRUE", reparsed.Value);
        Assert.Equal(opt.Description, reparsed.Description);
        Assert.Equal(File.ReadAllText(file).Split('\n').Length, changed.Split('\n').Length);
    }
}

public class PorymapBridgeTests
{
    private const string Script = @"C:\Users\someone\AppData\Local\TilesetToolkit\tileset_toolkit_bridge.js";

    [Fact]
    public void Install_AddsEntryAndKeepsOtherSettings()
    {
        var root = TestPaths.NewTempDir("bridge");
        var cfg = Path.Combine(root, "porymap.user.cfg");
        File.WriteAllText(cfg, "recent_map_or_layout=Route101\r\ncustom_scripts=other.js:1,disabled.js:0\r\nuse_encounter_json=1\r\n");

        var plan = PorymapBridgeInstaller.PlanInstall(root, Script);
        Assert.Equal(
            $"recent_map_or_layout=Route101\r\ncustom_scripts=other.js:1,disabled.js:0,{Script}:1\r\nuse_encounter_json=1\r\n",
            plan.UserConfigAfter);
        // Apply writes the script too (to a temp location here).
        var scriptPath = Path.Combine(root, "bridge.js");
        PorymapBridgeInstaller.Apply(PorymapBridgeInstaller.PlanInstall(root, scriptPath));
        Assert.True(PorymapBridgeInstaller.IsInstalled(root, scriptPath));
        Assert.True(PorymapBridgeInstaller.ScriptIsCurrent(scriptPath));
        Assert.True(File.Exists(cfg + ".toolkit-backup"));
        Assert.True(PorymapBridgeInstaller.PlanInstall(root, scriptPath).NothingToDo); // idempotent

        PorymapBridgeInstaller.Apply(PorymapBridgeInstaller.PlanUninstall(root, scriptPath));
        Assert.False(PorymapBridgeInstaller.IsInstalled(root, scriptPath));
        Assert.Contains("custom_scripts=other.js:1,disabled.js:0\r\n", File.ReadAllText(cfg));
    }

    [Fact]
    public void Install_CreatesConfigWhenMissing()
    {
        var root = TestPaths.NewTempDir("bridge");
        var plan = PorymapBridgeInstaller.PlanInstall(root, Script);
        Assert.Null(plan.UserConfigBefore);
        Assert.Equal($"custom_scripts={Script}:1\n", plan.UserConfigAfter);
    }

    [Fact]
    public void ParseScripts_FollowsPorymapRules()
    {
        Assert.Equal([("a.js", true), ("b.js", false), ("c.js", true)], PorymapBridgeInstaller.ParseScripts("a.js,b.js:0,c.js:1"));
    }

    [Fact]
    public void Message_ParsesEncodedFields()
    {
        var m = PorymapBridgeMessage.TryParse(
            "2026-11-25 10:00:00  [INFO] [TilesetToolkit] v=1;project=C%3A%2Fmy%20projects%2Freyamon;event=map;map=Route101;primary=gTileset_General;secondary=")!;
        Assert.Equal("map", m.Event);
        Assert.Equal("C:/my projects/reyamon", m.Project);
        Assert.Equal("Route101", m.Map);
        Assert.Null(m.Secondary);
        Assert.Null(PorymapBridgeMessage.TryParse("2026-11-25 10:00:00  [INFO] something else"));
    }

    [Fact]
    public void LogWatcher_ReadsOnlyNewCompleteLinesAndSurvivesTruncation()
    {
        var log = Path.Combine(TestPaths.NewTempDir("log"), "porymap.log");
        File.WriteAllText(log, "2026 [INFO] [TilesetToolkit] event=map;map=OldSession\n");
        using var w = new PorymapLogWatcher(log, TimeSpan.FromHours(1), startAtEnd: true);
        Assert.Empty(w.Poll());

        File.AppendAllText(log, "2026 [INFO] Loaded project\n2026 [INFO] [TilesetToolkit] event=map;map=Route101\n2026 [INFO] [TilesetToolkit] event=layout;la");
        var got = w.Poll();
        Assert.Equal("Route101", Assert.Single(got).Map);

        File.AppendAllText(log, "yout=LAYOUT_ROUTE101\n");
        Assert.Equal("LAYOUT_ROUTE101", Assert.Single(w.Poll()).Layout);

        File.WriteAllText(log, "2026 [INFO] [TilesetToolkit] event=project\n"); // truncated / rotated
        Assert.Equal("project", Assert.Single(w.Poll()).Event);
    }

    /// <summary>
    /// Runs the real bridge script with stand-ins for Porymap's "utility" and "map" objects in the Windows script
    /// host (JScript). Porymap's engine supports modules, JScript does not, so only the "export" keyword is removed.
    /// </summary>
    [Fact]
    public void BridgeScript_RunsAndProducesParsableMessages()
    {
        var js = new StringBuilder();
        js.AppendLine("""
            var logged = [];
            var actions = [];
            var utility = {
                log: function (m) { logged.push(m); },
                registerAction: function (fn, name) { actions.push(fn + "|" + name); return true; },
                showMessage: function (a, b) { }
            };
            var map = {
                getPrimaryTileset: function () { return "gTileset_General"; },
                getSecondaryTileset: function () { return "gTileset_TestSingle"; }
            };
            """);
        js.AppendLine(PorymapBridgeScript.Content.Replace("export function", "function"));
        js.AppendLine("""
            onProjectOpened("C:/Users/me/my projects/reyamon;x");
            onLayoutOpened("LAYOUT_ROUTE101");
            onMapOpened("Route101");
            onTilesetUpdated("gTileset_TestSingle");
            toolkitShowMap();
            toolkitNewTileset();
            onProjectClosed("C:/Users/me/my projects/reyamon;x");
            for (var i = 0; i < actions.length; i++) WScript.Echo("ACTION " + actions[i]);
            for (var j = 0; j < logged.length; j++) WScript.Echo("LOG " + logged[j]);
            """);
        var file = Path.Combine(TestPaths.NewTempDir("js"), "bridge_test.js");
        File.WriteAllText(file, js.ToString(), Encoding.Unicode); // cscript reads UTF-16 with BOM for non-ASCII text

        var psi = new ProcessStartInfo("cscript.exe") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        psi.ArgumentList.Add("//nologo");
        psi.ArgumentList.Add("//E:jscript");
        psi.ArgumentList.Add(file);
        using var p = Process.Start(psi)!;
        var output = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
        p.WaitForExit();
        Assert.True(p.ExitCode == 0, output);

        var lines = output.Split('\n').Select(l => l.TrimEnd('\r')).ToList();
        Assert.Contains("ACTION toolkitShowMap|PokePat DecompKit: Show map in toolkit", lines);
        var messages = lines.Where(l => l.StartsWith("LOG ")).Select(l => PorymapBridgeMessage.TryParse(l[4..])).ToList();
        Assert.All(messages, Assert.NotNull);
        Assert.Equal(["project", "layout", "map", "tileset", "action", "action", "closed"], messages.Select(m => m!.Event));
        var map = messages[2]!;
        Assert.Equal("C:/Users/me/my projects/reyamon;x", map.Project); // ';' survives thanks to encoding
        Assert.Equal(("Route101", "LAYOUT_ROUTE101", "gTileset_General", "gTileset_TestSingle"), (map.Map, map.Layout, map.Primary, map.Secondary));
        Assert.Equal("show-map", messages[4]!.Action);
        Assert.Equal("new-tileset", messages[5]!.Action);
        Assert.Equal("gTileset_General", messages[5]!.Primary);
    }
}
