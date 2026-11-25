using System.Text;
using TilesetEditor.Core.Artwork;
using TilesetEditor.Core.Gba;
using TilesetEditor.Core.Imaging;
using TilesetEditor.Core.Project;

namespace TilesetEditor.Compiler;

public sealed class CompileRequest
{
    public required ProjectInfo Project { get; init; }
    public required TilesetInfo Primary { get; init; }
    public required Artwork Artwork { get; init; }
    /// <summary>Name of the new secondary, e.g. gTileset_MyTown.</summary>
    public required string TilesetName { get; init; }
    /// <summary>Base directory for sandboxes. Default: %LOCALAPPDATA%\TilesetEditor\work.</summary>
    public string? WorkRoot { get; init; }
}

public sealed class CompileResult
{
    public required bool Success { get; init; }
    public required string Log { get; init; }
    public required IReadOnlyList<PorytilesDiagnostic> Diagnostics { get; init; }
    public required IReadOnlyList<Issue> Issues { get; init; }
    public required string SandboxRoot { get; init; }
    public string? OutputDir { get; init; }
    public TilesetData? Compiled { get; init; }
    public TilesetData? Primary { get; init; }
    public PorytilesVersion? CompilerVersion { get; init; }
    public Rgba32 SandboxTransparency { get; init; }
    public string? TilesetName { get; init; }
}

/// <summary>
/// Compiles the artwork with Porytiles inside a throw-away copy of the relevant project files. The real
/// project is only read. The sandbox contains: the engine headers, the chosen primary (imported into
/// Porytiles so that tile and palette reuse works) and a new Porytiles-managed secondary.
/// </summary>
public sealed class SandboxCompiler(PorytilesClient client)
{
    private static readonly string[] CopiedHeaders =
    [
        ProjectLoader.FieldmapFile,
        ProjectLoader.GlobalFieldmapFile,
        "include/constants/metatile_behaviors.h",
    ];

    public static string DefaultWorkRoot =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TilesetEditor", "work");

    public async Task<CompileResult> CompileAsync(CompileRequest req, IProgress<string>? progress, CancellationToken ct)
    {
        var log = new StringBuilder();
        void Log(string s)
        {
            log.AppendLine(s);
            progress?.Report(s);
        }

        var project = req.Project;
        var primary = req.Primary;
        var art = req.Artwork;
        string workRoot = req.WorkRoot ?? DefaultWorkRoot;
        Directory.CreateDirectory(workRoot);
        CleanupOldSandboxes(workRoot, keep: 5);
        string sandbox = Path.Combine(workRoot, DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N")[..6], "project");
        Directory.CreateDirectory(sandbox);

        var diags = new List<PorytilesDiagnostic>();
        var issues = new List<Issue>();
        CompileResult Fail(string message, PorytilesVersion? v = null, Rgba32 transparency = default)
        {
            Log("ERROR: " + message);
            issues.Insert(0, new Issue(IssueSeverity.Error, "COMPILE", message, null, "Porytiles"));
            return new CompileResult
            {
                Success = false, Log = log.ToString(), Diagnostics = diags, Issues = issues, SandboxRoot = sandbox,
                CompilerVersion = v, SandboxTransparency = transparency, TilesetName = req.TilesetName,
            };
        }

        // 1. Compiler version
        Log("Checking Porytiles …");
        PorytilesVersion version;
        try
        {
            version = await client.GetVersionAsync(ct).ConfigureAwait(false);
        }
        catch (PorytilesUnavailableException ex)
        {
            return Fail(ex.Message);
        }
        Log($"Porytiles {version.Version} found.");
        if (!version.Matches(client.Settings.ExpectedVersion))
        {
            var warn = $"Porytiles {version.Version} differs from the pinned version {client.Settings.ExpectedVersion}. " +
                       "The output may differ; use the pinned version for reproducible builds.";
            Log("WARNING: " + warn);
            issues.Add(new Issue(IssueSeverity.Warning, "COMPILER_VERSION", warn, null, "Porytiles"));
        }

        if (!primary.IsFullyResolved)
            return Fail($"The files of the primary tileset {primary.Name} could not be fully resolved.");

        // 2. Sandbox files
        Log($"Creating sandbox in {sandbox}");
        TilesetData primaryData;
        Rgba32 transparency;
        try
        {
            foreach (var h in CopiedHeaders)
            {
                if (!File.Exists(project.FullPath(h))) continue;
                var dst = ProjectPaths.Combine(sandbox, h);
                Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
                File.Copy(project.FullPath(h), dst);
            }
            WriteText(sandbox, "src/graphics.c", "// TilesetEditor sandbox stub\n");
            WriteText(sandbox, "src/tileset_anims.c", "// TilesetEditor sandbox stub\n");
            WriteText(sandbox, "include/tileset_anims.h", "// TilesetEditor sandbox stub\n");
            WriteText(sandbox, ProjectLoader.LayoutsFile, "{\n  \"layouts_table_label\": \"gMapLayouts\",\n  \"layouts\": []\n}\n");
            WriteText(sandbox, "porytiles/config.yaml",
                $"# Generated by TilesetEditor\nfieldmap:\n  metatile_attribute_size: {project.Attributes.SizeBytes}\n");

            // Primary: copied to the directory Porytiles expects for its name; animations are left out on purpose
            // (only tiles and palettes are needed to reuse them from the secondary).
            string primaryDir = project.PrimaryTilesetsDir + TilesetNaming.ToFolderName(primary.Name);
            CopyFile(project.FullPath(primary.TilesImagePath!), sandbox, primaryDir + "/tiles.png");
            CopyFile(project.FullPath(primary.MetatilesPath!), sandbox, primaryDir + "/metatiles.bin");
            CopyFile(project.FullPath(primary.AttributesPath!), sandbox, primaryDir + "/metatile_attributes.bin");
            var palPaths = new List<string>();
            for (int i = 0; i < primary.PalettePaths.Count; i++)
            {
                var rel = $"{primaryDir}/palettes/{i:00}.pal";
                CopyFile(project.FullPath(primary.PalettePaths[i]), sandbox, rel);
                palPaths.Add(rel);
            }
            WriteRegistration(sandbox, project, primary.Name, primaryDir, primary.PalettePaths.Count);

            primaryData = project.LoadTilesetData(primary);
            transparency = ChooseTransparency(primaryData.Palettes, art.OpaqueColors());
            Log($"Transparency color for the sandbox: {transparency} (appears neither in the primary nor in the image).");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException or InvalidOperationException)
        {
            return Fail("Could not create sandbox: " + ex.Message);
        }

        string root = client.ToToolPath(sandbox);
        string transArg = $"{transparency.R},{transparency.G},{transparency.B}";

        async Task<ProcessResult?> Step(string title, params string[] args)
        {
            Log($"> porytiles {string.Join(' ', args)}");
            var r = await client.RunAsync(args, ct).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(r.Combined)) Log(r.Combined.TrimEnd());
            Log($"({title}: Exit {r.ExitCode}, {r.Duration.TotalSeconds:0.0}s)");
            return r;
        }

        // 3. Import the primary
        var import = await Step("Import primary", "import-tileset", primary.Name, "-C", root, "--extrinsic-transparency", transArg);
        if (import!.ExitCode != 0)
        {
            AddDiagnostics(import.Combined, diags, issues, null);
            return Fail(DescribeFailure($"The primary tileset {primary.Name} could not be imported into Porytiles.", import), version, transparency);
        }

        // 4. Create the managed secondary. Pairing is off here: Porytiles seeds sample art that would otherwise be
        //    matched against the primary; it is replaced in the next step anyway.
        var create = await Step("Create secondary", "create-tileset", "--secondary", req.TilesetName, "-C", root,
            "--extrinsic-transparency", transArg, "--ignore-triple-layer-content", "--primary-pairing-mode", "off");
        if (create!.ExitCode != 0)
        {
            AddDiagnostics(create.Combined, diags, issues, null);
            return Fail(DescribeFailure("Porytiles could not create the secondary tileset in the sandbox.", create), version, transparency);
        }

        // 5. Write our layers and attributes
        string secDir = project.SecondaryTilesetsDir + TilesetNaming.ToFolderName(req.TilesetName);
        string srcDir = ProjectPaths.Combine(sandbox, secDir + "/porytiles_src");
        try
        {
            Directory.CreateDirectory(srcDir);
            // Porytiles always reads all three layer images; empty layers are written fully transparent.
            foreach (var layer in Enum.GetValues<Layer>())
                File.WriteAllBytes(Path.Combine(srcDir, LayerNames.PorytilesFile(layer)), Png.EncodeRgba(art.ToPorytilesSheet(layer, transparency)));
            // Header only: every metatile gets the schema default (behavior MB_NORMAL = 0). Behaviors are edited in Porymap.
            File.WriteAllText(Path.Combine(srcDir, "attributes.csv"), "id,behavior\n");
        }
        catch (IOException ex)
        {
            return Fail("Could not write layers to the sandbox: " + ex.Message, version, transparency);
        }

        // 6. Compile, paired with the chosen primary
        var compile = await Step("Compile", "compile-tileset", req.TilesetName, "-C", root,
            "--extrinsic-transparency", transArg, "--primary-pairing-mode", "manual", "--primary-pairing-partners", primary.Name);
        AddDiagnostics(compile!.Combined, diags, issues, art);
        if (compile.ExitCode != 0)
            return Fail(DescribeFailure("Porytiles did not compile the graphics. See the problem list for details.", compile), version, transparency);

        // 7. Load the output
        string binDir = ProjectPaths.Combine(sandbox, secDir + "/porytiles_bin");
        TilesetData compiled;
        try
        {
            compiled = TilesetData.Load(req.TilesetName,
                Path.Combine(binDir, "tiles.png"),
                Enumerable.Range(0, 16).Select(i => Path.Combine(binDir, "palettes", $"{i:00}.pal")).ToList(),
                Path.Combine(binDir, "metatiles.bin"),
                Path.Combine(binDir, "metatile_attributes.bin"),
                project.Constants.NumTilesPerMetatile,
                project.Attributes.SizeBytes);
        }
        catch (Exception ex) when (ex is IOException or FormatException or PngFormatException)
        {
            return Fail("Could not read the Porytiles output: " + ex.Message, version, transparency);
        }

        Log($"Done: {compiled.Tiles.Count} tiles, {compiled.MetatileCount} metatiles.");
        return new CompileResult
        {
            Success = true,
            Log = log.ToString(),
            Diagnostics = diags,
            Issues = issues,
            SandboxRoot = sandbox,
            OutputDir = binDir,
            Compiled = compiled,
            Primary = primaryData,
            CompilerVersion = version,
            SandboxTransparency = transparency,
            TilesetName = req.TilesetName,
        };
    }

    private static string DescribeFailure(string message, ProcessResult r)
    {
        if (r.TimedOut) return message + " (timed out)";
        if (PorytilesDiagnostics.LooksLikeCrash(r.Combined))
            return message + " Porytiles crashed with an internal error; the log contains the stack trace.";
        return message;
    }

    private static void AddDiagnostics(string output, List<PorytilesDiagnostic> diags, List<Issue> issues, Artwork? art)
    {
        foreach (var d in PorytilesDiagnostics.Parse(output))
        {
            diags.Add(d);
            issues.Add(PorytilesDiagnostics.ToIssue(d, art));
        }
    }

    /// <summary>
    /// Picks the transparency color for the sandbox. Porytiles rejects (or crashes on) primaries whose palettes contain
    /// the transparency color outside slot 0, and pokeemerald's General palette 04 contains 255,0,255 — so the color
    /// must not appear in any primary palette slot, nor as a real color in the artwork.
    /// </summary>
    public static Rgba32 ChooseTransparency(IReadOnlyList<Rgba32[]> primaryPalettes, IReadOnlySet<Rgba32> artColors)
    {
        var used = new HashSet<(byte, byte, byte)>();
        foreach (var p in primaryPalettes)
            foreach (var c in p) used.Add((c.R, c.G, c.B));
        foreach (var c in artColors) used.Add((c.R, c.G, c.B));
        Rgba32[] preferred = [new(255, 0, 255), new(0, 255, 255), new(255, 255, 0), new(0, 255, 0), new(1, 2, 3)];
        foreach (var c in preferred)
            if (!used.Contains((c.R, c.G, c.B))) return c;
        for (int r = 0; r < 256; r += 7)
            for (int g = 255; g >= 0; g -= 11)
                for (int b = 0; b < 256; b += 13)
                    if (!used.Contains(((byte)r, (byte)g, (byte)b))) return new Rgba32((byte)r, (byte)g, (byte)b);
        throw new InvalidOperationException("No free transparency color found.");
    }

    /// <summary>Minimal registration files containing only the primary.</summary>
    private static void WriteRegistration(string sandbox, ProjectInfo project, string name, string dir, int paletteCount)
    {
        string friendly = TilesetNaming.StripPrefix(name);
        int bits = project.Attributes.SizeBytes * 8;
        var headers = new StringBuilder();
        headers.Append($"const struct Tileset {name} =\n{{\n");
        if (project.Style.HeadersHaveIsCompressed) headers.Append("    .isCompressed = TRUE,\n");
        headers.Append("    .isSecondary = FALSE,\n");
        headers.Append($"    .tiles = gTilesetTiles_{friendly},\n");
        headers.Append($"    .palettes = gTilesetPalettes_{friendly},\n");
        headers.Append($"    .metatiles = gMetatiles_{friendly},\n");
        headers.Append($"    .metatileAttributes = gMetatileAttributes_{friendly},\n");
        if (project.Style.HeadersHaveCallback) headers.Append("    .callback = NULL,\n");
        headers.Append("};\n");
        WriteText(sandbox, ProjectLoader.HeadersFile, headers.ToString());

        var gfx = new StringBuilder();
        gfx.Append($"const u16 gTilesetPalettes_{friendly}[][16] =\n{{\n");
        for (int i = 0; i < paletteCount; i++) gfx.Append($"    INCBIN_U16(\"{dir}/palettes/{i:00}.gbapal\"),\n");
        gfx.Append("};\n\n");
        gfx.Append($"const u32 gTilesetTiles_{friendly}[] = INCBIN_U32(\"{dir}/tiles.4bpp.lz\");\n");
        WriteText(sandbox, ProjectLoader.GraphicsFile, gfx.ToString());

        WriteText(sandbox, ProjectLoader.MetatilesFile,
            $"const u16 gMetatiles_{friendly}[] = INCBIN_U16(\"{dir}/metatiles.bin\");\n" +
            $"const u{bits} gMetatileAttributes_{friendly}[] = INCBIN_U{bits}(\"{dir}/metatile_attributes.bin\");\n");
    }

    private static void WriteText(string root, string rel, string text)
    {
        var p = ProjectPaths.Combine(root, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        File.WriteAllText(p, text.Replace("\r\n", "\n"));
    }

    private static void CopyFile(string src, string root, string rel)
    {
        var p = ProjectPaths.Combine(root, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        File.Copy(src, p, overwrite: true);
    }

    private static void CleanupOldSandboxes(string workRoot, int keep)
    {
        try
        {
            foreach (var d in new DirectoryInfo(workRoot).GetDirectories().OrderByDescending(d => d.Name).Skip(keep))
                d.Delete(recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Old sandboxes are only a disk-space concern.
        }
    }
}
