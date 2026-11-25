using System.Text.Json;
using System.Text.Json.Serialization;

namespace TilesetEditor.Core.Events;

public enum EventTrigger
{
    /// <summary>Talking to a person/object (object_events). Uses lock/faceplayer … release.</summary>
    Person,
    /// <summary>Reading a sign (bg_events). Uses lockall … releaseall.</summary>
    Sign,
    /// <summary>Stepping on a trigger tile (coord_events). Uses lockall … releaseall.</summary>
    Trigger,
}

/// <summary>A building block of an event. Serialized with a "type" field.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(MessageStep), "message")]
[JsonDerivedType(typeof(YesNoStep), "yesno")]
[JsonDerivedType(typeof(IfFlagStep), "ifflag")]
[JsonDerivedType(typeof(SetFlagStep), "setflag")]
[JsonDerivedType(typeof(ClearFlagStep), "clearflag")]
[JsonDerivedType(typeof(GiveItemStep), "giveitem")]
[JsonDerivedType(typeof(CommandStep), "command")]
public abstract class EventStep
{
    [JsonIgnore] public abstract string Title { get; }
    [JsonIgnore] public abstract string Summary { get; }

    /// <summary>Nested step lists (branches) with their captions.</summary>
    public virtual IEnumerable<(string Caption, List<EventStep> Steps)> Branches() => [];
}

public sealed class MessageStep : EventStep
{
    public string Text { get; set; } = "";
    public override string Title => "Message";
    public override string Summary => Short(Text);

    internal static string Short(string s)
    {
        s = s.Replace("\r", "").Replace('\n', ' ').Trim();
        return s.Length > 60 ? s[..57] + "…" : s.Length == 0 ? "(empty)" : s;
    }
}

public sealed class YesNoStep : EventStep
{
    public string Question { get; set; } = "";
    public List<EventStep> Yes { get; set; } = [];
    public List<EventStep> No { get; set; } = [];
    public override string Title => "Yes/No question";
    public override string Summary => MessageStep.Short(Question);
    public override IEnumerable<(string, List<EventStep>)> Branches() => [("If YES", Yes), ("If NO", No)];
}

public sealed class IfFlagStep : EventStep
{
    public string Flag { get; set; } = "";
    public List<EventStep> Then { get; set; } = [];
    public List<EventStep> Else { get; set; } = [];
    public override string Title => "If flag set";
    public override string Summary => Flag.Length == 0 ? "(choose flag)" : Flag;
    public override IEnumerable<(string, List<EventStep>)> Branches() => [("Then (flag set)", Then), ("Else (not set)", Else)];
}

public sealed class SetFlagStep : EventStep
{
    public string Flag { get; set; } = "";
    public override string Title => "Set flag";
    public override string Summary => Flag.Length == 0 ? "(choose flag)" : Flag;
}

public sealed class ClearFlagStep : EventStep
{
    public string Flag { get; set; } = "";
    public override string Title => "Clear flag";
    public override string Summary => Flag.Length == 0 ? "(choose flag)" : Flag;
}

public sealed class GiveItemStep : EventStep
{
    public string Item { get; set; } = "ITEM_POTION";
    public int Count { get; set; } = 1;
    public override string Title => "Give item";
    public override string Summary => $"{Item} × {Count}";
}

/// <summary>A block from <see cref="BlockCatalog"/> (trainer battle, warp, movement, any script command, …).</summary>
public sealed class CommandStep : EventStep
{
    public string Block { get; set; } = "";
    public Dictionary<string, string> Args { get; set; } = [];
    public List<EventStep> Then { get; set; } = [];
    public List<EventStep> Else { get; set; } = [];
    /// <summary>Menu blocks: one step list per option (same order as the options).</summary>
    public List<List<EventStep>> Cases { get; set; } = [];

    [JsonIgnore] public BlockDef? Def => BlockCatalog.Find(Block);

    /// <summary>Makes sure there is one case list per menu option (options can be edited).</summary>
    public IReadOnlyList<(string Value, string Caption, List<EventStep> Steps)> CaseBranches()
    {
        if (Def is not { Cases: { } cases }) return [];
        var list = cases(new BlockArgs(Def, Args));
        while (Cases.Count < list.Count) Cases.Add([]);
        return list.Select((c, i) => (c.Value, c.Caption, Cases[i])).ToList();
    }
    public override string Title => Def?.Title ?? $"Unknown block {Block}";
    public override string Summary => Def is { } d ? MessageStep.Short(d.Summary(new BlockArgs(d, Args))) : "";

    public override IEnumerable<(string, List<EventStep>)> Branches() =>
        Def is { IsBranch: true } d ? [(d.ThenCaption, Then), (d.ElseCaption, Else)]
        : Def is { IsSwitch: true } ? CaseBranches().Select(c => (c.Caption, c.Steps)).ToList()
        : [];

    public static CommandStep Create(BlockDef def) => new()
    {
        Block = def.Id,
        Args = def.Params.Where(p => p.Default.Length > 0).ToDictionary(p => p.Key, p => p.Default),
    };
}

/// <summary>One toolkit-managed event script.</summary>
public sealed class ToolkitEvent
{
    /// <summary>Assembly label, e.g. Route101_TK_OldMan. Referenced from map.json "script" fields.</summary>
    public string Label { get; set; } = "";
    public string Map { get; set; } = "";
    public string Title { get; set; } = "";
    public EventTrigger Trigger { get; set; } = EventTrigger.Person;
    public List<EventStep> Steps { get; set; } = [];
}

/// <summary>Contents of data/scripts/toolkit_events.json – the source of truth for generated scripts.</summary>
public sealed class ToolkitEventDocument
{
    public int Version { get; set; } = 1;
    public List<ToolkitEvent> Events { get; set; } = [];

    public static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        IgnoreReadOnlyProperties = true, // Title/Summary are display-only
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        Converters = { new JsonStringEnumConverter() },
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public string Serialize() => JsonSerializer.Serialize(this, Json).Replace("\r\n", "\n") + "\n";

    public static ToolkitEventDocument Parse(string json)
    {
        var doc = JsonSerializer.Deserialize<ToolkitEventDocument>(json, Json) ?? new ToolkitEventDocument();
        foreach (var e in doc.Events) MigrateLegacyValues(e.Steps);
        return doc;
    }

    /// <summary>Older versions stored the menu block's "cancel" choice in German; map it to the current values.</summary>
    private static void MigrateLegacyValues(List<EventStep> steps)
    {
        foreach (var s in steps)
        {
            if (s is CommandStep c)
            {
                if (c.Block == "menu" && c.Args.TryGetValue("cancel", out var v))
                    c.Args["cancel"] = v switch { "erlaubt" => "allowed", "nicht erlaubt" => "not allowed", _ => v };
                MigrateLegacyValues(c.Then);
                MigrateLegacyValues(c.Else);
                foreach (var cs in c.Cases) MigrateLegacyValues(cs);
            }
            else foreach (var (_, branch) in s.Branches()) MigrateLegacyValues(branch);
        }
    }

    public ToolkitEventDocument Clone() => Parse(Serialize());
}
