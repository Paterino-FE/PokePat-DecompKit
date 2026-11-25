using TilesetEditor.Compiler;
using TilesetEditor.Core.Artwork;
using TilesetEditor.Core.Project;
using TilesetEditor.Export;
using TilesetEditor.Toolkit;

// Headless front end: same pipeline as the WPF app (import → check → compile in sandbox → plan → apply).
//
//   tileset-editor-cli info    --project <root>
//   tileset-editor-cli compile --project <root> --primary gTileset_General --name gTileset_MyTown
//                              (--image art.png [--layer middle] | --bottom b.png --middle m.png --top t.png)
//   tileset-editor-cli export  <same as compile> [--no-sources] [--yes]
//   tileset-editor-cli build   --project <root> [--env wsl|windows] [--command "make -j$(nproc)"] [--rom pokeemerald.gba]
//
// Common: --distro Ubuntu  --porytiles <path>  --native (run porytiles directly instead of via WSL)

Console.OutputEncoding = System.Text.Encoding.UTF8;
if (args.Length == 0 || args[0] is "-h" or "--help")
{
    Console.WriteLine("PokéPat DecompKit command line");
    Console.WriteLine("  install [--check]           get started: download and set up WSL tools, pokeemerald-expansion, Poryscript, Porytiles, Porymap, mGBA");
    Console.WriteLine("  setup                       first-time setup: project folder (required) and tool paths (optional)");
    Console.WriteLine("  info | build | events | flags | gamedata | trainer | trainer-pic | sprite-import | tileset-import | mods | mod-install | mod-uninstall | portrait-add | render-dialog | compile | export");
    Console.WriteLine("All commands use the project from \"setup\"; --project <folder> overrides it. Details: README.md.");
    return 0;
}

var cmd = args[0];
var opts = ParseOptions(args.Skip(1).ToArray());
string? Opt(string k) => opts.TryGetValue(k, out var v) ? v : null;

var store = new ToolkitSettingsStore();
var toolkitSettings = store.Load();
if (cmd == "install")
{
    // install [--check] [--project-path ~/pokeemerald-expansion] [--tools <folder>] [--distro Ubuntu] [--only buildtools,porytiles] [--tag expansion/1.17.2]
    try { return await Install.RunAsync(store, toolkitSettings, Opt, opts.ContainsKey("check")); }
    catch (ArgumentException ex) { Console.Error.WriteLine("Error: " + ex.Message); return 1; }
}
if (cmd == "setup")
{
    try { return Setup.Run(store, toolkitSettings, Opt, opts.ContainsKey("yes")); }
    catch (ArgumentException ex) { Console.Error.WriteLine("Error: " + ex.Message); return 1; }
}

try
{
    var root = Opt("project") ?? toolkitSettings.LastProject
               ?? throw new ArgumentException("No project yet: run \"setup\" first (or pass --project <folder>).");
    var saved = toolkitSettings.ForProject(root);
    var project = ProjectLoader.Load(Path.GetFullPath(root));
    PrintProject(project);
    if (cmd == "info") return 0;

    if (cmd == "render-dialog")
    {
        // Renders the message box like the game (project font, frame, colors) to a PNG, scaled 3x.
        // --text "…" [--frame 0|1..20] [--yesno] [--out preview.png]
        var font = TilesetEditor.Core.Text.GbaFont.LoadFromProject(project.Root);
        var cm = TilesetEditor.Core.Text.Charmap.Load(Path.Combine(project.Root, "charmap.txt"));
        var formatted = TilesetEditor.Core.Text.TextLayout.Format(Opt("text") ?? "Hello {PLAYER}!", font, cm);
        foreach (var problem in formatted.Problems) Console.WriteLine((problem.IsError ? "Error: " : "Note: ") + problem.Message);
        int frame = int.Parse(Opt("frame") ?? "0");
        var renderer = new TilesetEditor.Core.Text.DialogRenderer(TilesetEditor.Core.Text.DialogAssets.Load(project.Root), font, cm);
        var img = renderer.Render(new TilesetEditor.Core.Text.DialogScene
        {
            Style = frame > 0 ? TilesetEditor.Core.Text.DialogFrameStyle.Standard : TilesetEditor.Core.Text.DialogFrameStyle.Dialog,
            StdFrame = Math.Max(1, frame),
            Lines = formatted.Boxes[0].Take(2).Select(l => l.Replace("{PLAYER}", "PLAYER")).ToList(),
            ShowArrow = !opts.ContainsKey("yesno") && !opts.ContainsKey("menu"),
            YesNoCursor = opts.ContainsKey("yesno") ? 0 : null,
            MenuOptions = Opt("menu")?.Split('|'),
            MenuCursor = 1,
            MenuX = int.Parse(Opt("menu-x") ?? "0"),
            MenuY = int.Parse(Opt("menu-y") ?? "0"),
        });
        var big = new TilesetEditor.Core.Imaging.RgbaImage(img.Width * 3, img.Height * 3);
        for (int y = 0; y < big.Height; y++) for (int x = 0; x < big.Width; x++) big[x, y] = img[x / 3, y / 3];
        var outPath = Opt("out") ?? "dialog_preview.png";
        File.WriteAllBytes(outPath, TilesetEditor.Core.Imaging.Png.EncodeRgba(big));
        Console.WriteLine($"Written: {Path.GetFullPath(outPath)}  Text: {formatted.Encoded}");
        return 0;
    }

    if (cmd == "events")
    {
        // Validate, compile (project Poryscript) and optionally write toolkit events.
        //   --json <file>   replace the document with this file   --demo   add an event using every block (Route101)
        //   --yes           write into the project (otherwise only check)
        var root2 = project.Root;
        var font = TilesetEditor.Core.Text.GbaFont.LoadFromProject(root2);
        var cm = TilesetEditor.Core.Text.Charmap.Load(Path.Combine(root2, "charmap.txt"));
        var symbols = TilesetEditor.Core.Events.ProjectSymbols.Load(root2);
        var doc = Opt("json") is { } jf ? TilesetEditor.Core.Events.ToolkitEventDocument.Parse(File.ReadAllText(jf)) : EventsPlanner.LoadDocument(root2);
        if (opts.ContainsKey("demo"))
        {
            doc.Events.RemoveAll(e => e.Label == "Route101_TK_AllBlocks");
            var demo = new TilesetEditor.Core.Events.ToolkitEvent { Label = "Route101_TK_AllBlocks", Map = "Route101", Title = "All blocks (test)" };
            demo.Steps.Add(new TilesetEditor.Core.Events.MessageStep { Text = "This event uses every block of the toolkit." });
            foreach (var def in TilesetEditor.Core.Events.BlockCatalog.All.Where(d => d.RequiresMod == null))
            {
                var step = TilesetEditor.Core.Events.CommandStep.Create(def);
                if (def.Id == TilesetEditor.Core.Events.BlockCatalog.MacroBlockId) { step.Args["command"] = "setflag"; step.Args["arg0"] = "FLAG_UNUSED_0x021"; }
                if (def.IsBranch) step.Then.Add(new TilesetEditor.Core.Events.MessageStep { Text = $"Branch of {def.Title}." });
                if (def.IsSwitch) step.CaseBranches()[0].Steps.Add(new TilesetEditor.Core.Events.MessageStep { Text = "First option chosen." });
                demo.Steps.Add(step);
            }
            doc.Events.Add(demo);
        }
        var problems = TilesetEditor.Core.Events.PoryscriptGenerator.Validate(doc, font, cm, symbols, EventsPlanner.ExistingMapLabels(root2));
        foreach (var pr in problems) Console.WriteLine($"  [{(pr.IsError ? "Error" : "Note")}] {pr.Event}: {pr.Message}");
        if (problems.Any(pr => pr.IsError)) return 5;
        var pory = TilesetEditor.Core.Events.PoryscriptGenerator.Generate(doc, font, cm);
        var tools = new ProjectToolSettings { BuildEnvironment = saved.BuildEnvironment, BuildWslDistro = Opt("distro") ?? ProjectEnvironment.Detect(root2).WslDistro ?? saved.BuildWslDistro };
        var compiled = await new PoryscriptRunner(tools).CompileAsync(root2, pory, CancellationToken.None);
        Console.WriteLine(compiled.Success ? $"Poryscript {compiled.Version}: OK ({doc.Events.Count} Events)" : "Poryscript error: " + compiled.Output);
        if (!compiled.Success) return 6;
        if (!opts.ContainsKey("yes")) { Console.WriteLine(pory); return 0; }
        var eplan = EventsPlanner.Plan(root2, doc, pory, compiled.IncText!, []);
        foreach (var pf in eplan.Files) Console.WriteLine($"  {(pf.Action == PlannedAction.Create ? "+" : "~")} {pf.RelativePath}");
        var res = new ExportApplier().Apply(eplan);
        Console.WriteLine(res.Success ? "Written." : "Error: " + res.Error);
        return res.Success ? 0 : 3;
    }

    if (cmd == "trainer")
    {
        // --id TRAINER_X [--new] [--name NAME] [--add-mon SPECIES_X:LEVEL] [--moves MOVE_A,MOVE_B] [--yes]
        // Uses src/data/trainers.party when COMPETITIVE_PARTY_SYNTAX is TRUE, otherwise src/data/trainers.h.
        var source = TilesetEditor.Core.Trainers.TrainerSource.Load(project.Root);
        var trainersRel = source.RelativePath;
        var oppRel = TilesetEditor.Core.Trainers.TrainerFile.OpponentsFile;
        var tfull = Path.Combine(project.Root, trainersRel.Replace('/', Path.DirectorySeparatorChar));
        var ttext = source.Text;
        var all = source.Trainers;
        Console.WriteLine($"Trainers: {trainersRel} ({source.FormatName}), {all.Count} entries");
        var id = Opt("id") ?? throw new ArgumentException("--id is missing.");
        var original = all.FirstOrDefault(t => t.Id == id);
        if (original == null && !opts.ContainsKey("new")) { Console.WriteLine($"{id} not found ({all.Count} trainers). Use --new to create it."); return 4; }
        var template = all.FirstOrDefault(t => t.Id != "TRAINER_NONE" && t.Party.Count > 0) ?? all.FirstOrDefault();
        var data = original?.Clone() ?? new TilesetEditor.Core.Trainers.TrainerData
        {
            Id = id, Name = Opt("name") ?? "TRAINER", TrainerClass = template?.TrainerClass ?? "TRAINER_CLASS_PKMN_TRAINER_1",
            Pic = template?.Pic ?? "TRAINER_PIC_HIKER", Music = template?.Music ?? "", AiFlags = "AI_FLAG_BASIC_TRAINER",
        };
        if (Opt("name") is { } nm) data.Name = nm;
        if (Opt("add-mon") is { } am)
        {
            var parts = am.Split(':');
            var mon = source.NewMon(parts[0], parts.Length > 1 ? int.Parse(parts[1]) : 5);
            if (Opt("moves") is { } mv) mon.Moves = mv.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).ToList();
            data.Party.Add(mon);
        }
        var tfiles = new List<PlannedFile>();
        string newText;
        if (original != null) newText = source.Write(ttext, original, data);
        else
        {
            var oppFull = Path.Combine(project.Root, oppRel.Replace('/', Path.DirectorySeparatorChar));
            var oppText = File.ReadAllText(oppFull);
            var slot = TilesetEditor.Core.Trainers.TrainerFile.FindFreeId(oppText, all.Select(t => t.Id).ToList());
            if (slot.Problem != null) { Console.WriteLine(slot.Problem); return 4; }
            tfiles.Add(new PlannedFile(oppRel, PlannedAction.Modify, System.Text.Encoding.UTF8.GetBytes(TilesetEditor.Core.Trainers.TrainerFile.AddConstant(oppText, id, slot)),
                $"#define {id} {slot.Id}", Hashing.Sha256(File.ReadAllBytes(oppFull))));
            newText = source.Append(ttext, data);
        }
        tfiles.Add(new PlannedFile(trainersRel, PlannedAction.Modify, System.Text.Encoding.UTF8.GetBytes(newText), $"{id}: {data.PartySummary}", Hashing.Sha256(File.ReadAllBytes(tfull))));
        foreach (var pf in tfiles) Console.WriteLine($"  ~ {pf.RelativePath,-40} {pf.Description}");
        if (!opts.ContainsKey("yes")) return 0;
        var tres = new ExportApplier().Apply(ExportPlan.ForFiles(project.Root, "Trainer " + id, trainersRel, tfiles));
        Console.WriteLine(tres.Success ? "Written." : "Error: " + tres.Error);
        return tres.Success ? 0 : 3;
    }

    if (cmd == "gamedata")
    {
        // --table items --key ITEM_POTION --column Price --value 250 [--yes]
        // --add-class TRAINER_CLASS_X --name NAME [--money 10] [--yes]
        var gfiles = new List<PlannedFile>();
        PlannedFile GMod(string rel, string text, string desc) =>
            new(rel, PlannedAction.Modify, new System.Text.UTF8Encoding(false).GetBytes(text), desc, Hashing.Sha256(File.ReadAllBytes(Path.Combine(project.Root, rel.Replace('/', Path.DirectorySeparatorChar)))));
        if (Opt("add-class") is { } cls)
        {
            var cFile = TilesetEditor.Core.GameData.TrainerClasses.ConstantsFile;
            var tb = TilesetEditor.Core.GameData.TrainerClasses.TableFile;
            var add = TilesetEditor.Core.GameData.TrainerClasses.Add(TilesetEditor.Core.GameData.GameDataCatalog.ReadText(project.Root, cFile),
                TilesetEditor.Core.GameData.GameDataCatalog.ReadText(project.Root, tb), cls, Opt("name") ?? "NEW CLASS", Opt("money") is { } mo ? int.Parse(mo) : null, null);
            gfiles.Add(GMod(cFile, add.Constants, $"{cls} = {add.Value}"));
            gfiles.Add(GMod(tb, add.Table, $"gTrainerClasses[{cls}]"));
        }
        else
        {
            var def = TilesetEditor.Core.GameData.GameDataCatalog.Get(Opt("table") ?? throw new ArgumentException("--table is missing."));
            var table = TilesetEditor.Core.GameData.GameDataCatalog.Load(project.Root, def);
            var row = table.Rows.FirstOrDefault(r => r.Key == Opt("key")) ?? throw new ArgumentException($"{Opt("key")} not found in {def.Title}.");
            int col = def.Columns.ToList().FindIndex(x => x.Header.Equals(Opt("column"), StringComparison.OrdinalIgnoreCase));
            if (col < 0) throw new ArgumentException("--column must be one of: " + string.Join(", ", def.Columns.Select(x => x.Header)));
            if (!row.Cells[col].Editable) { Console.WriteLine($"Read-only: {row.Cells[col].Note}"); return 4; }
            Console.WriteLine($"{row.Key}.{def.Columns[col].Header}: {row.Cells[col].Original} → {Opt("value")}");
            row.Cells[col].Value = Opt("value") ?? "";
            var check = table.Validate(row, col);
            if (check.Error != null) { Console.WriteLine("Error: " + check.Error); return 5; }
            foreach (var f in TilesetEditor.Core.GameData.GameDataCatalog.ChangedFiles(table))
                gfiles.Add(GMod(f, TilesetEditor.Core.GameData.GameDataCatalog.Write(table, f, TilesetEditor.Core.GameData.GameDataCatalog.ReadText(project.Root, f)), $"{row.Key}.{def.Columns[col].Header}"));
        }
        foreach (var pf in gfiles) Console.WriteLine($"  ~ {pf.RelativePath,-40} {pf.Description}");
        if (!opts.ContainsKey("yes")) return 0;
        var gres = new ExportApplier().Apply(ExportPlan.ForFiles(project.Root, "Game data", "src/data", gfiles));
        Console.WriteLine(gres.Success ? "Written." : "Error: " + gres.Error);
        return gres.Success ? 0 : 3;
    }

    if (cmd == "flags")
    {
        // [--more-flags] [--more-vars] [--yes]   adds a block of free saved flags/variables (save-breaking)
        var fl = new List<PlannedFile>();
        var opp = File.ReadAllText(Path.Combine(project.Root, "include", "constants", "opponents.h"));
        foreach (var (key, rel) in new[] { ("more-flags", TilesetEditor.Core.Events.FlagCatalog.FlagsFile), ("more-vars", TilesetEditor.Core.Events.FlagCatalog.VarsFile) })
        {
            if (!opts.ContainsKey(key)) continue;
            var full = Path.Combine(project.Root, rel.Replace('/', Path.DirectorySeparatorChar));
            var text = File.ReadAllText(full);
            var (nt, names) = key == "more-flags" ? TilesetEditor.Core.Events.FlagEditor.AddFlags(text, 64, opp) : TilesetEditor.Core.Events.FlagEditor.AddVars(text);
            fl.Add(new PlannedFile(rel, PlannedAction.Modify, new System.Text.UTF8Encoding(false).GetBytes(nt), $"{names[0]} … {names[^1]}", Hashing.Sha256(File.ReadAllBytes(full))));
        }
        var cat = TilesetEditor.Core.Events.FlagCatalog.Load(project.Root);
        Console.WriteLine($"{cat.Entries.Count} flags/variables, free: {cat.Entries.Count(e => e.IsClaimable && cat.IsUnusedAnywhere(e.Name))}");
        foreach (var pf in fl) Console.WriteLine($"  ~ {pf.RelativePath,-34} {pf.Description}");
        if (fl.Count == 0 || !opts.ContainsKey("yes")) return 0;
        var flRes = new ExportApplier().Apply(ExportPlan.ForFiles(project.Root, "More flags/variables", "include/constants", fl));
        Console.WriteLine(flRes.Success ? "Written (save-breaking: old save files no longer match)." : "Error: " + flRes.Error);
        return flRes.Success ? 0 : 3;
    }

    if (cmd == "trainer-pic")
    {
        // --name SeaCaptain --png pic.png [--yes]   (64×64, max. 15 colors + transparency, gets its own palette)
        var tp = TrainerPicPlanner.Plan(project.Root, Opt("name") ?? throw new ArgumentException("--name is missing."), Opt("png") ?? throw new ArgumentException("--png is missing."));
        foreach (var pf in tp.Plan.Files) Console.WriteLine($"  {(pf.Action == PlannedAction.Create ? "+" : "~")} {pf.RelativePath,-48} {pf.Description}");
        if (!opts.ContainsKey("yes")) return 0;
        var tpRes = new ExportApplier().Apply(tp.Plan);
        Console.WriteLine(tpRes.Success ? $"{tp.Constant} registered." : "Error: " + tpRes.Error);
        return tpRes.Success ? 0 : 3;
    }

    if (cmd == "sprite-import")
    {
        // --name Guard --png sheet.png [--frame 16x32] [--palette OBJ_EVENT_PAL_TAG_NPC_1 | --new-palette Guard] [--nearest] [--yes]
        var sprites = TilesetEditor.Core.Sprites.OverworldSprites.Load(project.Root);
        var pals = TilesetEditor.Core.Sprites.OverworldSprites.Palettes(sprites)
            .ToDictionary(kv => kv.Key, kv => TilesetEditor.Core.Gba.JascPalette.Load(Path.Combine(project.Root, kv.Value)));
        var size = (Opt("frame") ?? "16x32").Split('x');
        var res = SpriteImportPlanner.Plan(project.Root, new SpriteImportRequest(Opt("name") ?? throw new ArgumentException("--name is missing."),
            Opt("png") ?? throw new ArgumentException("--png is missing."), int.Parse(size[0]), int.Parse(size[1]),
            Opt("palette") ?? "OBJ_EVENT_PAL_TAG_NPC_1", opts.ContainsKey("nearest"), Opt("new-palette")), sprites, pals);
        foreach (var pf in res.Plan.Files) Console.WriteLine($"  {(pf.Action == PlannedAction.Create ? "+" : "~")} {pf.RelativePath,-62} {pf.Description}");
        if (!opts.ContainsKey("yes")) return 0;
        var spriteApplied = new ExportApplier().Apply(res.Plan);
        Console.WriteLine(spriteApplied.Success ? $"{res.Constant} registered." : "Error: " + spriteApplied.Error);
        return spriteApplied.Success ? 0 : 3;
    }

    if (cmd is "mods" or "mod-install" or "mod-uninstall")
    {
        // mods                       list the mods (repository mods/ folder, next to the program, Documents/PokePat Mods)
        // mod-install --id <id> [--yes] / mod-uninstall --id <id> [--yes]
        var folders = new List<string> { Path.Combine(AppContext.BaseDirectory, "mods"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "PokePat Mods") };
        for (var dd = new DirectoryInfo(AppContext.BaseDirectory); dd != null; dd = dd.Parent)
            if (File.Exists(Path.Combine(dd.FullName, "TilesetEditor.sln"))) { folders.Add(Path.Combine(dd.FullName, "mods")); break; }
        if (Opt("mods") is { } extra) folders.Insert(0, extra);
        var (mods, modProblems) = TilesetEditor.Export.Mods.ModPackage.LoadAll(folders);
        var installedMods = TilesetEditor.Export.Mods.ModRegistry.Installed(project.Root);
        if (cmd == "mods")
        {
            foreach (var m in mods)
            {
                var check = TilesetEditor.Export.Mods.ModPlanner.Check(project.Root, m, project.ExpansionVersion);
                Console.WriteLine($"  {m.Manifest.Id,-24} {m.Manifest.Version,-8} {(check.Installed != null ? "installed" : check.CanInstall ? "available" : "does not fit")}  {m.Manifest.Name}");
                foreach (var pr in check.Problems) Console.WriteLine($"      ! {pr}");
            }
            foreach (var pr in modProblems) Console.WriteLine("  ! " + pr);
            return 0;
        }
        var id = Opt("id") ?? throw new ArgumentException("--id is missing.");
        ExportPlan modPlan;
        if (cmd == "mod-install")
        {
            var pkg = mods.FirstOrDefault(m => m.Manifest.Id == id) ?? throw new ArgumentException($"Mod {id} not found.");
            modPlan = TilesetEditor.Export.Mods.ModPlanner.PlanInstall(project.Root, pkg, project.ExpansionVersion);
        }
        else
        {
            var inst = installedMods.FirstOrDefault(m => m.Id == id) ?? throw new ArgumentException($"Mod {id} is not installed.");
            modPlan = TilesetEditor.Export.Mods.ModPlanner.PlanUninstall(project.Root, inst).Plan;
        }
        foreach (var pf in modPlan.Files) Console.WriteLine($"  {pf.ActionText,-7} {pf.RelativePath,-50} {pf.Description}");
        foreach (var n in modPlan.Notes) Console.WriteLine("  * " + n);
        if (!opts.ContainsKey("yes")) return 0;
        var mr = new ExportApplier().Apply(modPlan);
        Console.WriteLine(mr.Success ? "Written." : "Error: " + mr.Error);
        return mr.Success ? 0 : 3;
    }

    if (cmd == "portrait-add")
    {
        // portrait-add --name Birch (--png file.png | --trainer-pic TRAINER_PIC_X) [--yes]   (mod "dialog-portraits")
        var pname = Opt("name") ?? throw new ArgumentException("--name is missing.");
        TilesetEditor.Core.Imaging.PngImage picture;
        if (Opt("png") is { } pngFile) picture = TilesetEditor.Core.Imaging.Png.Load(pngFile);
        else
        {
            var tp = TilesetEditor.Core.Sprites.CharacterGraphics.TrainerPics(project.Root).FirstOrDefault(t => t.Constant == Opt("trainer-pic"))
                     ?? throw new ArgumentException("--png or a known --trainer-pic is needed.");
            var raw = TilesetEditor.Core.Imaging.Png.Load(Path.Combine(project.Root, tp.PngRel!));
            var pal = tp.PalRel != null ? TilesetEditor.Core.Gba.JascPalette.Load(Path.Combine(project.Root, tp.PalRel)) : raw.Palette!;
            var img = new TilesetEditor.Core.Imaging.RgbaImage(raw.Width, raw.Height);
            for (int i = 0; i < img.Pixels.Length; i++)
            {
                int idx = raw.Indices![i] & 0xF;
                img.Pixels[i] = idx == 0 || idx >= pal.Length ? TilesetEditor.Core.Imaging.Rgba32.Transparent : pal[idx] with { A = 255 };
            }
            picture = TilesetEditor.Core.Imaging.Png.Decode(TilesetEditor.Core.Imaging.Png.EncodeRgba(img));
        }
        var pp = TilesetEditor.Export.Mods.DialogPortraits.PlanAdd(project.Root, pname, picture);
        foreach (var pf in pp.Files) Console.WriteLine($"  {pf.ActionText,-7} {pf.RelativePath,-50} {pf.Description}");
        if (!opts.ContainsKey("yes")) return 0;
        var pr2 = new ExportApplier().Apply(pp);
        Console.WriteLine(pr2.Success ? "Written." : "Error: " + pr2.Error);
        return pr2.Success ? 0 : 3;
    }

    if (cmd == "tileset-import")
    {
        // Imports a finished tileset folder (asset repos, other hacks): analysis, then a new or replaced tileset.
        //   --folder <dir> [--name gTileset_X] [--kind primary|secondary] [--base gTileset_General] [--replace]
        //   [--metatiles package|base] [--no-animations] [--maps LAYOUT_A,LAYOUT_B] [--yes]
        var folder = Opt("folder") ?? throw new ArgumentException("--folder is missing.");
        var pkg = TilesetEditor.Core.Tilesets.TilesetPackage.Read(folder, project);
        Console.WriteLine($"Folder: {folder}");
        Console.WriteLine($"  tile image: {(pkg.TilesImage != null ? $"{Path.GetFileName(pkg.TilesImage)} ({pkg.TileCount} tiles)" : "–")}");
        Console.WriteLine($"  palettes: {(pkg.Palettes.Count == 0 ? "–" : string.Join(", ", pkg.Palettes.Keys.Select(k => k.ToString("00"))))}");
        Console.WriteLine($"  metatiles: {(pkg.MetatilesFile != null ? $"{pkg.MetatileCount} ({(pkg.TilesPerMetatile == 12 ? "triple" : pkg.TilesPerMetatile == 8 ? "dual" : "?")} layer)" : "–")}, attributes: {(pkg.AttributesFile != null ? "yes" : "–")}");
        Console.WriteLine($"  looks like: {pkg.SuggestedKind}{(pkg.SuggestedBase != null ? $" based on {pkg.SuggestedBase}" : "")}, name {pkg.SuggestedName}");
        foreach (var n in pkg.Notes) Console.WriteLine("  * " + n);
        foreach (var pr in pkg.Problems) Console.WriteLine("  ! " + pr);
        var importOptions = new TilesetImportOptions
        {
            Mode = opts.ContainsKey("replace") ? TilesetImportMode.Replace : TilesetImportMode.New,
            Kind = Opt("kind") is { } k ? Enum.Parse<TilesetEditor.Core.Tilesets.TilesetKind>(k, ignoreCase: true) : pkg.SuggestedKind,
            Name = Opt("name") ?? pkg.SuggestedName,
            Base = Opt("base") ?? pkg.SuggestedBase,
            Metatiles = Opt("metatiles") is "base" ? MetatileSource.Base : MetatileSource.Package,
            KeepAnimations = !opts.ContainsKey("no-animations"),
            AssignToLayouts = Opt("maps")?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) ?? [],
        };
        var ip = TilesetImportPlanner.Plan(project, pkg, importOptions);
        foreach (var pf in ip.Files) Console.WriteLine($"  {(pf.Action == PlannedAction.Create ? "+" : "~")} {pf.RelativePath,-60} {pf.Description}");
        foreach (var n in ip.Notes) Console.WriteLine("  * " + n);
        if (!opts.ContainsKey("yes")) return 0;
        var ir = new ExportApplier().Apply(ip);
        Console.WriteLine(ir.Success ? "Written." : "Error: " + ir.Error);
        return ir.Success ? 0 : 3;
    }

    if (cmd == "build")
    {
        // Same runner as the GUI's "Build ROM". Defaults from setup; --env wsl|windows|native, --command "...", --distro, --rom
        var env = ProjectEnvironment.Detect(project.Root);
        var tools = new ProjectToolSettings
        {
            BuildEnvironment = Opt("env") is { } envOpt ? Enum.Parse<BuildEnvironment>(envOpt, ignoreCase: true) : saved.BuildEnvironment,
            BuildWslDistro = Opt("distro") ?? env.WslDistro ?? saved.BuildWslDistro,
            BuildCommand = Opt("command") ?? saved.BuildCommand,
        };
        Console.WriteLine($"Branch: {env.GitBranch ?? "-"}, ROM: {Opt("rom") ?? env.RomFileName}");
        var br = await new BuildRunner().RunAsync(new BuildRequest
        {
            ProjectRoot = project.Root,
            Settings = tools,
            RomRelativePath = Opt("rom") ?? env.RomFileName,
        }, line => { if (opts.ContainsKey("verbose")) Console.WriteLine("  | " + line); }, CancellationToken.None);
        Console.WriteLine($"Result: success={br.Success}, exit={br.ExitCode}, ROM state={br.RomState}, confirmed ROM={br.ConfirmedRomPath ?? "(none)"}");
        Console.WriteLine(br.Message);
        foreach (var l in br.ErrorLines.TakeLast(10)) Console.WriteLine("  ! " + l);
        return br.Success ? 0 : 4;
    }

    var primaryName = Opt("primary") ?? "gTileset_General";
    var primary = project.FindTileset(primaryName) ?? throw new ArgumentException($"Primary {primaryName} not found.");
    if (primary.IsSecondary) throw new ArgumentException($"{primaryName} is a secondary tileset.");
    var name = Opt("name") ?? throw new ArgumentException("--name is missing (e.g. gTileset_MyTown).");
    var nameErr = TilesetNaming.Validate(name);
    if (nameErr != null) throw new ArgumentException(nameErr);

    Artwork art;
    if (Opt("image") is { } image)
    {
        var layer = Enum.Parse<Layer>(Opt("layer") ?? "Middle", ignoreCase: true);
        art = ArtworkLoader.LoadSingle(image, layer);
    }
    else
    {
        var files = new Dictionary<Layer, string>();
        foreach (var l in Enum.GetValues<Layer>())
            if (Opt(l.ToString().ToLowerInvariant()) is { } f) files[l] = f;
        art = ArtworkLoader.LoadLayers(files);
    }
    Console.WriteLine($"Import: {art.Width}×{art.Height} px, {art.MetatileCount} metatiles, layers: {string.Join(", ", art.PresentLayers().Select(l => LayerNames.German(l.Layer)))}");

    var primaryData = project.LoadTilesetData(primary);
    var pre = ArtworkAnalyzer.Analyze(art, project.Constants, primaryData.Palettes);
    PrintIssues("Pre-check", pre, art);
    if (pre.Any(i => i.Severity == IssueSeverity.Error))
    {
        Console.WriteLine("Pre-check reports errors – running Porytiles anyway to show its diagnostics.");
    }

    var settings = new PorytilesSettings();
    if (opts.ContainsKey("native")) settings.Mode = PorytilesMode.Native;
    if (Opt("distro") is { } d) settings.WslDistro = d;
    if (Opt("porytiles") is { } p) settings.Executable = p;
    var compiler = new SandboxCompiler(new PorytilesClient(settings));
    var progress = new Progress<string>(s => { if (opts.ContainsKey("verbose")) Console.WriteLine("  | " + s.Replace("\n", "\n  | ")); });
    var result = await compiler.CompileAsync(new CompileRequest
    {
        Project = project,
        Primary = primary,
        Artwork = art,
        TilesetName = name,
        WorkRoot = Opt("work"),
    }, progress, CancellationToken.None);
    PrintIssues("Porytiles", result.Issues.Where(i => i.Severity != IssueSeverity.Info).ToList(), art);
    Console.WriteLine($"Sandbox: {result.SandboxRoot}");
    if (!result.Success)
    {
        Console.WriteLine("Compilation failed.");
        if (!opts.ContainsKey("verbose")) Console.WriteLine("(--verbose shows the full log)");
        return 2;
    }
    var c = result.Compiled!;
    Console.WriteLine($"Compiled with Porytiles {result.CompilerVersion?.Version}: {c.Tiles.Count} tiles, {c.MetatileCount} metatiles, " +
                      $"palettes used: {string.Join(",", c.MetatileEntries.Select(e => e.Palette).Distinct().OrderBy(x => x))}");
    if (cmd == "compile") return 0;

    var plan = ExportPlanner.Plan(project, new ExportInput
    {
        TilesetName = name,
        PrimaryName = primary.Name,
        CompiledDir = result.OutputDir!,
        Compiled = c,
        Primary = result.Primary!,
        CompilerVersion = result.CompilerVersion!.Version,
        CompileTransparency = result.SandboxTransparency,
        SourceArtwork = art,
    }, new ExportOptions { IncludeSources = !opts.ContainsKey("no-sources") });

    Console.WriteLine($"\nExport plan for {plan.TilesetName} ({plan.TilesetFolder}):");
    foreach (var f in plan.Files)
        Console.WriteLine($"  {(f.Action == PlannedAction.Create ? "+" : "~")} {f.RelativePath,-70} {f.SizeText,9}  {f.Description}");
    foreach (var n in plan.Notes) Console.WriteLine("  * " + n);
    if (!opts.ContainsKey("yes"))
    {
        Console.WriteLine("\nNothing written. Use --yes to apply.");
        return 0;
    }

    var applied = new ExportApplier().Apply(plan, () =>
    {
        var reloaded = ProjectLoader.Load(project.Root);
        var t = reloaded.FindTileset(name);
        return t is { IsFullyResolved: true } ? null : "The new tileset cannot be resolved after writing.";
    });
    if (!applied.Success)
    {
        Console.WriteLine($"Export failed: {applied.Error}");
        if (applied.RolledBack) Console.WriteLine("Changes were rolled back." + (applied.RollbackProblems.Count > 0 ? " Problems: " + string.Join("; ", applied.RollbackProblems) : ""));
        return 3;
    }
    Console.WriteLine($"Export complete ({applied.Written.Count} files). Backup of the changed files: {applied.BackupDir}");
    return 0;
}
catch (Exception ex) when (ex is ArgumentException or ProjectLoadException or ArtworkImportException or ExportValidationException or IOException)
{
    Console.Error.WriteLine("Error: " + ex.Message);
    return 1;
}

static Dictionary<string, string> ParseOptions(string[] a)
{
    var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    for (int i = 0; i < a.Length; i++)
    {
        if (!a[i].StartsWith("--")) continue;
        var key = a[i][2..];
        if (i + 1 < a.Length && !a[i + 1].StartsWith("--")) d[key] = a[++i];
        else d[key] = "true";
    }
    return d;
}

static void PrintProject(ProjectInfo p)
{
    var c = p.Constants;
    Console.WriteLine($"Project: {p.Root}");
    if (p.ExpansionVersion != null) Console.WriteLine($"  pokeemerald-expansion {p.ExpansionVersion}");
    Console.WriteLine($"  Layer mode: {c.LayerMode} ({c.NumTilesPerMetatile} tiles/metatile), attributes: {p.Attributes.SizeBytes} bytes " +
                      $"(behavior 0x{p.Attributes.BehaviorMask:X}, layer 0x{p.Attributes.LayerTypeMask:X})");
    Console.WriteLine($"  Secondary: max {c.MaxSecondaryTiles} tiles, {c.MaxSecondaryMetatiles} metatiles, palettes {c.NumPalsInPrimary}–{c.NumPalsTotal - 1}");
    Console.WriteLine($"  Tilesets: {p.PrimaryTilesets.Count()} Primary, {p.SecondaryTilesets.Count()} Secondary; Primaries: {string.Join(", ", p.PrimaryTilesets)}");
    Console.WriteLine($"  Registration: isCompressed={p.Style.HeadersHaveIsCompressed}, callback={p.Style.HeadersHaveCallback}, " +
                      $"{p.Style.PaletteIncbinCount} palette INCBINs, tiles '{p.Style.TilesOutputExtension}', palettes '{p.Style.PalettesOutputExtension}'");
    foreach (var w in p.Warnings) Console.WriteLine("  Warning: " + w);
}

static void PrintIssues(string title, IReadOnlyList<Issue> issues, Artwork art)
{
    if (issues.Count == 0) { Console.WriteLine($"{title}: no problems."); return; }
    Console.WriteLine($"{title}: {issues.Count} message(s)");
    foreach (var i in issues.Take(40))
        Console.WriteLine($"  [{i.SeverityText}] {(i.Location != null ? i.Location.Describe(art) + ": " : "")}{i.Message}");
}
