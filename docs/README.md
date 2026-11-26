# PokéPat DecompKit für pokeemerald-expansion und pokeplatinum

Windows-Anwendung (C#/WPF, .NET 8) mit einem gemeinsamen Einstieg für Projekt-Werkzeuge, nach dem
Vorbild von FEBuilderGBA. Alle Module arbeiten direkt auf den Dateien des Decomp-Projekts.

| Modul | Stand |
|---|---|
| **Projekt** – Ordner öffnen, Branch/Version/ROM-Name erkennen, Werkzeugpfade | fertig |
| **Tilesets** – PNG → Prüfung → Porytiles → GBA-Vorschau → Export als Secondary Tileset | fertig |
| **Maps** – Maps/Layouts mit Tilesets, Skripten, Verbindungen, Warps (lesend) | fertig |
| **Features** – Optionen aus `include/config/*.h`; Schalter, Zahlen, Generationen direkt änderbar | fertig |
| **In Porymap öffnen**, **ROM bauen**, **Im Emulator öffnen** (Kopfleiste) | fertig |
| **Porymap-Brücke** – Porymap meldet die geöffnete Map, zwei Aktionen im Porymap-Menü *Tools* | fertig, mit echtem Porymap noch zu prüfen |
| **Dialoge & Events** – Baukasten mit 43 Bausteinen + jedem Skriptbefehl, Dialogvorschau, Personen/Schilder/Trigger anlegen | fertig |
| **Sprites** – drei Reiter: *Overworld sprites* (ansehen, ersetzen, neu eintragen – auf vorhandener oder **eigener neuer Palette**), *Player character* (alle Bilder von Brendan/May: Laufen, Rennen, Mach-/Kunstrad, Surfen, Tauchen, Feldaktion/Item-Nutzung, Angeln, Gießen, Dekorieren, Kampf vorne/hinten – ersetzen), *Trainer pictures* (ansehen, ersetzen, **neue Trainer-Bilder anlegen**) | fertig |
| **Flags & Variables** – alle Flags/Variablen mit Kategorie und jeder Verwendung (Skripte, C-Code, map.json), Filter pro Map, freie Einträge unter eigenem Namen nutzen, Sichtbarkeits-Flags von Personen ändern | fertig |
| **Trainers** – Teams ansehen/ändern (Name, Klasse, Bild, Items, KI, Pokémon mit Item/Fähigkeit/Wesen/Attacken/DVs), neue Trainer anlegen; `src/data/trainers.h` (C-Format) **und** `src/data/trainers.party` (Competitive Syntax, automatisch erkannt) | fertig |
| **Game data** – Tabellen bearbeiten: Trainerklassen (Name, Preisgeld, Ball; **neue Klassen anlegen**), Items (Name, Preis, Beschreibung), Attacken (Name, Typ, Kategorie, Stärke, Genauigkeit, AP, Beschreibung), Fähigkeiten, Pokémon (Name, Basiswerte, Typen, Fähigkeiten), Typen, Wesen, Ortsnamen | fertig (Werte, die von Config-Optionen abhängen oder in `#if` stehen, nur lesend) |
| **Graphics** – Spielgrafiken (Titelbild, Menüs, Textfenster, Kampfhintergründe …) mit Spielfarben ansehen und durch gleich große Bilder ersetzen | fertig |
| **Mod „Dialog portraits“ 1.1** – Porträt (64×64) links oder rechts über der Textbox, auf Wunsch gespiegelt (Baustein „Show portrait“ → *Where*; im Skript `setvar VAR_0x8005, PORTRAIT_POS_RIGHT` + `special ShowPortraitAt`); **Bilder** bis 128×96 (Karte, Brief …) mittig über der Textbox (Bausteine „Show picture“/„Hide picture“, `special ShowPicture`), unter Mods → Dialog portraits aus jedem PNG (größere werden verkleinert, 15 Farben). Ein Bild sind sechs 64×32-Sprites, gespeichert als 64×192-Streifen in `graphics/portraits/pictures/`. „⬆ Update“ behält vorhandene Porträts. | fertig (im Emulator noch anzusehen) |

Die Programmoberfläche ist englisch (seit 25.11.2026), diese Doku bleibt deutsch; Knopfnamen stehen in der englischen Form.

## Spiel wählen: Smaragd oder Platin

Beim ersten Start (und unter **Setup…**) wählst du in *Get started* zuerst das Spiel:

* **Pokémon Smaragd** – pokeemerald-expansion (GBA). Alle Module wie bisher.
* **Pokémon Platin** – [pret/pokeplatinum](https://github.com/pret/pokeplatinum) (NDS). *Get started* installiert die
  Build-Tools aus der INSTALL.md von pokeplatinum (`bison flex g++ gcc-arm-none-eabi git make ninja-build pkg-config python3 …`),
  klont pokeplatinum auf einen eigenen Branch `pokepat` und auf Windows melonDS als Emulator. Poryscript, Porytiles und
  Porymap werden für Platin nicht gebraucht.

Ein vorhandenes Projekt wird beim Öffnen automatisch erkannt (Smaragd: `include/fieldmap.h` + `src/data/tilesets/headers.h`,
Platin: `platinum.us/`, `res/pokemon/`, `include/data/map_headers.h`); die Navigation zeigt dann die Module dieses Spiels.

### Module für Platin

| Modul | Inhalt |
|---|---|
| Project | Ordner, Branch, Anzahl Pokémon/Attacken/Items/Trainer/Maps, Build-Befehl, ROM (`build/pokeplatinum.us.nds`), DS-Emulator |
| Pokémon | Kampf-Sprites vorne/hinten, normal und schillernd, weibliche Varianten; Sprite durch PNG ersetzen (80×80 oder 160×80; alle vier Bilder teilen eine 16-Farben-Palette, die übrigen Bilder werden umgefärbt, die Shiny-Farben von den nächstliegenden alten Farben übernommen); Level-Attacken; Entwicklungen |
| Trainers | Name, Klasse (mit Bild), Doppelkampf, Items, KI-Flags, Team (Pokémon, Level, Item, Attacken, IVs), Texte (`\n` neue Zeile, `\r` neue Box, `\f` scrollen) |
| Game data | Pokémon (Werte, Typen, Fähigkeiten, Fangrate, Geschlecht, Wachstum, Ei-Gruppen, Items, Pokédex-Texte), Attacken, Items, Namen von Fähigkeiten, Typen, Wesen, Trainerklassen und Orten |
| Maps | Event-Editor wie in Porymap: Events anklicken und ziehen (Kachel-Raster), Trigger an der Ecke größer/kleiner ziehen, mit „+ Person/Trainer/Sign/Warp/Trigger“ und Klick auf die Karte neue setzen, Eigenschaften seitlich bearbeiten (Warp-Ziele aus den Warps der Zielmap), Entf/„Delete“ löscht (Warnung, wenn Skripte die Person noch nutzen), „Open in Events & Dialogs“ für Skript und Texte. Neue Events ohne Skript bekommen `"script": 0`. Das Gelände selbst ist (noch) nicht bearbeitbar. Dazu die Vorschau jeder Map aus den Land-Daten, Personen mit ihrem Overworld-Sprite in Startblickrichtung: begehbar/blockiert, Bodenverhalten (Gras, Wasser, Türen …), Props (Gebäude, Bäume) und Events. Platin-Maps sind 3D-Modelle; einen Map-Editor für pokeplatinum gibt es noch nicht. |
| Trainers – neue Trainer | „+ New trainer…“: Kopie des gewählten Trainers (Team, Items, KI, Texte) mit Name und Klasse; Datei `res/trainers/data/<klasse>_<name>.json`, Konstante vor `MAX_TRAINERS` in `generated/trainers.txt` und das eigene Besiegt-Flag (der Build bricht ab, wenn es mehr Trainer als Trainer-Flags gibt). |
| Trainers – Klassen | „Neue Trainerklasse…“: Konstante am Ende von generated/trainer_classes.txt, Bilddateien aus einer ähnlichen Klasse kopiert (optional neues 80×80-Bild), Name in beiden Textbanken, Geschlecht (`include/data/trainer_class_genders.h`) und Preisgeld (`trainer_class_prize_mul.h`) – ohne diese beiden Einträge liest das Spiel über das Tabellenende hinaus. „Replace…“ unter dem Bild ersetzt das Kampfbild einer Klasse. |
| Events & Dialogs | Aufgebaut wie der Event-Baukasten von Smaragd: links Map und alle Events (Personen, Schilder, versteckte Items, Warps, Trigger-Felder; „+ Person“, „+ Sign“, „+ Trigger tile“, Duplicate, Delete), in der Mitte was das gewählte Event tut – Bausteine bei Skripten aus PokéPat, lesbare Schritte bei Original-Skripten (Texte direkt änderbar, „Rebuild from blocks“) –, rechts der Editor mit Vorschau in der Spielschrift (inkl. Mugshot). Eigenschaften jedes Events (Aussehen mit Sprite, Position per Klick auf die Karte, Bewegung, Versteck-Flag …), alle Texte der Map („All texts“) und die Skriptdatei als Code bleiben bearbeitbar. Bausteine werden als Kommentarzeile `/* PokePat blocks: … */` über dem Skript gespeichert und öffnen sich wieder als Bausteine. `'` und `"` werden automatisch zu ’ “ ” (die Spielschrift kennt nur diese), Zeichen, die das Spiel nicht darstellen kann, werden angezeigt. |
| Events – Bausteine | Nachricht, Ja/Nein-Frage, Mugshot · Item/Pokémon geben, Heilen · Trainer-/Wildkampf · „nur einmal“, Flag setzen/löschen, Variable setzen · Fanfare, Warten, Warp. Änderungen gehen sofort in Skript und Textbank (gespeichert mit „Save“); Befehle passend zur pokeplatinum-Version. Neue Trigger-Felder setzen am Ende ihre Variable, damit sie nur einmal auslösen. |
| Flags & Variables | Alle Flags und Variablen aus `generated/vars_flags.txt` nach Gruppe (Story, Personen verstecken, Geschenke erhalten, versteckte Items, besiegte Trainer, täglich, map-lokal, frei …) mit Nummer und jeder Stelle, die sie setzt, löscht oder prüft; freie Flags für eigene Events (auch benennen, die Nummer bleibt). „+ New flag“/„+ New variable“ vergibt einen freien, unbenutzten Platz unter deinem Namen (nie Flag 0, das bedeutet „kein Flag“; die Plätze direkt nach den Trainer-Flags bleiben für neue Trainer frei). „🔍 Check: never used“ listet benannte Flags, die nirgends vorkommen, und Flags, die gesetzt, aber nie geprüft werden. Trainer-Flags: das Spiel nutzt `TRAINER_DEFEATED_FLAGS_START + Trainernummer` – PokePat prüft, dass jeder Trainer sein Flag hat, und vergibt fehlende auf freie Plätze. |
| Wild Pokémon | Pro Map: Gras (12 Plätze mit festen Chancen 20/20/10/10/10/10/5/5/4/4/1/1 %), Ersatzlisten (Morgen/Tag und Nacht für Platz 3–4, Schwarm für 1–2, Pokéradar für 5/6/11/12, GBA-Slot für 9–10), Surfen und drei Angeln (5 Plätze, 60/30/5/4/1 %, Level-Spanne). Icons, Übersicht „wer wie oft“, Prüfung von Leveln und leeren Tabellen. Beide pokeplatinum-Versionen, Dateien behalten ihr Format. |
| Events – Trainer | „+ Trainer“ setzt einen Trainer auf die Map: Sichtweite, Blickrichtung, Aussehen automatisch aus der Trainerklasse; Team und Kampftexte werden angezeigt, Sprung ins Trainers-Modul. Beide Formate (neu: `"trainer": {id, sight_range}`, alt: `"script": "TRAINER_X"` + `"data": [Sicht]`). |
| Shops | Die Sortimente aus `include/data/mart_items.h`: die gemeinsame Liste aller Poké Marts (jedes Item mit der Orden-Stufe, ab der es verkauft wird: 0 → 1–2 → 3–4 → 5–6 → 7 → 8 Orden; höchstens 63 Items) und die Sonderläden (Stadt-Spezialitäten, Kaufhaus Schleiede, Untergrund-Waren, Siegel). Mit Icon und Preis, Reihenfolge ändern, Items hinzufügen/entfernen; zu jedem Laden die Maps, die ihn öffnen. Gespeichert werden nur geänderte Listen. |
| Events – Textsuche | Tab „🔍 Search all texts“: alle Texte des Spiels (~700 Textbanken) nach Wörtern oder Text-ID durchsuchen, auf Map-Texte beschränkbar; ein Treffer öffnet die Map mit diesem Text im Editor. |
| Events – Bewegung | Baustein „🚶 Move“ für kleine Zwischensequenzen: Person (oder Spieler) läuft Schritte (langsam/normal/schnell/rennen), dreht sich, springt, „!“/„!!“, wartet, wird (un)sichtbar; auf Wunsch wartet das Skript, bis die Bewegung fertig ist. Die Bewegungsdaten werden wie im Spiel ausgerichtet ans Skript angehängt. |
| Maps & Events – Bearbeiten | Mehrere Events per Rahmen oder Strg+Klick auswählen und zusammen ziehen/mit Pfeiltasten verschieben (Umschalt = 5 Felder), Strg+C/Strg+V (an der Mausposition), Strg+D duplizieren, Entf löschen. **Strg+Z/Strg+Y** machen ungespeicherte Änderungen im Map-Editor und in Events & Dialogs rückgängig. **Warps in beide Richtungen**: beim neuen Warp optional der passende Gegen-Warp auf der Zielmap. |
| Backups | Jede Änderung von PokéPat (neueste zuerst) mit neuen/geänderten/gelöschten Dateien und ob sie seitdem erneut geändert wurden. „↩ Undo this change“ nimmt eine Änderung zurück, „⏪ Back to before this“ setzt das Projekt auf den Stand davor (auch alle späteren Änderungen). Rückgängig machen hat selbst ein Backup. Alte Backups (> 30 Tage) löschen. |
| Mugshots | Erscheint, solange die Mod „Dialog mugshots“ an ist. Mugshots rechts neben der Textbox oder links (gespiegelt, damit die Person zum Text schaut): `ShowMugshot MUGSHOT_X, MUGSHOT_POS_LEFT`, im Baustein „🖼 Mugshot“ per Auswahl. Bilder (z. B. Karten, Briefe) mittig über der Textbox: `ShowPicture PICTURE_X` … `HidePicture` (verschwinden auch mit CloseMessage), Baustein „🗺 Picture“. Grenze: 192 Kacheln à 8×8 (z. B. 128×96, 96×128, 256×48) – der Bereich des Ortsnamen-Einblenders. **Vollbilder** (ab Mod 1.2): 256×192 mit bis zu 255 Farben auf einem eigenen Bildschirm wie die Stadtkarte – `ShowFullscreenPicture FULLSCREEN_X` blendet die Map aus, zeigt das Bild bis A oder B und kehrt zurück; im Baustein „🗺 Picture“ wählbar (schließt eine offene Textbox vorher). Gespeichert als 8-Bit-PNG hinter den Bildern im Archiv `area_win_gra.narc`. PNGs jeder Größe werden passend verkleinert (Seitenverhältnis bleibt). Seit Mod 1.1 nutzen Mugshots die freien Paletten 14/15 statt der Text-Palette 13. Veraltete Mods sind sichtbar: „⬆ n update(s) available“ unter Mods in der Navigation (geprüft kurz nach dem Start, auch ohne das Modul zu öffnen), ein Hinweis mit „⬆ Update all“ oben im Modul und ein Schild „⬆ alt → neu“ an der Mod. Ein Update schaltet die alte Version aus und die neue an; eigene Mugshots, Bilder und Einstellungen bleiben. |
| Sprites | Kampfbilder aller Trainerklassen (Galerie, ersetzen, neue Klasse); Overworld-Figuren mit allen Frames (ersetzen, neue Figuren anlegen – neueres pokeplatinum baut sie aus PNGs: Bild nach `res/graphics/field_sprites/npc/`, Eintrag in `meson.build` und `field_sprites.order`, Konstante, vier Tabelleneinträge in `object_event_gfx_data.c`, kopiert von einer Vorlagenfigur) |
| Sprites – Spielfigur | Alle Bilder von Lucas und Dawn an einer Stelle: Kampfbild vorne, Rückansicht (Wurf-Animation), Scan-Form, Begegnungs-Mugshot und alle Overworld-Formen (laufen, Rad, Surfer, Angeln, Pokétch, Speichern …) – anklicken = ersetzen |
| Game data – Items als Karten | Items als Karten mit Icon (Name, Preis, Tasche, Effekt, Beschreibung); Suche und Filter wie in der Tabelle, Umschalter „Cards with icons“. „Icon…“ ändert das Icon: gemeinsam genutzte Icons für alle oder ein eigenes Icon nur für dieses Item (bei TMs immer eigenes, weil alle TMs ein Bild mit verschiedenen Paletten teilen) |
| Graphics – Kategorien | Bilder als gleich große Kacheln, nach Ordner gestaffelt; die Reihen passen sich der Fensterbreite an (kein seitliches Scrollen). Bereiche: Kampffelder (Plattformen mit Tag/Abend/Nacht-Palette), Trainer-Rückansichten, Kampfoberfläche, Typ-Icons, Ballwürfe, Titelbild, Textfenster, Menüs, Beutel, Pokédex, Trainerpass, Karte, Pokétch, Untergrund, Kampfzone, Wettbewerbe … Die großen Kampfhintergründe liegen in pokeplatinum noch als fertiges Archiv vor (`res/prebuilt/battle/graphic/batt_bg.narc`) und sind noch nicht als Bilder bearbeitbar. |
| Game data – Hinzufügen | „+ Add trainer class…“ und „+ Add item…“ (Kopie eines ähnlichen Items vor `MAX_ITEMS`; Name, Preis, Beschreibung frei) |
| Graphics | Alle Bilder unter `res/graphics` nach Bildschirm (Vorschau, Palette, Prüfung, ersetzen in den alten Farben oder mit eigener Palette, als 4-bit speichern); Kampf-Healthboxen: prüft echtes 4-bit-PNG und Palettenreihenfolge wie player_singles.png, repariert beides verlustfrei, ersetzt Bilder (Farben werden exakt auf die Master-Palette abgebildet) |
| Mods | EP-Teiler als Basis-Item zum Ein-/Ausschalten (Flag 0x053C), Level-Caps nach Orden (Werte in PokePat änderbar), wiederverwendbare TMs, VMs ohne erlernte Attacke (VM im Beutel + Orden), Dialog-Mugshots (eigene Skriptbefehle `ShowMugshot`/`ClearMugshot`, Bilder in PokePat verwalten). Defined Protagonist (Spielername, Geschlecht und Rivalenname fest – das Intro überspringt die Fragen; Werte in den Mod-Einstellungen). Jede Mod wird ein- und ausgeschaltet; Ausschalten stellt die Originaldateien exakt wieder her (Vanilla). Mods, deren Änderungen schon im Projekt stecken, erkennt PokePat und kann sie ebenfalls ausschalten. Alle sechs zusammen auf einem unveränderten pokeplatinum gebaut. |

PokePat unterstützt beide Varianten von pokeplatinum: die ältere (`include/data/map_headers.h`, `res/field/events/…`) und die aktuelle mit einem Ordner pro Map (`res/field/maps/<map>/header.json`, `events.json`, `scripts.s`; Skripte per Label). Mods bringen für Dateien, die zwischen den Versionen umgezogen sind, Alternativen mit.

Build-Befehl für Platin: `make release` – ein einfaches `make` vergleicht das ROM zusätzlich mit dem Original und schlägt nach jeder Änderung fehl.

Die JSON-Dateien von pokeplatinum behalten ihr Format: einzelne Werte werden an Ort und Stelle ersetzt, Trainer werden
genau so geschrieben, wie das Projekt sie formatiert – `git diff` zeigt nur die echten Änderungen.

## Einrichtung (einmalig)

Voraussetzungen: Windows 10/11, WSL mit der Distribution, in der das Projekt bisher gebaut wird
(hier `Ubuntu` mit `make`, `arm-none-eabi-gcc` usw.), .NET 8 Desktop Runtime (ist installiert).

1. **.NET 8 SDK** (nur zum Bauen der App). Ohne Admin-Rechte:
   ```powershell
   Invoke-WebRequest https://dot.net/v1/dotnet-install.ps1 -OutFile $env:TEMP\dotnet-install.ps1
   & $env:TEMP\dotnet-install.ps1 -Channel 8.0 -InstallDir "$env:USERPROFILE\.dotnet"
   ```
   `build.ps1` findet das SDK dort automatisch. Die Version ist in `global.json` festgelegt (8.0.4xx).

2. **Porytiles 2.0.0** (festgelegt in `tools/porytiles.lock.json`, mit SHA-256-Prüfsumme). Porytiles 2.x gibt es
   nur für Linux/macOS, daher läuft es in WSL:
   ```powershell
   pwsh -ExecutionPolicy Bypass -File tools\setup-porytiles.ps1              # lädt, prüft, installiert in WSL
   pwsh -ExecutionPolicy Bypass -File tools\setup-porytiles.ps1 -ZipPath C:\Downloads\porytiles-linux-amd64.zip   # offline
   ```
   Ziel: `~/.local/share/tileseteditor/porytiles-2.0.0/…/porytiles` in WSL. Der Download erfolgt auf der Windows-Seite,
   WSL braucht keinen Internetzugang.

3. **App bauen**:
   ```powershell
   pwsh -ExecutionPolicy Bypass -File build.ps1                    # Restore (gesperrt), Build, Unit-Tests, Publish
   pwsh -ExecutionPolicy Bypass -File build.ps1 -IntegrationTests  # zusätzlich echte Porytiles-Läufe
   ```
   Liegt das Repository auf `\\wsl.localhost\…`, landet die App in `%LOCALAPPDATA%\TilesetToolkit\app`
   (Windows blockiert bzw. fragt beim Start von Programmen auf `\\`-Freigaben), sonst in `artifacts\app`.
   Hinweis: Windows PowerShell 5.1 führt Skripte von `\\wsl.localhost` nicht aus – `pwsh` (PowerShell 7) verwenden.

## Starten

`%LOCALAPPDATA%\TilesetToolkit\app\TilesetEditor.exe` – optional mit Projektordner als Argument:
`TilesetEditor.exe "\\wsl.localhost\Ubuntu\home\<user>\<projekt>"`. Das zuletzt geöffnete Projekt wird beim
Start automatisch wieder geöffnet.

## Projektansicht und Werkzeuge

*Projekt öffnen…* wählt den Ordner mit `Makefile` und `include/fieldmap.h`. Angezeigt werden Ordner, Git-Branch
und Commit (direkt aus `.git` gelesen), Expansion-Version (`include/constants/expansion.h`), ROM-Name
(`FILE_NAME` im Makefile), WSL-Distribution, Layer-Modus, Limits, Anzahl Layouts und ob Poryscript verwendet wird.

Werkzeugeinstellungen werden pro Projekt in `%APPDATA%\TilesetEditor\settings.json` gespeichert:

| Einstellung | Standard |
|---|---|
| Porymap | Pfad zu `porymap.exe` |
| Build-Umgebung | `Wsl` (Projekt auf `\\wsl.localhost\<Distro>` → diese Distribution), alternativ `Windows` (cmd) |
| Build-Befehl | `make -j$(nproc)` – läuft im Projektordner, in WSL über `bash -lc` |
| ROM-Datei | leer = `FILE_NAME.gba` aus dem Makefile |
| Emulator | optional, z. B. `mGBA.exe` |
| Porytiles | Modus `Wsl`, Programmpfad, Distribution, Knopf *Prüfen* zeigt die Version |

## Tileset-Arbeitsablauf

1. **Primary wählen** (z. B. `gTileset_General`): dessen Paletten 0–5 werden angezeigt und beim Kompilieren
   mitbenutzt – Tiles, deren Farben komplett in eine Primary-Palette passen, verwenden diese.
2. **PNG importieren** per Drag & Drop oder Knopf. Eine Datei = eine Ebene (Standard *Mitte*, d. h. Bodenebene bei
   Layer-Typ *Normal*). 2–3 Dateien = Ebenen; Zuordnung über `bottom/middle/top` bzw. `unten/mitte/oben` im Dateinamen.
   Breite und Höhe müssen Vielfache von 16 sein; jede Breite ist erlaubt, Metatiles werden zeilenweise gelesen.
   Transparent ist Alpha 0 oder `#FF00FF`.
3. **Vorprüfung** (sofort): >15 Farben je 8×8-Tile, Inhalt auf drei Ebenen bei Dual-Layer, Metatile-/Tile-Kapazität,
   Palettenkapazität (Paletten 6–12), halbtransparente Pixel, auf dem GBA nicht unterscheidbare Farben.
   Problemstellen werden im Raster rot/orange markiert; Klick auf eine Meldung springt zur Stelle.
4. **Auswahl**: Klick auf ein Tile zeigt dessen Farben je Ebene; nach dem Kompilieren zusätzlich Layer-Typ,
   Tile-ID, Palettenbank und die 16 Farben der Bank in GBA-Darstellung.
5. **Farben**: Liste aller Farben; *Ersetzen* (Zielfarbe eintippen oder Farbfeld anklicken) und *Auf GBA-Farben runden*.
   Werden dabei Farben zusammengeführt, kommt vorher eine Bestätigung mit Liste. *Rückgängig* ist möglich, die
   Quelldatei bleibt unverändert.
6. **Kompilieren (Porytiles)** läuft im Hintergrund in einer Sandbox unter `%LOCALAPPDATA%\TilesetEditor\work`
   (die letzten 5 bleiben zur Fehlersuche erhalten). Das echte Projekt wird dabei nur gelesen. Porytiles-Diagnosen
   werden auf die Rasterposition im Original-PNG zurückgerechnet. Der Reiter *Kompiliert (GBA-Farben)* zeigt das
   Ergebnis so, wie der GBA es zusammensetzt.
7. **Exportieren…** zeigt alle anzulegenden und zu ändernden Dateien. Erst nach Bestätigung wird geschrieben.

### Was der Export im Projekt anlegt bzw. ändert

Neu (Ordnername nach Porymap-Regel aus dem Namen, z. B. `gTileset_MyTown` → `my_town`):

```
data/tilesets/secondary/<ordner>/tiles.png                 indiziert (Porytiles „true-color“: Bank im oberen Nibble)
data/tilesets/secondary/<ordner>/metatiles.bin             8 Einträge à 16 Bit je Metatile
data/tilesets/secondary/<ordner>/metatile_attributes.bin   16 Bit: Verhalten MB_NORMAL, Layer-Typ aus den Ebenen
data/tilesets/secondary/<ordner>/palettes/00.pal … 15.pal  JASC-PAL mit CRLF (wie .gitattributes verlangt)
data/tilesets/secondary/<ordner>/tileset_editor/…           optional: RGBA-Quellebenen + Manifest (nicht Teil des Builds)
```

Angehängt (genau wie Porymaps „New Tileset“, `Project::createNewTileset`; Stil aus dem Projekt übernommen:
`.isCompressed`, `.callback = NULL`, 16 Paletten-INCBINs, `.4bpp.lz`, `.gbapal`, LF):

```
src/data/tilesets/headers.h     const struct Tileset gTileset_<Name>
src/data/tilesets/graphics.h    gTilesetPalettes_<Name>, gTilesetTiles_<Name>
src/data/tilesets/metatiles.h   gMetatiles_<Name>, gMetatileAttributes_<Name>
```

Sicherheit: Namens-, Ordner- und Symbolkollisionen werden geprüft; das Projekt wird vor dem Schreiben mit
den geplanten Änderungen neu eingelesen; geänderte Dateien werden nach
`%LOCALAPPDATA%\TilesetEditor\backups\<zeit>-<name>` gesichert; bei einem Schreibfehler wird alles
zurückgenommen; wurde eine Datei seit der Planung verändert (z. B. durch Porymap), bricht der Export ab.
Bestehende Tilesets werden nie verändert. Die Zuordnung zu einer Map erfolgt in Porymap.

Der Makefile braucht keine Änderung: neue Ordner werden über die generischen Regeln
`%.4bpp: %.png`, `%.gbapal: %.pal`, `%.lz: %` gebaut (gbagfx nimmt bei 8-Bit-PNGs den Index mod 16).

## Projekt-Sync (Änderungen außerhalb des Toolkits)

Das Toolkit prüft alle 3 Sekunden Größe und Zeitstempel einiger Schlüsseldateien (`layouts.json`, `map_groups.json`,
die drei Tileset-Registrierungsdateien, `src/graphics.c`, `fieldmap.h`, `Makefile`, `.git/HEAD`). Ändert Porymap,
Git oder ein Editor eine davon, wird das Projekt neu eingelesen und ein gelber Hinweis zeigt, was sich geändert hat
(z. B. „Git-Branch gewechselt“). Nicht exportierte Arbeit im Tileset-Editor bleibt erhalten. Eine zuvor gebaute ROM
gilt danach nicht mehr als aktuell. Eigene Schreibvorgänge des Toolkits lösen keinen Hinweis aus. Abfragen statt
Dateisystem-Benachrichtigungen, weil diese auf `\\wsl.localhost`-Freigaben nicht zuverlässig ankommen.

Umgekehrt überwacht Porymap selbst die Projektdateien: Nach einem Export bietet ein geöffnetes Porymap das Neuladen an.

## Dialoge & Events (Baukasten)

Events ohne Programmierkenntnisse bauen:

1. Map suchen und wählen, **+ Neues Event**, Titel und Auslöser (Person, Schild, Trigger-Feld) festlegen.
2. Bausteine aus der Bibliothek (mit Suchfeld) hinzufügen. Neue Bausteine landen hinter dem ausgewählten Baustein oder im
   ausgewählten Zweig; ↑/↓ verschiebt. Jeder Baustein hat Felder mit Auswahllisten aus dem Projekt (Flags, Variablen, Items,
   Pokémon, Trainer, Maps, Musik, Sounds, Wetter, Specials, Bewegungsschritte, Personen der Map):

   | Kategorie | Bausteine |
   |---|---|
   | Dialog | Nachricht, Ja/Nein-Frage, **Auswahlmenü** (2–6 Antworten, je ein Zweig + „abgebrochen (B)“), Pokémon-/Item-Name in Text (`{STR_VAR_1}` …), Notiz |
   | Kampf | Trainerkampf, Doppelkampf, Wilder Kampf, Wenn Trainer besiegt |
   | Pokémon | Pokémon geben, Wenn Team voll, Team heilen, Pokémon-Ruf |
   | Items & Geld | Item geben, Item wegnehmen, Wenn Item im Beutel, Geld geben/nehmen, Wenn genug Geld, **Laden (Pokémart)** mit Itemliste |
   | Flags & Variablen | Wenn Flag gesetzt, Flag setzen/löschen, Variable setzen/erhöhen/verringern, Wenn Variable (==, !=, <, <=, >, >=) |
   | Personen & Bewegung | Bewegung (Schritte wie `walk_up*2, face_down`), Sprechblase (!, ?, ♥ …), Drehen, Person verschwinden/erscheinen |
   | Map & Welt | Warp, Wetter, Bildschirm ab-/aufblenden |
   | Ton & Zeit | Soundeffekt, Fanfare, Musik wechseln, Map-Musik wiederherstellen, Warten |
   | Fortgeschritten | Special-Funktion, anderes Skript aufrufen, **Befehl** = jeder der 328 Befehle aus `asm/macros/event.inc` mit seinen Parametern und Beschreibung |

   Verzweigungen (Wenn …) haben die Zweige „Dann“/„Sonst“. In der Vorschau fragt das Toolkit bei Bedingungen, die es nicht
   kennen kann (Item im Beutel, Trainer besiegt …), ob sie erfüllt sind; Texte vor/nach Trainerkämpfen werden gezeigt.
3. Text einfach eintippen: Leerzeile = neues Textfenster, Zeilenumbruch = neue Zeile; der Umbruch nach Pixelbreite
   passiert automatisch mit den Zeichenbreiten aus `tools/poryscript/font_config.json` (208 px, 2 Zeilen). Rechts
   erscheint sofort, wie jedes Textfenster im Spiel aussieht. Zeichen, die es in `charmap.txt` nicht gibt, sowie `$`
   und `\` werden gemeldet; gerade Anführungszeichen werden zu „“ (Poryscript kann `\"` nicht).
4. Flags und Items kommen aus `include/constants/flags.h`, `items.h` und `src/data/items.h`; *Freies Flag nehmen*
   wählt ein als „Unused“ markiertes Flag, das noch kein Toolkit-Event benutzt.
5. **▶ Vorschau abspielen**: GBA-Bildschirm (240×160, 3-fach) mit `message_box.png`, Schrift `latin_normal.png`,
   Textfarben aus `text_pal1.pal`, Schreibmaschinen-Effekt mit Textgeschwindigkeit (8/4/1 Frames je Zeichen),
   Weiter-Pfeil, Scrollen bei `\l`, neues Fenster bei `\p`, JA/NEIN-Fenster (Rahmen 1–20 wählbar), simulierte Flags,
   Spielername. *Eigene Fenster-PNG laden…* zeigt ein eigenes Fensterdesign (56×16 wie `message_box.png` oder 24×24
   wie `text_window/1.png`) nur in der Vorschau.
6. **Auslösen durch**: Person/Schild/Trigger der Map wählen → *Event hier zuweisen*. **+ Neu auf der Map…** legt passend zum
   Auslöser des Events direkt im Toolkit eine Person (Sprite, Position, Verhalten, Höhe), ein Schild oder ein Trigger-Feld
   (Variable + Wert) an, optional gleich mit dem aktuellen Event – im Porymap-Format an `object_events`, `bg_events` bzw.
   `coord_events` der `map.json` angehängt (Feldreihenfolge wie bei vorhandenen Einträgen desselben Typs).
7. **Prüfen** übersetzt den Code mit dem Poryscript des Projekts; **Ins Projekt speichern…** zeigt die Dateiliste und
   schreibt transaktional (Sicherung, Rollback). Danach *▶ ROM bauen*.

Geschriebene Dateien (sonst nichts):

```
data/scripts/toolkit_events.json   Quelle (vom Toolkit gelesen und geschrieben)
data/scripts/toolkit_events.pory   erzeugtes Poryscript mit Prüfsumme – Handänderungen werden erkannt
data/scripts/toolkit_events.inc    mit tools/poryscript übersetzt (identisch zu dem, was make erzeugt)
data/event_scripts.s               einmalig eine Zeile: .include "data/scripts/toolkit_events.inc"
data/maps/<Map>/map.json           nur beim Zuweisen: genau der Wert "script" des gewählten Events (byteweise)
```

Handgeschriebene Map-Skripte werden nie verändert; sie werden nur gelesen, um doppelte Skriptnamen zu verhindern.
Erzeugter Code (Beispiel): `script Route101_TK_Event { lock faceplayer msgbox("…", MSGBOX_DEFAULT) giveitem(ITEM_POTION, 1) closemessage release end }`.
Die CLI kann ein Textfenster als Bild ausgeben: `tileset-editor-cli render-dialog --project <ordner> --text "…" [--yesno] [--frame 3]`.

## Sprites: eigene Paletten, Spielerfigur, Trainer-Bilder

- **Eigene Palette** (Reiter *Overworld sprites*, Schritt 2 „Create a new palette from the image's colors“): legt `graphics/object_events/palettes/<name>.pal` an, die Konstante `OBJ_EVENT_PAL_TAG_<NAME>` (nächster freier Wert im Bereich 0x1100–0x11FD, nach dem höchsten vorhandenen eingetragen), das `INCBIN_U16` in `object_event_graphics.h` und den Eintrag in `sObjectEventSpritePalettes` (`src/event_object_movement.c`, vor dem Tabellenende). `reflectionPaletteTag`/`paletteSlot` wie bei den eigenen Sprites des Branches (`OBJ_EVENT_PAL_TAG_NONE`, `PALSLOT_NPC_2`). CLI: `sprite-import … --new-palette Name`.
- **Player character**: jedes Bild durch ein gleich großes ersetzen (Frames in derselben Reihenfolge). Overworld-Bilder teilen sich `brendan.pal`/`may.pal` – „neue Farben“ ändert alle Bilder der Figur; Tauchen nutzt eine eigene Palette; das Kampf-Rückenbild nutzt die Palette des Frontbilds. Eine *dritte* Spielfigur (neues Geschlecht) ist Code-Arbeit und nicht enthalten.
- **Trainer pictures → Add new trainer picture…**: 64×64 PNG, höchstens 15 Farben + Transparenz → `graphics/trainers/front_pics/<name>.png`, eigene `graphics/trainers/palettes/<name>.pal`, `TRAINER_PIC_<NAME>` vor `TRAINER_PIC_COUNT` (Zähler +1), INCBINs und `TRAINER_SPRITE(…)` in `src/data/graphics/trainers.h`. Danach im Trainers-Modul als *Picture* wählbar. CLI: `trainer-pic --name SeaCaptain --png pic.png --yes`.

## Flags & Variables

Liest `include/constants/flags.h` und `vars.h` und durchsucht `data/maps/*/scripts.inc`, `*.pory`, `data/scripts/*` und `src/*.c` nach Verwendungen (setzt / löscht / prüft) sowie alle `map.json` (Sichtbarkeits-Flag von Personen, Trigger-Variablen, versteckte Items). Mit dem Map-Filter sieht man genau die Flags einer Map.

- **New flag… / New variable…** benennt den nächsten freien, nirgends verwendeten `FLAG_UNUSED_…` bzw. `VAR_UNUSED_…` um (Wert bleibt, Spielstände bleiben gültig). Nur diese eine Zeile ändert sich.
- **More flags… / More variables…** (wird auch angeboten, wenn bei „New flag…“ nichts mehr frei ist): legt 64 neue gespeicherte Flags (Block `TOOLKIT_FLAGS_START…TOOLKIT_FLAGS_END` hinter den Daily-Flags, `FLAGS_COUNT` zählt ihn mit) bzw. 16 neue Variablen (`VARS_END` +16) als `FLAG_UNUSED_…`/`VAR_UNUSED_…` an; danach wie gewohnt benennen. **Spielstand-brechend**: alte Spielstände passen danach nicht mehr. Ist der Spielstand voll, bricht der Build mit einem SaveBlock1-Fehler ab → unter Features → Save data `FREE_…`-Optionen nutzen. CLI: `flags --more-flags --more-vars --yes`.
- **Save description…** ändert nur den Kommentar am Ende der `#define`-Zeile.
- **Persons on <Map>**: das Flag, bei dem eine Person versteckt ist („0“ = immer sichtbar), direkt ändern; geschrieben wird nur das Feld `flag` in der `map.json`.

## Trainers

Das Format wird automatisch erkannt: `COMPETITIVE_PARTY_SYNTAX TRUE` → `src/data/trainers.party` (Showdown-Stil, Upstream-Standard; lesbare Namen werden beim Schreiben beibehalten, nicht vorhandene Felder bleiben weg, getestet: alle 855 Upstream-Trainer bleiben byte-gleich), sonst `src/data/trainers.h` (C-Format, wie in Reyamon). Beim Speichern wird nur der Block des geänderten Trainers neu geschrieben; unveränderte Felder und Pokémon behalten ihren exakten Text (getestet: alle 847 Trainer bleiben byte-gleich). Neue Trainer bekommen zuerst eine freie Nummer unterhalb von `TRAINERS_COUNT` (Lücke), sonst `TRAINERS_COUNT` (erhöht), höchstens bis `MAX_TRAINERS_COUNT`. CLI: `trainer --project <p> --id TRAINER_X [--new] [--name N] [--add-mon SPECIES_X:LV] [--moves A,B] [--yes]`.

## Graphics

Alle Ordner unter `graphics/` außer Pokémon, Trainer, Overworld-Sprites und Tilesets. Farbquelle: `x.pal` (dann nutzt das Spiel diese Datei), sonst die Palette des PNG (`x.gbapal` wird daraus gebaut), sonst eine gemeinsame Palette (dann sind nur „auf aktuelle Farben abbilden“ möglich). **Replace image…**: gleiche Größe, Transparenz = Index 0, Vorher/Nachher-Vergleich, Dateiliste, Sicherung. Bilder mit `.bin`-Tilemap (z. B. Titel-Logo) sind Kachel-Bögen – die Anordnung der Kacheln beibehalten.

## Game data

Liest die C-Tabellen des Branches (`src/battle_main.c`, `src/data/items.h`, `moves_info.h`, `abilities.h`, `pokemon/species_info/*`, `types_info.h`, `src/pokemon.c`, `region_map_sections.json`). Beim Speichern wird nur der geänderte Wert ersetzt (getestet: unverändert geschrieben = byte-gleich, eine Änderung = genau eine Zeile). Texte werden gegen `charmap.txt` und die Längen aus `include/constants/global.h` geprüft. Teilen sich mehrere Items eine Beschreibung, bekommt nur das bearbeitete eine eigene Kopie. Neue Items/Attacken/Pokémon/Fähigkeiten anlegen ist bewusst nicht enthalten (betrifft viele Tabellen, Grafiken, Kampfcode). CLI: `gamedata --table moves --key MOVE_TACKLE --column Name --value "Body Tackle" --yes`, `gamedata --add-class TRAINER_CLASS_X --name NAME --money 20 --yes`.

## Lauf-Events (Szenen)

- **Laufwege auf der Map** („🗺 Show on map…“ bei den Bausteinen *Movement*/*Move together*, „Show movements on map…“ in *Existing scripts*): Map mit allen Personen, Laufwege farbig mit Schrittnummern, Abspielen/Pause/Geschwindigkeit, Startpunkt des Spielers per Klick; Warnungen bei unbekannten Schritten, Verlassen der Map, blockierten Feldern, Zusammenstößen. Dauer der Schritte geschätzt; Kanten/Wasser/Höhenstufen werden nicht geprüft.

- Baustein **Move together (scene)**: eine Zeile pro Person `wer: Schritte` → alle laufen gleichzeitig, danach ein gemeinsames `waitmovement` (wie die Magma-Szene in Mossdeep).
- **Existing scripts on this map…** (Dialogs & Events): vorhandene Skripte der Map lesbar, Bewegungen als Schrittfolge, welche Person welches Skript nutzt; Doppelklick springt zum Ziel-Label.

## Maps

Liest `data/maps/map_groups.json` und alle `map.json` (im Hintergrund) sowie `data/layouts/layouts.json`.
Liste mit Filter (Name, Layout, Tileset, Gruppe); Details mit Layoutgröße, Primary/Secondary, Typ, Wetter, Musik,
Eventzahlen, Skriptdateien (`scripts.pory`/`scripts.inc`) und verwendeten Skript-Labels, Verbindungen und Warps
(Klick springt zur Map) sowie die anderen Maps mit demselben Secondary Tileset. *Neues Secondary Tileset für diese Map*
öffnet den Tileset-Editor mit dem Primary der Map. Nichts wird geschrieben; Maps legt weiterhin Porymap an.

## Features

Alle `#define` aus `include/config/*.h` des geöffneten Branches: Name, Wert, Art (Schalter TRUE/FALSE, Generation,
Zahl, Konstante, Ausdruck), Quelldatei und Zeile, Abschnitt und Beschreibung (Kommentar in der Zeile oder darüber).
**Ändern:** Schalter per Häkchen, Generationen per Auswahl (GEN_3 … GEN_LATEST), Zahlen per Eingabe – nur wo das Format
eindeutig ist und die Option nicht in `#if`/`#ifdef` steht („Bedingt“, schreibgeschützt). Geänderte Zeilen sind gelb;
*Änderungen speichern…* zeigt die Dateien, ersetzt nur den Wert in der jeweiligen Zeile (Kommentar-Spalte bleibt) und bricht
mit Konflikt ab, wenn die Zeile inzwischen anders aussieht. `species_enabled.h` und `test.h` sind standardmäßig ausgeblendet.

## Sprites

Alle Overworld-Sprites des Branches (`OBJ_EVENT_GFX_…` aus `src/data/object_events/*`) mit Frame-Größe, Palette und
Vorschau in Spielfarben. *PNG prüfen…* testet ein neues Sprite-Blatt: Frame-Größe, Anzahl Frames (laufende Person: 9 Frames
nebeneinander), höchstens 15 Farben + Transparenz und welche vorhandene Palette alle Farben enthält.

**Ins Projekt eintragen** (Name, vorhandene Palette, optional „ähnlichste Farbe“ mit Rückfrage) schreibt:

```
graphics/object_events/pics/people/<name>.png             4-bit indiziert auf die gewählte Palette
include/constants/event_objects.h                         OBJ_EVENT_GFX_<NAME> = NUM_OBJ_EVENT_GFX, NUM_OBJ_EVENT_GFX + 1
src/data/object_events/object_event_graphics.h            const u32 gObjectEventPic_<Name>[] = INCBIN_U32(… .4bpp)
src/data/object_events/object_event_pic_tables.h          sPicTable_<Name> (overworld_ascending_frames)
src/data/object_events/object_event_graphics_info.h       gObjectEventGraphicsInfo_<Name> (Palettenfelder von einem Sprite derselben
                                                          Palette, Größenfelder von einem Sprite derselben Frame-Größe)
src/data/object_events/object_event_graphics_info_pointers.h  extern-Deklaration + [OBJ_EVENT_GFX_<NAME>] = &…
spritesheet_rules.mk                                      gbagfx-Regel mit -mwidth/-mheight
```

Eingefügt wird jeweils nach dem letzten Eintrag außerhalb von `#if`-Blöcken. Eigene neue Paletten (Palettentag) folgen später.
CLI: `tileset-editor-cli sprite-import --project <ordner> --name Guard --png sheet.png [--frame 16x32] [--palette OBJ_EVENT_PAL_TAG_NPC_1] [--nearest] [--yes]`.

## Porymap-Brücke

Porymap-Skripte können weder Programme starten noch Dateien schreiben, wohl aber in Porymaps Log schreiben.
Die Brücke nutzt das: *Projekt → Porymap-Brücke → Einrichten…* legt `%LOCALAPPDATA%\TilesetToolkit\tileset_toolkit_bridge.js`
an und trägt es (nach Vorschau, mit Sicherung `porymap.user.cfg.toolkit-backup`) in `porymap.user.cfg` des Projekts ein
(Schlüssel `custom_scripts`; die Datei ist per `.gitignore` vom Repo ausgeschlossen). Porymap dabei geschlossen halten,
weil es diese Datei beim Speichern seiner Einstellungen überschreibt.

- Porymap meldet geöffnetes Projekt, Map, Layout und Tilesets. Das Toolkit zeigt das in der Kopfleiste und markiert
  die Map im Maps-Modul. Meldungen eines anderen Projekts werden nur angezeigt, nicht ausgeführt.
- Porymap-Menü *Tools*: „PokéPat DecompKit: Map im Toolkit anzeigen“ (Toolkit springt zur Map) und
  „PokéPat DecompKit: Neues Tileset für diese Map“ (Tileset-Editor mit dem Primary der Map).
- Das Toolkit liest `%APPDATA%\pret\porymap\porymap.log` sekündlich mit (nur neue Zeilen).
- Wirksam nach dem nächsten Öffnen des Projekts in Porymap oder über *Options → Custom Scripts… → Refresh Scripts*.
- *Entfernen…* nimmt nur den eigenen Eintrag heraus; andere Porymap-Skripte bleiben.

## In Porymap öffnen

Porymap 6.x (geprüft am Quellcode 6.3.1) nimmt keinen Projektpfad auf der Kommandozeile an, sondern öffnet das
zuletzt verwendete Projekt aus seiner `porymap.cfg`. Das Toolkit startet deshalb Porymap, kopiert den Projektpfad
in die Zwischenablage und meldet, ob Porymap dieses Projekt ohnehin zuletzt geöffnet hatte (die Porymap-Konfiguration
wird nur gelesen, nie geschrieben). Sonst in Porymap *File → Open Project…* und den Pfad einfügen.
Nach dem Tileset-Export: Layout öffnen → *Secondary Tileset* auf das neue Tileset stellen → Metatiles setzen → speichern.

## ROM bauen

*▶ ROM bauen* führt den Build-Befehl im Projektordner aus (WSL: `wsl -d <Distro> --cd <Pfad> bash -lc "<Befehl>"`)
und zeigt die Ausgabe live. Regeln:

- Gibt es im Toolkit noch nicht exportierte Änderungen, wird vorher darauf hingewiesen (gebaut wird nur, was gespeichert ist).
- Pro Projektordner läuft höchstens ein Build, auch über mehrere Toolkit-Fenster hinweg (Sperrdatei).
- Vor dem Build wird die vorhandene ROM per SHA-256 erfasst. **Erfolg** = Exit-Code 0 **und** ROM vorhanden; die
  Meldung unterscheidet „neu erzeugt“ und „bereits aktuell, inhaltlich unverändert“.
- **Fehlschlag**: Fehlerzeilen werden hervorgehoben, es wird **keine** ROM verlinkt; eine vorhandene ROM wird
  ausdrücklich als altes Ergebnis bezeichnet. Abbrechen liefert ebenfalls kein Ergebnis.
- **Problemliste**: Neben der Ausgabe stehen die Fehler als Liste „Datei:Zeile – Meldung“ (Compiler, Assembler, Linker,
  Datenwerkzeuge wie mapproc und der Text-Encoder; Pfade relativ zum Projekt, doppelte zusammengefasst, der erste Fehler
  – meist die Ursache – fett). Ein Klick springt bei Platin-Skripten, -Events und -Texten direkt ins Modul Events & Dialogs
  (Skript an der Zeile, Text mit ausgewählter ID), sonst öffnet 📄 die Datei an der Zeile (VS Code, falls installiert, sonst
  das Standardprogramm).
- *Im Emulator öffnen* ist nur nach einem erfolgreichen Build und mit eingetragenem Emulator aktiv.
- Nach jedem erfolgreichen Build kopiert PokéPat das ROM in den Ordner `ROM` neben PokéPat (z. B. `ROM\pokeplatinum.nds`,
  benannt nach dem Projektordner) und ersetzt es beim nächsten Build. Emulator und „ROM anzeigen“ nutzen diese Kopie
  (startet schneller als die Datei in WSL). Ist das ROM gerade im Emulator geöffnet, meldet die Statuszeile, dass es nicht
  ersetzt werden konnte.

## Kommandozeile (gleiche Bibliotheken, z. B. für Automatisierung)

```
tileset-editor-cli info    --project <ordner>
tileset-editor-cli compile --project <ordner> --primary gTileset_General --name gTileset_MyTown --image art.png [--layer middle]
tileset-editor-cli export  … wie compile … [--yes]          (ohne --yes nur der Plan)
tileset-editor-cli build   --project <ordner> [--env wsl|windows] [--command "make -j$(nproc)"]
```

## Aufbau

```
src/TilesetEditor.Core      PNG/JASC/GBA-Formate, Projekt-Parser, Import, Vorprüfung, Farboperationen (ohne UI)
src/TilesetEditor.Compiler  Porytiles: Sandbox, Aufruf (WSL/nativ), Diagnose-Parser
src/TilesetEditor.Export    Exportplan, Validierung, transaktionales Schreiben mit Rollback
src/TilesetEditor.Toolkit   Projektumgebung (Branch, ROM), Einstellungen, BuildRunner, Porymap-/Emulator-Start
src/TilesetEditor.App       WPF-Shell und Module (Projekt, Tilesets, geplante Module)
src/TilesetEditor.Cli       Kommandozeile
tests/                      Unit-, Export-, Build- und Integrationstests; tests/assets mit Test-PNGs und Porytiles-Logs
```

## Unterstützte Expansion-Versionen

Getestet mit pokeemerald-expansion **1.10.2** und **1.17.2** (alle Unit- und Integrationstests laufen gegen beide; mit
1.17.2 wurde nach Tileset-Export, neuem Sprite, Trainerbild, Dekoration, Game-Data-, Trainer-, Flag- und Event-Änderungen
erfolgreich ein ROM gebaut). Seit 1.13 haben sich mehrere Dateiformate geändert (`INCGFX` statt `INCBIN`, Konstanten als
`enum`, `gTrainerPicInfo`, `ITEM_NAME`, Dekorationstexte direkt in `header.h` …). Das Kit liest beide Varianten und schreibt
immer im Stil, den das Projekt bereits verwendet. Die Übersicht der Unterschiede steht im englischen README
(„Supported pokeemerald-expansion versions“).

## Abgeleitete Regeln für dieses Projekt (pokemerald-reyamon, Branch main)

- pokeemerald-expansion 1.10.2; `NUM_TILES_PER_METATILE 8` → Dual-Layer.
- Secondary: 512 Tiles, 512 Metatiles, Paletten 6–12 (`NUM_PALS_IN_PRIMARY 6`, `NUM_PALS_TOTAL 13`).
- Attribute 16 Bit: Verhalten `0x00FF`, Layer-Typ `0xF000` (`include/global.fieldmap.h`). Standardverhalten `MB_NORMAL`.
- Primaries `gTileset_General`/`Building` sind in `src/graphics.c` deklariert, Secondaries in `src/data/tilesets/graphics.h`.
- Palette 04 von `gTileset_General` enthält `255,0,255` in den Plätzen 3–5. Porytiles' Standard-Transparenzfarbe
  führt damit zu einem Fehler bzw. Absturz; die Sandbox wählt deshalb automatisch eine freie Transparenzfarbe und
  normalisiert Platz 0 beim Export wieder auf `255,0,255`.
