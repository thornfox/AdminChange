# AdminChange

**Author:** ThornFox · **Version:** 1.1.2 · **Framework:** Oxide (uMod) for Rust

## English

Toggles admins and moderators between player mode and a temporary staff mode with its own loadout, safe inventory storage in assigned chests, automatic deactivation, an AdvancedStatus countdown and RealPVE loot integration.

### Features
- Staff mode for admins (`/radmin_a`) and moderators (`/radminm`)
- Own inventory is moved into four assigned large wooden boxes (wear, main, belt, backpack) and returned on deactivation
- Activation is cancelled safely if a chest is missing or the inventory does not fit
- Inventory backups with restore command
- Automatic deactivation after a configurable time, countdown bar via AdvancedStatus
- Staff items are tagged and destroyed when dropped
- State, timer and status bar survive plugin reloads and server restarts
- Map wipe handling: chest assignments and active modes are reset
- Automatic migration from version 1.0.x (config and data)

### Commands
| Command | Access | Description |
|---|---|---|
| `/radmin_a` | Admin | Toggle admin mode |
| `/radminm` | Moderator | Toggle moderator mode |
| `/radmin_as <steamID\|_self> <1-4>` | Admin/Moderator | Assign the large wooden box you are looking at as chest 1–4 |
| `/radmin_chests` | Admin | List assigned chests |
| `/radmin_a_restore <steamID\|_self> [index] [confirm]` | Admin | List, inspect or restore a backup into your own inventory |
| `/radmin_r_m` | Admin | Remove backups older than 24 hours |
| `/radmin_r_c` | Admin | Delete all backups |
| `/radmin_ei` | Admin | Show info about the entity you are looking at |
| `/radmin_hand` | Admin | Show info about the item in your hand |
| `/aclear` | Admin | Reset main inventory capacity to 24 |
| `/radmin_loot_sync [steamID\|_self]` | Admin | Flip RealPVE loot access once if it is out of sync |

### Permissions
- `adminchange.admin` – admin access (alternative: configured admin group)
- `adminchange.mod` – moderator access (alternative: configured moderator group)

### Dependencies (all optional)
AdminChange runs without any other plugin. Every integration is used only if the plugin is installed **and** enabled in the config, and each one can be switched off.

| Plugin | What it adds | Config switch |
|---|---|---|
| AdvancedStatus | Status bar with countdown | `Optional plugin integrations` → `AdvancedStatus` |
| Backpacks | Backpack contents are stored and returned | `Optional plugin integrations` → `Backpacks` |
| Godmode | God mode while staff mode is active | `Optional plugin integrations` → `Godmode` |
| Vanish | Invisible in admin mode | `Optional plugin integrations` → `Vanish` |
| AdminRadar | Radar is turned off on deactivation | `Optional plugin integrations` → `AdminRadar` |
| RealPVE | Unrestricted loot access in staff mode | `RealPVE integration` → `Enabled` |

```json
"Optional plugin integrations (true = use if installed, false = never use)": {
  "AdvancedStatus (status bar with countdown)": true,
  "Backpacks (store and return the backpack contents)": true,
  "Godmode (god mode while staff mode is active)": true,
  "Vanish (invisible in admin mode)": true,
  "AdminRadar (turn the radar off on deactivation)": true
}
```

### Loadout example
Each mode (`Admin mode`, `Moderator mode`) has its own `Wear items` and `Belt items` list:
```json
"Wear items": [
  { "Short name": "hoodie", "Skin ID": 3066673296, "Amount": 1 },
  { "Short name": "tactical.gloves", "Skin ID": 0, "Amount": 1 }
],
"Belt items": [
  { "Short name": "rifle.lr300", "Skin ID": 2024164450, "Amount": 1 }
]
```
Reload the plugin afterwards with `oxide.reload AdminChange`.

### Running without RealPVE
RealPVE is optional. If you do not use it, disable the integration in `oxide/config/AdminChange.json`:
```json
"RealPVE integration": {
  "Enabled": false,
  "Plugin name": "RealPVE",
  "Server command that toggles unrestricted loot access ({0} = Steam ID)": "adminpve loot {0}"
}
```
With `"Enabled": false` no loot command is sent and `/radmin_loot_sync` is inactive. If you use a different PvE plugin, you can instead set `Plugin name` and the toggle command to match that plugin.

The follow-up chat command (default `/radmin_menu`) can be turned off the same way by setting `"Chat command executed for the player after (de)activation (empty = disabled)": ""`.

### RealPVE loot access out of sync
`adminpve loot <id>` is a toggle in RealPVE, so its state can get reversed (access granted in player mode, revoked in staff mode).
- **Single player:** run `/radmin_loot_sync <steamID|_self>` once while that player is in player mode.
- **Several players:** let all staff members leave staff mode first, then run `adminpve loot clear` in the server console. Anyone still in staff mode during the clear needs `/radmin_loot_sync <steamID>` afterwards.

### Works with CommandMenu
AdminChange and [CommandMenu](https://github.com/thornfox/CommandMenu) are designed to work together. By default, AdminChange runs `/radmin_menu` after staff mode is activated or deactivated, which toggles the admin button menu of CommandMenu. Staff members get their admin commands as buttons right away.
- Install `CommandMenu.cs` and grant `commandmenu.admin` to your admin/moderator group.
- CommandMenu is optional. Without it, set `"Chat command executed for the player after (de)activation (empty = disabled)": ""`.

### Tested on RustRentner
All ThornFox plugins are tested extensively on [rustrentner.de](https://rustrentner.de).

### License
MIT – see [LICENSE](LICENSE).

---

## Deutsch

Schaltet Admins und Moderatoren zwischen Spielermodus und einem zeitlich begrenzten Teammodus um. Dazu gehören eigenes Loadout, sichere Inventarablage in zugewiesenen Kisten, automatische Deaktivierung, AdvancedStatus-Countdown und RealPVE-Loot-Anbindung.

### Funktionen
- Teammodus für Admins (`/radmin_a`) und Moderatoren (`/radminm`)
- Das eigene Inventar wird in vier zugewiesene große Holzkisten gelegt (Kleidung, Inventar, Gürtel, Rucksack) und beim Beenden zurückgegeben
- Fehlt eine Kiste oder passt das Inventar nicht hinein, wird die Aktivierung sicher abgebrochen
- Inventar-Backups mit Wiederherstellungsbefehl
- Automatische Deaktivierung nach einstellbarer Zeit, Countdown-Balken über AdvancedStatus
- Team-Items sind markiert und verschwinden beim Fallenlassen
- Status, Timer und Statusbalken bleiben nach Plugin-Reload und Server-Neustart erhalten
- Wipe-Behandlung: Kistenzuweisungen und aktive Modi werden zurückgesetzt
- Automatische Migration von Version 1.0.x (Config und Daten)

### Befehle
| Befehl | Zugriff | Beschreibung |
|---|---|---|
| `/radmin_a` | Admin | Admin-Modus umschalten |
| `/radminm` | Moderator | Moderator-Modus umschalten |
| `/radmin_as <steamID\|_self> <1-4>` | Admin/Moderator | Angesehene große Holzkiste als Kiste 1–4 zuweisen |
| `/radmin_chests` | Admin | Zugewiesene Kisten anzeigen |
| `/radmin_a_restore <steamID\|_self> [Index] [confirm]` | Admin | Backups auflisten, ansehen oder ins eigene Inventar wiederherstellen |
| `/radmin_r_m` | Admin | Backups älter als 24 Stunden löschen |
| `/radmin_r_c` | Admin | Alle Backups löschen |
| `/radmin_ei` | Admin | Infos zum angesehenen Entity |
| `/radmin_hand` | Admin | Infos zum Item in der Hand |
| `/aclear` | Admin | Inventarkapazität auf 24 zurücksetzen |
| `/radmin_loot_sync [steamID\|_self]` | Admin | RealPVE-Plünderrechte einmal umschalten, falls sie verdreht sind |

### Berechtigungen
- `adminchange.admin` – Admin-Zugriff (alternativ: konfigurierte Admin-Gruppe)
- `adminchange.mod` – Moderator-Zugriff (alternativ: konfigurierte Moderator-Gruppe)

### Abhängigkeiten (alle optional)
AdminChange läuft ohne jedes andere Plugin. Jede Anbindung wird nur genutzt, wenn das Plugin installiert **und** in der Config aktiviert ist, und jede lässt sich abschalten.

| Plugin | Was es ergänzt | Schalter in der Config |
|---|---|---|
| AdvancedStatus | Statusbalken mit Countdown | `Optional plugin integrations` → `AdvancedStatus` |
| Backpacks | Rucksackinhalt wird gesichert und zurückgegeben | `Optional plugin integrations` → `Backpacks` |
| Godmode | Unverwundbar im Teammodus | `Optional plugin integrations` → `Godmode` |
| Vanish | Unsichtbar im Admin-Modus | `Optional plugin integrations` → `Vanish` |
| AdminRadar | Radar wird beim Beenden ausgeschaltet | `Optional plugin integrations` → `AdminRadar` |
| RealPVE | Freier Loot-Zugriff im Teammodus | `RealPVE integration` → `Enabled` |

```json
"Optional plugin integrations (true = use if installed, false = never use)": {
  "AdvancedStatus (status bar with countdown)": true,
  "Backpacks (store and return the backpack contents)": true,
  "Godmode (god mode while staff mode is active)": true,
  "Vanish (invisible in admin mode)": true,
  "AdminRadar (turn the radar off on deactivation)": true
}
```

### Loadout-Beispiel
Jeder Modus (`Admin mode`, `Moderator mode`) hat eigene Listen `Wear items` und `Belt items`:
```json
"Wear items": [
  { "Short name": "hoodie", "Skin ID": 3066673296, "Amount": 1 },
  { "Short name": "tactical.gloves", "Skin ID": 0, "Amount": 1 }
],
"Belt items": [
  { "Short name": "rifle.lr300", "Skin ID": 2024164450, "Amount": 1 }
]
```
Danach das Plugin mit `oxide.reload AdminChange` neu laden.

### Betrieb ohne RealPVE
RealPVE ist optional. Wer es nicht nutzt, schaltet die Anbindung in `oxide/config/AdminChange.json` ab:
```json
"RealPVE integration": {
  "Enabled": false,
  "Plugin name": "RealPVE",
  "Server command that toggles unrestricted loot access ({0} = Steam ID)": "adminpve loot {0}"
}
```
Mit `"Enabled": false` wird kein Loot-Befehl gesendet und `/radmin_loot_sync` ist ohne Funktion. Wer ein anderes PvE-Plugin nutzt, kann stattdessen `Plugin name` und den Umschalt-Befehl passend zu diesem Plugin eintragen.

Der Folgebefehl im Chat (Standard `/radmin_menu`) lässt sich genauso abschalten: `"Chat command executed for the player after (de)activation (empty = disabled)": ""`.

### RealPVE-Plünderrechte verdreht
`adminpve loot <id>` ist in RealPVE ein Umschalter, deshalb kann der Stand verdreht sein (im Spielermodus erteilt, im Teammodus entzogen).
- **Einzelner Spieler:** `/radmin_loot_sync <steamID|_self>` einmal ausführen, während der Spieler im Spielermodus ist.
- **Mehrere Spieler:** Alle Teammitglieder beenden zuerst ihren Teammodus, dann in der Server-Konsole `adminpve loot clear` ausführen. Wer beim Clear noch im Teammodus war, braucht danach einmal `/radmin_loot_sync <steamID>`.

### Zusammenspiel mit CommandMenu
AdminChange und [CommandMenu](https://github.com/thornfox/CommandMenu) sind aufeinander abgestimmt. AdminChange führt nach dem Aktivieren oder Beenden des Teammodus standardmäßig `/radmin_menu` aus und blendet damit das Admin-Button-Menü von CommandMenu ein bzw. aus. Teammitglieder haben ihre Admin-Befehle so sofort als Buttons zur Hand.
- `CommandMenu.cs` installieren und der Admin-/Moderator-Gruppe `commandmenu.admin` geben.
- CommandMenu ist optional. Ohne das Plugin `"Chat command executed for the player after (de)activation (empty = disabled)": ""` setzen.

### Getestet auf RustRentner
Alle Plugins von ThornFox werden ausgiebig auf [rustrentner.de](https://rustrentner.de) getestet.

### Lizenz
MIT – siehe [LICENSE](LICENSE).
