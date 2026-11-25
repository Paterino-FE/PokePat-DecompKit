using System.Text;
using System.Text.RegularExpressions;
using TilesetEditor.Core.Events;
using TilesetEditor.Core.Project;

namespace TilesetEditor.Export;

public sealed record ScriptAssignment(string Map, MapEventKind Kind, int Index, string Label);

/// <summary>
/// Plans writing the toolkit-managed events. Touches only:
/// data/scripts/toolkit_events.{json,pory,inc}, one include line in data/event_scripts.s (added once) and,
/// for assignments, the "script" value of single events in data/maps/&lt;Map&gt;/map.json.
/// Hand-written map scripts are never modified.
/// </summary>
public static partial class EventsPlanner
{
    public const string JsonFile = "data/scripts/toolkit_events.json";
    public const string PoryFile = "data/scripts/toolkit_events.pory";
    public const string IncFile = "data/scripts/toolkit_events.inc";
    public const string EventScriptsFile = "data/event_scripts.s";
    public const string IncludeLine = "\t.include \"data/scripts/toolkit_events.inc\"";

    public static ToolkitEventDocument LoadDocument(string projectRoot)
    {
        var p = ProjectPaths.Combine(projectRoot, JsonFile);
        return File.Exists(p) ? ToolkitEventDocument.Parse(File.ReadAllText(p)) : new ToolkitEventDocument();
    }

    /// <summary>False if the generated .pory was edited by hand since the toolkit wrote it.</summary>
    public static bool GeneratedFileIsUnmodified(string projectRoot)
    {
        var p = ProjectPaths.Combine(projectRoot, PoryFile);
        return !File.Exists(p) || PoryscriptGenerator.IsUnmodified(File.ReadAllText(p));
    }

    public static ExportPlan Plan(string projectRoot, ToolkitEventDocument doc, string poryText, string incText,
        IReadOnlyList<ScriptAssignment> assignments, bool overwriteHandEdits = false)
    {
        if (!overwriteHandEdits && !GeneratedFileIsUnmodified(projectRoot))
            throw new ExportValidationException([$"{PoryFile} was edited by hand. The toolkit only overwrites the file after explicit confirmation."]);

        var files = new List<PlannedFile?>
        {
            ExportPlan.CreateOrModify(projectRoot, JsonFile, Encoding.UTF8.GetBytes(doc.Serialize()), $"Source of the toolkit events ({doc.Events.Count} events)"),
            ExportPlan.CreateOrModify(projectRoot, PoryFile, Encoding.UTF8.GetBytes(poryText), "Generated Poryscript"),
            ExportPlan.CreateOrModify(projectRoot, IncFile, Encoding.UTF8.GetBytes(incText), "Compiled by Poryscript (as in the build)"),
        };

        var es = ProjectPaths.Combine(projectRoot, EventScriptsFile);
        if (!File.Exists(es)) throw new ExportValidationException([$"{EventScriptsFile} is missing."]);
        var esText = File.ReadAllText(es);
        if (!esText.Contains("data/scripts/toolkit_events.inc", StringComparison.Ordinal))
        {
            string nl = esText.Contains("\r\n") ? "\r\n" : "\n";
            var sb = new StringBuilder(esText);
            if (!esText.EndsWith('\n')) sb.Append(nl);
            sb.Append(nl).Append(IncludeLine).Append(nl);
            files.Add(new PlannedFile(EventScriptsFile, PlannedAction.Modify, Encoding.UTF8.GetBytes(sb.ToString()),
                "Append one line: .include of the toolkit events", Hashing.Sha256(File.ReadAllBytes(es))));
        }

        foreach (var group in assignments.GroupBy(a => a.Map))
        {
            var rel = $"data/maps/{group.Key}/map.json";
            var full = ProjectPaths.Combine(projectRoot, rel);
            if (!File.Exists(full)) throw new ExportValidationException([$"{rel} is missing."]);
            var original = File.ReadAllBytes(full);
            var text = Encoding.UTF8.GetString(original);
            foreach (var a in group) text = MapEventScripts.AssignScript(text, a.Kind, a.Index, a.Label);
            files.Add(new PlannedFile(rel, PlannedAction.Modify, new UTF8Encoding(false).GetBytes(text),
                "Assign script: " + string.Join(", ", group.Select(a => $"{a.Kind} #{a.Index + 1} → {a.Label}")), Hashing.Sha256(original)));
        }

        var plan = ExportPlan.ForFiles(projectRoot, "Dialogs & Events", "data/scripts", files.Where(f => f != null).Cast<PlannedFile>().ToList());
        plan.Notes.Add("Handwritten map scripts (the maps' scripts.inc/.pory) are not changed.");
        plan.Notes.Add("The .inc was generated and checked with the project's Poryscript; make regenerates it identically when needed.");
        if (assignments.Count > 0) plan.Notes.Add("In map.json only the \"script\" value of the selected events is replaced – Porymap shows the assignment afterwards.");
        return plan;
    }

    [GeneratedRegex(@"^\s*([A-Za-z_]\w*)::?|^\s*script\s+([A-Za-z_]\w*)", RegexOptions.Multiline)]
    private static partial Regex LabelDefinition();

    /// <summary>Labels defined in the map scripts (for collision checks). Reads data/maps/*/scripts.{inc,pory}.</summary>
    public static HashSet<string> ExistingMapLabels(string projectRoot)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        var maps = ProjectPaths.Combine(projectRoot, "data/maps");
        if (!Directory.Exists(maps)) return set;
        foreach (var dir in Directory.EnumerateDirectories(maps))
            foreach (var name in new[] { "scripts.inc", "scripts.pory" })
            {
                var f = Path.Combine(dir, name);
                if (!File.Exists(f)) continue;
                foreach (Match m in LabelDefinition().Matches(File.ReadAllText(f)))
                    set.Add(m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value);
            }
        return set;
    }
}
