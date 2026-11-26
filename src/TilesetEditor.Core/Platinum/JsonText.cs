using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace TilesetEditor.Core.Platinum;

/// <summary>Where a value sits in a JSON text: character offset and length of its raw token (strings with quotes, arrays with brackets).</summary>
public sealed record JsonSpan(string Path, int Start, int Length, JsonValueKind Kind, string Raw);

/// <summary>
/// Reads and changes pokeplatinum's JSON files without reformatting them: values are located by path
/// ("base_stats.hp", "types[0]", "messages[3].en_US") and only their raw text is replaced, so a change shows up in git
/// as exactly the changed values.
/// </summary>
public static class JsonText
{
    private static readonly UTF8Encoding Utf8 = new(false);

    /// <summary>Every value of the document by path (objects and arrays included, root = "").</summary>
    public static Dictionary<string, JsonSpan> Locate(string text)
    {
        var bytes = Utf8.GetBytes(text);
        // Byte offset → char offset (the files contain ’, é, Japanese …).
        var charAt = new int[bytes.Length + 1];
        {
            int c = 0, b = 0;
            while (b < bytes.Length)
            {
                charAt[b] = c;
                int len = (bytes[b] & 0x80) == 0 ? 1 : (bytes[b] & 0xE0) == 0xC0 ? 2 : (bytes[b] & 0xF0) == 0xE0 ? 3 : 4;
                for (int k = 1; k < len && b + k < bytes.Length; k++) charAt[b + k] = c;
                b += len;
                c += len == 4 ? 2 : 1; // outside the BMP: a surrogate pair in .NET strings
            }
            charAt[bytes.Length] = c;
        }

        var result = new Dictionary<string, JsonSpan>(StringComparer.Ordinal);
        var reader = new Utf8JsonReader(bytes, new JsonReaderOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        // Stack of containers: path, kind, next array index, start byte.
        var stack = new Stack<(string Path, bool IsArray, int Index, long Start)>();
        string? pendingName = null;

        string NextPath()
        {
            if (stack.Count == 0) return "";
            var top = stack.Pop();
            string p;
            if (top.IsArray)
            {
                p = $"{top.Path}[{top.Index}]";
                top.Index++;
            }
            else p = top.Path.Length == 0 ? pendingName! : top.Path + "." + pendingName;
            stack.Push(top);
            return p;
        }

        while (reader.Read())
        {
            switch (reader.TokenType)
            {
                case JsonTokenType.PropertyName:
                    pendingName = reader.GetString();
                    break;
                case JsonTokenType.StartObject:
                case JsonTokenType.StartArray:
                    stack.Push((NextPath(), reader.TokenType == JsonTokenType.StartArray, 0, reader.TokenStartIndex));
                    break;
                case JsonTokenType.EndObject:
                case JsonTokenType.EndArray:
                {
                    var c = stack.Pop();
                    long end = reader.TokenStartIndex + 1;
                    int s = charAt[c.Start], e = charAt[end];
                    result[c.Path] = new JsonSpan(c.Path, s, e - s, c.IsArray ? JsonValueKind.Array : JsonValueKind.Object, text[s..e]);
                    break;
                }
                default:
                {
                    var p = NextPath();
                    long start = reader.TokenStartIndex;
                    long end = start + (reader.HasValueSequence ? reader.ValueSequence.Length : reader.ValueSpan.Length)
                               + (reader.TokenType == JsonTokenType.String ? 2 : 0);
                    int s = charAt[start], e = charAt[end];
                    var kind = reader.TokenType switch
                    {
                        JsonTokenType.String => JsonValueKind.String,
                        JsonTokenType.Number => JsonValueKind.Number,
                        JsonTokenType.True => JsonValueKind.True,
                        JsonTokenType.False => JsonValueKind.False,
                        _ => JsonValueKind.Null,
                    };
                    result[p] = new JsonSpan(p, s, e - s, kind, text[s..e]);
                    break;
                }
            }
        }
        return result;
    }

    /// <summary>The value at a path as shown to the user: strings unquoted, numbers/true/false/null as written; null if missing.</summary>
    public static string? Display(Dictionary<string, JsonSpan> spans, string path)
    {
        if (!spans.TryGetValue(path, out var s)) return null;
        return s.Kind switch
        {
            JsonValueKind.String => JsonSerializer.Deserialize<string>(s.Raw) ?? "",
            JsonValueKind.Array or JsonValueKind.Object => s.Raw,
            _ => s.Raw,
        };
    }

    /// <summary>Replaces raw tokens; the spans must come from <paramref name="text"/> and must not overlap.</summary>
    public static string Replace(string text, IEnumerable<(JsonSpan Span, string NewRaw)> changes)
    {
        var sb = new StringBuilder(text);
        foreach (var (span, raw) in changes.OrderByDescending(c => c.Span.Start))
        {
            if (text.Substring(span.Start, span.Length) != span.Raw)
                throw new InvalidOperationException($"The JSON value '{span.Path}' changed while it was being edited.");
            sb.Remove(span.Start, span.Length).Insert(span.Start, raw);
        }
        return sb.ToString();
    }

    /// <summary>The indentation of the line the span starts in (to format a replaced array like its neighbours).</summary>
    public static string IndentAt(string text, int offset)
    {
        int lineStart = text.LastIndexOf('\n', Math.Max(0, offset - 1)) + 1;
        int i = lineStart;
        while (i < text.Length && text[i] is ' ' or '\t') i++;
        return text[lineStart..i];
    }

    /// <summary>A JSON string literal the way Python's json.dumps(ensure_ascii=False) writes it.</summary>
    public static string Quote(string s)
    {
        var sb = new StringBuilder(s.Length + 2).Append('"');
        foreach (var ch in s)
        {
            switch (ch)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                case '\b': sb.Append("\\b"); break;
                case '\f': sb.Append("\\f"); break;
                default:
                    if (ch < 0x20) sb.Append("\\u").Append(((int)ch).ToString("x4", CultureInfo.InvariantCulture));
                    else sb.Append(ch);
                    break;
            }
        }
        return sb.Append('"').ToString();
    }

    /// <summary>
    /// Serializes like Python's json.dumps(value, indent=n, ensure_ascii=False) – the format of pokeplatinum's trainer, item
    /// and move files – so rewriting a whole file changes only what was edited. Numbers keep their written form.
    /// </summary>
    public static string Dump(JsonNode? node, int indent, string baseIndent = "")
    {
        var sb = new StringBuilder();
        Write(sb, node, indent, baseIndent);
        return sb.ToString();
    }

    private static void Write(StringBuilder sb, JsonNode? node, int indent, string current)
    {
        switch (node)
        {
            case null:
                sb.Append("null");
                return;
            case JsonObject obj:
            {
                if (obj.Count == 0) { sb.Append("{}"); return; }
                var inner = current + new string(' ', indent);
                sb.Append("{\n");
                int i = 0;
                foreach (var (key, value) in obj)
                {
                    sb.Append(inner).Append(Quote(key)).Append(": ");
                    Write(sb, value, indent, inner);
                    if (++i < obj.Count) sb.Append(',');
                    sb.Append('\n');
                }
                sb.Append(current).Append('}');
                return;
            }
            case JsonArray arr:
            {
                if (arr.Count == 0) { sb.Append("[]"); return; }
                var inner = current + new string(' ', indent);
                sb.Append("[\n");
                for (int i = 0; i < arr.Count; i++)
                {
                    sb.Append(inner);
                    Write(sb, arr[i], indent, inner);
                    if (i + 1 < arr.Count) sb.Append(',');
                    sb.Append('\n');
                }
                sb.Append(current).Append(']');
                return;
            }
            case JsonValue v:
            {
                if (!v.TryGetValue<JsonElement>(out var el))
                {
                    // Values created in code (JsonValue.Create).
                    if (v.TryGetValue<string>(out var str)) sb.Append(Quote(str));
                    else if (v.TryGetValue<bool>(out var b)) sb.Append(b ? "true" : "false");
                    else sb.Append(v.ToJsonString());
                    return;
                }
                switch (el.ValueKind)
                {
                    case JsonValueKind.String: sb.Append(Quote(el.GetString()!)); break;
                    case JsonValueKind.True: sb.Append("true"); break;
                    case JsonValueKind.False: sb.Append("false"); break;
                    case JsonValueKind.Null: sb.Append("null"); break;
                    default: sb.Append(el.GetRawText()); break;
                }
                return;
            }
        }
    }

    /// <summary>Parses keeping numbers as written (JsonElement-backed values), for <see cref="Dump"/>.</summary>
    public static JsonNode? Parse(string text) =>
        JsonNode.Parse(text, documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });

    /// <summary>A value node from a string, number text, true/false/null (as the user typed it).</summary>
    public static JsonNode? Value(string kindHint, string value) => kindHint switch
    {
        "number" => JsonNode.Parse(value),
        "bool" => JsonValue.Create(value == "true"),
        _ => JsonValue.Create(value),
    };

    /// <summary>Reads a string array whose elements are lines ("…\n") as one text; real line breaks become the two characters \n.</summary>
    public static string JoinLines(JsonElement array) =>
        ToEscapes(string.Concat(array.EnumerateArray().Select(e => e.ValueKind == JsonValueKind.String ? e.GetString() : "")));

    /// <summary>Real control characters → the escapes shown in the editor (\n, \r, \f).</summary>
    public static string ToEscapes(string s) => s.Replace("\\", "\\\\").Replace("\n", "\\n").Replace("\r", "\\r").Replace("\f", "\\f");

    /// <summary>Editor escapes → real characters.</summary>
    public static string FromEscapes(string s)
    {
        var sb = new StringBuilder(s.Length);
        for (int i = 0; i < s.Length; i++)
        {
            if (s[i] == '\\' && i + 1 < s.Length)
            {
                char n = s[++i];
                sb.Append(n switch { 'n' => '\n', 'r' => '\r', 'f' => '\f', _ => n });
            }
            else sb.Append(s[i]);
        }
        return sb.ToString();
    }

    /// <summary>Splits a text back into the line elements pokeplatinum uses: each ends after \n, \r or \f.</summary>
    public static List<string> SplitLines(string real)
    {
        var lines = new List<string>();
        var cur = new StringBuilder();
        foreach (var ch in real)
        {
            cur.Append(ch);
            if (ch is '\n' or '\r' or '\f') { lines.Add(cur.ToString()); cur.Clear(); }
        }
        if (cur.Length > 0 || lines.Count == 0) lines.Add(cur.ToString());
        return lines;
    }

    /// <summary>A string array written over several lines, its elements indented one level deeper than <paramref name="indent"/>.</summary>
    public static string LinesArray(IReadOnlyList<string> lines, string indent, int step)
    {
        var inner = indent + new string(' ', step);
        return "[\n" + string.Join(",\n", lines.Select(l => inner + Quote(l))) + "\n" + indent + "]";
    }
}
