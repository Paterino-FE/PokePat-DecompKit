# PokéPat DecompKit für pokeemerald-expansion

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

Die Programmoberfläche ist englisch (seit 25.11.2026), diese Doku bleibt deutsch; Knopfnamen stehen in der englischen Form.

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
- *Im Emulator öffnen* ist nur nach einem erfolgreichen Build und mit eingetragenem Emulator aktiv.

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

## Abgeleitete Regeln für dieses Projekt (pokemerald-reyamon, Branch main)

- pokeemerald-expansion 1.10.2; `NUM_TILES_PER_METATILE 8` → Dual-Layer.
- Secondary: 512 Tiles, 512 Metatiles, Paletten 6–12 (`NUM_PALS_IN_PRIMARY 6`, `NUM_PALS_TOTAL 13`).
- Attribute 16 Bit: Verhalten `0x00FF`, Layer-Typ `0xF000` (`include/global.fieldmap.h`). Standardverhalten `MB_NORMAL`.
- Primaries `gTileset_General`/`Building` sind in `src/graphics.c` deklariert, Secondaries in `src/data/tilesets/graphics.h`.
- Palette 04 von `gTileset_General` enthält `255,0,255` in den Plätzen 3–5. Porytiles' Standard-Transparenzfarbe
  führt damit zu einem Fehler bzw. Absturz; die Sandbox wählt deshalb automatisch eine freie Transparenzfarbe und
  normalisiert Platz 0 beim Export wieder auf `255,0,255`.
