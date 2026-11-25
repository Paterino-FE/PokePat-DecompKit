# PokéPat DecompKit

<img src="src/TilesetEditor.App/Assets/decompkit-256.png" width="96" align="right" alt="PokéPat DecompKit logo">

**A friendly editing kit for pokeemerald-expansion ROM hacks** – red roof, white walls, everything in one place.

A desktop toolkit for ROM hacks based on [pokeemerald-expansion](https://github.com/rh-hideout/pokeemerald-expansion),
made so that people without coding experience can change their game. It edits the project's source files directly –
every change shows the list of files first, makes a backup and is rolled back completely if anything fails.

| Module | What it does |
|---|---|
| **Project** | Opens your project, shows branch/version, tool paths, Porymap bridge |
| **Tilesets** | PNG → checks → Porytiles → preview in GBA colors → new secondary tileset |
| **Maps** | Maps, layouts, tilesets, scripts, connections (read only) |
| **Dialogs & Events** | Event builder with building blocks (messages, choices, items, battles, movements, scenes …), dialog preview, movement paths on the map, existing scripts in readable form |
| **Flags & Variables** | Every flag/variable with its uses, name free ones, add more |
| **Trainers** | Trainer teams (`trainers.h` or `trainers.party`), create trainers |
| **Game data** | Trainer classes, items, moves, abilities, Pokémon, types, natures, location names |
| **Secret Bases** | Locations, interiors, decorations (incl. new dolls/cushions) |
| **Sprites** | Overworld sprites (new, with own palettes), player character images, trainer pictures |
| **Graphics** | Title screen, menus, windows, battle backgrounds … view and replace |
| **Features** | `include/config` options by category |
| **Build ROM** | Runs `make` (in WSL on Windows), shows the output, opens the emulator |

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
2. Start `PokePatDecompKit.exe`. On the first start the **setup** asks for:
   1. **your pokeemerald-expansion project folder – required.** It is checked right away (Makefile, constants, maps).
   2. how the ROM is built: WSL (distribution, e.g. `Ubuntu`) or Windows cmd, and the build command (default `make -j$(nproc)`);
   3. Porymap – optional, *Skip* to leave it out;
   4. an emulator – optional;
   5. Porytiles – optional (run `tools\setup-porytiles.ps1` first if you want to import tilesets).

   Everything except the project folder can be skipped and set later with **Setup…** in the top bar or in the
   Project module.
3. Settings are stored in `%APPDATA%\TilesetEditor\settings.json`.

## Linux

```bash
tar xzf PokePat-DecompKit-<version>-linux-x64.tar.gz
cd PokePat-DecompKit-<version>-linux-x64
chmod +x decompkit tools/setup-porytiles.sh
./decompkit setup          # asks for the project folder (required) and the optional tools
./decompkit info           # shows what was detected in your project
./decompkit build          # builds the ROM with your build command
```

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

The tests read a real project. Copy `test-paths.example.txt` to `test-paths.local.txt` (not committed) and point it at
a pokeemerald-expansion based checkout, or set the environment variables named there.

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
