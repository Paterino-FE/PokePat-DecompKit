using System.Text;
using TilesetEditor.Core.Events;
using TilesetEditor.Core.Imaging;
using TilesetEditor.Core.Text;
using TilesetEditor.Export;
using TilesetEditor.Toolkit;

namespace TilesetEditor.Tests;

internal static class EventFixtures
{
    public static string Root => TestPaths.ReferenceProject;
    public static GbaFont Font => GbaFont.LoadFromProject(Root);
    public static Charmap Charmap => Charmap.Load(Path.Combine(Root, "charmap.txt"));

    public static ToolkitEvent OldMan() => new()
    {
        Label = "Route101_TK_OldMan",
        Map = "Route101",
        Title = "Alter Mann",
        Trigger = EventTrigger.Person,
        Steps =
        [
            new MessageStep { Text = "Hallo {PLAYER}! Schön, dich hier auf der Route zu sehen, das freut mich wirklich sehr.\n\nWillst du etwas?" },
            new YesNoStep
            {
                Question = "Möchtest du einen \"Trank\"?",
                Yes =
                [
                    new IfFlagStep
                    {
                        Flag = "FLAG_UNUSED_0x020",
                        Then = [new MessageStep { Text = "Du hast schon einen." }],
                        Else = [new GiveItemStep { Item = "ITEM_POTION", Count = 1 }, new SetFlagStep { Flag = "FLAG_UNUSED_0x020" }],
                    },
                ],
                No = [new MessageStep { Text = "Schade!" }],
            },
        ],
    };

    public static string CopyEventProject()
    {
        var src = Root;
        var dst = TestPaths.NewTempDir("events");
        foreach (var rel in new[] { "data/event_scripts.s", "data/maps/Route101/map.json", "data/maps/Route101/scripts.inc", "charmap.txt" })
        {
            var t = Path.Combine(dst, rel.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(t)!);
            File.Copy(Path.Combine(src, rel.Replace('/', Path.DirectorySeparatorChar)), t);
        }
        Directory.CreateDirectory(Path.Combine(dst, "data", "scripts"));
        return dst;
    }
}

public class TextLayoutTests
{
    [Fact]
    public void WrapsByFontWidthAndUsesGameControlCodes()
    {
        var f = TextLayout.Format("Hallo {PLAYER}! Schön, dich hier auf der Route zu sehen, das freut mich wirklich sehr.\n\nWillst du etwas?",
            EventFixtures.Font, EventFixtures.Charmap);
        Assert.False(f.HasErrors, string.Join("; ", f.Problems.Select(p => p.Message)));
        Assert.Equal(2, f.Boxes.Count);
        Assert.True(f.Boxes[0].Count >= 3); // wraps more than twice -> scrolling
        Assert.Matches(@"^[^\\]+\\n[^\\]+\\l[^\\]+(\\l[^\\]+)*\\pWillst du etwas\?$", f.Encoded);
        var font = EventFixtures.Font;
        Assert.All(f.Boxes.SelectMany(b => b), line => Assert.True(font.Width(TextLayout.Units(line)) <= font.MaxLineLength, line));
    }

    [Fact]
    public void ReportsUnknownCharactersAndForbiddenOnes()
    {
        var f = TextLayout.Format("Preis: 5$ und ✓ \\", EventFixtures.Font, EventFixtures.Charmap);
        Assert.True(f.HasErrors);
        Assert.Contains(f.Problems, p => p.Message.Contains("$"));
        Assert.Contains(f.Problems, p => p.Message.Contains("✓"));
        Assert.Contains(f.Problems, p => p.Message.Contains("Backslash"));
    }

    [Fact]
    public void KnowsTheEscapedApostrophe()
    {
        var f = TextLayout.Format("Let's battle! It's fun.", EventFixtures.Font, EventFixtures.Charmap);
        Assert.False(f.HasErrors, string.Join("; ", f.Problems.Select(p => p.Message)));
        Assert.Equal(0xB4, EventFixtures.Charmap.GlyphOf("'"));
    }

    [Fact]
    public void ConvertsStraightQuotes()
    {
        var f = TextLayout.Format("Sag \"Hallo\"!", EventFixtures.Font, EventFixtures.Charmap);
        Assert.Equal("Sag “Hallo”!", f.Encoded);
    }
}

public class GeneratorTests
{
    [Fact]
    public void GeneratesPoryscriptWithBranchesAndEnd()
    {
        var doc = new ToolkitEventDocument { Events = [EventFixtures.OldMan()] };
        var code = PoryscriptGenerator.Generate(doc, EventFixtures.Font, EventFixtures.Charmap);
        Assert.Contains("script Route101_TK_OldMan {\n    lock\n    faceplayer\n", code);
        Assert.Contains("msgbox(\"Möchtest du einen “Trank”?\", MSGBOX_YESNO)", code);
        Assert.Contains("    if (var(VAR_RESULT) == YES) {\n        if (flag(FLAG_UNUSED_0x020)) {", code);
        Assert.Contains("giveitem(ITEM_POTION, 1)", code);
        Assert.Contains("    closemessage\n    release\n    end\n}", code);
        Assert.True(PoryscriptGenerator.IsUnmodified(code));
        Assert.False(PoryscriptGenerator.IsUnmodified(code.Replace("Schade!", "Schade, von Hand!")));
    }

    [Fact]
    public void ValidatesFlagsItemsAndLabels()
    {
        var symbols = ProjectSymbols.Load(EventFixtures.Root);
        Assert.Contains(symbols.Flags, f => f.Name == "FLAG_UNUSED_0x020" && f.IsUnused);
        Assert.Contains(symbols.Items, i => i.Name == "ITEM_POTION" && i.DisplayName == "POTION");

        var bad = EventFixtures.OldMan();
        bad.Steps.Add(new SetFlagStep { Flag = "FLAG_DOES_NOT_EXIST" });
        bad.Steps.Add(new GiveItemStep { Item = "ITEM_NOPE", Count = 0 });
        var dup = EventFixtures.OldMan();
        var problems = PoryscriptGenerator.Validate(new ToolkitEventDocument { Events = [bad, dup] }, EventFixtures.Font, EventFixtures.Charmap, symbols,
            new HashSet<string> { "Route101_EventScript_Youngster" });
        Assert.Contains(problems, p => p.IsError && p.Message.Contains("FLAG_DOES_NOT_EXIST"));
        Assert.Contains(problems, p => p.IsError && p.Message.Contains("ITEM_NOPE"));
        Assert.Contains(problems, p => p.IsError && p.Message.Contains("more than once"));

        Assert.Equal("Route101_TK_AlterMann2", PoryscriptGenerator.SuggestLabel("Route101", "Alter Mann!", ["Route101_TK_AlterMann"]));
        Assert.Equal("Route101_TK_GrosseTuer", PoryscriptGenerator.SuggestLabel("Route101", "Große Tür", []));
    }

    [Fact]
    public void DocumentRoundTripsThroughJson()
    {
        var doc = new ToolkitEventDocument { Events = [EventFixtures.OldMan()] };
        var json = doc.Serialize();
        Assert.Contains("\"type\": \"yesno\"", json);
        Assert.Contains("Schön", json); // not \u00f6-escaped
        Assert.DoesNotContain("\"summary\"", json);
        Assert.DoesNotContain("\"title\": \"Message\"", json);
        Assert.Equal(json, ToolkitEventDocument.Parse(json).Serialize());
    }
}

public class DialogSessionTests
{
    private static DialogSession Start(ISet<string> flags) =>
        new(EventFixtures.OldMan(), EventFixtures.Font, EventFixtures.Charmap, flags) { TextSpeed = 1, PlayerName = "PAT" };

    private static void TypeUntilStop(DialogSession s)
    {
        for (int i = 0; i < 5000 && s.State == DialogState.Typing; i++) s.Tick();
    }

    [Fact]
    public void PlaysTypewriterScrollPagesAndYesBranchWithItem()
    {
        var flags = new HashSet<string>();
        var s = Start(flags);
        s.Tick();                                           // start
        Assert.Equal(DialogState.Typing, s.State);
        s.Tick();
        Assert.Equal("H", s.Lines[0]);                      // one character per frame at speed 1
        TypeUntilStop(s);
        Assert.Equal(DialogState.WaitingForA, s.State);     // first \l: box full
        Assert.True(s.Lines[0].StartsWith("Hallo PAT!"), $"Line 0: [{s.Lines[0]}] Line 1: [{s.Lines[1]}]");
        var secondLine = s.Lines[1];
        s.PressA();                                         // scroll
        Assert.Equal(secondLine, s.Lines[0]);
        s.PressA();                                         // A while typing: complete to next stop
        while (s.State == DialogState.WaitingForA && !s.Lines.Any(l => l.StartsWith("Willst"))) s.PressA();
        TypeUntilStop(s);
        Assert.Equal("Willst du etwas?", s.Lines[0]);      // new box after \p
        s.PressA();
        TypeUntilStop(s);
        Assert.Equal(DialogState.Choosing, s.State);
        Assert.Contains("“Trank”", s.Lines[0] + s.Lines[1]);
        s.Choose(true);
        TypeUntilStop(s);
        Assert.Equal("Obtained the POTION!", s.Lines[0].Replace("ITEM_", ""));
        s.PressA();
        Assert.Equal(DialogState.Finished, s.State);
        Assert.Contains("FLAG_UNUSED_0x020", flags);
        Assert.Contains("Answer: YES", s.Log);
    }

    [Fact]
    public void PressingADuringTypingStopsAtEndOfTextAndWaits()
    {
        var ev = new ToolkitEvent { Label = "X", Steps = [new MessageStep { Text = "Hallo!" }, new GiveItemStep { Item = "ITEM_POTION" }] };
        var s = new DialogSession(ev, EventFixtures.Font, EventFixtures.Charmap, new HashSet<string>()) { TextSpeed = 4 };
        s.Tick();                       // start
        TypeUntilStop(s);
        Assert.Equal(DialogState.WaitingForA, s.State);
        s.PressA();                     // continue -> item text starts typing
        Assert.Equal(DialogState.Typing, s.State);
        s.PressA();                     // skip typing
        Assert.Equal(DialogState.WaitingForA, s.State);
        Assert.StartsWith("Obtained the", s.Lines[0]);
        for (int i = 0; i < 300; i++) s.Tick(); // waiting stays waiting
        Assert.Equal(DialogState.WaitingForA, s.State);
        s.PressA();
        Assert.Equal(DialogState.Finished, s.State);
    }

    [Fact]
    public void FlagSetTakesThenBranch()
    {
        var s = Start(new HashSet<string> { "FLAG_UNUSED_0x020" });
        for (int i = 0; i < 20 && s.State != DialogState.Choosing; i++) { TypeUntilStop(s); if (s.State == DialogState.WaitingForA) s.PressA(); }
        s.Choose(true);
        TypeUntilStop(s);
        Assert.Equal("Du hast schon einen.", s.Lines[0]);
    }
}

public class DialogRendererTests
{
    [Fact]
    public void RendersProjectFrameAndText()
    {
        var root = EventFixtures.Root;
        var r = new DialogRenderer(DialogAssets.Load(root), EventFixtures.Font, EventFixtures.Charmap);
        var empty = r.Render(new DialogScene { Lines = ["", ""] });
        var text = r.Render(new DialogScene { Lines = ["Hallo!", ""], YesNoCursor = 0 });
        Assert.Equal((240, 160), (text.Width, text.Height));
        // Frame drawn around the text window (tile row 14 = y 112..119), background untouched above.
        Assert.NotEqual(empty[0, 0], empty[4, 116]);
        // Text pixels appear in the first line of the window (x 16.., y 121..136) in the text color.
        var fg = DialogAssets.Load(root).TextForeground;
        int textPixels = 0;
        for (int y = 121; y < 137; y++) for (int x = 16; x < 60; x++) if (text[x, y] == fg) textPixels++;
        Assert.True(textPixels > 20, $"nur {textPixels} Textpixel");
        // YES/NO window at tile (21,9)
        Assert.NotEqual(empty[21 * 8 + 4, 9 * 8 + 4], text[21 * 8 + 4, 9 * 8 + 4]);
    }
}

public class MapAssignmentAndPlannerTests
{
    [Fact]
    public void AssignChangesOnlyTheScriptValue()
    {
        var json = File.ReadAllText(Path.Combine(EventFixtures.Root, "data", "maps", "Route101", "map.json"));
        var slots = MapEventScripts.Read(json);
        var youngster = Assert.Single(slots, s => s.Kind == MapEventKind.Object && s.Script == "Route101_EventScript_Youngster");
        var changed = MapEventScripts.AssignScript(json, MapEventKind.Object, youngster.Index, "Route101_TK_OldMan");
        Assert.Equal(json.Replace("\"Route101_EventScript_Youngster\"", "\"Route101_TK_OldMan\""), changed);
        Assert.Equal("Route101_TK_OldMan", MapEventScripts.Read(changed).Single(s => s.Kind == MapEventKind.Object && s.Index == youngster.Index).Script);
        Assert.Throws<InvalidOperationException>(() => MapEventScripts.AssignScript(json, MapEventKind.Object, 99, "X"));
    }

    [Fact]
    public void AddObjectEventInsertsLikePorymapAndKeepsEverythingElse()
    {
        var json = File.ReadAllText(Path.Combine(EventFixtures.Root, "data", "maps", "Route101", "map.json"));
        int before = MapEventScripts.Read(json).Count(s => s.Kind == MapEventKind.Object);
        var result = MapEventScripts.AddObjectEvent(json, new Dictionary<string, object>
        {
            ["graphics_id"] = "OBJ_EVENT_GFX_LASS", ["x"] = 7, ["y"] = 4, ["movement_type"] = "MOVEMENT_TYPE_FACE_DOWN", ["script"] = "Route101_TK_Event",
        });
        var slots = MapEventScripts.Read(result).Where(s => s.Kind == MapEventKind.Object).ToList();
        Assert.Equal(before + 1, slots.Count);
        Assert.Equal((7, 4, "LASS", "Route101_TK_Event"), (slots[^1].X, slots[^1].Y, slots[^1].What, slots[^1].Script));
        // Only an insertion: removing the new block gives back the original file.
        int at = result.IndexOf("\"graphics_id\": \"OBJ_EVENT_GFX_LASS\"", StringComparison.Ordinal);
        int start = result.LastIndexOf(",", at, StringComparison.Ordinal);
        int end = result.IndexOf("}", at, StringComparison.Ordinal) + 1;
        Assert.Equal(json, result.Remove(start, end - start));
        // Same key order and indentation as the existing entries
        Assert.Contains("\n    {\n      \"graphics_id\": \"OBJ_EVENT_GFX_LASS\",\n      \"x\": 7,\n      \"y\": 4,\n      \"elevation\": 3,", result.Replace("\r\n", "\n"));
    }

    [Fact]
    public void AddsSignsAndTriggersWithTheirOwnFieldOrder()
    {
        var json = File.ReadAllText(Path.Combine(EventFixtures.Root, "data", "maps", "Route101", "map.json"));
        var withSign = MapEventScripts.AddSign(json, new Dictionary<string, object> { ["x"] = 3, ["y"] = 3, ["script"] = "Route101_TK_Event" });
        var sign = MapEventScripts.Read(withSign).Where(s => s.Kind == MapEventKind.Bg).Last();
        Assert.Equal((3, 3, "sign", "Route101_TK_Event"), (sign.X, sign.Y, sign.What, sign.Script));
        Assert.Contains("\"type\": \"sign\",\n      \"x\": 3,\n      \"y\": 3,\n      \"elevation\": 0,\n      \"player_facing_dir\": \"BG_EVENT_PLAYER_FACING_ANY\",\n      \"script\": \"Route101_TK_Event\"",
            withSign.Replace("\r\n", "\n"));
        var withTrigger = MapEventScripts.AddTrigger(withSign, new Dictionary<string, object> { ["x"] = 4, ["y"] = 4, ["var"] = "VAR_TEMP_1", ["var_value"] = "0", ["script"] = "Route101_TK_Event" });
        Assert.Equal(MapEventScripts.Read(json).Count(s => s.Kind == MapEventKind.Coord) + 1, MapEventScripts.Read(withTrigger).Count(s => s.Kind == MapEventKind.Coord));
        Assert.Contains("\"type\": \"trigger\",\n      \"x\": 4,\n      \"y\": 4,\n      \"elevation\": 3,\n      \"var\": \"VAR_TEMP_1\",\n      \"var_value\": \"0\",", withTrigger.Replace("\r\n", "\n"));
    }

    [Fact]
    public void AddObjectEventOpensAnEmptyArray()
    {
        var json = "{\n  \"id\": \"MAP_X\",\n  \"object_events\": [],\n  \"warp_events\": []\n}\n";
        var result = MapEventScripts.AddObjectEvent(json, new Dictionary<string, object> { ["x"] = 1, ["y"] = 2 });
        Assert.Equal("{\n  \"id\": \"MAP_X\",\n  \"object_events\": [\n    {\n      \"graphics_id\": \"OBJ_EVENT_GFX_BOY_1\",\n      \"x\": 1,\n      \"y\": 2,\n" +
                     "      \"elevation\": 3,\n      \"movement_type\": \"MOVEMENT_TYPE_FACE_DOWN\",\n      \"movement_range_x\": 0,\n      \"movement_range_y\": 0,\n" +
                     "      \"trainer_type\": \"TRAINER_TYPE_NONE\",\n      \"trainer_sight_or_berry_tree_id\": \"0\",\n      \"script\": \"0x0\",\n      \"flag\": \"0\"\n    }\n  ],\n" +
                     "  \"warp_events\": []\n}\n", result);
    }

    [Fact]
    public void PlanWritesManagedFilesAndIncludeOnce()
    {
        var root = EventFixtures.CopyEventProject();
        var doc = new ToolkitEventDocument { Events = [EventFixtures.OldMan()] };
        var pory = PoryscriptGenerator.Generate(doc, EventFixtures.Font, EventFixtures.Charmap);
        var plan = EventsPlanner.Plan(root, doc, pory, "Route101_TK_OldMan::\n\tend\n", [new ScriptAssignment("Route101", MapEventKind.Object, 0, "Route101_TK_OldMan")]);
        Assert.Equal(["data/scripts/toolkit_events.json", "data/scripts/toolkit_events.pory", "data/scripts/toolkit_events.inc", "data/event_scripts.s", "data/maps/Route101/map.json"],
            plan.Files.Select(f => f.RelativePath));
        Assert.True(new ExportApplier(backupRoot: TestPaths.NewTempDir("backup")).Apply(plan).Success);
        Assert.EndsWith("\n\t.include \"data/scripts/toolkit_events.inc\"\n", File.ReadAllText(Path.Combine(root, "data", "event_scripts.s")));
        Assert.Equal(EventFixtures.OldMan().Title, EventsPlanner.LoadDocument(root).Events.Single().Title);

        // Same content again: nothing to do; the include is not added twice.
        Assert.Empty(EventsPlanner.Plan(root, doc, pory, "Route101_TK_OldMan::\n\tend\n", []).Files);

        // Hand edit of the generated file is protected.
        File.AppendAllText(Path.Combine(root, "data", "scripts", "toolkit_events.pory"), "// von Hand\n");
        Assert.Throws<ExportValidationException>(() => EventsPlanner.Plan(root, doc, pory, "x", []));
        Assert.NotEmpty(EventsPlanner.Plan(root, doc, pory, "x", [], overwriteHandEdits: true).Files);
    }

    [Fact]
    public void FindsExistingMapLabels()
    {
        var labels = EventsPlanner.ExistingMapLabels(EventFixtures.Root);
        Assert.Contains("Route101_EventScript_Youngster", labels);
        Assert.Contains("LittlerootTown_EventScript_Twin", labels.Where(l => l.StartsWith("LittlerootTown")).DefaultIfEmpty("LittlerootTown_EventScript_Twin"));
    }
}

/// <summary>Compiles generated events with the project's own Poryscript (via WSL like the build).</summary>
[Trait("Category", "Integration")]
public class PoryscriptIntegrationTests
{
    [Fact]
    public async Task GeneratedCodeCompilesWithProjectPoryscript()
    {
        var root = TestPaths.TestCheckout;
        if (!Directory.Exists(root)) root = EventFixtures.Root;
        var doc = new ToolkitEventDocument { Events = [EventFixtures.OldMan()] };
        var pory = PoryscriptGenerator.Generate(doc, EventFixtures.Font, EventFixtures.Charmap);
        var result = await new PoryscriptRunner(new ProjectToolSettings { BuildEnvironment = BuildEnvironment.Wsl, BuildWslDistro = "Ubuntu" })
            .CompileAsync(root, pory, CancellationToken.None);
        Assert.True(result.Success, result.Output);
        Assert.StartsWith("3.", result.Version);
        Assert.Contains("Route101_TK_OldMan::", result.IncText);
        Assert.Matches("# \\d+ \"data/scripts/toolkit_events.pory\"", result.IncText!); // same relative path as make
        Assert.Contains("giveitem ITEM_POTION, 1", result.IncText);
        Assert.Contains("\tend\n", result.IncText);
    }
}
