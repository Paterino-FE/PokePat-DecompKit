# PokéPat DecompKit

<img src="src/TilesetEditor.App/Assets/decompkit-256.png" width="96" align="right" alt="PokéPat DecompKit logo">

**A friendly editing kit for Pokémon ROM hacks** – red roof, white walls, everything in one place.

A desktop toolkit for ROM hacks based on [pokeemerald-expansion](https://github.com/rh-hideout/pokeemerald-expansion)
(Pokémon Emerald) or [pokeplatinum](https://github.com/pret/pokeplatinum) (Pokémon Platinum), made so that people
without coding experience can change their game. At the start you choose which game you want to hack; PokéPat
recognizes the game of an existing project by itself and shows the modules for it. It edits the project's source files directly –
every change shows the list of files first, makes a backup and is rolled back completely if anything fails.

| Module | What it does |
|---|---|
| **Project** | Opens your project, shows branch/version, tool paths, Porymap bridge |
| **Tilesets** | PNG → checks → Porytiles → preview in GBA colors → new secondary tileset; edit existing tilesets as images (round trip with your paint program); import finished tilesets (asset repos) and put them on maps |
| **Maps** | Maps, layouts, tilesets, scripts, connections (read only) |
| **Dialogs & Events** | Every existing person, sign and text of the game (searchable, rendered like in game, usable as template); event builder with building blocks (messages, choices, items, battles, movements, scenes …), dialog preview, movement paths on the map, existing scripts in readable form |
| **Flags & Variables** | Every flag/variable with its uses, name free ones, add more |
| **Trainers** | Trainer teams (`trainers.h` or `trainers.party`), create trainers |
| **Game data** | Trainer classes, items, moves, abilities, Pokémon, types, natures, location names |
| **Secret Bases** | Locations, interiors, decorations (incl. new dolls/cushions) |
| **Sprites** | Overworld sprites (new, with own palettes), player character images, trainer pictures |
| **Graphics** | Title screen, menus, windows, battle backgrounds … view and replace |
| **Features** | `include/config` options by category |
| **Build ROM** | Runs `make` (in WSL on Windows), shows the output and a clickable list of build problems (file:line), opens the emulator |
| **Mods** | Installs extensions into your game (checked first, shown as a file list, removable again) – e.g. *Dialog portraits* |

Each game has its own look: green for Emerald, purple and silver for Platinum, with the game's mascot (Rayquaza / Giratina,
taken from the open project's own sprites) in the corner; the Pokémon Center red stays while no project is open. Light and
dark mode work for all of them.

PokéPat updates itself from its GitHub releases: the button at the top right checks (⟳) or shows "⬆ PokéPat x.y" when a newer
release exists (also checked on start). The update window shows what is new, downloads the Windows package, checks it against
GitHub's SHA-256, and swaps the files after PokéPat closes (backup in `.update-backup`, restored if a step fails), then starts
the new version. Settings, own mods (Documents\PokePat Mods) and projects are not touched. To publish an update: raise
`<Version>` in Directory.Build.props, build the packages (`./dev-build.sh --release` or release.ps1) and attach
`PokePat-DecompKit-<version>-windows-x64.zip` to a new GitHub release (the version is read from the tag, the title or the file name).

The **Backups** module lists every change PokéPat made (newest first, with created/changed/deleted files and whether they were
changed again since). A change can be undone on its own, or the project goes back to the state before it (undoing every later
change too); undoing has its own backup. Old backups can be deleted after 30 days.

Opening a project loads only the module on screen; the others load when they are opened – game data, trainers and Pokémon are loaded in the background shortly after the start (files are read in parallel, which matters a lot on a WSL share). Slow steps are written to
`%LOCALAPPDATA%\TilesetEditor\perf.log`. Started from a Windows drive, PokéPat starts several seconds faster than from
`\\wsl.localhost` (Windows reads and checks the whole program over the share on every start).

### Pokémon Platinum (pokeplatinum)

| Module | What it does |
|---|---|
| **Project** | Folder, branch, counts, build command, ROM (`build/pokeplatinum.us.nds`), DS emulator |
| **Pokémon** | Battle sprites front/back, normal and shiny (female variants too); replace a sprite with a PNG (80×80 or 160×80, one shared 16-colour palette, shiny colours derived); level-up learnset; evolutions |
| **Trainers** | Name, class (with picture), double battle, items, AI flags, team (species, level, item, moves, IVs) and the trainer's texts |
| **Game data** | Pokémon (stats, types, abilities, catch rate, gender, growth, egg groups, held items, Pokédex texts), moves, items, ability/type/nature/trainer class/place names |
| **Trainers** (classes) | New trainers (copy of the selected one, with their own defeated flag – the build stops without it); new trainer classes with own name, battle picture, gender and prize money; replace a class's battle picture |
| **Maps** | Every map seen from above (3D models with textures, or walkable/blocked tiles and tile behaviors) with props, and an **event editor like Porymap's**: click an event to select it, drag it to move it, drag a trigger's corner to resize it; "+ Person / Trainer / Sign / Warp / Trigger" and a click on the map add one; the fields of the selected event on the side (warp destinations chosen from the target map's warps); Delete warns when scripts still use a person. Several events can be selected (frame or Ctrl+click) and moved together, copied and pasted (Ctrl+C/V/D); Ctrl+Z/Y undo and redo; a new warp can create the matching warp back on the target map. "Open in Events & Dialogs" gives an event its script and texts. The terrain itself cannot be edited yet. |
| **Events & Dialogs** | Built like the event builder of the Emerald part: map and all its events on the left (people, signs, hidden items, warps, trigger tiles – with "+ Person", "+ Sign", "+ Trigger tile"), in the middle what the selected event does – blocks for scripts made with PokéPat (message, yes/no question, give item/Pokémon, trainer/wild battle, heal, flags, variables, only once, fanfare, wait, mugshot, warp), trainers on the map ("+ Trainer": sight range, looks from the trainer class, team and battle texts shown), readable steps for the game's own scripts (texts editable, "Rebuild from blocks") – and on the right the editor with the in-game text preview (incl. mugshot). Properties (looks with sprite, position on the map, movement, hide flag …) of every event, every text of the map and the script file as code stay editable. Scripts made from blocks keep their blocks in a comment line, so they open as blocks again. The block "🚶 Move" makes people (or the player) walk, turn, jump and show ! for small cutscenes. "🔍 Search all texts" finds any text of the game and opens it in the writer; Ctrl+Z/Y undo unsaved changes |
| **Flags & Variables** | Every flag and variable of generated/vars_flags.txt by group (story, hide people, items received, hidden items, trainers defeated, daily, map-local, free …) with its number and every place that sets, clears or checks it; "+ New flag" / "+ New variable" (a free, unused place gets your name – no number moves); "Check: never used" lists named flags nothing uses and flags that are set but never checked; trainer flags: checks that every trainer has its defeated flag (the game uses TRAINER_DEFEATED_FLAGS_START + trainer number) and fixes missing ones |
| **Wild Pokémon** | Per map: grass (12 slots), the lists that replace grass slots (morning/day, night, swarm, Poké Radar, GBA slot), surfing and the three rods (5 slots with a level range) – every slot with its fixed chance and the Pokémon's icon, a summary of how often each Pokémon appears, checks for levels and empty tables. Both layouts of pokeplatinum; files keep their format |
| **Shops** | What every Poké Mart sells and from how many badges on (include/data/mart_items.h, at most 63 items), and the special shops (city specialties, department store, Underground goods, seals) with icon and price; the maps that open each shop |
| **Mugshots** | Appears while the mod "Dialog mugshots" is on: mugshots (right of the box, or left and mirrored: `ShowMugshot MUGSHOT_X, MUGSHOT_POS_LEFT`), bigger pictures shown in the middle (`ShowPicture PICTURE_X` … `HidePicture`, up to 192 tiles of 8×8, e.g. 128×96 or 256×48) and full-screen pictures (256×192, 255 colours) on a screen of their own like the Town Map (`ShowFullscreenPicture FULLSCREEN_X`, A or B closes it). PNGs of any size are scaled to fit (proportions kept). Mods with a newer version show "⬆ Update", which keeps the settings and every own mugshot and picture |
| **Sprites** | Player character (Lucas/Dawn: battle front/back, mugshot, every overworld form, the head on the Town Map) in one place; battle pictures of all trainer classes (gallery, replace, new class); overworld characters with every frame (replace, add new characters – newer pokeplatinum builds them from PNG) |
| **Graphics** | Every picture of res/graphics by screen, as tiles grouped by folder that follow the window width (no sideways scrolling) (preview, palette, checks, replace in the old colours or with an own palette, save as 4-bit); battle healthboxes: true 4-bit and palette order of player_singles.png |
| **Mods** | Exp. Share as an on/off key item · level caps by badges (caps adjustable in PokePat) · reusable TMs · HMs without the move · dialog mugshots (pictures managed in PokePat) · defined protagonist (fixed player name, gender and rival name – the intro skips the questions). Every mod is switched on and off; switching off restores the original files exactly. All six were built together on an unchanged pokeplatinum. |

Both layouts of pokeplatinum are supported: the older one (include/data/map_headers.h, res/field/events) and the current
one with a folder per map (res/field/maps/<map>/header.json, events.json, scripts.s). Mods carry alternative forms for
files that moved between versions. Game data can add trainer classes and items.

The JSON files of pokeplatinum keep their format: single values are replaced in place, trainers are written exactly
the way the project formats them, so `git diff` shows only what you changed.

Detailed documentation of every module: [docs/README.md](docs/README.md) (German).

## Downloads

Ready-to-use packages are attached to the [GitHub releases](../../releases):

| Package | Contents |
|---|---|
| `PokePat-DecompKit-<version>-windows-x64.zip` | `PokePatDecompKit.exe` – the full graphical app (single file, no .NET installation needed) and `cli\decompkit.exe` |
| `PokePat-DecompKit-<version>-linux-x64.tar.gz` | `decompkit` – the command line version (needs the .NET 8 runtime) |

The graphical app is Windows-only (it uses WPF). On Linux the same functions are available through the command
line tool; see [Linux](#linux).

## What you need

| Tool | Required? | Used for | Windows | Linux |
|---|---|---|---|---|
| Your **pokeemerald-expansion** or **pokeplatinum project** | **yes** | everything | a folder that builds with `make`, usually inside WSL (`\\wsl.localhost\Ubuntu\home\you\your-hack`) | a folder that builds with `make` |
| Build environment of the project | for *Build ROM* | building the ROM | **WSL 2 with Ubuntu** (recommended) with the toolchain from the pokeemerald-expansion install guide; alternatively MSYS2/devkitPro on the Windows PATH | the toolchain from the pokeemerald-expansion install guide |
| .NET 8 runtime | Linux only | running the tool | not needed (included in the exe) | `sudo apt install dotnet-runtime-8.0` or [other distributions](https://learn.microsoft.com/dotnet/core/install/linux) |
| [Porymap](https://github.com/huderlem/porymap) | optional | *Open in Porymap*, map editing, bridge | `porymap.exe` | `porymap` |
| Emulator, e.g. [mGBA](https://mgba.io) | optional | *Open in emulator* after a build | `mGBA.exe` | `mgba-qt` |
| [Porytiles](https://github.com/grunt-lucas/porytiles) **2.0.0** | optional | importing tilesets (Tilesets module) | Linux binary inside WSL – install with `tools\setup-porytiles.ps1` | `tools/setup-porytiles.sh` |
| [Poryscript](https://github.com/huderlem/poryscript) | optional | *Dialogs & Events* | in your project under `tools/poryscript/` (as the Poryscript install guide for pokeemerald describes) | same |
| Git | optional | shows the branch, notices branch switches | inside WSL | `git` |

Porytiles is pinned to 2.0.0 (`tools/porytiles.lock.json`, checked by SHA-256) because the tileset output was tested
against exactly that version. The setup scripts download it from the official GitHub release; pass an already
downloaded zip if your network blocks downloads.

## Windows

1. Download and unzip `PokePat-DecompKit-<version>-windows-x64.zip` to a local folder (not to a `\\wsl.localhost` share –
   Windows may refuse to start programs from there).
2. Start `PokePatDecompKit.exe`. On the first start **Get started** opens. Check the two folders (your game inside
   WSL, default `~/pokeemerald-expansion`; the programs folder, default `%LOCALAPPDATA%\PokePat\tools`) and click
   **Install everything**. PokéPat sets up only what is missing:

   | Part | What happens |
   |---|---|
   | WSL with Ubuntu | `wsl --install -d Ubuntu` (asks for admin rights; Ubuntu then asks for a user name and password once, sometimes Windows wants a restart) |
   | Build tools | a console window runs `sudo apt-get install build-essential binutils-arm-none-eabi gcc-arm-none-eabi libnewlib-arm-none-eabi libpng-dev git python3` – enter your Linux password there |
   | pokeemerald-expansion | `git clone` of the latest release into your folder, on a new branch `pokepat` |
   | Poryscript | latest release into `tools/poryscript/`, plus the Makefile rules so `.pory` files are compiled during the build |
   | Porytiles 2.0.0 | the pinned release (SHA-256 checked) into WSL |
   | Porymap, mGBA | latest Windows releases into the programs folder |

   Every part can also be installed on its own; existing parts are kept. **Open my project** stores everything and
   opens the project. Already have a project and tools? Click **I already have a project – set paths manually…**
   (project folder required, everything else optional). Both are available later under **Setup…** in the top bar.

   **Updates:** *Check for updates* in the same window shows for every part whether a newer release exists and updates
   Porymap, mGBA and Poryscript with one click. pokeemerald-expansion is updated by merging the new release into your
   branch – only when all your changes are committed, and if the release touches the same lines as your changes the
   merge is undone completely, so nothing is left half-done. Porytiles stays pinned on purpose. With *Check for updates
   when PokéPat starts* (on by default, at most once a day) a notice appears at the top when something is new.
3. Settings are stored in `%APPDATA%\TilesetEditor\settings.json`.

## Linux

```bash
tar xzf PokePat-DecompKit-<version>-linux-x64.tar.gz
cd PokePat-DecompKit-<version>-linux-x64
chmod +x decompkit tools/setup-porytiles.sh
./decompkit install        # get started: installs pokeemerald-expansion, Poryscript and Porytiles (--check only checks)
./decompkit install --game platinum   # the same for Pokémon Platinum: build tools and pret/pokeplatinum
./decompkit setup          # or: use an existing project and set the tool paths yourself
./decompkit info           # shows what was detected in your project
./decompkit build          # builds the ROM with your build command
```

`install` takes `--project-path <folder>` (default `~/pokeemerald-expansion`), `--tag expansion/<version>` and
`--only buildtools,poryscript,…`; `install --updates` lists new versions, `install --update porymap,expansion` updates
those parts; the build tools are installed with `sudo apt-get` in your terminal.
`setup` asks the same questions as the Windows setup; press Enter to keep a suggested value or type `-` to skip an
optional tool. It can also run without questions:
`./decompkit setup --project ~/my-hack --porymap /usr/bin/porymap --emulator mgba-qt --yes`.
Settings are stored in `~/.config/TilesetEditor/settings.json`.

Commands (all use the project from `setup`; `--project <folder>` overrides it):

| Command | Purpose |
|---|---|
| `info` | project facts (version, layer mode, tilesets) |
| `build` | build the ROM |
| `compile`, `export` | tileset import (PNG → Porytiles → new secondary tileset) |
| `events` | check/compile/write events from a JSON document |
| `flags --more-flags --more-vars` | add free saved flags/variables (save-breaking) |
| `gamedata --table moves --key MOVE_TACKLE --column Name --value "Body Tackle"` | edit game data tables |
| `trainer --id TRAINER_X [--new] [--add-mon SPECIES_Y:LEVEL]` | trainers |
| `trainer-pic`, `sprite-import` | new trainer pictures and overworld sprites |
| `render-dialog --text "Hello!"` | render a message box like the game does |

Every command only shows what it would change; add `--yes` to write.

## Safety

* Nothing is written without showing the list of files first; the toolkit backs up every file it changes and restores
  all of them if one write fails.
* Only the edited lines/values change – unchanged text stays byte-identical (covered by tests on real projects).
* Changes that make existing save files unusable (more flags/variables) are marked and need confirmation.
* Work in a branch or a copy of your project and commit before larger changes.

## Building from source

Requirements: [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) (the exact version is pinned in `global.json`).

| | Windows | Linux |
|---|---|---|
| Build + unit tests + local install | `pwsh -File build.ps1` (`-IntegrationTests` also runs Porytiles/WSL tests) | `./build.sh` (command line tool only) |
| Release packages → `dist\` | `pwsh -File release.ps1` | – (build the Linux package on Windows with `release.ps1`) |
| Windows app from Linux/WSL (cross-compile) | – | `./dev-build.sh` → unpacked app in `testbuild/`; `--release` also writes both packages to `release/`; `--tests` runs the unit tests first |

The tests read a real project. Copy `test-paths.example.txt` to `test-paths.local.txt` (not committed) and point it at
a pokeemerald-expansion based checkout, or set the environment variables named there. The Platinum tests also read a
pokeplatinum checkout (`TILESETEDITOR_PLATINUM_REFERENCE`) and check that every trainer and learnset is written back
byte-identical; they pass without checking when it is missing. They are written against plain
upstream releases and pass with **1.10.2** and **1.17.2**; run them against both when you change a parser.

### Supported pokeemerald-expansion versions

Tested with 1.10.2 and 1.17.2 (the ROM builds after every kind of change the kit makes). Newer releases changed the
file formats; the kit reads and writes both styles and follows whatever the project already uses:

| Since | Change | What the kit does |
|---|---|---|
| 1.12 | `region_map_sections.json` uses `"id"` instead of `"map_section"` | reads both |
| 1.13 | graphics declared with `INCGFX_U32("x.png", ".4bpp.smol", "-mwidth 2")` instead of `INCBIN_U32("x.4bpp.lz")` | reads both; new tilesets, sprites, trainer pictures and icons are written in the project's style (sprites carry `-mwidth/-mheight` instead of a make rule) |
| 1.13 | trainer classes, trainer pictures, object event graphics, items … are `enum`s | evaluates enums; new entries become enumerators before `…_COUNT` |
| 1.13 | trainer pictures in `gTrainerPicInfo[]` (`TRAINER_FRONT_PIC(…)`) instead of `gTrainerSprites[]` | both |
| 1.13 | `ITEM_NAME("…")` for item names | editable like `_("…")` |
| 1.13 | trainers only in `trainers.party`; `trainers.h` is generated by the build | never edits the generated file |
| 1.13 | FRLG fields in `global.fieldmap.h`, counts behind `#if IS_FRLG` | Porytiles sandbox gets `src/fieldmap.c`; Emerald values are used |
| 1.16 | decorations keep description and icon in `header.h` (`description.h`, `icon.h` removed) | edits them in place |

```
src/TilesetEditor.Core      images, palettes, project parsing, events, trainers, flags, game data, maps, sprites
src/TilesetEditor.Compiler  Porytiles integration (sandbox compile, diagnostics)
src/TilesetEditor.Export    planning and transactional writing of project files
src/TilesetEditor.Toolkit   settings, setup checks, build runner, Porymap bridge, Poryscript
src/TilesetEditor.App       Windows app (WPF)
src/TilesetEditor.Cli       command line tool (Windows and Linux)
tests/TilesetEditor.Tests   xUnit tests
tools/                      Porytiles setup scripts and pin
packaging/                  Linux launcher
```

## License

[MIT](LICENSE) © 2026 Paterino-FE.

PokéPat DecompKit is a fan-made tool and is not affiliated with Nintendo, Creatures Inc., GAME FREAK inc. or The Pokémon Company.
Pokémon and all related names are trademarks of their respective owners. The kit contains no game assets; it edits your own project.
