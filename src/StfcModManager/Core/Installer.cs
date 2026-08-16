using System.Diagnostics;
using System.Security.Cryptography;

namespace StfcModManager.Core;

/// <summary>
/// Fuehrt Dateiplaene transaktional aus: erst sichern, dann kopieren. Schlaegt ein
/// Schritt fehl, werden die Sicherungen zurueckgespielt und nichts bleibt halb installiert.
/// </summary>
public static class Installer
{
    public static string Sha256File(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }

    public static string? FileVersionOf(string path)
    {
        try { return FileVersionInfo.GetVersionInfo(path).FileVersion; }
        catch (FileNotFoundException) { return null; }
    }

    /// <summary>Wirft, wenn ein Pfad das gegebene Wurzelverzeichnis verliesse. Zweite
    /// Verteidigungslinie hinter PackageMapper — der Pfad kann zwischen Pruefung und Anwendung
    /// nicht mehr wandern. Wird sowohl fuer den Spielordner als auch fuer das Sicherungsverzeichnis
    /// benutzt.</summary>
    private static string ResolveUnder(string root, string relativeTarget, string errorContext)
    {
        var full = Path.GetFullPath(Path.Combine(root, relativeTarget));
        if (!full.StartsWith(NormalizeRootPrefix(root), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"{errorContext}: {relativeTarget}");
        return full;
    }

    private static string ResolveInside(string gameRoot, string relativeTarget)
        => ResolveUnder(gameRoot, relativeTarget, "target escapes the game folder");

    /// <summary>Normalisiert eine Wurzel fuer den Enthaltenseins-Vergleich zu einem Praefix, der
    /// garantiert mit genau einem Trennzeichen endet. Ein Trailing-Separator in der Wurzel (ein
    /// Ordnerauswahl-Dialog liefert den bei einem Laufwerk als Spielordner, "C:\", ebenso wie ein
    /// handbearbeitetes state.GamePath) wuerde sonst aus dem Vergleichspraefix "C:\\\\" machen
    /// und JEDEN Zielpfad ablehnen (Pre-Flight-Review Fix Round 1, M1). Path.TrimEndingDirectorySeparator
    /// laesst einen nackten Laufwerksbuchstaben ("D:\") bewusst unangetastet -- "D:" ohne Separator
    /// waere ein GANZ ANDERER, laufwerksrelativer Pfad --, dieser Fall wird deshalb separat behandelt.</summary>
    private static string NormalizeRootPrefix(string root)
    {
        var full = Path.GetFullPath(root);
        var trimmed = Path.TrimEndingDirectorySeparator(full);
        return trimmed.EndsWith(Path.DirectorySeparatorChar) ? trimmed : trimmed + Path.DirectorySeparatorChar;
    }

    /// <summary>True, wenn <paramref name="candidate"/> das Wurzelverzeichnis SELBST ist oder
    /// darunter liegt. ResolveUnder verlangt bewusst echtes Enthaltensein (eine Zieldatei ist nie
    /// ihr eigener Ordner); fuer VERZEICHNIS-Vergleiche ist Gleichheit dagegen zwingend erlaubt --
    /// das Zielverzeichnis eines Ziels direkt im Spielordner (z. B. "version.dll") IST der
    /// Spielordner.</summary>
    private static bool IsRootOrUnder(string candidate, string root)
    {
        var prefix = NormalizeRootPrefix(root);
        return candidate.Equals(Path.TrimEndingDirectorySeparator(prefix), StringComparison.OrdinalIgnoreCase)
               || candidate.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Das aufgeloeste Ziel eines Reparse-Points, oder null, wenn der Pfad keiner ist
    /// (bzw. sich nicht aufloesen laesst -- dann bleibt es beim rein textuellen Urteil).</summary>
    private static string? ResolveLinkOrNull(string dir)
    {
        try { return Directory.ResolveLinkTarget(dir, returnFinalTarget: true)?.FullName; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>Tiefenverteidigung gegen Reparse-Points (Junctions/Symlinks) INNERHALB des
    /// Spielordners: Path.GetFullPath ist rein textuell und sieht nicht, dass z. B.
    /// "BepInEx\plugins" per "mklink /J" (keine Elevation noetig) auf ein Verzeichnis AUSSERHALB
    /// des Spielordners zeigt -- ein textuell harmloses Ziel landet dann physisch trotzdem
    /// draussen (Pre-Flight-Review Fix Round 1, I5). Nichts in dieser App legt selbst
    /// Reparse-Points an, deshalb bewusst billig gehalten: nur das unmittelbare Zielverzeichnis
    /// wird aufgeloest, nicht jedes Zwischenelement auf dem Weg dorthin.</summary>
    private static void RejectReparsedEscape(string dir, string gameRoot, string errorContext)
    {
        var resolved = ResolveLinkOrNull(dir);
        if (resolved is null) return; // kein Reparse-Point -- nichts zu tun

        var resolvedFull = Path.GetFullPath(resolved);

        // Zwei zulaessige Wurzeln, und die zweite ist keine Kuer: ist der SPIELORDNER SELBST ein
        // Reparse-Point (ein per mklink auf eine andere Platte ausgelagerter Spielordner ist eine
        // voellig normale Nutzerkonfiguration), loest schon das Zielverzeichnis eines Ziels im
        // Wurzelverzeichnis ("version.dll" -> dir == gameRoot) auf einen Pfad auf, der textuell
        // ausserhalb liegt. Ohne den Vergleich gegen die aufgeloeste Wurzel wuerde diese Pruefung
        // dann JEDE Installation in einen solchen Spielordner ablehnen -- fail closed heisst,
        // echte Ausbrueche zu stoppen, nicht legitime Einrichtungen.
        var resolvedRoot = ResolveLinkOrNull(gameRoot) ?? Path.GetFullPath(gameRoot);
        if (IsRootOrUnder(resolvedFull, gameRoot) || IsRootOrUnder(resolvedFull, resolvedRoot)) return;

        throw new InvalidOperationException($"{errorContext} via a reparse point pointing to '{resolvedFull}'");
    }

    /// <summary>Legt das Zielverzeichnis an und merkt sich, welche Ebenen dabei neu entstanden
    /// sind (tiefste zuerst). Der Rollback in Apply() will genau diese Ebenen wieder entfernen,
    /// wenn sie am Ende leer bleiben -- eine bereits vorhandene Ebene (z. B. "BepInEx\plugins")
    /// darf dabei niemals angefasst werden.</summary>
    private static List<string> CreateDirectoryTracked(string dir)
    {
        var created = new List<string>();
        var probe = dir;
        while (!string.IsNullOrEmpty(probe) && !Directory.Exists(probe))
        {
            created.Add(probe);
            probe = Path.GetDirectoryName(probe);
        }
        Directory.CreateDirectory(dir);
        return created;
    }

    /// <summary>Fuehrt einen Dateiplan transaktional aus: erst sichern, dann kopieren. Schlaegt
    /// ein Schritt fehl, werden die Sicherungen zurueckgespielt und nichts bleibt halb installiert.
    /// Vorbedingung: <paramref name="ops"/> darf keinen Zielpfad doppelt enthalten (Pre-Flight-
    /// Review Fix Round 1, I2) und kein Ziel darf mit einem Verzeichnistrenner enden (I1/M4) --
    /// beides wirft sofort, bevor irgendetwas angefasst wird.</summary>
    public static IReadOnlyList<InstalledFile> Apply(
        string gameRoot, IReadOnlyList<(string Source, string Target)> ops)
    {
        // Vorflug: erst pruefen und aufloesen, dann erst anfassen. Alles, was hier wirft, wirft
        // bevor ein einziges Byte geschrieben ist -- es gibt dann nichts zurueckzurollen.
        var plan = new List<(string Source, string Target, string Full)>(ops.Count);
        var seenTargets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (source, target) in ops)
        {
            // Ein Ziel, das mit einem Trenner endet, ist nie eine Datei -- ungeprueft wuerde
            // File.Exists() dafuer false liefern (es ist ja keine Datei), der Eintrag landete
            // faelschlich in "written", und ein spaeteres Rollback koennte per File.Delete()
            // versuchen, ein bestehendes VERZEICHNIS zu loeschen (z. B. das echte plugins\, falls
            // das Ziel woertlich "BepInEx\plugins\" war) -- Fehlalarm auf unberuehrten Daten.
            // Muss auf der ROHFORM geprueft werden: Path.GetFullPath schneidet den Trenner ab.
            if (target.EndsWith('\\') || target.EndsWith('/'))
                throw new ArgumentException($"target is not a file path (ends in a separator): '{target}'", nameof(ops));

            var full = ResolveInside(gameRoot, target);

            // PackageMapper dedupliziert pro Archiv, aber Apply() ist oeffentlich und nimmt eine
            // beliebige Liste entgegen. Zwei Ops auf dasselbe Ziel wuerden die zweite Sicherung
            // ueber die (vom ersten Op bereits neu installierte) Datei ziehen statt ueber das
            // echte Original -- ein Rollback stellte dann die falsche Version wieder her.
            // Verglichen wird die AUFGELOESTE Form, nicht die Aufrufer-Zeichenkette: sonst gaelten
            // "BepInEx\plugins\A.dll" und "BepInEx/plugins/A.dll" als zwei verschiedene Ziele,
            // obwohl sie dieselbe Datei bezeichnen -- derselbe Roh-statt-kanonisch-Fehler, den C1
            // fuer den gespeicherten Pfad beschreibt.
            if (!seenTargets.Add(full))
                throw new ArgumentException($"duplicate target in ops: '{target}'", nameof(ops));

            plan.Add((source, target, full));
        }

        var opId = DateTime.Now.ToString("yyyyMMdd-HHmmss-fff");
        var backupDir = Path.Combine(AppPaths.BackupDir, opId);
        var restored = new List<(string Backup, string Original)>();
        var written = new List<string>();
        var createdDirs = new List<string>();
        var result = new List<InstalledFile>();

        try
        {
            Directory.CreateDirectory(backupDir);

            foreach (var (source, target, full) in plan)
            {
                var dir = Path.GetDirectoryName(full)!;
                createdDirs.AddRange(CreateDirectoryTracked(dir));
                RejectReparsedEscape(dir, gameRoot, "target directory escapes the game folder");

                if (File.Exists(full))
                {
                    // Die Sicherung spiegelt die relative Struktur des Ziels, statt sie mit '_'
                    // zu einem flachen Dateinamen zu verschmelzen: "a\b_c.dll" und "a_b\c.dll"
                    // wuerden mit target.Replace('\\','_') beide zu "a_b_c.dll" und sich beim
                    // Sichern gegenseitig ueberschreiben -- eine der beiden Originaldateien waere
                    // dann beim Rollback unwiederbringlich durch die falsche ersetzt.
                    var backup = ResolveUnder(backupDir, target, "backup path escapes the backup folder");
                    Directory.CreateDirectory(Path.GetDirectoryName(backup)!);
                    File.Copy(full, backup, overwrite: true);
                    restored.Add((backup, full));
                }
                else
                {
                    written.Add(full);
                }

                File.Copy(source, full, overwrite: true);

                // Der gespeicherte Schluessel muss die kanonisch aufgeloeste Form sein, nicht die
                // rohe Aufrufer-Zeichenkette: jede spaetere Referenzzaehlung (RegisterShared/
                // ReleaseShared, PhysicalPath) vergleicht Ganz-Strings, und Apply() ist oeffentlich
                // -- ein Aufrufer koennte z. B. "bepinex/PLUGINS/Datei.dll" uebergeben, was
                // textuell zum selben full-Pfad aufloest, aber als eigener String nie wieder
                // gefunden wuerde (Pre-Flight-Review Fix Round 1, C1).
                var relPath = Path.GetRelativePath(Path.GetFullPath(gameRoot), full);
                result.Add(new InstalledFile { Path = relPath, Sha256 = Sha256File(full) });
            }

            AppLog.Info($"applied {ops.Count} file(s), backup {opId}");
            return result;
        }
        // Bewusst NICHT nach dem sonst in diesem Codebestand ueblichen Muster
        // "catch (Exception e) when (e is IOException or UnauthorizedAccessException)" gefiltert:
        // die Schleife oben kann durch praktisch jede Ausnahme unterbrochen werden (u. a.
        // InvalidOperationException aus RejectReparsedEscape bei einer Junction im Spielordner,
        // ArgumentException aus File.Copy bei einem ungueltigen Quellpfad) -- und in jedem dieser
        // Faelle MUSS das Rollback trotzdem laufen, sonst bleibt der Spielordner halb installiert.
        // Ein gefilterter Catch wuerde genau die Faelle ungefangen durchlassen, die das Rollback
        // am dringendsten braucht (Pre-Flight-Review Fix Round 1, M2/M3).
        catch (Exception e)
        {
            AppLog.Error($"install failed, rolling back {opId}", e);
            var affected = new List<string>();

            foreach (var (backup, original) in restored)
            {
                // restored.Add() laeuft VOR dem zerstoerenden Kopiervorgang, damit die Sicherung
                // fuer den Fall existiert, dass er scheitert -- aber genau dieser Fall (Ziel vom
                // laufenden Spiel gesperrt, DER Alltagsfall) bedeutet, dass die Originaldatei nie
                // angefasst wurde. Ein Rollback, der dann trotzdem versucht, die identische Datei
                // ueber sich selbst zu kopieren, scheitert an derselben Sperre und meldet einen
                // Fehlalarm auf einem voellig unberuehrten Spielordner (Pre-Flight-Review Fix
                // Round 1, I1/M4). Ein Hash-Vergleich unterscheidet "nie angefasst" von "wurde
                // ersetzt und muss zurueck" -- und erkennt zusaetzlich den Fall, dass File.Copy
                // mittendrin abbrach und eine halb geschriebene Datei hinterliess.
                bool needsRestore;
                try
                {
                    needsRestore = !File.Exists(original) || Sha256File(original) != Sha256File(backup);
                }
                catch (Exception he) when (he is IOException or UnauthorizedAccessException)
                {
                    // Laesst sich der Ist-Zustand nicht LESEN, ist unbekannt, ob die Datei angefasst
                    // wurde. Dann wird die Wiederherstellung versucht statt ungeprueft uebersprungen
                    // (sonst bliebe eine womoeglich zerstoerte Datei stehen) und auch nicht
                    // ungeprueft als Fehlschlag gemeldet: Lesen und Schreiben scheitern nicht
                    // zwangslaeufig gemeinsam -- eine Freigabe, die nur Schreibzugriff erlaubt,
                    // laesst das Lesen scheitern, waehrend das Zurueckkopieren gelingt.
                    AppLog.Warn($"rollback cannot compare '{original}' with its backup, attempting an unconditional restore");
                    needsRestore = true;
                }

                if (!needsRestore) continue;

                try { File.Copy(backup, original, overwrite: true); }
                catch (Exception re) when (re is IOException or UnauthorizedAccessException)
                {
                    AppLog.Error($"rollback could not restore '{original}' from backup '{backup}'", re);
                    affected.Add(original);
                }
            }
            foreach (var f in written)
            {
                try { File.Delete(f); }
                catch (Exception re) when (re is IOException or UnauthorizedAccessException)
                {
                    AppLog.Error($"rollback could not delete newly written '{f}'", re);
                    affected.Add(f);
                }
            }

            // Rein kosmetisch: neu angelegte, jetzt wieder leere Verzeichnisse aufraeumen.
            // Bewusst NICHT Teil von 'affected' -- ein liegen gebliebener leerer Ordner ist kein
            // Datenverlust, anders als die beiden Faelle oben. Tiefste zuerst, sonst scheitert
            // das Loeschen der Elternebene, weil das (noch nicht entfernte) Kind sie nicht leer
            // erscheinen laesst.
            foreach (var dir in createdDirs.Distinct()
                         .OrderByDescending(d => d.Count(c => c == Path.DirectorySeparatorChar)))
            {
                try
                {
                    if (Directory.Exists(dir) && !Directory.EnumerateFileSystemEntries(dir).Any())
                        Directory.Delete(dir);
                }
                catch (Exception de) when (de is IOException or UnauthorizedAccessException) { }
            }

            if (affected.Count > 0)
            {
                // Ein Rollback, der selbst scheitert, darf nie klanglos im normalen Fehlerpfad
                // untergehen: der Spielordner kann jetzt Dateien enthalten, die weder das
                // Original noch das neue Release sind. Strukturierte Pfade statt Prosa-Saetzen --
                // eine UI, die dem Nutzer die betroffenen Dateien zeigen soll, kann aus einem Satz
                // keinen Pfad zurueckgewinnen, und die OS-Fehlermeldung ist lokalisiert (auf
                // diesem Rechner deutsch), was der Konvention rein englischer Nutzertexte
                // widerspraeche -- die OS-Details stehen stattdessen oben im Log
                // (Pre-Flight-Review Fix Round 1, M2/M3).
                AppLog.Error($"rollback for {opId} incomplete, {affected.Count} path(s) may be left modified: {string.Join(", ", affected)}");
                throw new InstallRollbackException(
                    $"Installation failed and the automatic rollback could not fully undo it. " +
                    $"{affected.Count} file(s) may be left in a modified state. A backup of the pre-install " +
                    $"files is available at '{backupDir}'.",
                    e, affected, backupDir);
            }

            throw;
        }
    }

    public static void RegisterShared(AppState state, string relPath, string sha, string fileVersion, string modId)
    {
        var existing = state.SharedFiles.FirstOrDefault(
            f => f.Path.Equals(relPath, StringComparison.OrdinalIgnoreCase));

        if (existing is null)
        {
            state.SharedFiles.Add(new SharedFile
            {
                Path = relPath, Sha256 = sha, FileVersion = fileVersion, Providers = [modId]
            });
            return;
        }

        if (!existing.Providers.Contains(modId)) existing.Providers.Add(modId);

        // Bei Versionskonflikt gewinnt die hoehere Dateiversion.
        if (CompareVersions(fileVersion, existing.FileVersion) > 0)
        {
            AppLog.Warn($"shared file {relPath}: {modId} provides {fileVersion}, superseding {existing.FileVersion}");
            existing.FileVersion = fileVersion;
            existing.Sha256 = sha;
        }
    }

    /// <summary>Entfernt einen Anbieter. True heisst: kein Mod braucht die Datei mehr, sie darf weg.</summary>
    public static bool ReleaseShared(AppState state, string relPath, string modId)
    {
        var existing = state.SharedFiles.FirstOrDefault(
            f => f.Path.Equals(relPath, StringComparison.OrdinalIgnoreCase));
        if (existing is null) return true;

        existing.Providers.Remove(modId);
        if (existing.Providers.Count > 0) return false;

        state.SharedFiles.Remove(existing);
        return true;
    }

    internal static int CompareVersions(string a, string b)
        => Version.TryParse(a, out var va) && Version.TryParse(b, out var vb)
            ? va.CompareTo(vb)
            : string.CompareOrdinal(a, b);

    /// <summary>Leitet aus dem kanonischen, gespeicherten Pfad und dem aktuellen Enabled-Zustand
    /// des Mods her, wo eine Datei WIRKLICH auf der Platte liegt. InstalledFile.Path ist per
    /// Vertrag IMMER die aktivierte Lage (z. B. "BepInEx\plugins\X.dll") und wird von SetEnabled
    /// nie umgeschrieben (s. dort) -- diese Funktion ist die einzige Stelle, die daraus ableitet,
    /// ob die Datei gerade in plugins oder in plugins-disabled liegt. Ohne einen aus der
    /// gespeicherten Wahrheit abgeleiteten, stabilen Weg dorthin laufen Buchfuehrung
    /// (state.SharedFiles, per Pfad referenziert) und Wirklichkeit auseinander, sobald ein Mod
    /// deaktiviert wurde -- genau das hat vorher dazu gefuehrt, dass Remove() eine noch von einem
    /// anderen Mod benoetigte, geteilte Datei geloescht hat (Pre-Flight-Review Fix Round 1, C1).</summary>
    public static string PhysicalPath(GameInstall game, ModEntry mod, InstalledFile file)
        => PhysicalPathFor(game, mod, file, mod.Enabled);

    /// <summary>Wie PhysicalPath, aber fuer einen frei gewaehlten Zustand statt fuer den aktuellen.
    /// SetEnabled braucht beide Seiten derselben Ableitung -- Herkunft aus dem ALTEN, Ziel aus dem
    /// NEUEN Zustand --, damit Quelle und Ziel einer Verschiebung garantiert aus demselben
    /// kanonischen Schluessel stammen.</summary>
    private static string PhysicalPathFor(GameInstall game, ModEntry mod, InstalledFile file, bool enabled)
    {
        // Enthaltenseins-Pruefung wie bei Apply: file.Path stammt (mittelbar) aus state.json und
        // darf nicht ungeprueft mit game.Root kombiniert werden (Pre-Flight-Review Fix Round 1, I4).
        var canonical = ResolveInside(game.Root, file.Path);

        if (mod.SourceKind == "native")
        {
            // Native Mods docken sich nicht ueber BepInEx/plugins an, sondern ersetzen version.dll
            // direkt im Spielordner; ihr Aus-Zustand ist die "_"-Datei daneben, nicht
            // plugins-disabled. Das betrifft aber AUSSCHLIESSLICH version.dll selbst: ein natives
            // Paket bringt regelmaessig noch doorstop_config.ini oder eine .toml mit (s.
            // PackageMapper.GameRootFiles), und die verschiebt auch SetEnable nicht. Wuerde diese
            // Funktion fuer jede Datei eines nativen Mods version.dll liefern, loeschte Remove()
            // version.dll mehrfach und die Beilagen nie -- sie blieben fuer immer liegen.
            if (!canonical.Equals(Path.GetFullPath(game.VersionDll), StringComparison.OrdinalIgnoreCase))
                return canonical;
            return Path.GetFullPath(enabled ? game.VersionDll : game.VersionDllDisabled);
        }

        // Nur .dll-Dateien wandern nach plugins-disabled; alles andere (Configs, TOMLs, ...)
        // bleibt an seinem kanonischen Ort, auch wenn der Mod gerade deaktiviert ist.
        if (enabled || !file.Path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
            return canonical;

        // Path.GetFileName schneidet jeden Verzeichnisanteil (und damit jede Traversal-
        // Moeglichkeit) ohnehin ab, das Ergebnis liegt also automatisch innerhalb von
        // game.PluginsDisabled.
        return Path.Combine(game.PluginsDisabled, Path.GetFileName(file.Path));
    }

    /// <summary>Sichert die aktuelle Zieldatei, falls vorhanden, bevor sie durch
    /// File.Move(..., overwrite: true) klanglos ersetzt wird. Reproduziert: version.dll haelt
    /// den aktuellen Community Patch, ein liegen gebliebenes version.dll_ eine aeltere Fassung --
    /// ein Enable wuerde ohne diese Sicherung die aktuelle Datei unwiederbringlich durch die alte
    /// ersetzen (Pre-Flight-Review Fix Round 1, I3). Wirft absichtlich nichts extra: schlaegt das
    /// Sichern fehl (z. B. Backup-Ordner nicht beschreibbar), soll das genauso wie jeder andere
    /// I/O-Fehler in der aufrufenden Schleife vom bestehenden Rollback aufgefangen werden, statt
    /// die eigentliche Aktion klanglos ohne Sicherung durchzufuehren.</summary>
    private static void BackupBeforeOverwrite(string to, string modId)
    {
        if (!File.Exists(to)) return;

        var dir = Path.Combine(AppPaths.BackupDir, "toggle-" + DateTime.Now.ToString("yyyyMMdd-HHmmss-fff"));
        Directory.CreateDirectory(dir);
        var backup = Path.Combine(dir, Path.GetFileName(to));
        File.Copy(to, backup, overwrite: true);
        AppLog.Warn($"toggling {modId}: '{to}' already existed and would have been overwritten; backed it up to '{backup}' first");
    }

    /// <summary>An/Aus per Verschieben — die Konvention, die der Bestand schon benutzt. Kann
    /// (Pre-Flight-Review Fix Round 1, M2/M3) genau wie Apply() eine InstallRollbackException
    /// werfen, wenn ein Mehrdatei-Mod nach einem Teilfehlschlag nicht vollstaendig zurueckgeschoben
    /// werden kann.</summary>
    public static void SetEnabled(GameInstall game, ModEntry mod, bool enabled)
    {
        if (mod.SourceKind == "native")
        {
            var from = mod.Enabled ? game.VersionDll : game.VersionDllDisabled;
            var to   = enabled     ? game.VersionDll : game.VersionDllDisabled;
            // Eine einzelne Umbenennung ist auf demselben Volume atomar: sie gelingt ganz oder
            // gar nicht, ein halb verschobenes version.dll gibt es nicht. Existiert weder die
            // aktive noch die deaktivierte Datei (z. B. Community Patch nie installiert), ist das
            // kein Fehler -- File.Exists faengt das ab, bevor File.Move ueberhaupt gerufen wird.
            // from == to heisst: der Mod ist bereits im gewuenschten Zustand. Dann gar nichts tun,
            // statt eine Datei ueber sich selbst zu schieben und dabei eine sinnlose Sicherung
            // samt Warnung zu erzeugen.
            if (!from.Equals(to, StringComparison.OrdinalIgnoreCase) && File.Exists(from))
            {
                try
                {
                    BackupBeforeOverwrite(to, mod.Id);
                    File.Move(from, to, overwrite: true);
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    AppLog.Error($"could not move {from} to {to} while toggling {mod.Id}", e);
                    throw;
                }
            }
            mod.Enabled = enabled;
            AppLog.Info($"{mod.Id} {(enabled ? "enabled" : "disabled")}");
            return;
        }

        // Mehrdatei-Mods sollen nicht in einem Mischzustand enden, wenn eine mittlere Datei
        // scheitert (z. B. vom laufenden Spiel gesperrt): jede erfolgreiche Verschiebung wird
        // gemerkt, damit ein spaeterer Fehlschlag alle bereits bewegten Dateien wieder
        // zurueckschieben kann, bevor mod.Enabled ueberhaupt angefasst wird. f.Path wird dabei nie
        // veraendert (s. PhysicalPath) -- es bleibt der stabile Schluessel, unter dem state.SharedFiles
        // dieselbe Datei womoeglich noch fuehrt.
        // Nur die reinen Pfade: seit f.Path nie mehr umgeschrieben wird (C1), gibt es beim
        // Zurueckrollen nichts an der Buchfuehrung wiederherzustellen -- die Datei muss nur
        // physisch zurueck.
        var moved = new List<(string From, string To)>();
        try
        {
            foreach (var f in mod.Files.Where(f => f.Path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)))
            {
                // BEIDE Seiten aus demselben kanonischen Schluessel ableiten. Ein Ziel, das statt
                // dessen stumpf aus plugins\<Dateiname> gebaut wird, verliert die Verzeichnisstruktur:
                // eine Datei aus "BepInEx\plugins\MyMod\X.dll" landete beim Wieder-Aktivieren in
                // "BepInEx\plugins\X.dll", PhysicalPath zeigte danach dauerhaft ins Leere, ein
                // spaeteres Deaktivieren fasste sie nie wieder an (sie bliebe fuer BepInEx aktiv,
                // obwohl die UI "deaktiviert" zeigt) und Remove() liesse sie fuer immer liegen.
                var from = PhysicalPathFor(game, mod, f, mod.Enabled); // ALTER Zustand
                var to   = PhysicalPathFor(game, mod, f, enabled);     // NEUER Zustand
                if (from.Equals(to, StringComparison.OrdinalIgnoreCase)) continue; // schon im Zielzustand
                if (!File.Exists(from)) continue;                                  // fehlt -- nichts zu tun

                Directory.CreateDirectory(Path.GetDirectoryName(to)!);
                BackupBeforeOverwrite(to, mod.Id);
                File.Move(from, to, overwrite: true);
                moved.Add((from, to));
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            AppLog.Error($"toggling {mod.Id} failed after moving {moved.Count} file(s), rolling back", e);
            var stuck = new List<string>();
            for (var i = moved.Count - 1; i >= 0; i--)
            {
                var (from, to) = moved[i];
                try { File.Move(to, from, overwrite: true); }
                catch (Exception re) when (re is IOException or UnauthorizedAccessException)
                {
                    stuck.Add(to);
                    AppLog.Error($"could not move {to} back to {from} while rolling back {mod.Id}", re);
                }
            }

            if (stuck.Count > 0)
            {
                AppLog.Error($"toggling {mod.Id} left {stuck.Count} file(s) stuck: {string.Join(", ", stuck)}");
                throw new InstallRollbackException(
                    $"Toggling '{mod.Id}' failed and {stuck.Count} file(s) could not be moved back to their " +
                    "original location. The mod may now be in a mixed enabled/disabled state.",
                    e, stuck);
            }

            throw;
        }

        mod.Enabled = enabled;
        AppLog.Info($"{mod.Id} {(enabled ? "enabled" : "disabled")}");
    }

    /// <summary>Deinstalliert. Configs werden nie geloescht, nur gesichert (Spec §6.6).</summary>
    public static void Remove(AppState state, GameInstall game, ModEntry mod)
    {
        foreach (var f in mod.Files)
        {
            // Erst aufloesen, dann erst die Buchfuehrung anfassen: PhysicalPath wirft bei einem
            // Pfad, der den Spielordner verliesse (Pre-Flight-Review Fix Round 1, I4) -- geschaehe
            // das nach ReleaseShared, waere der Anbieter-Eintrag bereits ausgetragen, obwohl der
            // Abbruch verhindert, dass ueberhaupt je etwas geloescht wird.
            var full = PhysicalPath(game, mod, f);
            if (!ReleaseShared(state, f.Path, mod.Id)) continue;
            DeleteIfExists(full, mod.Id);
        }

        // Ein Mod bringt oft mehr Dateien mit, als sein eigener Files-Eintrag zeigt: die
        // Hauptdatei steht in mod.Files, alles Weitere (mitgelieferte Abhaengigkeiten) landet
        // stattdessen in state.SharedFiles mit diesem Mod als einem von womoeglich mehreren
        // Anbietern. Ohne diese Schleife saehe Remove() solche Dateien nie, sie blieben fuer
        // immer auf der Platte liegen. Snapshot per ToList() vor der Iteration: ReleaseShared
        // entfernt Eintraege aus state.SharedFiles, eine Live-Iteration darueber wuerfe eine
        // InvalidOperationException.
        foreach (var shared in state.SharedFiles.Where(s => s.Providers.Contains(mod.Id)).ToList())
        {
            var (path, sha, ver) = (shared.Path, shared.Sha256, shared.FileVersion);
            if (!ReleaseShared(state, path, mod.Id)) continue;

            string full;
            try
            {
                // Geteilte Dateien werden von SetEnabled nie zwischen plugins und plugins-disabled
                // verschoben (nur mod.Files ist davon betroffen) -- ihr kanonischer Ort ist deshalb
                // auch ihr tatsaechlicher, PhysicalPath ist hier nicht noetig. Die Enthaltenseins-
                // Pruefung bleibt trotzdem Pflicht: 'path' stammt aus state.json (Pre-Flight-Review
                // Fix Round 1, I4).
                full = ResolveInside(game.Root, path);
            }
            catch (InvalidOperationException e)
            {
                // Noch nichts geloescht -- die Buchfuehrung wiederherstellen, statt einen
                // Anbieter zu verlieren, ohne dass je etwas passiert ist.
                RegisterShared(state, path, sha, ver, mod.Id);
                AppLog.Error($"shared file path '{path}' for {mod.Id} escapes the game folder, refusing to delete", e);
                throw;
            }

            try
            {
                DeleteIfExists(full, mod.Id);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // Der letzte Anbieter ist oben schon ausgetragen (der Eintrag existiert in
                // state.SharedFiles jetzt nicht mehr) -- aber die Datei liegt noch auf der
                // Platte, weil das Loeschen fehlgeschlagen ist (z. B. vom laufenden Spiel
                // gesperrt). Anders als bei mod.Files oben ist das hier NICHT von selbst
                // reparierbar: ein erneuter Remove()-Versuch wuerde diesen Eintrag nie
                // wiederfinden, weil er nicht mehr in state.SharedFiles steht. Deshalb hier
                // wiederherstellen. Providers wird bewusst wieder nur mit mod.Id gefuellt:
                // ReleaseShared liefert 'true' ausschliesslich dann, wenn mod.Id tatsaechlich
                // der letzte verbliebene Anbieter war -- alle anderen sind laengst weg.
                RegisterShared(state, path, sha, ver, mod.Id);
                throw;
            }
        }

        BackupConfig(game, mod.Id);
        state.Mods.Remove(mod);
        AppLog.Info($"removed {mod.Id}");
    }

    private static void DeleteIfExists(string full, string modId)
    {
        try { if (File.Exists(full)) File.Delete(full); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            AppLog.Error($"could not delete {full} while removing {modId}", e);
            throw;
        }
    }

    private static void BackupConfig(GameInstall game, string modId)
    {
        // modId kommt ungefiltert aus [BepInPlugin] einer Fremd-DLL (ModInspector) und wird dort
        // nirgends validiert -- effektiv angreifer-kontrollierter Text. Ohne Saeuberung wuerde
        // z. B. modId == "..\\..\\evil" aus dem Config-Dateinamen einen Pfad machen, der aus dem
        // vorgesehenen Ordner hinauszeigt (Pre-Flight-Review Fix Round 1, I4). ModInspector selbst
        // bleibt unveraendert -- die GUID bleibt die Identitaet des Mods, sie darf nur nie
        // ungefiltert Teil eines Pfads werden.
        var safeId = SanitizeForFileName(modId);

        string cfg, dest;
        try
        {
            cfg = ResolveInside(game.Root, Path.Combine("BepInEx", "config", safeId + ".cfg"));
            dest = ResolveUnder(AppPaths.ConfigBackupDir,
                $"{safeId}-{DateTime.Now:yyyyMMdd-HHmmss}.cfg", "config backup path escapes the backup folder");
        }
        catch (InvalidOperationException e)
        {
            AppLog.Error($"config path for mod '{modId}' would escape its folder, skipping backup", e);
            return;
        }

        if (!File.Exists(cfg)) return;

        try
        {
            Directory.CreateDirectory(AppPaths.ConfigBackupDir);
            File.Move(cfg, dest);
            AppLog.Info($"config for {modId} moved to {dest}");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Scheitert nur die Config-Sicherung (z. B. Zielordner nicht beschreibbar), bleibt
            // die Config an ihrem Originalort liegen -- kein Datenverlust, denn sie wird nie
            // geloescht, nur verschoben (Spec §6.6). Die eigentliche Deinstallation (die Mod-
            // Dateien sind zu diesem Zeitpunkt schon weg) darf daran trotzdem nicht scheitern,
            // sonst wuerde state.Mods den Mod weiterhin fuehren, obwohl seine Dateien bereits
            // von der Platte verschwunden sind -- state.json und Platte liefen dann auseinander.
            AppLog.Error($"could not back up config for {modId}, leaving it at {cfg}", e);
        }
    }

    /// <summary>Macht eine Mod-Id sicher fuer die Verwendung als (Teil eines) Dateinamens: ersetzt
    /// jedes auf Windows in Dateinamen verbotene Zeichen (deckt u. a. \, /, : ab) durch '_' und
    /// entschaerft Punktfolgen wie ".." zusaetzlich, weil die keinen verbotenen Buchstaben
    /// enthalten, aber trotzdem ein Verzeichnis-Aufstieg sind, sobald sie als eigenes Pfadsegment
    /// auftauchen.</summary>
    internal static string SanitizeForFileName(string id)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string(id.Select(c => invalid.Contains(c) ? '_' : c).ToArray());
        cleaned = cleaned.Replace("..", "__");
        return cleaned.Length == 0 ? "_" : cleaned;
    }

    /// <summary>Sicherungen aelter als 30 Tage aufraeumen. Beim Start aufgerufen.</summary>
    public static void PruneBackups()
    {
        if (!Directory.Exists(AppPaths.BackupDir)) return;
        var cutoff = DateTime.Now.AddDays(-30);
        foreach (var dir in Directory.EnumerateDirectories(AppPaths.BackupDir))
            if (Directory.GetLastWriteTime(dir) < cutoff)
                try { Directory.Delete(dir, recursive: true); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }
}

/// <summary>Wird geworfen, wenn Apply() oder SetEnabled() sowohl scheitern als auch ihr eigenes
/// Rollback nicht vollstaendig durchfuehren koennen. Der Aufrufer MUSS das dem Nutzer sichtbar
/// machen -- der Spielordner kann jetzt Dateien enthalten, die weder dem alten noch dem neuen
/// Zustand entsprechen.</summary>
public sealed class InstallRollbackException : Exception
{
    /// <summary>Die Pfade, bei denen das Rollback (Wiederherstellen aus der Sicherung bzw.
    /// Zurueckschieben einer bereits verschobenen Datei) fehlgeschlagen ist. Reine Pfade, keine
    /// Fehlertexte -- die sind OS-lokalisiert (auf diesem Rechner z. B. deutsch) und wuerden eine
    /// rein englische Nutzermeldung verunreinigen; die Details dazu stehen im Manager-Log. Gedacht
    /// fuer eine UI, die dem Nutzer genau zeigen muss, welche Dateien betroffen sind. Wird sowohl
    /// von Apply() als auch von SetEnabled() geworfen.</summary>
    public IReadOnlyList<string> AffectedPaths { get; }

    /// <summary>Wo die Sicherung fuer diesen (fehlgeschlagenen) Versuch liegt, falls vorhanden --
    /// null bei SetEnabled() (das sind Verschiebungen, deren Rueckweg fehlgeschlagen ist, keine
    /// eigene Sicherung), gesetzt bei Apply().</summary>
    public string? BackupDirectory { get; }

    public InstallRollbackException(string message, Exception inner, IReadOnlyList<string> affectedPaths,
        string? backupDirectory = null)
        : base(message, inner)
    {
        AffectedPaths = affectedPaths;
        BackupDirectory = backupDirectory;
    }
}
