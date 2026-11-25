using System.Text.RegularExpressions;
using TilesetEditor.Core.Events;

namespace TilesetEditor.Core.Maps;

public enum StepKind
{
    /// <summary>Walks/runs/slides one tile (or diagonally).</summary>
    Move,
    /// <summary>Jumps 1 or 2 tiles.</summary>
    Jump,
    /// <summary>Only changes the facing (face_*, walk_in_place_*, jump_in_place_*).</summary>
    Turn,
    /// <summary>Only takes time (delay_*, emotes, animations, flags).</summary>
    Wait,
    /// <summary>Teleport (setobjectxy) – zero time.</summary>
    Place,
    /// <summary>Not a known movement action: treated as standing still.</summary>
    Unknown,
}

/// <summary>What one movement action does: tiles moved, new facing, approximate duration in frames (60 per second).</summary>
public sealed record ActionInfo(string Name, StepKind Kind, int Dx, int Dy, Facing? Facing, int Frames, string? Special = null)
{
    public bool Moves => Dx != 0 || Dy != 0;
}

/// <summary>
/// The movement actions of asm/macros/movement.inc. Distances follow src/event_object_movement.c (jump_2_* and
/// acro_wheelie_jump_* = 2 tiles, jump_* / jump_special_* / acro_wheelie_hop_* = 1, jump_in_place_* = 0); frame counts are
/// approximate (walk 16 frames per tile, slow 32, fast/run/slide 8, faster 4).
/// </summary>
public static partial class MovementActions
{
    [GeneratedRegex(@"^(.*?)_(down|up|left|right|northwest|northeast|southwest|southeast)$")]
    private static partial Regex DirSuffix();

    [GeneratedRegex(@"^delay_(\d+)$")]
    private static partial Regex Delay();

    [GeneratedRegex(@"^jump_in_place_(down|up|left|right)_(down|up|left|right)$")]
    private static partial Regex JumpInPlaceTurn();

    [GeneratedRegex(@"^@(-?\d+),(-?\d+)$")]
    private static partial Regex PlaceAt();

    // base name → (tiles, frames). 0 tiles = facing only.
    private static readonly Dictionary<string, (int Tiles, int Frames, StepKind Kind)> Directed = new(StringComparer.Ordinal)
    {
        ["face"] = (0, 2, StepKind.Turn),
        ["walk_slow"] = (1, 32, StepKind.Move),
        ["walk"] = (1, 16, StepKind.Move),
        ["walk_fast"] = (1, 8, StepKind.Move),
        ["walk_faster"] = (1, 4, StepKind.Move),
        ["walk_fastest"] = (1, 2, StepKind.Move),
        ["walk_slower"] = (1, 48, StepKind.Move),
        ["walk_slowest"] = (1, 64, StepKind.Move),
        ["player_run"] = (1, 8, StepKind.Move),
        ["slide"] = (1, 8, StepKind.Move),
        ["ride_water_current"] = (1, 6, StepKind.Move),
        ["walk_diag"] = (1, 16, StepKind.Move),
        ["walk_slow_diag"] = (1, 32, StepKind.Move),
        ["jump_2"] = (2, 32, StepKind.Jump),
        ["jump"] = (1, 16, StepKind.Jump),
        ["jump_special"] = (1, 16, StepKind.Jump),
        ["jump_in_place"] = (0, 16, StepKind.Turn),
        ["walk_in_place_slow"] = (0, 32, StepKind.Turn),
        ["walk_in_place"] = (0, 16, StepKind.Turn),
        ["walk_in_place_fast"] = (0, 8, StepKind.Turn),
        ["walk_in_place_faster"] = (0, 4, StepKind.Turn),
        ["acro_wheelie_face"] = (0, 8, StepKind.Turn),
        ["acro_pop_wheelie"] = (0, 8, StepKind.Turn),
        ["acro_end_wheelie_face"] = (0, 8, StepKind.Turn),
        ["acro_wheelie_hop_face"] = (0, 16, StepKind.Turn),
        ["acro_wheelie_in_place"] = (0, 8, StepKind.Turn),
        ["acro_wheelie_hop"] = (1, 16, StepKind.Jump),
        ["acro_wheelie_jump"] = (2, 32, StepKind.Jump),
        ["acro_pop_wheelie_move"] = (1, 8, StepKind.Move),
        ["acro_wheelie_move"] = (1, 8, StepKind.Move),
        ["acro_end_wheelie_move"] = (1, 8, StepKind.Move),
    };

    // Actions without a direction suffix that do not move.
    private static readonly Dictionary<string, int> Undirected = new(StringComparer.Ordinal)
    {
        ["emote_exclamation_mark"] = 60, ["emote_question_mark"] = 60, ["emote_heart"] = 60, ["emote_x"] = 60,
        ["emote_double_exclamation_mark"] = 60, ["nurse_joy_bow"] = 32, ["reveal_trainer"] = 16, ["rock_smash_break"] = 32,
        ["cut_tree"] = 32, ["figure_8"] = 64, ["fly_up"] = 32, ["fly_down"] = 32, ["exit_pokeball"] = 32, ["enter_pokeball"] = 32,
        ["levitate"] = 1, ["stop_levitate"] = 1, ["destroy_extra_task"] = 1, ["start_anim_in_direction"] = 1,
        ["enable_jump_landing_ground_effect"] = 0, ["disable_jump_landing_ground_effect"] = 0, ["disable_anim"] = 0, ["restore_anim"] = 0,
        ["set_fixed_priority"] = 0, ["clear_fixed_priority"] = 0, ["init_affine_anim"] = 0, ["clear_affine_anim"] = 0,
        ["hide_reflection"] = 0, ["show_reflection"] = 0, ["lock_anim"] = 0, ["unlock_anim"] = 0, ["step_end"] = 0,
    };

    public static ActionInfo Describe(string action, IReadOnlySet<string>? knownActions = null)
    {
        var a = action.Trim();
        if (PlaceAt().Match(a) is { Success: true } pm)
            return new ActionInfo(a, StepKind.Place, int.Parse(pm.Groups[1].Value), int.Parse(pm.Groups[2].Value), null, 0, "place");
        switch (a)
        {
            case "face_player": return new(a, StepKind.Turn, 0, 0, null, 2, "face_player");
            case "face_away_player": return new(a, StepKind.Turn, 0, 0, null, 2, "face_away_player");
            case "face_original_direction": return new(a, StepKind.Turn, 0, 0, null, 2, "face_original");
            case "lock_facing_direction": return new(a, StepKind.Wait, 0, 0, null, 0, "lock_facing");
            case "unlock_facing_direction": return new(a, StepKind.Wait, 0, 0, null, 0, "unlock_facing");
            case "set_invisible": return new(a, StepKind.Wait, 0, 0, null, 0, "invisible");
            case "set_visible": return new(a, StepKind.Wait, 0, 0, null, 0, "visible");
            // Affine walks (used for the S.S. Tidal / Birch's bag scenes): still one tile.
            case "walk_down_affine" or "walk_down_start_affine": return new(a, StepKind.Move, 0, 1, Facing.Down, 16);
            case "walk_left_affine": return new(a, StepKind.Move, -1, 0, Facing.Left, 16);
            case "walk_right_affine": return new(a, StepKind.Move, 1, 0, Facing.Right, 16);
        }
        if (Delay().Match(a) is { Success: true } dm) return new(a, StepKind.Wait, 0, 0, null, int.Parse(dm.Groups[1].Value));
        if (Undirected.TryGetValue(a, out var frames)) return new(a, StepKind.Wait, 0, 0, null, frames);
        if (JumpInPlaceTurn().Match(a) is { Success: true } jm)
            return new(a, StepKind.Turn, 0, 0, FacingExtensions.Parse(jm.Groups[2].Value), 32);
        if (DirSuffix().Match(a) is { Success: true } m && Directed.TryGetValue(m.Groups[1].Value, out var d))
        {
            var dir = m.Groups[2].Value;
            int dx = 0, dy = 0;
            Facing facing;
            if (dir.Length > 5) // diagonal: the sprite faces the horizontal component
            {
                dy = dir.StartsWith("north", StringComparison.Ordinal) ? -1 : 1;
                dx = dir.EndsWith("west", StringComparison.Ordinal) ? -1 : 1;
                facing = dx < 0 ? Facing.Left : Facing.Right;
            }
            else
            {
                facing = FacingExtensions.Parse(dir)!.Value;
                (dx, dy) = facing.Delta();
            }
            return new(a, d.Kind, dx * d.Tiles, dy * d.Tiles, facing, d.Frames);
        }
        // Defined in the project's movement.inc but not modelled here: assume it stays on its tile.
        if (knownActions != null && knownActions.Contains(a)) return new(a, StepKind.Wait, 0, 0, null, 8);
        return new(a, StepKind.Unknown, 0, 0, null, 0);
    }

    /// <summary>"walk_up*2, face_down" → [walk_up, walk_up, face_down] (counts capped at 255).</summary>
    public static List<string> Expand(string moves)
    {
        var result = new List<string>();
        foreach (var (action, count) in BlockCatalog.ParseMovement(moves))
            for (int i = 0; i < Math.Clamp(count, 1, 255); i++) result.Add(action);
        return result;
    }
}

/// <summary>One person (or the player) moving in a phase: its step list.</summary>
public sealed record MovementOrder(string Target, IReadOnlyList<string> Steps);

/// <summary>Movements that run at the same time (applymovement … applymovement, waitmovement).</summary>
public sealed class MovementPhase
{
    public string Caption { get; init; } = "";
    public List<MovementOrder> Orders { get; } = [];

    /// <summary>Phase of a "Movement" block.</summary>
    public static MovementPhase FromMove(string target, string moves, string caption = "") =>
        new MovementPhase { Caption = caption }.Add(target, moves);

    /// <summary>Phase of a "Move together" block ("who: steps" per line).</summary>
    public static MovementPhase FromGroup(string group, string caption = "")
    {
        var p = new MovementPhase { Caption = caption };
        foreach (var (target, moves) in BlockCatalog.Group(group)) p.Add(target, moves);
        return p;
    }

    public MovementPhase Add(string target, string moves)
    {
        Orders.Add(new MovementOrder(target, MovementActions.Expand(moves)));
        return this;
    }
}

/// <summary>Who a target is and where it stands before the first phase.</summary>
public sealed record ActorStart(string Key, string Name, int X, int Y, Facing Facing, bool IsPlayer = false);

public sealed record SimStep(int Index, string Action, StepKind Kind, int FromX, int FromY, int X, int Y, Facing Facing,
    int StartFrame, int Frames, string? Warning);

/// <summary>The path of one actor in one phase.</summary>
public sealed class SimTrack
{
    public required string Key { get; init; }
    public required string Name { get; init; }
    public required string Target { get; init; }
    public required bool IsPlayer { get; init; }
    public required int Phase { get; init; }
    public required int StartX { get; init; }
    public required int StartY { get; init; }
    public required Facing StartFacing { get; init; }
    public List<SimStep> Steps { get; } = [];
    public List<string> Warnings { get; } = [];
    public int EndX => Steps.Count > 0 ? Steps[^1].X : StartX;
    public int EndY => Steps.Count > 0 ? Steps[^1].Y : StartY;
    public Facing EndFacing => Steps.Count > 0 ? Steps[^1].Facing : StartFacing;
    public int Frames => Steps.Sum(s => s.Frames);
}

public sealed class SimPhase
{
    public required int Index { get; init; }
    public required string Caption { get; init; }
    public required int StartFrame { get; init; }
    public int Frames { get; set; }
    public List<SimTrack> Tracks { get; } = [];
}

public sealed class SimulationResult
{
    public List<SimPhase> Phases { get; } = [];
    public List<string> Warnings { get; } = [];
    /// <summary>Start state of every actor that appears in a phase.</summary>
    public Dictionary<string, ActorStart> Actors { get; } = [];
    public int TotalFrames => Phases.Count == 0 ? 0 : Phases[^1].StartFrame + Phases[^1].Frames;
    public IEnumerable<SimTrack> Tracks => Phases.SelectMany(p => p.Tracks);
    public IEnumerable<string> AllWarnings => Warnings.Concat(Tracks.SelectMany(t => t.Warnings.Select(w => $"{t.Name}: {w}")));

    /// <summary>Final tile and facing of an actor (its start if it never moves).</summary>
    public (int X, int Y, Facing Facing) EndOf(string key)
    {
        var last = Tracks.LastOrDefault(t => t.Key == key);
        if (last != null) return (last.EndX, last.EndY, last.EndFacing);
        var a = Actors[key];
        return (a.X, a.Y, a.Facing);
    }

    /// <summary>Position (in tiles, fractional while walking) and facing of an actor at a frame, for the animated play-through.</summary>
    public (double X, double Y, Facing Facing) StateAt(string key, double frame)
    {
        if (!Actors.TryGetValue(key, out var a)) return (0, 0, Facing.Down);
        (double X, double Y, Facing F) s = (a.X, a.Y, a.Facing);
        foreach (var t in Tracks.Where(t => t.Key == key))
            foreach (var step in t.Steps)
            {
                if (frame >= step.StartFrame + step.Frames) { s = (step.X, step.Y, step.Facing); continue; }
                if (frame < step.StartFrame) return s;
                double f = step.Frames <= 0 ? 1 : (frame - step.StartFrame) / step.Frames;
                return (step.FromX + (step.X - step.FromX) * f, step.FromY + (step.Y - step.FromY) * f, step.Facing);
            }
        return s;
    }
}

/// <summary>
/// Runs movement phases on a map: every target starts where it stands (persons from map.json, the player where the user
/// put it), phases run one after another, the orders of one phase run at the same time. Collects warnings for unknown
/// actions, steps off the map and steps onto tiles whose collision bits are set.
/// </summary>
public static class MovementSimulator
{
    public const string PlayerKey = "PLAYER";

    /// <param name="resolve">Target text → actor (null = unknown target).</param>
    /// <param name="grid">Blockdata for bounds/collision checks (optional).</param>
    /// <param name="bystanders">Persons that stand still unless they move (for "ends on the same tile" checks).</param>
    public static SimulationResult Simulate(IReadOnlyList<MovementPhase> phases, Func<string, ActorStart?> resolve, MapGrid? grid = null,
        IEnumerable<ActorStart>? bystanders = null, IReadOnlySet<string>? knownActions = null)
    {
        var result = new SimulationResult();
        var state = new Dictionary<string, (int X, int Y, Facing F, bool Locked)>();
        var original = new Dictionary<string, Facing>();
        foreach (var b in bystanders ?? [])
            if (!state.ContainsKey(b.Key))
            {
                state[b.Key] = (b.X, b.Y, b.Facing, false);
                original[b.Key] = b.Facing;
            }

        ActorStart? Get(string target)
        {
            var a = resolve(target);
            if (a == null) return null;
            if (!result.Actors.ContainsKey(a.Key)) result.Actors[a.Key] = a;
            if (!state.ContainsKey(a.Key))
            {
                state[a.Key] = (a.X, a.Y, a.Facing, false);
                original[a.Key] = a.Facing;
            }
            return a;
        }

        int frame = 0;
        for (int pi = 0; pi < phases.Count; pi++)
        {
            var phase = phases[pi];
            var sp = new SimPhase { Index = pi, Caption = phase.Caption, StartFrame = frame };
            result.Phases.Add(sp);
            // face_player looks at where the player stands when the phase starts.
            var player = Get("OBJ_EVENT_ID_PLAYER");
            var playerPos = player != null ? state[player.Key] : default;
            var seen = new HashSet<string>();
            // Actors that move in this phase change place; everyone else is an obstacle where they stand.
            var phaseKeys = phase.Orders.Select(o => resolve(o.Target)?.Key).OfType<string>().ToHashSet();
            foreach (var order in phase.Orders)
            {
                var actor = Get(order.Target);
                if (actor == null)
                {
                    result.Warnings.Add($"Phase {pi + 1}: \"{order.Target}\" is not a person of this map – its steps are not shown.");
                    continue;
                }
                var s = state[actor.Key];
                var track = new SimTrack
                {
                    Key = actor.Key, Name = actor.Name, Target = order.Target, IsPlayer = actor.IsPlayer, Phase = pi,
                    StartX = s.X, StartY = s.Y, StartFacing = s.F,
                };
                if (!seen.Add(actor.Key))
                    track.Warnings.Add($"gets two movements in phase {pi + 1}; in the game the second one replaces the first.");
                int t = frame;
                for (int i = 0; i < order.Steps.Count; i++)
                {
                    var info = MovementActions.Describe(order.Steps[i], knownActions);
                    int fx = s.X, fy = s.Y;
                    var facing = s.F;
                    string? warning = null;
                    switch (info.Special)
                    {
                        case "face_player" when player != null && !actor.IsPlayer:
                            facing = FacingExtensions.Towards(s.X, s.Y, playerPos.X, playerPos.Y, s.F); break;
                        case "face_away_player" when player != null && !actor.IsPlayer:
                            facing = FacingExtensions.Towards(s.X, s.Y, playerPos.X, playerPos.Y, s.F).Opposite(); break;
                        case "face_original": facing = original[actor.Key]; break;
                        case "lock_facing": s.Locked = true; break;
                        case "unlock_facing": s.Locked = false; break;
                    }
                    if (info.Kind == StepKind.Place)
                    {
                        s.X = info.Dx;
                        s.Y = info.Dy;
                    }
                    else
                    {
                        if (info.Facing is { } f && !(s.Locked && info.Moves)) facing = f;
                        s.X += info.Dx;
                        s.Y += info.Dy;
                    }
                    var label = $"step {i + 1} ({info.Name})";
                    if (info.Kind == StepKind.Unknown)
                        warning = $"{label}: unknown action – shown as standing still.";
                    else if (grid != null && (info.Moves || info.Kind == StepKind.Place) && !grid.InBounds(s.X, s.Y))
                        warning = $"{label}: leaves the map at ({s.X},{s.Y}).";
                    else if (grid != null && info.Moves && grid.Collision(s.X, s.Y) != 0)
                        warning = $"{label}: walks onto a blocked tile at ({s.X},{s.Y}).";
                    if (warning == null && info.Moves &&
                        state.FirstOrDefault(kv => !phaseKeys.Contains(kv.Key) && kv.Value.X == s.X && kv.Value.Y == s.Y).Key is { } hit)
                        warning = $"{label}: bumps into {NameOf(hit, result, bystanders)} at ({s.X},{s.Y}).";
                    if (warning != null) track.Warnings.Add(warning);
                    s.F = facing;
                    track.Steps.Add(new SimStep(i + 1, info.Name, info.Kind, fx, fy, s.X, s.Y, facing, t, info.Frames, warning));
                    t += info.Frames;
                }
                state[actor.Key] = s;
                sp.Tracks.Add(track);
                sp.Frames = Math.Max(sp.Frames, t - frame);
            }
            // Two actors on one tile after the phase (only for those that moved in it).
            foreach (var track in sp.Tracks.Where(tr => tr.Steps.Any(st => st.FromX != st.X || st.FromY != st.Y)))
            {
                var me = state[track.Key];
                var other = state.FirstOrDefault(kv => kv.Key != track.Key && kv.Value.X == me.X && kv.Value.Y == me.Y);
                if (other.Key != null)
                    track.Warnings.Add($"ends phase {pi + 1} on the same tile ({me.X},{me.Y}) as {NameOf(other.Key, result, bystanders)}.");
            }
            frame += sp.Frames;
        }
        return result;
    }

    private static string NameOf(string key, SimulationResult r, IEnumerable<ActorStart>? bystanders) =>
        r.Actors.TryGetValue(key, out var a) ? a.Name : bystanders?.FirstOrDefault(b => b.Key == key)?.Name ?? key;
}
