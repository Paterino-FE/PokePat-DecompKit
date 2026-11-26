using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using TilesetEditor.Core.Platinum;

namespace TilesetEditor.App.Platinum;

/// <summary>What the event fields need: the project, the events file and lists to choose from.</summary>
public sealed record EventEditContext(PlatinumProject Project, PlatinumJsonFile Events, IReadOnlyList<string> Flags, IReadOnlyList<string> Vars,
    IReadOnlyList<string>? ScriptLabels, Action Changed);

/// <summary>
/// The fields of one event (person, trainer, sign, hidden item, warp, trigger tile) with readable names and lists to choose
/// from – shared by Events & Dialogs and the map editor of the Maps module.
/// </summary>
public static class PlatinumEventEditor
{
    private static Brush Muted => (Brush)Application.Current.FindResource("MutedBrush");

    public static WrapPanel Fields(JsonObject node, EventEditContext c, bool narrow = false)
    {
        var p = c.Project;
        var row = new EventRowVm(node, c.Changed);
        var wrap = new WrapPanel();
        double wide = narrow ? 300 : 250, small = narrow ? 92 : 120;
        (string Label, IReadOnlyList<string>? Options, string Tip) Meta(string key) => key switch
        {
            "id" => ("Name (id)", null, "LOCALID_… of a person (scripts use it), WARP_… of a warp."),
            "graphics_id" => ("Looks like", p.ConstantList("object_events_gfx"), "The overworld sprite (Sprites module: add your own)."),
            "movement_type" => ("Movement", p.ConstantList("movement_types"), "How the person moves or where it looks."),
            "trainer_type" => ("Trainer", p.ConstantList("trainer_types"), "TRAINER_TYPE_NORMAL: a trainer who sees the player (range = sight)."),
            "hidden_flag" => ("Hidden while flag set", ["0", .. c.Flags], "The person is not there while this flag is set (0 = always there)."),
            "script" => ("Script", c.ScriptLabels, "The script label (or ScriptEntry number in older pokeplatinum; 0 = none)."),
            "initial_dir" => ("Faces", ["DIR_NORTH", "DIR_SOUTH", "DIR_WEST", "DIR_EAST"], "Direction at the start (0 north, 1 south, 2 west, 3 east in older versions)."),
            "movement_range_x" => ("Walk range x", null, "Tiles the person may walk left/right."),
            "movement_range_z" => ("Walk range z", null, "Tiles the person may walk up/down."),
            "x" => ("x (tile)", null, "Drag it on the map, or type."),
            "z" => ("z (tile)", null, "Drag it on the map, or type."),
            "y" => ("Height", null, "Height level (usually the same as the people around)."),
            "type" => ("Sign type", p.ConstantList("bg_event_types"), ""),
            "player_facing_dir" => ("Readable from", p.ConstantList("bg_event_dirs"), "The side the player must stand on."),
            "hidden_item" => ("Picked-up flag", c.Flags, ""),
            "dest_header_id" => ("Leads to map", p.MapHeaders.Select(h => h.Id).ToList(), ""),
            "dest_warp_id" => ("Arrive at warp", WarpTargets(p, row["dest_header_id"]), "The warp of the destination map the player arrives at."),
            "width" => ("Width (tiles)", null, ""),
            "length" => ("Length (tiles)", null, ""),
            "var" => ("Variable", c.Vars, "The trigger runs while this variable has the value next to it."),
            "value" => ("Value", null, ""),
            _ => (key, null, ""),
        };
        if (PlatinumMapScripts.TrainerOf(node) is { } trainerOf)
        {
            // Trainer and sight range (in "trainer": {…} or "script" + "data", whichever the file uses).
            var trainers = p.ConstantList("trainers").Where(t => t != "TRAINER_NONE").ToList();
            var tcell = new StackPanel { Width = narrow ? 300 : 280, Margin = new Thickness(0, 0, 10, 6) };
            tcell.Children.Add(new TextBlock { Text = "Trainer", FontSize = 11, Foreground = Muted });
            var tcb = Combo(trainers, trainerOf.Trainer);
            var scell = new StackPanel { Width = small, Margin = new Thickness(0, 0, 10, 6) };
            scell.Children.Add(new TextBlock { Text = "Sight (tiles)", FontSize = 11, Foreground = Muted, ToolTip = "How far the trainer sees the player (1–15)." });
            var sbox = new TextBox { Text = trainerOf.Sight.ToString() };
            void ApplyTrainer()
            {
                var t = tcb.Text.Trim().ToUpperInvariant();
                if (!trainers.Contains(t) || !int.TryParse(sbox.Text.Trim(), out var sight)) return;
                if (PlatinumMapScripts.TrainerOf(node) is { } n && n.Trainer == t && n.Sight == sight) return;
                PlatinumMapScripts.SetTrainer(c.Events, node, t, sight);
                c.Changed();
            }
            tcb.LostFocus += (_, _) => ApplyTrainer();
            tcb.SelectionChanged += (_, _) => { if (tcb.SelectedItem is string s) { tcb.Text = s; ApplyTrainer(); } };
            sbox.LostFocus += (_, _) => ApplyTrainer();
            tcell.Children.Add(tcb);
            scell.Children.Add(sbox);
            wrap.Children.Add(tcell);
            wrap.Children.Add(scell);
        }
        ComboBox? destWarp = null;
        foreach (var (key, value) in node.ToList())
        {
            if (value is JsonArray or JsonObject) continue;
            if (key == "script" && PlatinumMapScripts.TrainerOf(node) != null) continue;
            var (label, options, tip) = Meta(key);
            var cell = new StackPanel { Width = options != null ? wide : small, Margin = new Thickness(0, 0, 10, 6) };
            cell.Children.Add(new TextBlock { Text = label, FontSize = 11, Foreground = Muted, ToolTip = tip.Length > 0 ? tip : null });
            if (options != null)
            {
                var cb = Combo(options, row[key]);
                cb.ToolTip = tip.Length > 0 ? tip : null;
                cb.LostFocus += (_, _) => row[key] = cb.Text;
                cb.SelectionChanged += (_, _) => { if (cb.SelectedItem is string s) row[key] = s; };
                if (key == "dest_warp_id") destWarp = cb;
                if (key == "dest_header_id")
                    // Another destination: its warps become the choices for "Arrive at warp" (the first one if the old does not exist there).
                    cb.SelectionChanged += (_, _) =>
                    {
                        if (destWarp == null || cb.SelectedItem is not string dest || WarpTargets(p, dest) is not { } targets) return;
                        destWarp.ItemsSource = targets;
                        if (!targets.Contains(row["dest_warp_id"]) && targets.Count > 0) { destWarp.Text = targets[0]; row["dest_warp_id"] = targets[0]; }
                    };
                cell.Children.Add(cb);
            }
            else
            {
                var tb = new TextBox { Text = row[key], ToolTip = tip.Length > 0 ? tip : null };
                tb.LostFocus += (_, _) => row[key] = tb.Text;
                cell.Children.Add(tb);
            }
            wrap.Children.Add(cell);
        }
        return wrap;
    }

    private static ComboBox Combo(IReadOnlyList<string> items, string text)
    {
        var cb = new ComboBox { ItemsSource = items, IsEditable = true, IsTextSearchEnabled = true, Text = text, FontSize = 12 };
        cb.ItemsPanel = new ItemsPanelTemplate(new FrameworkElementFactory(typeof(VirtualizingStackPanel)));
        return cb;
    }

    /// <summary>The warps of the destination map as "dest_warp_id" names them (ids or numbers); null if unknown.</summary>
    private static IReadOnlyList<string>? WarpTargets(PlatinumProject p, string destHeader)
    {
        var h = p.MapHeaders.FirstOrDefault(x => x.Id == destHeader);
        if (h == null) return null;
        try { return PlatinumMapScripts.WarpTargets(p.Root, h).Select(t => t.Value).ToList(); }
        catch (Exception ex) when (ex is System.IO.IOException or System.Text.Json.JsonException or FormatException) { return null; }
    }
}
