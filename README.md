# PokéPat DecompKit

<img src="src/TilesetEditor.App/Assets/decompkit-256.png" width="96" align="right" alt="PokéPat DecompKit logo">

**A friendly editing kit for pokeemerald-expansion ROM hacks** – red roof, white walls, everything in one place.

A desktop toolkit for ROM hacks based on [pokeemerald-expansion](https://github.com/rh-hideout/pokeemerald-expansion),
made so that people without coding experience can change their game. It edits the project's source files directly –
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
| **Build ROM** | Runs `make` (in WSL on Windows), shows the output, opens the emulator |
| **Mods** | Installs extensions into your game (checked first, shown as a file list, removable again) – e.g. *Dialog portraits* |

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
| Your **pokeemerald-expansion project** | **yes** | everything | a folder that builds with `make`, usually inside WSL (`\\wsl.localhost\Ubuntu\home\you\your-hack`) | a folder that builds with `make` |
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
a pokeemerald-expansion based checkout, or set the environment variables named there. They are written against plain
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
