using TilesetEditor.Core.Events;
using TilesetEditor.Core.Text;
using TilesetEditor.Toolkit;

namespace TilesetEditor.Tests;

public class BlockCatalogTests
{
    private static readonly Lazy<ProjectSymbols> Symbols = new(() => ProjectSymbols.Load(EventFixtures.Root));

    private static ToolkitEvent AllBlocks()
    {
        var ev = new ToolkitEvent { Label = "Route101_TK_AllBlocks", Map = "Route101", Title = "All" };
        foreach (var def in BlockCatalog.All.Where(d => d.RequiresMod == null)) // mod blocks need the mod installed
        {
            var step = CommandStep.Create(def);
            if (def.Id == BlockCatalog.MacroBlockId) { step.Args["command"] = "setflag"; step.Args["arg0"] = "FLAG_UNUSED_0x021"; }
            if (def.IsBranch) step.Then.Add(new MessageStep { Text = "Yes branch." });
            ev.Steps.Add(step);
        }
        return ev;
    }

    [Fact]
    public void CatalogHasManyBlocksInAllCategories()
    {
        Assert.True(BlockCatalog.All.Count >= 30);
        Assert.Equal(BlockCatalog.All.Count, BlockCatalog.All.Select(b => b.Id).Distinct().Count());
        Assert.Contains("Battle", BlockCatalog.Categories);
        Assert.Contains("People & Movement", BlockCatalog.Categories);
    }

    [Fact]
    public void EveryBlockIsValidWithItsDefaults()
    {
        var doc = new ToolkitEventDocument { Events = [AllBlocks()] };
        var problems = PoryscriptGenerator.Validate(doc, EventFixtures.Font, EventFixtures.Charmap, Symbols.Value);
        Assert.Empty(problems.Where(p => p.IsError).Select(p => p.Message));
    }

    [Fact]
    public void ValidatesBlockParameters()
    {
        var step = CommandStep.Create(BlockCatalog.Find("move")!);
        step.Args["moves"] = "walk_up*2, fly_to_moon";
        var wild = CommandStep.Create(BlockCatalog.Find("wild_battle")!);
        wild.Args["species"] = "SPECIES_NOPE";
        wild.Args["level"] = "five";
        var mac = CommandStep.Create(BlockCatalog.Find(BlockCatalog.MacroBlockId)!);
        mac.Args["command"] = "givemon";
        var doc = new ToolkitEventDocument { Events = [new ToolkitEvent { Label = "X_TK_A", Map = "X", Steps = [step, wild, mac] }] };
        var msgs = PoryscriptGenerator.Validate(doc, EventFixtures.Font, EventFixtures.Charmap, Symbols.Value).Select(p => p.Message).ToList();
        Assert.Contains(msgs, m => m.Contains("fly_to_moon"));
        Assert.Contains(msgs, m => m.Contains("SPECIES_NOPE"));
        Assert.Contains(msgs, m => m.Contains("must be a number"));
        Assert.Contains(msgs, m => m.Contains("species") && m.Contains("required"));
    }

    [Fact]
    public void GeneratesCommandsBranchesMovementAndInlineTexts()
    {
        var code = PoryscriptGenerator.Generate(new ToolkitEventDocument { Events = [AllBlocks()] }, EventFixtures.Font, EventFixtures.Charmap);
        Assert.Contains("trainerbattle_single(TRAINER_CALVIN_1, \"You there! Let's battle!\", \"I lost...\")", code);
        Assert.Contains("applymovement(OBJ_EVENT_ID_PLAYER, moves(walk_up * 2, face_down))\n    waitmovement(0)", code);
        Assert.Contains("    checkitem(ITEM_POTION, 1)\n    if (var(VAR_RESULT) == TRUE) {", code);
        Assert.Contains("if (var(VAR_TEMP_1) == 1) {", code);
        Assert.Contains("if (defeated(TRAINER_CALVIN_1)) {", code);
        Assert.Contains("    setflag(FLAG_UNUSED_0x021)\n", code);
        Assert.Contains("    warp(MAP_LITTLEROOT_TOWN, 5, 5)\n    waitstate\n", code);
        Assert.Contains("// TODO", code);
    }

    [Fact]
    public void ElseOnlyBranchUsesNegatedCondition()
    {
        var s = CommandStep.Create(BlockCatalog.Find("if_var")!);
        s.Args["op"] = "<";
        s.Else.Add(new MessageStep { Text = "Big." });
        var code = PoryscriptGenerator.Generate(new ToolkitEventDocument { Events = [new ToolkitEvent { Label = "A_TK_B", Steps = [s] }] },
            EventFixtures.Font, EventFixtures.Charmap);
        Assert.Contains("if (var(VAR_TEMP_1) >= 1) {", code);
    }

    [Fact]
    public void PreviewShowsTrainerTextsAndAsksForConditions()
    {
        var trainer = CommandStep.Create(BlockCatalog.Find("trainer_single")!);
        var ifItem = CommandStep.Create(BlockCatalog.Find("if_item")!);
        ifItem.Then.Add(new MessageStep { Text = "You have it." });
        ifItem.Else.Add(new MessageStep { Text = "You don't." });
        var buffer = CommandStep.Create(BlockCatalog.Find("buffer_species")!);
        var ev = new ToolkitEvent { Label = "X", Steps = [trainer, ifItem, buffer, new MessageStep { Text = "It's {STR_VAR_1}!" }] };
        var s = new DialogSession(ev, EventFixtures.Font, EventFixtures.Charmap, new HashSet<string>()) { TextSpeed = 1 };
        void Run() { for (int i = 0; i < 3000 && s.State == DialogState.Typing; i++) s.Tick(); }
        s.Tick();
        Run();
        Assert.StartsWith("You there!", s.Lines[0]);
        s.PressA();
        Run();
        Assert.Equal("I lost...", s.Lines[0]);
        Assert.Contains(s.Log, l => l.Contains("Battle against TRAINER_CALVIN_1"));
        s.PressA();
        Assert.Equal(DialogState.Deciding, s.State);
        Assert.Contains("ITEM_POTION", s.DecisionPrompt);
        s.Choose(false);
        Run();
        Assert.Equal("You don't.", s.Lines[0]);
        s.PressA();
        Run();
        Assert.Equal("It's PIKACHU!", s.Lines[0]);
    }

    [Fact]
    public void MenuGeneratesSwitchAndMartGeneratesTopLevelList()
    {
        var menu = CommandStep.Create(BlockCatalog.Find("menu")!);
        var cases = menu.CaseBranches();
        Assert.Equal(["0", "1", "2", "MULTI_B_PRESSED"], cases.Select(c => c.Value));
        var mart = CommandStep.Create(BlockCatalog.Find("mart")!);
        cases[0].Steps.Add(mart);
        cases[3].Steps.Add(new MessageStep { Text = "Come again!" });
        var code = PoryscriptGenerator.Generate(new ToolkitEventDocument { Events = [new ToolkitEvent { Label = "Route101_TK_Shop", Steps = [menu] }] },
            EventFixtures.Font, EventFixtures.Charmap);
        Assert.Contains("    message(\"What would you like to do?\")\n    waitmessage\n    dynmultichoice(0, 0, FALSE, 6, 0, DYN_MULTICHOICE_CB_NONE, \"Buy\", \"Sell\", \"Nothing\")\n", code);
        Assert.Contains("    switch (var(VAR_RESULT)) {\n        case 0:\n            pokemart(Route101_TK_Shop_Mart0)\n        case MULTI_B_PRESSED:\n", code);
        Assert.Contains("mart Route101_TK_Shop_Mart0 {\n    ITEM_POTION\n    ITEM_ANTIDOTE\n    ITEM_POKE_BALL\n}\n", code);
        Assert.True(PoryscriptGenerator.IsUnmodified(code));
    }

    [Fact]
    public void MenuPreviewShowsOptionsAndFollowsTheChosenBranch()
    {
        var menu = CommandStep.Create(BlockCatalog.Find("menu")!);
        var cases = menu.CaseBranches();
        cases[1].Steps.Add(new MessageStep { Text = "Selling!" });
        cases[3].Steps.Add(new MessageStep { Text = "Cancelled." });
        var s = new DialogSession(new ToolkitEvent { Label = "X", Steps = [menu] }, EventFixtures.Font, EventFixtures.Charmap, new HashSet<string>()) { TextSpeed = 1 };
        s.Tick();
        for (int i = 0; i < 2000 && s.State == DialogState.Typing; i++) s.Tick();
        Assert.Equal(DialogState.Choosing, s.State);
        Assert.Equal(["Buy", "Sell", "Nothing"], s.MenuOptions);
        var scene = s.Scene(DialogFrameStyle.Dialog, 1, null);
        Assert.NotNull(scene.MenuOptions);
        Assert.Null(scene.YesNoCursor);
        s.MoveCursor(1);
        s.PressA();
        for (int i = 0; i < 2000 && s.State == DialogState.Typing; i++) s.Tick();
        Assert.Equal("Selling!", s.Lines[0]);
        // B cancels into the last branch
        var s2 = new DialogSession(new ToolkitEvent { Label = "X", Steps = [menu] }, EventFixtures.Font, EventFixtures.Charmap, new HashSet<string>()) { TextSpeed = 1 };
        s2.Tick();
        for (int i = 0; i < 2000 && s2.State == DialogState.Typing; i++) s2.Tick();
        s2.PressB();
        for (int i = 0; i < 2000 && s2.State == DialogState.Typing; i++) s2.Tick();
        Assert.Equal("Cancelled.", s2.Lines[0]);
    }

    [Fact]
    public void ValidatesMenuOptionsAndMartItems()
    {
        var menu = CommandStep.Create(BlockCatalog.Find("menu")!);
        menu.Args["options"] = "Only one";
        var mart = CommandStep.Create(BlockCatalog.Find("mart")!);
        mart.Args["items"] = "ITEM_POTION, ITEM_UNKNOWN_THING";
        var msgs = PoryscriptGenerator.Validate(new ToolkitEventDocument { Events = [new ToolkitEvent { Label = "A_TK_B", Steps = [menu, mart] }] },
            EventFixtures.Font, EventFixtures.Charmap, Symbols.Value).Select(p => p.Message).ToList();
        Assert.Contains(msgs, m => m.Contains("at least two answers"));
        Assert.Contains(msgs, m => m.Contains("ITEM_UNKNOWN_THING"));
    }

    [Fact]
    public void ParsesMacrosWithSpaceSeparatedVarargs()
    {
        var dyn = Symbols.Value.Macros.Single(m => m.Name == "dynmultichoice");
        Assert.Equal(["left", "top", "ignoreBPress", "maxBeforeScroll", "initialSelected", "callbacks", "argv"], dyn.Params.Select(p => p.Name));
    }

    [Fact]
    public void ParsesAllMacrosWithParameters()
    {
        var macros = Symbols.Value.Macros;
        Assert.True(macros.Count > 300, $"nur {macros.Count}");
        var give = macros.Single(m => m.Name == "givemon");
        Assert.Equal(("species", true), (give.Params[0].Name, give.Params[0].Required));
        Assert.Contains("Pokémon", give.Description);
        var checkitem = macros.Single(m => m.Name == "checkitem");
        Assert.Equal("1", checkitem.Params[1].Default);
        Assert.Contains(Symbols.Value.MovementActions, a => a == "walk_up");
        Assert.Contains("MAP_ROUTE101", Symbols.Value.Maps);
        Assert.Contains("SE_SELECT", Symbols.Value.Sounds);
        Assert.Contains("HealPlayerParty", Symbols.Value.Specials);
    }
}

[Trait("Category", "Integration")]
public class BlockCatalogIntegrationTests
{
    [Fact]
    public async Task AllBlocksCompileWithProjectPoryscript()
    {
        var root = TestPaths.TestCheckout;
        if (!Directory.Exists(root)) root = EventFixtures.Root;
        var ev = new ToolkitEvent { Label = "Route101_TK_AllBlocksTest", Map = "Route101" };
        foreach (var def in BlockCatalog.All.Where(d => d.RequiresMod == null)) // mod blocks need the mod installed
        {
            var step = CommandStep.Create(def);
            if (def.Id == BlockCatalog.MacroBlockId) step.Args["command"] = "nop";
            if (def.IsBranch) { step.Then.Add(new MessageStep { Text = "A" }); step.Else.Add(new MessageStep { Text = "B" }); }
            ev.Steps.Add(step);
        }
        var pory = PoryscriptGenerator.Generate(new ToolkitEventDocument { Events = [ev] }, EventFixtures.Font, EventFixtures.Charmap);
        var r = await new PoryscriptRunner(new ProjectToolSettings()).CompileAsync(root, pory, CancellationToken.None);
        Assert.True(r.Success, r.Output);
        Assert.Contains("trainerbattle_single TRAINER_CALVIN_1", r.IncText);
        Assert.Contains("step_end", r.IncText);
    }
}
