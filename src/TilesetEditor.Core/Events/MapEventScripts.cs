using System.Text;
using System.Text.Json;

namespace TilesetEditor.Core.Events;

public enum MapEventKind
{
    Object,
    Coord,
    Bg,
}

/// <summary>An event on a map that can run a script (person, trigger tile, sign).</summary>
public sealed record MapEventSlot(MapEventKind Kind, int Index, int X, int Y, string What, string Script)
{
    public string ArrayName => Kind switch
    {
        MapEventKind.Object => "object_events",
        MapEventKind.Coord => "coord_events",
        _ => "bg_events",
    };

    public string Display => Kind switch
    {
        MapEventKind.Object => $"Person #{Index + 1} ({X},{Y}) {What} – script: {Script}",
        MapEventKind.Coord => $"Trigger #{Index + 1} ({X},{Y}) – script: {Script}",
        _ => $"Sign #{Index + 1} ({X},{Y}) {What} – script: {Script}",
    };

    public override string ToString() => Display;
}

/// <summary>
/// Reads the script slots of a map.json and changes exactly one "script" value, leaving every other byte as
/// Porymap wrote it (no re-serialization).
/// </summary>
public static class MapEventScripts
{
    public static List<MapEventSlot> Read(string mapJson)
    {
        var result = new List<MapEventSlot>();
        using var doc = JsonDocument.Parse(mapJson);
        void Collect(string array, MapEventKind kind)
        {
            if (!doc.RootElement.TryGetProperty(array, out var arr) || arr.ValueKind != JsonValueKind.Array) return;
            int i = 0;
            foreach (var e in arr.EnumerateArray())
            {
                string S(string k) => e.TryGetProperty(k, out var v) ? v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : v.ToString() : "";
                int I(string k) => e.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : 0;
                string what = kind == MapEventKind.Object ? S("graphics_id").Replace("OBJ_EVENT_GFX_", "") : kind == MapEventKind.Bg ? S("type") : "";
                // Only slots that have a script field can be assigned (hidden items etc. have none).
                if (e.TryGetProperty("script", out _))
                    result.Add(new MapEventSlot(kind, i, I("x"), I("y"), what, S("script")));
                i++;
            }
        }
        Collect("object_events", MapEventKind.Object);
        Collect("coord_events", MapEventKind.Coord);
        Collect("bg_events", MapEventKind.Bg);
        return result;
    }

    public const int MaxObjectEvents = 64; // OBJECT_EVENT_TEMPLATES_COUNT

    /// <summary>Default values for keys of a new object event (Porymap's defaults for pokeemerald).</summary>
    private static readonly (string Key, object Value)[] ObjectDefaults =
    [
        ("graphics_id", "OBJ_EVENT_GFX_BOY_1"), ("x", 0), ("y", 0), ("elevation", 3), ("movement_type", "MOVEMENT_TYPE_FACE_DOWN"),
        ("movement_range_x", 0), ("movement_range_y", 0), ("trainer_type", "TRAINER_TYPE_NONE"), ("trainer_sight_or_berry_tree_id", "0"),
        ("script", "0x0"), ("flag", "0"),
    ];

    /// <summary>
    /// Appends a person (object event) to map.json. Key order and indentation follow the existing entries so the
    /// result looks as if Porymap had written it; all other bytes stay unchanged.
    /// </summary>
    public static string AddObjectEvent(string mapJson, IReadOnlyDictionary<string, object> values) =>
        AddEvent(mapJson, "object_events", ObjectDefaults, MaxObjectEvents, "people", values);

    private static readonly (string Key, object Value)[] SignDefaults =
    [
        ("type", "sign"), ("x", 0), ("y", 0), ("elevation", 0), ("player_facing_dir", "BG_EVENT_PLAYER_FACING_ANY"), ("script", "0x0"),
    ];

    private static readonly (string Key, object Value)[] TriggerDefaults =
    [
        ("type", "trigger"), ("x", 0), ("y", 0), ("elevation", 3), ("var", "VAR_TEMP_1"), ("var_value", "0"), ("script", "0x0"),
    ];

    /// <summary>Appends a sign (bg_events, type "sign"). Signs are read when the player faces them and presses A.</summary>
    public static string AddSign(string mapJson, IReadOnlyDictionary<string, object> values) =>
        AddEvent(mapJson, "bg_events", SignDefaults, 255, "signs/background events", values);

    /// <summary>Appends a trigger tile (coord_events). It fires when the player steps on it while var == var_value.</summary>
    public static string AddTrigger(string mapJson, IReadOnlyDictionary<string, object> values) =>
        AddEvent(mapJson, "coord_events", TriggerDefaults, 255, "trigger tiles", values);

    private static readonly (string Key, object Value)[] SecretBaseDefaults =
    [
        ("type", "secret_base"), ("x", 0), ("y", 0), ("elevation", 0), ("secret_base_id", "0"),
    ];

    /// <summary>Appends a secret base entrance (bg_events, type "secret_base"): Secret Power on this tile opens the base with the given id.</summary>
    public static string AddSecretBase(string mapJson, int x, int y, int elevation, string secretBaseId) =>
        AddEvent(mapJson, "bg_events", SecretBaseDefaults, 255, "signs/background events",
            new Dictionary<string, object> { ["x"] = x, ["y"] = y, ["elevation"] = elevation, ["secret_base_id"] = secretBaseId });

    private static string AddEvent(string mapJson, string arrayName, (string Key, object Value)[] defaults, int max, string what,
        IReadOnlyDictionary<string, object> values)
    {
        List<string>? sameTypeKeys = null;
        using (var doc = JsonDocument.Parse(mapJson))
        {
            if (doc.RootElement.TryGetProperty(arrayName, out var arr) && arr.ValueKind == JsonValueKind.Array)
            {
                if (arr.GetArrayLength() >= max) throw new InvalidOperationException($"The map already has {max} {what} (maximum).");
                var type = defaults.FirstOrDefault(d => d.Key == "type").Value as string;
                foreach (var e in arr.EnumerateArray())
                {
                    if (type != null && !(e.TryGetProperty("type", out var t) && t.GetString() == type)) continue;
                    sameTypeKeys = e.EnumerateObject().Select(p => p.Name).ToList();
                    break;
                }
            }
        }
        string nl = mapJson.Contains("\r\n") ? "\r\n" : "\n";
        var bytes = Encoding.UTF8.GetBytes(mapJson);
        var reader = new Utf8JsonReader(bytes);
        int depth = 0, arrayDepth = -1;
        long arrayStart = -1, lastObjectEnd = -1, firstObjectStart = -1, propertyStart = -1;
        bool expect = false;
        List<string>? templateKeys = null;
        var currentKeys = new List<string>();
        while (reader.Read())
        {
            switch (reader.TokenType)
            {
                case JsonTokenType.PropertyName:
                    if (depth == 1 && reader.ValueTextEquals(arrayName)) { expect = true; propertyStart = reader.TokenStartIndex; }
                    else if (arrayDepth >= 0 && depth == arrayDepth + 1) currentKeys.Add(reader.GetString()!);
                    break;
                case JsonTokenType.StartArray:
                    depth++;
                    if (expect) { arrayDepth = depth; arrayStart = reader.TokenStartIndex; expect = false; }
                    break;
                case JsonTokenType.StartObject:
                    depth++;
                    if (arrayDepth >= 0 && depth == arrayDepth + 1)
                    {
                        if (firstObjectStart < 0) firstObjectStart = reader.TokenStartIndex;
                        currentKeys = [];
                    }
                    break;
                case JsonTokenType.EndObject:
                    if (arrayDepth >= 0 && depth == arrayDepth + 1)
                    {
                        lastObjectEnd = reader.TokenStartIndex;
                        templateKeys ??= currentKeys;
                    }
                    depth--;
                    break;
                case JsonTokenType.EndArray:
                    if (depth == arrayDepth)
                    {
                        long arrayEnd = reader.TokenStartIndex;
                        string text = mapJson;
                        string LineIndent(long byteIndex)
                        {
                            int charIndex = Encoding.UTF8.GetCharCount(bytes, 0, (int)byteIndex);
                            int lineStart = text.LastIndexOf('\n', Math.Max(0, charIndex - 1)) + 1;
                            int i = lineStart;
                            while (i < text.Length && (text[i] == ' ' || text[i] == '\t')) i++;
                            return text[lineStart..i];
                        }
                        string baseIndent = LineIndent(propertyStart);
                        string unit = baseIndent.Length > 0 ? baseIndent : "  ";
                        string objIndent = firstObjectStart >= 0 ? LineIndent(firstObjectStart) : baseIndent + unit;
                        string fieldIndent = objIndent + unit;

                        // Template: keys of the first existing entry of the same type (signs and hidden items share bg_events).
                        var keys = sameTypeKeys ?? defaults.Select(d => d.Key).ToList();
                        _ = templateKeys;
                        foreach (var k in values.Keys) if (!keys.Contains(k)) keys.Add(k);
                        var sb = new StringBuilder();
                        sb.Append(objIndent).Append('{').Append(nl);
                        for (int i = 0; i < keys.Count; i++)
                        {
                            var key = keys[i];
                            object v = values.TryGetValue(key, out var given) ? given : defaults.FirstOrDefault(d => d.Key == key).Value ?? "0";
                            var json = v is int or long ? Convert.ToString(v, System.Globalization.CultureInfo.InvariantCulture)! : JsonSerializer.Serialize(v.ToString());
                            sb.Append(fieldIndent).Append(JsonSerializer.Serialize(key)).Append(": ").Append(json).Append(i < keys.Count - 1 ? "," : "").Append(nl);
                        }
                        sb.Append(objIndent).Append('}');

                        string Prefix(long byteIndex) => text[..Encoding.UTF8.GetCharCount(bytes, 0, (int)byteIndex)];
                        string Suffix(long byteIndex) => text[Encoding.UTF8.GetCharCount(bytes, 0, (int)byteIndex)..];
                        if (lastObjectEnd >= 0)
                            return Prefix(lastObjectEnd + 1) + "," + nl + sb + Suffix(lastObjectEnd + 1);
                        // Empty array "[]": open it up.
                        return Prefix(arrayStart + 1) + nl + sb + nl + baseIndent + Suffix(arrayEnd);
                    }
                    depth--;
                    break;
            }
        }
        throw new InvalidOperationException($"\"{arrayName}\" was not found in map.json.");
    }

    /// <summary>Returns the JSON with the script of one event replaced; throws if the slot cannot be located exactly.</summary>
    public static string AssignScript(string mapJson, MapEventKind kind, int index, string label) =>
        SetStringField(mapJson, kind, index, "script", label);

    /// <summary>Replaces one string field of one event (e.g. "flag" of a person); every other byte stays unchanged.</summary>
    public static string SetStringField(string mapJson, MapEventKind kind, int index, string field, string value)
    {
        var array = new MapEventSlot(kind, index, 0, 0, "", "").ArrayName;
        var label = value;
        var bytes = Encoding.UTF8.GetBytes(mapJson);
        var reader = new Utf8JsonReader(bytes, new JsonReaderOptions { CommentHandling = JsonCommentHandling.Skip });
        int depth = 0, arrayDepth = -1, element = -1;
        bool expectArray = false;
        while (reader.Read())
        {
            switch (reader.TokenType)
            {
                case JsonTokenType.StartObject:
                    depth++;
                    if (arrayDepth >= 0 && depth == arrayDepth + 1) element++;
                    break;
                case JsonTokenType.EndObject:
                    depth--;
                    break;
                case JsonTokenType.StartArray:
                    depth++;
                    if (expectArray) { arrayDepth = depth; expectArray = false; }
                    break;
                case JsonTokenType.EndArray:
                    if (depth == arrayDepth) arrayDepth = -1;
                    depth--;
                    break;
                case JsonTokenType.PropertyName:
                    if (depth == 1 && reader.ValueTextEquals(array)) expectArray = true;
                    else if (arrayDepth >= 0 && depth == arrayDepth + 1 && element == index && reader.ValueTextEquals(field))
                    {
                        reader.Read();
                        if (reader.TokenType != JsonTokenType.String)
                            throw new InvalidOperationException($"The field \"{field}\" is not text.");
                        int start = (int)reader.TokenStartIndex; // position of the opening quote
                        int length = reader.ValueSpan.Length + 2;  // escaped content + both quotes
                        var replacement = JsonSerializer.Serialize(label);
                        var result = new byte[bytes.Length - length + Encoding.UTF8.GetByteCount(replacement)];
                        bytes.AsSpan(0, start).CopyTo(result);
                        int n = Encoding.UTF8.GetBytes(replacement, result.AsSpan(start));
                        bytes.AsSpan(start + length).CopyTo(result.AsSpan(start + n));
                        return Encoding.UTF8.GetString(result);
                    }
                    break;
            }
        }
        throw new InvalidOperationException($"{array}[{index}] with field \"{field}\" was not found in map.json.");
    }
}
