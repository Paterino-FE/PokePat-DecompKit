using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using TilesetEditor.Core.Project;

namespace TilesetEditor.Export.Mods;

/// <summary>A file the mod brings: copied from <see cref="From"/> (inside the mod folder) to <see cref="To"/> (in the project).</summary>
public sealed record ModFile(string From, string To);

/// <summary>
/// A change to an existing project file. <see cref="Type"/>: "insert_after" / "insert_before" (<see cref="Anchor"/> must occur
/// exactly once), "append" (at the end of the file) or "replace" (<see cref="Anchor"/> → <see cref="Text"/>). Newlines are
/// written in the file's own style.
/// </summary>
public sealed record ModEdit(string File, string Type, string Anchor, string Text)
{
    /// <summary>
    /// Other forms of the same change for other versions of the base project (e.g. a file that moved). The first form whose
    /// file exists and whose anchor fits is used; the installation record keeps the form that was applied.
    /// </summary>
    public List<ModEdit>? Alt { get; init; }

    [JsonIgnore]
    public IEnumerable<ModEdit> Forms => Alt is { Count: > 0 } ? new[] { this with { Alt = null } }.Concat(Alt) : [this];
}

/// <summary>
/// A value of an installed mod the user can change in PokePat: the number in <see cref="File"/> between <see cref="Before"/>
/// and <see cref="After"/> (both must occur exactly once around it).
/// </summary>
public sealed record ModSetting(string Label, string File, string Before, string After, int Min = 0, int Max = 255, string Hint = "")
{
    /// <summary>"number" (default), "name" (a text written as pokeplatinum character codes CHAR_x, …) or "choice" (one of <see cref="Choices"/>).</summary>
    public string Kind { get; init; } = "number";
    public List<string>? Choices { get; init; }
    /// <summary>For "name": the longest name the game stores.</summary>
    public int MaxLength { get; init; } = 7;
}

/// <summary>mod.json of a PokePat mod.</summary>
public sealed class ModManifest
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Version { get; set; } = "1.0.0";
    public string Author { get; set; } = "";
    public string Description { get; set; } = "";
    public string Category { get; set; } = "Gameplay";
    /// <summary>The game the mod is made for: "emerald" (pokeemerald-expansion, default) or "platinum" (pokeplatinum).</summary>
    public string Game { get; set; } = "emerald";
    public GameKind GameKind => GameKinds.Parse(Game);
    /// <summary>Settings PokePat lets the user change after installing (e.g. the level of each cap), see <see cref="ModSetting"/>.</summary>
    public List<ModSetting> Settings { get; set; } = [];
    /// <summary>Lowest / highest pokeemerald-expansion version the mod was made for (inclusive), e.g. "1.10.0".</summary>
    public string? MinExpansion { get; set; }
    public string? MaxExpansion { get; set; }
    public List<ModFile> Files { get; set; } = [];
    public List<ModEdit> Edits { get; set; } = [];
    /// <summary>How to use the mod after installing (shown in PokePat).</summary>
    public string Usage { get; set; } = "";
    /// <summary>
    /// Folders whose content belongs to the mod although PokePat adds to it later (e.g. the mugshot pictures): every file in
    /// them is removed when the mod is switched off.
    /// </summary>
    public List<string> OwnedFolders { get; set; } = [];
}

/// <summary>A mod found on disk.</summary>
public sealed record ModPackage(string Folder, ModManifest Manifest)
{
    public const string ManifestFile = "mod.json";

    public static ModPackage Load(string folder)
    {
        var text = File.ReadAllText(Path.Combine(folder, ManifestFile));
        var manifest = JsonSerializer.Deserialize<ModManifest>(text, ModRegistry.Json) ?? throw new InvalidDataException($"{ManifestFile} is empty.");
        if (string.IsNullOrWhiteSpace(manifest.Id)) throw new InvalidDataException($"{ManifestFile} has no id.");
        return new ModPackage(folder, manifest);
    }

    /// <summary>Every mod (a folder with mod.json) directly inside the given folders; broken ones are reported, not thrown.</summary>
    public static (List<ModPackage> Mods, List<string> Problems) LoadAll(IEnumerable<string> folders)
    {
        var mods = new List<ModPackage>();
        var problems = new List<string>();
        foreach (var dir in folders.Where(Directory.Exists))
            foreach (var sub in Directory.EnumerateDirectories(dir).OrderBy(d => d, StringComparer.OrdinalIgnoreCase))
            {
                if (!File.Exists(Path.Combine(sub, ManifestFile))) continue;
                try
                {
                    var m = Load(sub);
                    if (mods.All(x => x.Manifest.Id != m.Manifest.Id)) mods.Add(m);
                }
                catch (Exception ex) when (ex is IOException or JsonException or InvalidDataException)
                {
                    problems.Add($"{Path.GetFileName(sub)}: {ex.Message}");
                }
            }
        return (mods, problems);
    }
}

/// <summary>What was installed, so it can be removed again: kept in &lt;project&gt;/.pokepat/mods/&lt;id&gt;.json.</summary>
public sealed class InstalledMod
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Version { get; set; } = "";
    public DateTime InstalledAt { get; set; }
    /// <summary>Created files with the hash they had when installed.</summary>
    public Dictionary<string, string> Files { get; set; } = [];
    public List<ModEdit> Edits { get; set; } = [];
    public List<string> OwnedFolders { get; set; } = [];
    /// <summary>The mod's changes were found in the project without an installation record (added by hand or by an older PokePat).</summary>
    public bool Detected { get; set; }
}

public sealed record ModCheck(bool CanInstall, IReadOnlyList<string> Problems, InstalledMod? Installed);

public static class ModRegistry
{
    public const string Folder = ".pokepat/mods";

    public static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static string RecordPath(string id) => $"{Folder}/{id}.json";

    public static List<InstalledMod> Installed(string root)
    {
        var dir = ProjectPaths.Combine(root, Folder);
        if (!Directory.Exists(dir)) return [];
        var result = new List<InstalledMod>();
        foreach (var f in Directory.EnumerateFiles(dir, "*.json"))
        {
            try { if (JsonSerializer.Deserialize<InstalledMod>(File.ReadAllText(f), Json) is { } m) result.Add(m); }
            catch (JsonException) { }
        }
        return result;
    }
}

/// <summary>
/// Installs and removes PokePat mods as file plans (shown first, backed up, all-or-nothing). A mod is compatible when its
/// version range fits and every edit finds its anchor exactly once; installed edits are recorded and undone exactly.
/// </summary>
public static class ModPlanner
{
    public static ModCheck Check(string root, ModPackage mod, string? expansionVersion)
    {
        var problems = new List<string>();
        var installed = ModRegistry.Installed(root).FirstOrDefault(m => m.Id == mod.Manifest.Id) ?? DetectApplied(root, mod);
        var m = mod.Manifest;
        if (expansionVersion != null && Version.TryParse(expansionVersion, out var v))
        {
            if (m.MinExpansion != null && Version.TryParse(m.MinExpansion, out var min) && v < min)
                problems.Add($"Needs pokeemerald-expansion {m.MinExpansion} or newer (the project is {expansionVersion}).");
            if (m.MaxExpansion != null && Version.TryParse(m.MaxExpansion, out var max) && v > max)
                problems.Add($"Made for pokeemerald-expansion up to {m.MaxExpansion} (the project is {expansionVersion}) – it may not fit.");
        }
        if (installed == null)
        {
            foreach (var f in m.Files)
            {
                if (!File.Exists(Path.Combine(mod.Folder, f.From))) problems.Add($"The mod is incomplete: {f.From} is missing.");
                if (File.Exists(ProjectPaths.Combine(root, f.To))) problems.Add($"{f.To} already exists in the project.");
            }
            try { Resolve(root, m.Edits); }
            catch (ExportValidationException ex) { problems.AddRange(ex.Errors); }
        }
        return new ModCheck(installed == null && problems.Count == 0, problems, installed);
    }

    public static ExportPlan PlanInstall(string root, ModPackage mod, string? expansionVersion)
    {
        var check = Check(root, mod, expansionVersion);
        if (check.Installed != null) throw new ExportValidationException([$"{mod.Manifest.Name} is already installed."]);
        if (!check.CanInstall) throw new ExportValidationException(check.Problems);
        var m = mod.Manifest;
        var files = new List<PlannedFile>();
        var edits = Resolve(root, m.Edits);
        var record = new InstalledMod { Id = m.Id, Name = m.Name, Version = m.Version, InstalledAt = DateTime.Now, Edits = edits, OwnedFolders = m.OwnedFolders };
        foreach (var f in m.Files)
        {
            var bytes = File.ReadAllBytes(Path.Combine(mod.Folder, f.From));
            files.Add(new PlannedFile(ProjectPaths.Normalize(f.To), PlannedAction.Create, bytes, $"{m.Name}: new file"));
            record.Files[ProjectPaths.Normalize(f.To)] = Hashing.Sha256(bytes);
        }
        foreach (var group in edits.GroupBy(e => ProjectPaths.Normalize(e.File)))
        {
            var full = ProjectPaths.Combine(root, group.Key);
            var original = File.ReadAllBytes(full);
            var text = Encoding.UTF8.GetString(original);
            foreach (var e in group) text = ApplyEdit(text, e);
            files.Add(new PlannedFile(group.Key, PlannedAction.Modify, new UTF8Encoding(false).GetBytes(text),
                $"{m.Name}: {group.Count()} change(s)", Hashing.Sha256(original)));
        }
        files.Add(new PlannedFile(ModRegistry.RecordPath(m.Id), PlannedAction.Create,
            Encoding.UTF8.GetBytes(JsonSerializer.Serialize(record, ModRegistry.Json) + "\n"), "What was installed (used to uninstall)"));
        var plan = ExportPlan.ForFiles(root, $"Switch on: {m.Name}", ".pokepat", files);
        if (m.Usage.Length > 0) plan.Notes.Add(m.Usage);
        plan.Notes.Add("Build the ROM afterwards. \"Switch off\" in the Mods module restores the original files again.");
        return plan;
    }

    /// <summary>The uninstall plan and the parts that cannot be undone automatically (changed since installing).</summary>
    public static (ExportPlan Plan, List<string> Kept) PlanUninstall(string root, InstalledMod mod)
    {
        var files = new List<PlannedFile>();
        var kept = new List<string>();
        // Content the user added to the mod's own folders later (e.g. pictures) goes with it.
        foreach (var folder in mod.OwnedFolders)
        {
            var dir = ProjectPaths.Combine(root, folder);
            if (!Directory.Exists(dir)) continue;
            foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
            {
                var rel = ProjectPaths.Normalize(Path.GetRelativePath(root, f));
                if (mod.Files.ContainsKey(rel)) continue;
                var b = File.ReadAllBytes(f);
                files.Add(new PlannedFile(rel, PlannedAction.Delete, [], "Remove (added to the mod later)", Hashing.Sha256(b)));
            }
        }
        foreach (var (rel, hash) in mod.Files)
        {
            var full = ProjectPaths.Combine(root, rel);
            if (!File.Exists(full)) continue;
            var bytes = File.ReadAllBytes(full);
            // Files the mod manages for the user (e.g. the portrait list) change after installing: removed as well.
            files.Add(new PlannedFile(rel, PlannedAction.Delete, [], Hashing.Sha256(bytes) == hash ? "Remove" : "Remove (was changed after installing)",
                Hashing.Sha256(bytes)));
        }
        foreach (var group in mod.Edits.GroupBy(e => ProjectPaths.Normalize(e.File)))
        {
            var full = ProjectPaths.Combine(root, group.Key);
            if (!File.Exists(full)) { kept.Add($"{group.Key} no longer exists."); continue; }
            var original = File.ReadAllBytes(full);
            var text = Encoding.UTF8.GetString(original);
            foreach (var e in group.Reverse())
            {
                try { text = UndoEdit(text, e); }
                catch (InvalidOperationException ex) { kept.Add(ex.Message); }
            }
            files.Add(new PlannedFile(group.Key, PlannedAction.Modify, new UTF8Encoding(false).GetBytes(text), $"Undo {mod.Name}", Hashing.Sha256(original)));
        }
        var record = ProjectPaths.Combine(root, ModRegistry.RecordPath(mod.Id));
        if (File.Exists(record))
            files.Add(new PlannedFile(ModRegistry.RecordPath(mod.Id), PlannedAction.Delete, [], "Installation record", Hashing.Sha256(File.ReadAllBytes(record))));
        var plan = ExportPlan.ForFiles(root, $"Switch off: {mod.Name}", ".pokepat", files);
        plan.Notes.Add("Scripts that still use the mod (e.g. its specials or constants) stop building – remove those uses as well.");
        foreach (var k in kept) plan.Notes.Add("Not undone automatically: " + k);
        return (plan, kept);
    }

    // ---------------------------------------------------------------- settings of installed mods

    private static (int Start, int Length)? SettingSpan(string text, ModSetting s)
    {
        int b = text.IndexOf(s.Before, StringComparison.Ordinal);
        if (b < 0 || text.IndexOf(s.Before, b + 1, StringComparison.Ordinal) >= 0) return null;
        int start = b + s.Before.Length;
        int end = text.IndexOf(s.After, start, StringComparison.Ordinal);
        if (end < 0) return null;
        var value = text[start..end].Trim();
        if (!int.TryParse(value, out _)) return null;
        return (start, end - start);
    }

    /// <summary>The current value of a setting in the project, or null if it cannot be found.</summary>
    public static int? ReadSetting(string root, ModSetting s) => int.TryParse(ReadSettingText(root, s), out var v) ? v : null;

    /// <summary>The current value as shown to the user (number, name or choice), or null.</summary>
    public static string? ReadSettingText(string root, ModSetting s)
    {
        var full = ProjectPaths.Combine(root, s.File);
        if (!File.Exists(full)) return null;
        var text = File.ReadAllText(full);
        var span = RawSpan(text, s);
        if (span == null) return null;
        var raw = text.Substring(span.Value.Start, span.Value.Length);
        return s.Kind == "name" ? CharcodesToText(raw) : raw.Trim();
    }

    private static (int Start, int Length)? RawSpan(string text, ModSetting s)
    {
        if (s.Kind == "number") return SettingSpan(text, s);
        int b = text.IndexOf(s.Before, StringComparison.Ordinal);
        if (b < 0 || text.IndexOf(s.Before, b + 1, StringComparison.Ordinal) >= 0) return null;
        int start = b + s.Before.Length;
        int end = text.IndexOf(s.After, start, StringComparison.Ordinal);
        return end < 0 ? null : (start, end - start);
    }

    private static readonly Dictionary<char, string> Punctuation = new()
    {
        [' '] = "SPACE", ['.'] = "PERIOD", [','] = "COMMA", ['!'] = "EXCLAMATION", ['?'] = "QUESTION", ['-'] = "MINUS", ['+'] = "PLUS",
        ['&'] = "AMPERSAND", ['/'] = "SLASH", [':'] = "COLON", ['\''] = "SINGLE_QUOTE_CLOSE", ['♂'] = "MALE", ['♀'] = "FEMALE", ['…'] = "ELLIPSIS",
    };

    /// <summary>"Reya" → "CHAR_R, CHAR_e, CHAR_y, CHAR_a, " (letters, digits and some punctuation).</summary>
    public static string TextToCharcodes(string name)
    {
        var sb = new StringBuilder();
        foreach (var c in name)
        {
            string code = c is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9' ? c.ToString()
                : Punctuation.TryGetValue(c, out var p) ? p
                : throw new InvalidOperationException($"\"{c}\" cannot be used in a name here (letters, digits, space and . , ! ? - + & / : ' ♂ ♀ … are possible).");
            sb.Append("CHAR_").Append(code).Append(", ");
        }
        return sb.ToString();
    }

    public static string CharcodesToText(string raw)
    {
        var sb = new StringBuilder();
        foreach (var token in raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!token.StartsWith("CHAR_", StringComparison.Ordinal)) continue;
            var code = token[5..];
            if (code == "EOS") break;
            if (code.Length == 1) sb.Append(code);
            else if (Punctuation.FirstOrDefault(p => p.Value == code) is { Value: not null } p) sb.Append(p.Key);
            else sb.Append('?');
        }
        return sb.ToString();
    }

    /// <summary>The file text with the setting changed (number, name or choice), checked against its limits.</summary>
    public static string WriteSettingText(string text, ModSetting s, string value)
    {
        value = value.Trim();
        switch (s.Kind)
        {
            case "number":
                if (!int.TryParse(value, out var n)) throw new InvalidOperationException($"{s.Label}: \"{value}\" is not a number.");
                return WriteSetting(text, s, n);
            case "name":
                if (value.Length == 0) throw new InvalidOperationException($"{s.Label}: the name is empty.");
                if (value.Length > s.MaxLength) throw new InvalidOperationException($"{s.Label}: at most {s.MaxLength} characters fit.");
                break;
            case "choice":
                if (s.Choices is { Count: > 0 } && !s.Choices.Contains(value)) throw new InvalidOperationException($"{s.Label}: choose one of {string.Join(", ", s.Choices)}.");
                break;
        }
        var span = RawSpan(text, s) ?? throw new InvalidOperationException($"{s.Label}: the value was not found in {s.File} (was the file changed by hand?).");
        var raw = s.Kind == "name" ? TextToCharcodes(value) : value;
        return text[..span.Start] + raw + text[(span.Start + span.Length)..];
    }

    /// <summary>The file text with the setting's number replaced.</summary>
    public static string WriteSetting(string text, ModSetting s, int value)
    {
        if (value < s.Min || value > s.Max) throw new InvalidOperationException($"{s.Label}: {value} is not between {s.Min} and {s.Max}.");
        var span = SettingSpan(text, s) ?? throw new InvalidOperationException($"{s.Label}: the value was not found in {s.File} (was the file changed by hand?).");
        var old = text.Substring(span.Start, span.Length);
        var lead = old[..(old.Length - old.TrimStart().Length)];
        var trail = old[old.TrimEnd().Length..];
        // Keep the column width ("{ BADGE_ID_COAL,   14 }") when the new number is shorter.
        var number = value.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (number.Length < old.Trim().Length) number = number.PadLeft(old.Trim().Length);
        return text[..span.Start] + lead + number + trail + text[(span.Start + span.Length)..];
    }

    /// <summary>
    /// The form of every edit that fits the project (applied one after the other, so later edits see earlier ones).
    /// Throws with one readable problem per edit that fits in no form.
    /// </summary>
    /// <summary>A file the build writes itself: its first lines say "Generated …" and "DO NOT EDIT".</summary>
    public static bool IsGenerated(string? text)
    {
        if (text == null) return false;
        var head = text.Length > 300 ? text[..300] : text;
        return head.Contains("DO NOT EDIT", StringComparison.OrdinalIgnoreCase) && head.Contains("Generated", StringComparison.OrdinalIgnoreCase);
    }

    public static List<ModEdit> Resolve(string root, IReadOnlyList<ModEdit> edits)
    {
        var texts = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        string? Text(string file)
        {
            if (!texts.TryGetValue(file, out var t))
            {
                var full = ProjectPaths.Combine(root, file);
                texts[file] = t = File.Exists(full) ? File.ReadAllText(full) : null;
            }
            return t;
        }
        var result = new List<ModEdit>();
        var problems = new List<string>();
        foreach (var e in edits)
        {
            string? problem = null;
            ModEdit? chosen = null;
            // A file the build makes from another one ("DO NOT EDIT", e.g. src/meson.build from platinum.us/main.lsf.in in
            // newer pokeplatinum) would lose the change on the next fresh build: an alternative that edits the source comes first.
            var forms = e.Forms.OrderBy(f => IsGenerated(Text(f.File)) ? 1 : 0).ToList();
            foreach (var form in forms)
            {
                var t = Text(form.File);
                if (t == null) { problem ??= $"{form.File} does not exist in the project."; continue; }
                try
                {
                    texts[form.File] = ApplyEdit(t, form);
                    chosen = form;
                    break;
                }
                catch (InvalidOperationException ex) { problem = ex.Message; }
            }
            if (chosen != null) result.Add(chosen);
            else problems.Add(e.Forms.Count() > 1
                ? $"{problem} (checked {string.Join(", ", e.Forms.Select(f => f.File).Distinct())} – this version of the project differs from the ones the mod knows)"
                : problem ?? $"{e.File}: the change does not fit.");
        }
        if (problems.Count > 0) throw new ExportValidationException(problems);
        return result;
    }

    /// <summary>
    /// The mod's changes when they are all present in the project although it has no installation record – so a mod
    /// added by hand (or before PokePat kept records) can still be switched off. Null when anything is missing.
    /// </summary>
    public static InstalledMod? DetectApplied(string root, ModPackage mod)
    {
        var m = mod.Manifest;
        if (m.Edits.Count == 0 && m.Files.Count == 0) return null;
        var record = new InstalledMod { Id = m.Id, Name = m.Name, Version = m.Version, Edits = m.Edits, OwnedFolders = m.OwnedFolders, Detected = true };
        foreach (var f in m.Files)
        {
            var full = ProjectPaths.Combine(root, f.To);
            var src = Path.Combine(mod.Folder, f.From);
            if (!File.Exists(full) || !File.Exists(src)) return null;
            record.Files[ProjectPaths.Normalize(f.To)] = Hashing.Sha256(File.ReadAllBytes(full));
        }
        var found = new List<ModEdit>();
        foreach (var e in m.Edits)
        {
            ModEdit? match = null;
            foreach (var form in e.Forms)
            {
                var full = ProjectPaths.Combine(root, form.File);
                if (!File.Exists(full)) continue;
                try { UndoEdit(File.ReadAllText(full), form); match = form; break; }
                catch (InvalidOperationException) { }
            }
            if (match == null) return null;
            found.Add(match);
        }
        record.Edits = found;
        return record;
    }

    private static string Nl(string text) => text.Contains("\r\n") ? "\r\n" : "\n";
    private static string InStyle(string s, string nl) => s.Replace("\r\n", "\n").Replace("\n", nl);

    private static int Once(string text, string anchor, string file)
    {
        int i = text.IndexOf(anchor, StringComparison.Ordinal);
        if (i < 0) throw new InvalidOperationException($"{file}: the place to change was not found (the file differs from what the mod expects).");
        if (text.IndexOf(anchor, i + 1, StringComparison.Ordinal) >= 0) throw new InvalidOperationException($"{file}: the place to change occurs more than once.");
        return i;
    }

    public static string ApplyEdit(string text, ModEdit e)
    {
        var nl = Nl(text);
        var anchor = InStyle(e.Anchor, nl);
        var insert = InStyle(e.Text, nl);
        switch (e.Type)
        {
            case "insert_after": { int i = Once(text, anchor, e.File) + anchor.Length; return text[..i] + insert + text[i..]; }
            case "insert_before": { int i = Once(text, anchor, e.File); return text[..i] + insert + text[i..]; }
            case "replace": { int i = Once(text, anchor, e.File); return text[..i] + insert + text[(i + anchor.Length)..]; }
            case "append": return (text.Length > 0 && !text.EndsWith(nl, StringComparison.Ordinal) ? text + nl : text) + insert;
            default: throw new InvalidOperationException($"{e.File}: unknown change type \"{e.Type}\".");
        }
    }

    public static string UndoEdit(string text, ModEdit e)
    {
        var nl = Nl(text);
        var anchor = InStyle(e.Anchor, nl);
        var insert = InStyle(e.Text, nl);
        string Undo(string done, string before)
        {
            int i = Once(text, done, e.File);
            return text[..i] + before + text[(i + done.Length)..];
        }
        return e.Type switch
        {
            "insert_after" => Undo(anchor + insert, anchor),
            "insert_before" => Undo(insert + anchor, anchor),
            "replace" => Undo(insert, anchor),
            "append" => text.EndsWith(insert, StringComparison.Ordinal) ? text[..^insert.Length]
                : throw new InvalidOperationException($"{e.File}: the added text is no longer at the end of the file."),
            _ => text,
        };
    }
}
