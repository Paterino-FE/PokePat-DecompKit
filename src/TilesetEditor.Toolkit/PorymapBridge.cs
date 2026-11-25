using System.Text;

namespace TilesetEditor.Toolkit;

/// <summary>
/// Porymap → Toolkit hand-off. Porymap custom scripts (QJSEngine) can neither start processes nor write files,
/// but they can write to porymap.log via utility.log(). The bridge script reports the open project, map,
/// layout and tilesets there, and adds two actions to Porymap's Tools menu. The toolkit tails the log.
/// Message format (one line): "[TilesetToolkit] event=map;map=Route101;primary=gTileset_General;…",
/// values URI-encoded so separators cannot clash.
/// </summary>
public static class PorymapBridgeScript
{
    public const int Version = 1;
    public const string Tag = "[TilesetToolkit] ";
    public const string FileName = "tileset_toolkit_bridge.js";

    public static string DefaultScriptPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TilesetToolkit", FileName);

    /// <summary>
    /// Written in plain ES5 (plus the "export" keyword Porymap requires), so it also runs in older engines
    /// used by the tests. Uses only documented Porymap 6.x API: onProjectOpened, onMapOpened, onLayoutOpened,
    /// onTilesetUpdated, utility.registerAction, utility.log, utility.showMessage, map.getPrimaryTileset,
    /// map.getSecondaryTileset.
    /// </summary>
    public static string Content => $$"""
        // PokePat DecompKit – Porymap bridge (version {{Version}}, auto-generated).
        // Reports the open project, map, layout and tilesets to the PokePat DecompKit via porymap.log
        // and adds two entries to the "Tools" menu. The script does not write any project files.
        // To remove: in the toolkit under Project → Porymap bridge, or in Porymap under Options → Custom Scripts.

        var toolkitProject = "";
        var toolkitMap = "";
        var toolkitLayout = "";

        function toolkitSend(fields) {
            var parts = ["v={{Version}}", "project=" + encodeURIComponent(toolkitProject)];
            for (var key in fields) {
                if (fields.hasOwnProperty(key)) parts.push(key + "=" + encodeURIComponent(fields[key] === undefined || fields[key] === null ? "" : String(fields[key])));
            }
            utility.log("{{Tag}}" + parts.join(";"));
        }

        function toolkitTilesets(fields) {
            try {
                fields.primary = map.getPrimaryTileset();
                fields.secondary = map.getSecondaryTileset();
            } catch (e) {
                // no map/layout open yet
            }
            return fields;
        }

        export function onProjectOpened(projectPath) {
            toolkitProject = projectPath;
            utility.registerAction("toolkitShowMap", "PokePat DecompKit: Show map in toolkit");
            utility.registerAction("toolkitNewTileset", "PokePat DecompKit: New tileset for this map");
            toolkitSend({ event: "project" });
        }

        export function onProjectClosed(projectPath) {
            toolkitSend({ event: "closed" });
            toolkitProject = "";
        }

        export function onMapOpened(mapName) {
            toolkitMap = mapName;
            toolkitSend(toolkitTilesets({ event: "map", map: mapName, layout: toolkitLayout }));
        }

        export function onLayoutOpened(layoutName) {
            toolkitLayout = layoutName;
            toolkitSend(toolkitTilesets({ event: "layout", map: toolkitMap, layout: layoutName }));
        }

        export function onTilesetUpdated(tilesetName) {
            toolkitSend({ event: "tileset", tileset: tilesetName });
        }

        export function toolkitShowMap() {
            toolkitSend(toolkitTilesets({ event: "action", action: "show-map", map: toolkitMap, layout: toolkitLayout }));
            utility.showMessage("Sent to the PokePat DecompKit.", "The toolkit shows the map " + toolkitMap + " if it is open.");
        }

        export function toolkitNewTileset() {
            toolkitSend(toolkitTilesets({ event: "action", action: "new-tileset", map: toolkitMap, layout: toolkitLayout }));
            utility.showMessage("Sent to the PokePat DecompKit.", "The toolkit opens the tileset editor with this map's primary tileset.");
        }

        """;
}

/// <summary>A parsed bridge message from porymap.log.</summary>
public sealed record PorymapBridgeMessage(string Event, IReadOnlyDictionary<string, string> Fields)
{
    public string? Get(string key) => Fields.TryGetValue(key, out var v) && v.Length > 0 ? v : null;
    public string? Project => Get("project");
    public string? Map => Get("map");
    public string? Layout => Get("layout");
    public string? Primary => Get("primary");
    public string? Secondary => Get("secondary");
    public string? Action => Get("action");

    public static PorymapBridgeMessage? TryParse(string logLine)
    {
        int at = logLine.IndexOf(PorymapBridgeScript.Tag, StringComparison.Ordinal);
        if (at < 0) return null;
        var fields = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var part in logLine[(at + PorymapBridgeScript.Tag.Length)..].Trim().Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            int eq = part.IndexOf('=');
            if (eq <= 0) continue;
            try
            {
                fields[part[..eq]] = Uri.UnescapeDataString(part[(eq + 1)..]);
            }
            catch (UriFormatException)
            {
                fields[part[..eq]] = part[(eq + 1)..];
            }
        }
        return fields.TryGetValue("event", out var ev) ? new PorymapBridgeMessage(ev, fields) : null;
    }
}

/// <summary>Tails porymap.log (polling; the file is appended to by Porymap) and reports bridge messages.</summary>
public sealed class PorymapLogWatcher : IDisposable
{
    private readonly string _path;
    private readonly Timer _timer;
    private readonly object _gate = new();
    private long _position;
    private string _partial = "";
    private bool _disposed;

    public event Action<PorymapBridgeMessage>? MessageReceived;

    /// <summary>Porymap's log location (QStandardPaths::AppDataLocation with organization "pret", application "porymap").</summary>
    public static string DefaultLogPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "pret", "porymap", "porymap.log");

    public string LogPath => _path;

    /// <param name="startAtEnd">Ignore what is already in the log (old sessions).</param>
    public PorymapLogWatcher(string path, TimeSpan interval, bool startAtEnd = true)
    {
        _path = path;
        _position = startAtEnd && File.Exists(path) ? new FileInfo(path).Length : 0;
        _timer = new Timer(_ => Poll(), null, interval, interval);
    }

    /// <summary>Reads new log content once. Public for tests.</summary>
    public IReadOnlyList<PorymapBridgeMessage> Poll()
    {
        var found = new List<PorymapBridgeMessage>();
        lock (_gate)
        {
            if (_disposed || !File.Exists(_path)) return found;
            try
            {
                using var fs = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                if (fs.Length < _position)
                {
                    // Porymap truncates logs over 20 MB; start over.
                    _position = 0;
                    _partial = "";
                }
                if (fs.Length == _position) return found;
                fs.Position = _position;
                using var reader = new StreamReader(fs, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, leaveOpen: true);
                var text = _partial + reader.ReadToEnd();
                _position = fs.Length;
                int lastNewline = text.LastIndexOf('\n');
                _partial = lastNewline < 0 ? text : text[(lastNewline + 1)..];
                var complete = lastNewline < 0 ? "" : text[..lastNewline];
                foreach (var line in complete.Split('\n'))
                    if (PorymapBridgeMessage.TryParse(line.TrimEnd('\r')) is { } m) found.Add(m);
            }
            catch (IOException)
            {
                return found; // Porymap is writing; next poll
            }
        }
        foreach (var m in found) MessageReceived?.Invoke(m);
        return found;
    }

    public void Dispose()
    {
        lock (_gate) _disposed = true;
        _timer.Dispose();
    }
}

public sealed record BridgeInstallPlan(
    string UserConfigPath,
    string? UserConfigBefore,
    string UserConfigAfter,
    string ScriptPath,
    string ScriptContent,
    bool ScriptChanged)
{
    public bool UserConfigChanged => UserConfigBefore != UserConfigAfter;
    public bool NothingToDo => !UserConfigChanged && !ScriptChanged;
}

/// <summary>
/// Registers or removes the bridge script in the project's porymap.user.cfg (key "custom_scripts",
/// format "path:1,path:0,…" as parsed by Porymap's UserConfig::parseCustomScripts). The file is user-local
/// (the expansion's .gitignore excludes porymap.*.cfg). Porymap rewrites this file when it saves its settings,
/// so it should be closed while the entry is changed.
/// </summary>
public static class PorymapBridgeInstaller
{
    public const string UserConfigFile = "porymap.user.cfg";
    private const string Key = "custom_scripts";

    public static string UserConfigPath(string projectRoot) => Path.Combine(projectRoot, UserConfigFile);

    public static bool IsInstalled(string projectRoot, string scriptPath)
    {
        var p = UserConfigPath(projectRoot);
        if (!File.Exists(p)) return false;
        return ParseScripts(ReadKey(File.ReadAllText(p))).Any(s => SamePath(s.Path, scriptPath) && s.Enabled);
    }

    public static bool ScriptIsCurrent(string scriptPath) =>
        File.Exists(scriptPath) && File.ReadAllText(scriptPath) == PorymapBridgeScript.Content;

    public static BridgeInstallPlan PlanInstall(string projectRoot, string? scriptPath = null)
    {
        scriptPath ??= PorymapBridgeScript.DefaultScriptPath;
        if (scriptPath.Contains(','))
            throw new ArgumentException("The script path must not contain a comma (Porymap separates the list with commas).");
        var cfg = UserConfigPath(projectRoot);
        string? before = File.Exists(cfg) ? File.ReadAllText(cfg) : null;
        var scripts = ParseScripts(ReadKey(before ?? "")).Where(s => !SamePath(s.Path, scriptPath)).ToList();
        scripts.Add((scriptPath, true));
        var after = WriteKey(before ?? "", Format(scripts));
        return new BridgeInstallPlan(cfg, before, after, scriptPath, PorymapBridgeScript.Content, !ScriptIsCurrent(scriptPath));
    }

    public static BridgeInstallPlan PlanUninstall(string projectRoot, string? scriptPath = null)
    {
        scriptPath ??= PorymapBridgeScript.DefaultScriptPath;
        var cfg = UserConfigPath(projectRoot);
        string? before = File.Exists(cfg) ? File.ReadAllText(cfg) : null;
        if (before == null) return new BridgeInstallPlan(cfg, null, "", scriptPath, "", false) { };
        var scripts = ParseScripts(ReadKey(before)).Where(s => !SamePath(s.Path, scriptPath)).ToList();
        return new BridgeInstallPlan(cfg, before, WriteKey(before, Format(scripts)), scriptPath, "", false);
    }

    /// <summary>Writes the plan. The previous porymap.user.cfg is kept as porymap.user.cfg.toolkit-backup.</summary>
    public static void Apply(BridgeInstallPlan plan)
    {
        if (plan.ScriptChanged)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(plan.ScriptPath)!);
            WriteAtomic(plan.ScriptPath, plan.ScriptContent);
        }
        if (plan.UserConfigChanged && !(plan.UserConfigBefore == null && plan.UserConfigAfter.Length == 0))
        {
            if (plan.UserConfigBefore != null)
                File.WriteAllText(plan.UserConfigPath + ".toolkit-backup", plan.UserConfigBefore);
            WriteAtomic(plan.UserConfigPath, plan.UserConfigAfter);
        }
    }

    private static void WriteAtomic(string path, string text)
    {
        var tmp = path + ".tileseteditor-tmp";
        File.WriteAllText(tmp, text, new UTF8Encoding(false));
        File.Move(tmp, path, overwrite: true);
    }

    public static string ReadKey(string cfg)
    {
        foreach (var line in cfg.Split('\n'))
        {
            var l = line.TrimEnd('\r');
            int eq = l.IndexOf('=');
            if (eq > 0 && l[..eq].Trim() == Key) return l[(eq + 1)..].Trim();
        }
        return "";
    }

    /// <summary>Same rules as Porymap: comma separated, optional ":0"/":1" suffix, default enabled.</summary>
    public static List<(string Path, bool Enabled)> ParseScripts(string value)
    {
        var list = new List<(string, bool)>();
        foreach (var raw in value.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            var p = raw;
            bool enabled = !p.EndsWith(":0", StringComparison.Ordinal);
            if (!enabled || p.EndsWith(":1", StringComparison.Ordinal)) p = p[..^2];
            if (p.Length > 0) list.Add((p, enabled));
        }
        return list;
    }

    private static string Format(IEnumerable<(string Path, bool Enabled)> scripts) =>
        string.Join(",", scripts.Select(s => $"{s.Path}:{(s.Enabled ? 1 : 0)}"));

    /// <summary>Replaces (or adds/removes) the custom_scripts line and keeps everything else, including line endings.</summary>
    public static string WriteKey(string cfg, string value)
    {
        string nl = cfg.Contains("\r\n") ? "\r\n" : "\n";
        var lines = cfg.Length == 0 ? new List<string>() : cfg.Replace("\r\n", "\n").Split('\n').ToList();
        if (lines.Count > 0 && lines[^1].Length == 0) lines.RemoveAt(lines.Count - 1);
        int idx = lines.FindIndex(l => l.IndexOf('=') is var eq && eq > 0 && l[..eq].Trim() == Key);
        if (value.Length == 0)
        {
            if (idx >= 0) lines.RemoveAt(idx);
        }
        else if (idx >= 0) lines[idx] = $"{Key}={value}";
        else lines.Add($"{Key}={value}");
        return lines.Count == 0 ? "" : string.Join(nl, lines) + nl;
    }

    private static bool SamePath(string a, string b) => PorymapLauncher.SamePath(a, b);
}
