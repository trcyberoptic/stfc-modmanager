# STFC Mod Manager — Design

**Datum:** 2026-08-15
**Status:** abgenommen (Entwurf im Chat), Implementierungsplan folgt

---

## 1. Ziel und Abgrenzung

Eine kleine Windows-Anwendung, die BepInEx-Mods für **Star Trek Fleet Command**
installiert, aktualisiert, an- und abschaltet und im Fehlerfall ein Supportpaket
erzeugt.

**Zielgruppe:** die öffentliche STFC-Modding-Community, nicht nur die CH-Allianz.
Der Manager kennt **keine** CH-Sonderlogik und keine kuratierte Modliste. Mods
kommen aus beliebigen GitHub-Repositories oder aus einem lokalen Ordner.

**UI-Sprache:** Englisch. Diese Spec und die Entwicklungsnotizen sind Deutsch.

**Nicht das Ziel:** ein Ersatz für den bestehenden Inno-Installer der CH-Allianz.
Der bleibt bestehen; der Manager übernimmt eine bestehende Installation, statt
neben ihr zu installieren (siehe §7 Adoption).

---

## 2. Kontext — geprüfte Befunde

Alle Angaben am 2026-08-15 auf einer realen Installation verifiziert
(`C:\Games\Star Trek Fleet Command\STFC\default\game`).

### 2.1 Spielordner

| Pfad | Inhalt |
|---|---|
| `<game>\prime.exe` | Spiel-Executable. FileVersion ist die **Unity**-Version (`6000.0.59.10882416`), **nicht** die Spielversion. |
| `<game>\.version` | `&game=254` — der Client-Build. Verlässlicher Versionsmarker. |
| `<game>\.doorstop_version` | `4.5.0` — Doorstop, nicht BepInEx. |
| `<game>\changelog.txt` | BepInEx-Changelog des installierten Builds. |
| `<game>\winhttp.dll` | Doorstop-Loader (BepInEx-Einstiegspunkt). |
| `<game>\version.dll` | C++ „Community Mod" (`community_patch`). Aktiv. |
| `<game>\version.dll_` | Dieselbe, deaktiviert (Unterstrich-Konvention). |
| `<game>\community_patch_settings.toml` | Config der Community-Mod. |
| `<game>\BepInEx\core\` | BepInEx-Laufzeit. `BepInEx.Core.dll` trägt die belastbare Versionsnummer. |
| `<game>\BepInEx\plugins\` | Aktive Plugins. |
| `<game>\BepInEx\plugins-disabled\` | Deaktivierte Plugins (bestehende Konvention). |
| `<game>\BepInEx\config\` | Plugin-Configs, Dateiname = Plugin-GUID. |
| `<game>\BepInEx\interop\` | Von BepInEx generierte Interop-Assemblies. |
| `<game>\BepInEx\LogOutput.log`, `ErrorLog.log` | BepInEx-Logs. |
| `%USERPROFILE%\AppData\LocalLow\Digit Game Studios Ltd\Star Trek Fleet Command\` | Unity-Logs `Player.log`, `Player-prev.log`. |

### 2.2 Drei Befunde, die das Design bestimmen

**(a) Der GAME_PATH-Schlüssel trägt einen Präfix.**
In `%LOCALAPPDATA%\Star Trek Fleet Command\launcher_settings.ini` steht:

```
152033..GAME_PATH=C:/Games/Star Trek Fleet Command/STFC/default/game/
152033..GAME_TEMP_PATH=C:/Games/Star Trek Fleet Command/STFC/default/update/
PUBLISHER_PID=152033
```

Der Inno-Installer sucht mit `Pos('GAME_PATH=', line) = 1`, verlangt also den
Zeilenanfang. Durch den `152033..`-Präfix schlägt die Erkennung **still fehl**;
gerettet wird sie nur vom hartkodierten Fallback `C:\Games\…`. Auf einer
Installation außerhalb von `C:\Games` greift sie daneben.

→ Der Manager parst mit `^(?<pid>\d+\.\.)?GAME_PATH\s*=\s*(?<path>.+)$`,
normalisiert `/` → `\` und schneidet den abschließenden Trenner ab.
`GAME_TEMP_PATH` matcht dabei nicht (kein `GAME_PATH=`-Teilstring am
Musteranfang) — trotzdem wird gegen den vollständigen Schlüssel geankert.

**(b) `BepInEx\config\` enthält Datencaches in dreistelliger Megabyte-Größe.**
Gemessen: `Optimus.STFC.Berserker.StaticSync.json` = **68 MB**,
`Optimus.STFC.Berserker.GalaxyNodes.json` = 2,1 MB, `Buezer_dynamic_nodes.json`
= 576 KB. Ein Supportpaket, das `config\` blind einpackt, ist unbrauchbar.

→ Nur `*.cfg` wird eingesammelt, mit Größendeckel (§9).

**(c) Nicht jede DLL in `plugins\` ist ein Mod.**
Gemessener Bestand: 8 Plugins (`Hellebarde`, `Biergofie`, `Buezer`, `NDB`,
`HoppdeBaese`, `Ufpeppler`, `BepInExConfigManager`) plus 4 geteilte Bibliotheken
(`Newtonsoft.Json.dll`, `protobuf-net.dll`, `protobuf-net.Core.dll`,
`UniverseLib.BIE.IL2CPP.Interop.dll`) plus 3 JPG-Ladebildschirme.

→ Ein Mod ist eine DLL mit `[BepInPlugin]`-Attribut. Alles andere ist Beiwerk und
wird über Referenzzählung verwaltet (§6.3).

---

## 3. Entscheidungen

| Frage | Entscheidung | Grund |
|---|---|---|
| Stack | C# / WinForms, **net10.0-windows**, self-contained single-file x64 | Keine Runtime-Voraussetzung beim Nutzer; C# liest BepInPlugin-Metadaten aus jeder Mod-DLL mit Bordmitteln. .NET 10 ist LTS bis Nov 2028. |
| Mod-Identität | Reflection über `System.Reflection.Metadata` (BCL), **kein Manifest** | Funktioniert mit jedem heute existierenden STFC-Mod. Ein Pflicht-Manifest hieße: am Tag 1 kennt der Manager null Mods. |
| Installationsmodell | Direkte Installation in den Spielordner mit Buchführung | Store-und-Hardlink (Vortex/MO2) löst ein Problem, das STFC nicht hat: es geht um ~12 Dateien à wenige hundert KB. Hardlinks brauchen dasselbe Volume, Symlinks Admin oder Developer Mode. |
| Zustand | Eine JSON-Datei | Keine Datenbank für ein Dutzend Datensätze. |
| Tests | `--selftest`-Schalter mit Asserts | Kein Test-Framework für vier reine Funktionen. |

**Voraussetzung auf dem Entwicklungsrechner:** aktuell ist nur SDK 6.0.428
(`C:\dotnet6`) installiert; `dotnet` im PATH hat **keinen** SDK, nur Runtimes
(bis `Microsoft.WindowsDesktop.App 10.0.11`). Das .NET-10-SDK muss vor dem
ersten Build installiert werden (erster Schritt des Implementierungsplans).

---

## 4. Abweichungen vom abgenommenen Entwurf

Zwei bewusste Änderungen, beide in Richtung weniger Code:

**(1) Keine `known-conflicts.json`.** Im Entwurf war eine mitgelieferte,
nachladbare Regeldatei vorgesehen. Sie ist überflüssig: Plugin-gegen-Plugin-
Konflikte deklariert BepInEx selbst über `[BepInIncompatibility]`, und der
einzige Konflikt, den das nicht abdeckt, ist die native `version.dll` — dafür
genügt eine fest eingebaute Prüfung (§8, Check 6). Eine Regeldatei kommt dazu,
sobald ein zweiter nativer STFC-Mod existiert.

**(2) Neu: Adoption bestehender Installationen (§7).** Fehlte im Entwurf. Ohne
sie wäre der Manager für jeden bestehenden STFC-Modder unbrauchbar, weil er
neben dem vorhandenen Bestand installieren würde statt ihn zu übernehmen.

---

## 5. Komponenten

Reine Logik liegt in `Core/` ohne Bezug zur UI, damit `--selftest` sie ohne
Fenster prüfen kann. Ein Fenster, zwei Tabs, eine EXE.

| Komponente | Verantwortung |
|---|---|
| `GameLocator` | Spielordner finden (INI-Parse → Fallbacks → manuelle Wahl), validieren (`prime.exe` vorhanden, Ordner beschreibbar), `.version` lesen. |
| `RuntimeInstaller` | BepInEx-Bootstrap: erkennt vorhandene Installation über `BepInEx\core\BepInEx.Core.dll`; lädt sonst den gepinnten Build über HTTPS, prüft Größe, entpackt mit `System.IO.Compression`. |
| `ModInspector` | Liest `[BepInPlugin]`, `[BepInDependency]`, `[BepInIncompatibility]` aus einer DLL. Klassifiziert: Plugin / geteilte Bibliothek / Beiwerk. |
| `PackageMapper` | Archiv oder Einzeldatei → Liste `(Quelle → Zielpfad relativ zum Spielordner)`. Verweigert ausführbare Dateien und Pfadausbrüche. |
| `Installer` | Führt einen Dateiplan transaktional aus: Backup → Kopieren → State schreiben; bei Fehler vollständiges Rollback. Referenzzählung für geteilte Dateien. |
| `GitHubSource` | Releases-API mit ETag-Caching, Asset-Auswahl, Download. |
| `HealthCheck` | Die neun Prüfungen aus §8. |
| `LogReader` | Extrahiert Fehler und Warnungen aus `LogOutput.log`, gruppiert nach Plugin. |
| `SupportBundle` | Sammeln, redigieren, zippen. |
| `SelfUpdate` | Eigenes Release prüfen und einspielen. |
| `AppLog` | Rollierendes Manager-Log. |

---

## 6. Datenmodell und Kernabläufe

### 6.1 Zustand

Eine Datei: `%LOCALAPPDATA%\StfcModManager\state.json`, atomar geschrieben
(Temp-Datei + `File.Move` mit Überschreiben).

```jsonc
{
  "schemaVersion": 1,
  "gamePath": "C:\\Games\\Star Trek Fleet Command\\STFC\\default\\game",
  "lastKnownClientBuild": "254",
  "mods": [
    {
      "id": "Optimus.STFC.Berserker",        // BepInPlugin-GUID, sonst Dateiname
      "name": "Hellebarde",
      "version": "1.10.12",
      "enabled": true,
      "source": {
        "kind": "github",                     // github | local | adopted
        "repo": "trcyberoptic/STFC.Hellebarde",
        "releaseTag": "v1.10.12",
        "assetName": "Hellebarde.dll",
        "etag": "W/\"a1b2c3\"",
        "autoUpdate": false
      },
      "files": [
        { "path": "BepInEx\\plugins\\Hellebarde.dll", "sha256": "…", "shared": false }
      ],
      "installedAt": "2026-08-15T19:04:11Z",
      "installedAgainstClientBuild": "254"
    }
  ],
  "sharedFiles": [
    { "path": "BepInEx\\plugins\\Newtonsoft.Json.dll", "sha256": "…",
      "fileVersion": "13.0.3", "providers": ["Optimus.STFC.Berserker", "stfc.soundwave"] }
  ],
  "trustedRepos": ["trcyberoptic/STFC.Hellebarde"]
}
```

### 6.2 Mod aus GitHub hinzufügen

1. Nutzer gibt eine Repo-URL ein. Akzeptiert werden nur `https://github.com/<owner>/<repo>`.
2. `GET https://api.github.com/repos/{owner}/{repo}/releases/latest`, Header
   `User-Agent: StfcModManager/<ver>`, optional `Authorization` bei
   hinterlegtem PAT. Pre-Releases werden ignoriert.
3. **Asset-Auswahl**, erste zutreffende Regel gewinnt:
   1. im State gemerkter `assetName` (exakte Übereinstimmung)
   2. genau ein `.zip` im Release → dieses
   3. genau ein `.dll` im Release → dieses
   4. sonst: Auswahldialog mit allen Assets
4. Download nur über `https://`, Host muss `github.com` oder
   `*.githubusercontent.com` sein. SHA256 wird berechnet.
5. **Vertrauensdialog** (einmal pro Repo): zeigt Repo, Release-Tag, Asset-Name,
   Größe, SHA256, und welche Dateien wohin geschrieben würden. Nach Bestätigung
   landet das Repo in `trustedRepos`.
6. `PackageMapper` erzeugt den Dateiplan, `ModInspector` liest die Metadaten,
   `Installer` führt aus.

### 6.3 Zuordnungsregeln (`PackageMapper`)

Eingang ist eine `.dll` oder ein `.zip`. Ausgang ist eine Liste von Zielpfaden
relativ zum Spielordner.

| Fall | Ziel |
|---|---|
| Archiv enthält ein Pfadsegment `BepInEx/` | Alles ab dem gemeinsamen Wurzelverzeichnis 1:1 über den Spielordner legen. |
| Archiv ohne `BepInEx/`: `*.dll` | `BepInEx\plugins\` |
| Archiv ohne `BepInEx/`: `version.dll`, `winhttp.dll`, `doorstop_config.ini`, `*.toml` | Spielordner-Wurzel |
| Archiv ohne `BepInEx/`: alles Übrige (Bilder, Textdateien) | `BepInEx\plugins\` |
| Einzelne `.dll` | `BepInEx\plugins\` |

**Verweigert wird** (Abbruch mit Meldung, nichts wird geschrieben):

- ausführbare Erweiterungen: `.exe .bat .cmd .ps1 .msi .scr .vbs .js .jar`
- absolute Pfade, Laufwerksbuchstaben, `..`-Segmente (Zip-Slip)
- Einträge, deren aufgelöster Zielpfad außerhalb des Spielordners liegt
- verschachtelte Archive (werden nicht rekursiv ausgepackt, sondern ignoriert)

Der Manager führt **nie** etwas aus einem Release aus. Er kopiert nur.

### 6.4 Geteilte Dateien

Eine Zieldatei, die von mehr als einem Mod geliefert wird, steht in
`sharedFiles` mit einer `providers`-Liste. Eine Deinstallation entfernt nur den
eigenen Eintrag; die Datei wird erst gelöscht, wenn die Liste leer ist.

Liefern zwei Mods dieselbe Datei in unterschiedlichen Versionen, gewinnt die
höhere `FileVersion`. Die Entscheidung wird ins Manager-Log geschrieben und im
Problems-Tab als Hinweis angezeigt.

### 6.5 An- und Abschalten

Verschieben zwischen `BepInEx\plugins\` und `BepInEx\plugins-disabled\` — die
Konvention, die das bestehende `switch-mod-profile.cmd` benutzt. Für die native
`version.dll` gilt stattdessen die Unterstrich-Konvention
(`version.dll` ↔ `version.dll_`), ebenfalls schon im Bestand vorhanden.

### 6.6 Deinstallation

Verbuchte Dateien löschen, geteilte gemäß §6.4. **Configs werden nie gelöscht**:
`BepInEx\config\<GUID>.cfg` wandert nach
`%LOCALAPPDATA%\StfcModManager\config-backup\<GUID>-<Zeitstempel>.cfg`.
Zusätzlich gibt es einen Knopf „Remove all mods" für den vollständigen Rückbau —
der erste Schritt in jedem Supportfall.

### 6.7 Transaktionalität

Jede schreibende Operation läuft als Plan:
Dateiplan erstellen → betroffene Zieldateien nach
`%LOCALAPPDATA%\StfcModManager\backup\<Operations-Id>\` sichern → ausführen →
`state.json` schreiben. Schlägt ein Schritt fehl, werden die Sicherungen
zurückgespielt und der State bleibt unverändert. Backups älter als 30 Tage
werden beim Start aufgeräumt.

---

## 7. Adoption bestehender Installationen

Beim ersten Start (und bei jedem Rescan) durchsucht der Manager
`BepInEx\plugins\` und `BepInEx\plugins-disabled\`:

- DLL mit `[BepInPlugin]` → Mod mit `source.kind = "adopted"`, Version aus dem
  Attribut, `files` = diese eine Datei.
- DLL ohne `[BepInPlugin]` → geteilte Bibliothek, `providers` leer, Anzeige als
  „unmanaged".
- Nicht-DLL → Beiwerk, wird nur inventarisiert, nicht angefasst.
- `version.dll` bzw. `version.dll_` im Spielordner → Eintrag „Community Mod
  (native)", an/aus schaltbar, ohne Update-Quelle.

Ein adoptierter Mod kann im UI eine GitHub-Quelle zugewiesen bekommen
(„Set update source…"); ab dann läuft er über den normalen Update-Weg. Ohne
zugewiesene Quelle bleibt er sichtbar, schaltbar und deinstallierbar, nur nicht
aktualisierbar.

Der Manager fasst nichts an, was er nicht kennt.

---

## 8. Health-Checks

Laufen beim Start, nach jeder Operation und auf Knopfdruck. Ergebnis ist eine
Liste im Problems-Tab, jede Zeile mit Schweregrad und, wo möglich, einer
konkreten Abhilfe.

| # | Prüfung | Schweregrad |
|---|---|---|
| 1 | Spielordner gültig: `prime.exe` vorhanden, Ordner beschreibbar | Fehler |
| 2 | `prime.exe` läuft — blockiert alle Schreiboperationen | Hinweis |
| 3 | BepInEx installiert (`winhttp.dll` + `BepInEx\core\BepInEx.Core.dll`), Version ermittelt | Fehler |
| 4 | `.version` weicht von `installedAgainstClientBuild` ab → „Client wurde aktualisiert, Mods können brechen; der erste Start dauert Minuten, weil BepInEx die Interop-Assemblies neu erzeugt" | Warnung |
| 5 | `[BepInIncompatibility]`-Verletzung zwischen zwei aktiven Mods | Fehler |
| 6 | `version.dll` aktiv **und** in `community_patch_settings.toml` steht `[patches] game_version = true` oder `uiscalehooks = true` → nativer Doppel-Hook-Crash beim Login; Abhilfe: beide Schlüssel auf `false` | Fehler |
| 7 | `[BepInDependency]` eines aktiven Mods nicht erfüllt | Fehler |
| 8 | Fehler- und Warnzeilen aus `LogOutput.log` seit dem letzten Spielstart | Warnung |
| 9 | Dateien in `plugins\`, die keinem verwalteten Mod gehören | Hinweis |

Check 6 ist die einzige fest eingebaute Konfliktregel (§4).

**Log-Parser.** BepInEx schreibt `[<Level>:<Quelle>] <Text>`, gelesen mit
`^\[(?<level>Info|Debug|Message|Warning|Error|Fatal)\s*:\s*(?<src>[^\]]+)\]\s*(?<msg>.*)$`.
Gezeigt werden `Warning`, `Error`, `Fatal`, gruppiert nach Quelle, mit Anzahl.
Gelesen werden nur die letzten 5000 Zeilen der Datei.

---

## 9. Supportpaket

Ein Knopf, ein ZIP: `stfc-support-<yyyyMMdd-HHmm>.zip` im
Downloads-Ordner des Nutzers, danach „Show in folder".

**Inhalt**

| Quelle | Anmerkung |
|---|---|
| `BepInEx\LogOutput.log`, `LogOutput.log.1` | auf die letzten 5 MB gekürzt |
| `BepInEx\ErrorLog.log` | |
| `<game>\community_patch.log` | |
| `Player.log`, `Player-prev.log` aus `…\LocalLow\Digit Game Studios Ltd\Star Trek Fleet Command\` | auf die letzten 5 MB gekürzt |
| `BepInEx\config\*.cfg` | **nur** `.cfg`, keine `.json`-Datencaches |
| `<game>\community_patch_settings.toml`, `doorstop_config.ini` | |
| Manager-Log | |
| `inventory.json` | Mods mit Version, Quelle, SHA256, enabled, Installationszeit |
| `environment.txt` | Windows-Build, Client-Build (`.version`), BepInEx-Version, Doorstop-Version, Spielpfad, Manager-Version |
| `health.txt` | Ergebnis der Prüfungen aus §8 zum Zeitpunkt der Erzeugung |

**Redaktion — läuft über jede Textdatei, bevor sie ins ZIP geht.**
Notwendig, weil unter anderem der UniversalTranslator seinen DeepL-API-Schlüssel
in der `.cfg` hält und Logs Spieler-IDs enthalten können.

| Muster | Ersetzung |
|---|---|
| Zeilen mit `key`, `token`, `secret`, `password`, `passwort`, `api` links eines `=` oder `:` | Wert → `[REDACTED]` |
| E-Mail-Adressen | `[REDACTED-EMAIL]` |
| Hex-Spieler-IDs (`\b[0-9a-f]{24,}\b`) | `[REDACTED-ID]` |
| `Bearer <token>` | `Bearer [REDACTED]` |

Das ZIP ist auf 20 MB gedeckelt. Was ausgelassen oder gekürzt wurde, steht mit
Grund in `SKIPPED.txt`. Vor dem Erzeugen zeigt ein Dialog die Dateiliste und den
Hinweis, dass Pfade und Windows-Benutzername enthalten sein können.

---

## 10. Updates

**Mods.** Beim Start und auf Knopfdruck: `releases/latest` je Quelle, mit
`If-None-Match` gegen das gemerkte ETag — das hält das unauthentifizierte
Limit von 60 Anfragen pro Stunde ein. Standardverhalten ist **melden, nicht
installieren**; `autoUpdate` ist ein Schalter pro Mod, Standard aus. „Update
all" installiert alle gemeldeten. Vor jedem Update wird die ersetzte Datei
gesichert (§6.7), sodass „Roll back" die vorige Version zurückholt.

**Manager selbst.** Gleiche Mechanik gegen das eigene Repo. Weil eine laufende
EXE sich nicht überschreiben lässt, aber **umbenennen** lässt:

1. Neue Version nach `StfcModManager.exe.new` laden
2. laufende `StfcModManager.exe` → `StfcModManager.exe.old` umbenennen
3. `.new` an ihren Platz verschieben
4. Prozess neu starten; beim nächsten Start wird `.old` gelöscht

Kein Hilfsprozess, kein Installer.

---

## 11. Sicherheit

Der Manager lädt fremde DLLs, die anschließend in den Spielprozess geladen
werden. Deshalb:

- HTTPS-Pflicht, Host auf `github.com` und `*.githubusercontent.com` begrenzt
- Vertrauensdialog einmal pro Repo, mit SHA256 und vollständiger Dateiliste
- nichts aus einem Release wird ausgeführt; ausführbare Erweiterungen werden
  abgelehnt (§6.3)
- Zip-Slip-Schutz: jeder aufgelöste Zielpfad muss unterhalb des Spielordners
  liegen
- Configs überleben Deinstallation (§6.6)
- Auto-Update ist opt-in, nicht Standard
- Manager-Log und Supportpaket enthalten nie unredigierte Schlüssel oder IDs

Die EXE ist **unsigniert**. Windows SmartScreen wird beim ersten Start warnen;
das gehört ins README, Code-Signing ist außerhalb des Umfangs.

---

## 12. Benutzeroberfläche

Ein Fenster, WinForms, feste Struktur:

```
┌─ STFC Mod Manager ──────────────────────────────────────────┐
│ Game: C:\Games\...\default\game              [Change]       │
│ Client 254 · BepInEx 6.0.0-be.755 · ● game not running      │
├─ Mods ─────────────────────────── Problems (2) ─────────────┤
│ ☑ Hellebarde         1.10.12  github/trcyberoptic  ↑1.10.13 │
│ ☑ Biergofie           2.3.9   github/trcyberoptic     ok    │
│ ☐ UniversalTranslator 1.7.0   local                   ok    │
│ ☑ Community Mod (version.dll)  —   native        ⚠ conflict │
├─────────────────────────────────────────────────────────────┤
│ [Add from GitHub…] [Add local…] [Check updates] [Update all] │
└─────────────────────────────────────────────────────────────┘
```

- Kopfzeile: Spielpfad, Client-Build, BepInEx-Version, Laufzustand des Spiels.
  Läuft `prime.exe`, sind alle schreibenden Knöpfe deaktiviert und die Zeile
  erklärt warum.
- Tab **Mods**: Liste mit Häkchen (an/aus), Name, Version, Quelle, Status.
  Kontextmenü: Update, Roll back, Set update source…, Open config, Remove.
- Tab **Problems**: Health-Checks und Log-Fehler, darunter
  `[Generate support package]` und `[Remove all mods]`.
- Lokale Mods: Ordner `LocalMods\` neben der EXE, Knopf „Open folder", Rescan
  bei Fensterfokus. Drag & Drop einer Datei oder eines Ordners aufs Fenster geht
  denselben Weg wie „Add local…".

---

## 13. Fehlerbehandlung

- Jeder erwartbare Fehler (kein Netz, Rate-Limit erreicht, Datei gesperrt,
  Spielordner nicht beschreibbar, Release ohne passendes Asset, Archiv
  abgelehnt) erzeugt eine Zeile im Problems-Tab mit Klartext und Abhilfe — keine
  Ausnahme-Dialoge.
- Unerwartete Ausnahmen landen im Manager-Log mit Stacktrace und als eine Zeile
  „Unexpected error — see support package".
- Netz-Zeitlimit 30 s, ein Wiederholungsversuch.
- GitHub-Rate-Limit: `X-RateLimit-Remaining` wird gelesen; bei 0 zeigt die
  Meldung die Rücksetzzeit und den Hinweis auf einen optionalen PAT.

---

## 14. Tests

`StfcModManager.exe --selftest` prüft mit Asserts die fünf Stellen, an denen ein
Fehler weh tut, und beendet sich mit Rückgabewert ≠ 0 bei Abweichung. Läuft in
der CI, sobald das Repository steht (§16).

1. **INI-Parser** — `152033..GAME_PATH=C:/x/`, `GAME_PATH=C:/x`,
   Schrägstrich-Normalisierung, abschließender Trenner, fehlende Datei,
   `GAME_TEMP_PATH` darf nicht anschlagen.
2. **Asset-Auswahl** — null/ein/zwei ZIPs, gemischte Assets, gemerkter Name
   gewinnt, Fall „Dialog nötig".
3. **Zuordnung** — ZIP mit `BepInEx/plugins/x.dll`; ZIP mit flacher DLL; ZIP mit
   `../evil.dll` muss abgelehnt werden; ZIP mit `setup.exe` muss abgelehnt
   werden; Einzel-DLL.
4. **Redaktion** — `api_key = abc123` wird maskiert; E-Mail; Hex-ID;
   `Bearer x`; eine harmlose Zeile bleibt unverändert.
5. **Referenzzählung** — zwei Mods liefern dieselbe Bibliothek, einer wird
   deinstalliert, die Datei bleibt; der zweite wird deinstalliert, die Datei
   verschwindet.

---

## 15. Nicht im Umfang

Manifest-Format · Mod-Katalog oder Discovery · Profilverwaltung über an/aus
hinaus · Nexus- oder Thunderstore-Anbindung · Ban-Risiko-Bewertung (bei fremden
Mods nicht wissbar) · Mehrsprachigkeit · Code-Signing · mehrere parallele
Spielinstallationen · Linux/macOS.

---

## 16. Offene Punkte

- Repository-Name und Organisation (Vorschlag: `trcyberoptic/stfc-modmanager`,
  öffentlich). Ohne Remote gibt es kein Selbst-Update und keine CI.
- Der gepinnte BepInEx-Build für den Bootstrap: aktuell im Bestand
  `6.0.0-be.755+3fab71a`. Beim Anheben muss die URL im `RuntimeInstaller`
  mitwandern.
