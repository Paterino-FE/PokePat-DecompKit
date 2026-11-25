using TilesetEditor.Core.Events;
using TilesetEditor.Core.Maps;
using TilesetEditor.Core.Project;

namespace TilesetEditor.Tests;

public class MapPreviewTests
{
    // ---------------------------------------------------------------- renderer

    [Fact]
    public void Route101_RendersAtSixteenPixelsPerBlock()
    {
        var project = ProjectLoader.Load(TestPaths.ReferenceProject);
        var scene = MapScene.Load(project, "Route101");
        Assert.NotNull(scene.Grid);
        Assert.Equal(20, scene.Width);
        Assert.Equal(20, scene.Height);
        var img = MapRenderer.Render(project, scene);
        Assert.NotNull(img);
        Assert.Equal(scene.Layout!.Width * 16, img!.Width);
        Assert.Equal(scene.Layout.Height * 16, img.Height);
        // Every pixel is covered (backdrop or tile), and the map is not a flat color.
        Assert.All(new[] { img[0, 0], img[8, 8], img[img.Width / 2, img.Height / 2], img[img.Width - 1, img.Height - 1] }, p => Assert.Equal(255, p.A));
        Assert.True(img.Pixels.Distinct().Count() > 16);
        // Two blocks with the same metatile render identically; the cache returns the same result on a second call.
        var again = MapRenderer.Render(project, scene)!;
        Assert.Equal(img.Pixels, again.Pixels);
    }

    [Fact]
    public void Route101_SameMetatileRendersTheSame()
    {
        var project = ProjectLoader.Load(TestPaths.ReferenceProject);
        var scene = MapScene.Load(project, "Route101");
        var g = scene.Grid!;
        var img = MapRenderer.Render(project, scene)!;
        var byId = new Dictionary<int, (int X, int Y)>();
        int compared = 0;
        for (int y = 0; y < g.Height && compared < 5; y++)
            for (int x = 0; x < g.Width && compared < 5; x++)
            {
                int id = g.MetatileId(x, y);
                if (!byId.TryGetValue(id, out var first)) { byId[id] = (x, y); continue; }
                for (int py = 0; py < 16; py++)
                    for (int px = 0; px < 16; px++)
                        Assert.Equal(img[first.X * 16 + px, first.Y * 16 + py], img[x * 16 + px, y * 16 + py]);
                compared++;
            }
        Assert.True(compared > 0);
    }

    [Fact]
    public void Route101_ObjectsAndBlockdata()
    {
        var project = ProjectLoader.Load(TestPaths.ReferenceProject);
        var scene = MapScene.Load(project, "Route101");
        Assert.Empty(scene.Problems);
        var youngster = scene.Objects.First(o => o.GraphicsId == "OBJ_EVENT_GFX_YOUNGSTER");
        Assert.Equal((16, 8), (youngster.X, youngster.Y));
        Assert.Same(youngster, scene.FindObject(youngster.Number.ToString()));
        Assert.Equal(Facing.Right, scene.Objects.First(o => o.GraphicsId == "OBJ_EVENT_GFX_MOM").InitialFacing);
        Assert.Null(scene.FindObject("OBJ_EVENT_ID_PLAYER"));
        // Collision bits exist somewhere (trees/ledges) and the map has walkable tiles.
        var g = scene.Grid!;
        var all = Enumerable.Range(0, g.Width * g.Height).Select(i => g.Collision(i % g.Width, i / g.Width)).ToList();
        Assert.Contains(all, c => c != 0);
        Assert.Contains(all, c => c == 0);
    }

    // ---------------------------------------------------------------- simulator

    private static Func<string, ActorStart?> Actors(params ActorStart[] actors) => target =>
        MapScene.IsPlayer(target) ? actors.FirstOrDefault(a => a.IsPlayer) : actors.FirstOrDefault(a => a.Key == target);

    [Fact]
    public void Actions_MoveTheRightDistance()
    {
        Assert.Equal((0, -1), D("walk_up"));
        Assert.Equal((-1, 0), D("walk_slow_left"));
        Assert.Equal((1, 0), D("player_run_right"));
        Assert.Equal((0, 1), D("slide_down"));
        Assert.Equal((0, 1), D("ride_water_current_down"));
        Assert.Equal((2, 0), D("jump_2_right"));
        Assert.Equal((-1, 0), D("jump_left"));
        Assert.Equal((0, -1), D("jump_special_up"));
        Assert.Equal((0, 0), D("jump_in_place_down"));
        Assert.Equal((0, 0), D("walk_in_place_fast_left"));
        Assert.Equal((-1, -1), D("walk_diag_northwest"));
        Assert.Equal((0, 0), D("delay_16"));
        Assert.Equal(StepKind.Unknown, MovementActions.Describe("moonwalk_up").Kind);
        Assert.Equal(Facing.Left, MovementActions.Describe("walk_in_place_left").Facing);
        Assert.Equal(16, MovementActions.Describe("delay_16").Frames);
        Assert.True(MovementActions.Describe("walk_slow_up").Frames > MovementActions.Describe("walk_up").Frames);
        Assert.True(MovementActions.Describe("walk_up").Frames > MovementActions.Describe("walk_fast_up").Frames);

        static (int, int) D(string a) { var i = MovementActions.Describe(a); return (i.Dx, i.Dy); }
    }

    [Fact]
    public void Simulator_FollowsStepsAndFacing()
    {
        var player = new ActorStart(MovementSimulator.PlayerKey, "Player", 5, 5, Facing.Down, true);
        var r = MovementSimulator.Simulate([MovementPhase.FromMove("OBJ_EVENT_ID_PLAYER", "walk_up*2, face_down, walk_in_place_left, jump_2_right")], Actors(player));
        var t = r.Tracks.Single();
        Assert.Equal([(5, 4), (5, 3), (5, 3), (5, 3), (7, 3)], t.Steps.Select(s => (s.X, s.Y)));
        Assert.Equal([Facing.Up, Facing.Up, Facing.Down, Facing.Left, Facing.Right], t.Steps.Select(s => s.Facing));
        Assert.Equal((7, 3, Facing.Right), r.EndOf(MovementSimulator.PlayerKey));
        Assert.Empty(r.AllWarnings);
    }

    [Fact]
    public void Simulator_MoveTogetherRunsAtTheSameTime()
    {
        var player = new ActorStart(MovementSimulator.PlayerKey, "Player", 5, 5, Facing.Down, true);
        var npc = new ActorStart("1", "Rival", 8, 5, Facing.Left);
        var phases = ScriptMovementPhases.FromBlocks([
            new CommandStep { Block = "move_together", Args = new() { ["group"] = "OBJ_EVENT_ID_PLAYER: face_right\n1: walk_left*2, face_right" } },
            new CommandStep { Block = "move", Args = new() { ["target"] = "1", ["moves"] = "walk_down" } },
        ]);
        Assert.Equal(2, phases.Count);
        var r = MovementSimulator.Simulate(phases, Actors(player, npc));
        var p1 = r.Phases[0];
        Assert.Equal(2, p1.Tracks.Count);
        Assert.All(p1.Tracks, t => Assert.Equal(0, t.Steps[0].StartFrame));
        Assert.Equal(p1.Tracks.Max(t => t.Frames), p1.Frames);
        Assert.Equal((5, 5, Facing.Right), (p1.Tracks[0].EndX, p1.Tracks[0].EndY, p1.Tracks[0].EndFacing));
        Assert.Equal((6, 5, Facing.Right), (p1.Tracks[1].EndX, p1.Tracks[1].EndY, p1.Tracks[1].EndFacing));
        // Phase 2 starts where phase 1 ended, after it.
        var p2 = r.Phases[1];
        Assert.Equal(p1.Frames, p2.StartFrame);
        Assert.Equal((6, 5), (p2.Tracks[0].StartX, p2.Tracks[0].StartY));
        Assert.Equal((6, 6, Facing.Down), r.EndOf("1"));
        // Halfway through the first walk_left the rival is between the tiles.
        var half = r.StateAt("1", 8);
        Assert.Equal(7.5, half.X, 3);
        Assert.Equal(5, half.Y, 3);
        Assert.Equal((8.0, 5.0), (r.StateAt("1", 0).X, r.StateAt("1", 0).Y));
    }

    [Fact]
    public void Simulator_WarnsAboutUnknownStepsEdgesAndBlockedTiles()
    {
        var blocks = new ushort[4 * 4];
        blocks[1 * 4 + 2] = 0x0C00; // (2,1) blocked
        var grid = new MapGrid(4, 4, blocks);
        var player = new ActorStart(MovementSimulator.PlayerKey, "Player", 1, 1, Facing.Down, true);
        var r = MovementSimulator.Simulate([MovementPhase.FromMove("OBJ_EVENT_ID_PLAYER", "walk_right, dance_left, walk_up*2"),
            MovementPhase.FromMove("LOCALID_NOBODY", "walk_up")], Actors(player), grid);
        var w = r.Tracks.Single().Warnings;
        Assert.Contains(w, x => x.Contains("blocked tile at (2,1)"));
        Assert.Contains(w, x => x.Contains("unknown action"));
        Assert.Contains(w, x => x.Contains("leaves the map at (2,-1)"));
        Assert.Contains(r.Warnings, x => x.Contains("LOCALID_NOBODY"));
        Assert.Equal(StepKind.Unknown, r.Tracks.Single().Steps[1].Kind);
    }

    [Fact]
    public void Simulator_WarnsWhenWalkingIntoAStandingPerson()
    {
        var player = new ActorStart(MovementSimulator.PlayerKey, "Player", 5, 9, Facing.Up, true);
        var man = new ActorStart("2", "Man", 5, 7, Facing.Down);
        var r = MovementSimulator.Simulate([MovementPhase.FromMove("OBJ_EVENT_ID_PLAYER", "walk_up*2")], Actors(player, man), bystanders: [man]);
        Assert.Contains(r.Tracks.Single().Warnings, w => w.Contains("bumps into Man at (5,7)"));
        // When both move at the same time, the other one is not treated as an obstacle.
        var r2 = MovementSimulator.Simulate([MovementPhase.FromGroup("OBJ_EVENT_ID_PLAYER: walk_up*2\n2: walk_left")], Actors(player, man), bystanders: [man]);
        Assert.DoesNotContain(r2.AllWarnings, w => w.Contains("bumps"));
    }

    [Fact]
    public void Simulator_FacePlayerLooksAtThePlayer()
    {
        var player = new ActorStart(MovementSimulator.PlayerKey, "Player", 5, 9, Facing.Up, true);
        var npc = new ActorStart("1", "Man", 5, 5, Facing.Left);
        var r = MovementSimulator.Simulate([MovementPhase.FromMove("1", "face_player, face_away_player, face_original_direction")], Actors(player, npc));
        Assert.Equal([Facing.Down, Facing.Up, Facing.Left], r.Tracks.Single().Steps.Select(s => s.Facing));
    }

    // ---------------------------------------------------------------- scripts

    [Fact]
    public void MossdeepMagmaScene_BecomesSimultaneousPhases()
    {
        var root = TestPaths.ReferenceProject;
        var r = ScriptMovementPhases.Read(root, "MossdeepCity", "MossdeepCity_EventScript_TeamMagmaEnterSpaceCenter");
        Assert.True(r.Phases.Count >= 3);
        var enter = r.Phases.Single(p => p.Orders.Any(o => o.Target == "LOCALID_MAXIE") && p.Orders.Count == 5);
        Assert.Equal(["walk_down", "walk_right", "walk_right", "walk_down", "walk_down", "walk_down",
            "walk_right", "walk_right", "walk_right", "walk_right", "walk_right", "walk_right", "walk_right"],
            enter.Orders.Single(o => o.Target == "LOCALID_MAXIE").Steps);

        var project = ProjectLoader.Load(root);
        var scene = MapScene.Load(project, "MossdeepCity");
        var maxie = scene.FindObject("LOCALID_MAXIE");
        Assert.NotNull(maxie);
        Assert.Equal(14, maxie!.Number);
        Assert.Equal((45, 25), (maxie.X, maxie.Y)); // the 14th object_events entry

        var sim = MovementSimulator.Simulate(r.Phases,
            t => MapScene.IsPlayer(t) ? new ActorStart(MovementSimulator.PlayerKey, "Player", 0, 0, Facing.Down, true)
                : scene.FindObject(t) is { } o ? new ActorStart($"obj{o.Index}", o.Name, o.X, o.Y, o.InitialFacing) : null, scene.Grid);
        var track = sim.Tracks.Single(t => t.Target == "LOCALID_MAXIE" && t.Steps.Count == 13);
        Assert.Equal((maxie.X + 9, maxie.Y + 4), (track.EndX, track.EndY));
        Assert.Empty(sim.Warnings);
    }
}
