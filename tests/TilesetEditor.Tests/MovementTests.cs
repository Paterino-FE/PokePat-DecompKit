using TilesetEditor.Core.Events;

namespace TilesetEditor.Tests;

public class MovementTests
{
    [Fact]
    public void ReadsTheMossdeepMagmaScene()
    {
        var r = MapScriptReader.Read(TestPaths.ReferenceProject, "MossdeepCity");
        var scene = r.Labels.Single(l => l.Name == "MossdeepCity_EventScript_TeamMagmaEnterSpaceCenter");
        Assert.Equal(ScriptLabelKind.Script, scene.Kind);
        // LOCALID_MAXIE (set in scripts.inc) up to expansion 1.12, LOCALID_MOSSDEEP_MAXIE (local_id in map.json) later.
        var maxie = MapPreviewTests.MaxieLocalId(TestPaths.ReferenceProject)["LOCALID_".Length..];
        Assert.Contains(scene.Lines, l => l.Text == maxie + ": walk_down, walk_right ×2, walk_down ×3, walk_right ×7" && l.Jump == "MossdeepCity_Movement_MaxieEnterSpaceCenter");
        Assert.Contains(scene.Lines, l => l.Text == "Wait until the movements are finished");
        Assert.Contains(scene.Lines, l => l.Text == "Set FLAG_HIDE_MOSSDEEP_CITY_TEAM_MAGMA");
        var move = r.Labels.Single(l => l.Name == "MossdeepCity_Movement_GruntFaceSpaceCenter");
        Assert.Equal(ScriptLabelKind.Movement, move.Kind);
        Assert.Equal("face_right, delay_16 ×3", move.Lines.Single().Text);
        Assert.Contains(r.Labels, l => l.Kind == ScriptLabelKind.Text);
        Assert.Contains(r.Labels, l => l.UsedBy.Count > 0);
        // Common movements from data/scripts/movement.inc are resolved too.
        var man = r.Labels.Single(l => l.Name == "MossdeepCity_EventScript_Man");
        Assert.Contains(man.Lines, l => l.Text.StartsWith("the person talked to: ", StringComparison.Ordinal) && !l.Text.Contains("Common_Movement"));
    }

    [Fact]
    public void MoveTogetherWritesOneWaitForEveryone()
    {
        var def = BlockCatalog.Find("move_together")!;
        var args = new BlockArgs(def, new Dictionary<string, string> { ["group"] = "OBJ_EVENT_ID_PLAYER: face_right\n3: walk_left*2, face_right\n" });
        Assert.Equal(["applymovement(OBJ_EVENT_ID_PLAYER, moves(face_right))", "applymovement(3, moves(walk_left * 2, face_right))", "waitmovement(0)"],
            def.Lines(args).ToList());
        var bad = BlockCatalog.Group("walk_up\n3: walk_left\n3: walk_up");
        Assert.Equal(("", "walk_up"), bad[0]);
    }
}

public class DialogCatalogTests
{
    [Fact]
    public void Lists_people_signs_and_their_texts()
    {
        var r = DialogCatalog.Load(TestPaths.ReferenceProject);
        Assert.True(r.Maps.Count > 400);
        var sign = Assert.Single(r.Events, e => e.Map == "LittlerootTown" && e.Script == "LittlerootTown_EventScript_TownSign");
        Assert.Equal(ExistingEventKind.Sign, sign.Kind);
        Assert.Contains(sign.Dialogs, d => d.EditorText.StartsWith("LITTLEROOT TOWN", StringComparison.Ordinal));
        var twin = r.Events.First(e => e.Map == "LittlerootTown" && e.Script == "LittlerootTown_EventScript_Twin");
        Assert.Equal(ExistingEventKind.Person, twin.Kind);
        Assert.True(twin.Dialogs.Count >= 2); // followed through goto/call
        Assert.Contains(r.Dialogs, d => d.Map == "(shared)" && d.File.StartsWith("data/text/", StringComparison.Ordinal));
        Assert.Equal("A\nB\n\nC", DialogCatalog.ToEditorText(@"A\nB\pC$"));
    }
}
