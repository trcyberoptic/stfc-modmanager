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
        // Ein Pfad, den Path.GetFullPath gar nicht erst verarbeiten kann (verbotene Zeichen, zu
        // lang), wird wie jeder andere Enthaltenseins-Fehlschlag behandelt statt seine eigene
        // Ausnahmeart nach aussen zu tragen: die Aufrufer fangen genau InvalidOperationException,
        // um so einen Eintrag zu ueberspringen -- eine durchgereichte ArgumentException risse
        // Remove() stattdessen ab und liesse den Mod fuer immer in state.Mods stehen
        // (Fix Round 3, Minor 2).
        string full;
        try { full = Path.GetFullPath(Path.Combine(root, relativeTarget)); }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new InvalidOperationException($"{errorContext}: {relativeTarget}", e);
        }

        if (!full.StartsWith(NormalizeRootPrefix(root), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"{errorContext}: {relativeTarget}");
        return full;
    }

    /// <summary>Loest einen relativen Pfad rein textuell gegen den Spielordner auf, ohne zu werfen.
    /// Fuer Klassifizierungsfragen ("ist das eine Config?", "liegt das unter plugins?"), die eine
    /// Antwort brauchen und keine Ausnahme.</summary>
    private static string? TryResolve(GameInstall game, string relPath)
    {
        try { return Path.GetFullPath(Path.Combine(game.Root, relPath)); }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    /// <summary>Zwei relative Pfade bezeichnen dieselbe Datei im Spielordner. Verglichen wird die
    /// aufgeloeste Form, nicht die Schreibweise -- "BepInEx\config\X.cfg" und
    /// "BepInEx\.\config\X.cfg" sind derselbe Eintrag.</summary>
    private static bool SameRelativePath(GameInstall game, string a, string b)
    {
        var (ra, rb) = (TryResolve(game, a), TryResolve(game, b));
        return ra is not null && rb is not null && IsSamePath(ra, rb);
    }

    /// <summary>DIE Enthaltenseins-Pruefung fuer alles, was im Spielordner angefasst wird: erst
    /// textuell (ResolveUnder), dann physisch (RejectReparsedEscape). Bewusst EIN Helfer fuer
    /// Apply, Remove, SetEnabled und PhysicalPath -- vorher hatte nur Apply die physische Haelfte,
    /// und Remove/SetEnabled erfuellten die Zusage "kein Zugriff ausserhalb des Spielordners" damit
    /// nur textuell: ueber eine Junction in "BepInEx\plugins" hat Remove nachweislich eine Datei
    /// AUSSERHALB geloescht und SetEnabled eine von aussen hereingeholt (Fix Round 2, I3). Drei
    /// Aufrufstellen, eine Implementierung, keine Drift.</summary>
    private static string ResolveInside(string gameRoot, string relativePath)
    {
        var full = ResolveUnder(gameRoot, relativePath, "path escapes the game folder");

        // Path.GetDirectoryName liefert null, wenn der aufgeloeste Pfad SELBST eine Wurzel ist.
        // Bei einem Spielordner, der eine blosse Laufwerkswurzel ist ("X:\" -- genau der Fall, fuer
        // den es den Trailing-Separator-Fix gibt), laesst die Praefixpruefung oben einen leeren
        // gespeicherten Pfad durch, und das null wanderte ungeprueft in die Reparse-Pruefung: eine
        // ArgumentNullException, die den Ueberspringen-Pfad in Remove() nicht abfaengt und den Mod
        // dauerhaft in state.Mods festnagelte (Fix Round 3, Minor 2). Ein Pfad, der das
        // Wurzelverzeichnis selbst ist, ist ohnehin nie eine gueltige Datei.
        var dir = Path.GetDirectoryName(full);
        if (dir is null)
            throw new InvalidOperationException($"path is the game folder itself, not a file inside it: '{relativePath}'");

        RejectReparsedEscape(dir, gameRoot, "path escapes the game folder");
        return full;
    }

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
    /// draussen (Pre-Flight-Review Fix Round 1, I5).
    ///
    /// Geprueft wird JEDE Ebene vom Zielverzeichnis aufwaerts bis zur Spielordner-Wurzel, nicht
    /// nur das unmittelbare Elternverzeichnis: liegt die Junction weiter oben (etwa auf
    /// "BepInEx"), dann ist "BepInEx\plugins" ein ganz normales, echtes Verzeichnis INNERHALB des
    /// Junction-Ziels, Directory.ResolveLinkTarget liefert dafuer null, und ein Ziel wie
    /// "BepInEx\plugins\Evil.dll" wurde nachweislich ausserhalb des Spielordners geschrieben,
    /// waehrend das flachere "BepInEx\x.dll" korrekt abgelehnt wurde (Fix Round 2, I2).
    ///
    /// Der TIEFSTE Reparse-Point auf dem Weg entscheidet: ResolveLinkTarget(returnFinalTarget:
    /// true) liefert bereits ein absolutes Ziel, in dem alle darueber liegenden Verknuepfungen
    /// aufgeloest sind -- an dieses Ziel muss nur noch der Rest des Pfades unterhalb der
    /// gefundenen Ebene angehaengt werden.</summary>
    private static void RejectReparsedEscape(string dir, string gameRoot, string errorContext)
    {
        var rootFull = Path.GetFullPath(gameRoot);
        var probe = Path.GetFullPath(dir);
        var remainder = "";

        while (true)
        {
            var link = ResolveLinkOrNull(probe);
            if (link is not null)
            {
                var landing = Path.GetFullPath(remainder.Length == 0 ? link : Path.Combine(link, remainder));

                // Zwei zulaessige Wurzeln, und die zweite ist keine Kuer: ist der SPIELORDNER
                // SELBST ein Reparse-Point (ein per mklink auf eine andere Platte ausgelagerter
                // Spielordner ist eine voellig normale Nutzerkonfiguration), loest jeder Pfad
                // darin auf eine Stelle auf, die textuell ausserhalb liegt. Ohne den Vergleich
                // gegen die aufgeloeste Wurzel wuerde diese Pruefung dann JEDE Installation in
                // einen solchen Spielordner ablehnen -- fail closed heisst, echte Ausbrueche zu
                // stoppen, nicht legitime Einrichtungen.
                var resolvedRoot = ResolveLinkOrNull(rootFull) ?? rootFull;
                if (IsRootOrUnder(landing, rootFull) || IsRootOrUnder(landing, resolvedRoot)) return;

                throw new InvalidOperationException($"{errorContext} via a reparse point pointing to '{landing}'");
            }

            // Wurzel erreicht, ohne unterwegs auf eine Verknuepfung zu stossen: der rein
            // textuelle Befund gilt.
            if (IsSamePath(probe, rootFull)) return;

            var parent = Path.GetDirectoryName(probe);
            if (parent is null) return; // Laufwerkswurzel ueberschritten (kann bei validen Zielen nicht passieren)

            remainder = remainder.Length == 0
                ? Path.GetFileName(probe)
                : Path.Combine(Path.GetFileName(probe), remainder);
            probe = parent;
        }
    }

    private static bool IsSamePath(string a, string b)
        => Path.TrimEndingDirectorySeparator(a).Equals(
               Path.TrimEndingDirectorySeparator(b), StringComparison.OrdinalIgnoreCase);

    /// <summary>Eine Konfigurationsdatei unterhalb von BepInEx\config. Die wird NIE geloescht, nur
    /// gesichert (Spec §6.6) -- und zwar unabhaengig davon, wie sie in mod.Files geraten ist.
    /// Genau das war die Luecke: ein Archiv mit "MyMod/BepInEx/config/MyMod.cfg" bildet (durch
    /// PackageMapper.MapEntries nachgemessen) auf ein regulaeres Ziel ab, landete damit in
    /// mod.Files -- und Remove() hat die vom Nutzer eingestellte Config schlicht geloescht, bevor
    /// BackupConfig sie sichern konnte (Fix Round 2, kritisch).</summary>
    private static bool IsProtectedConfig(GameInstall game, string relPath)
    {
        if (!relPath.EndsWith(".cfg", StringComparison.OrdinalIgnoreCase)) return false;

        // Entschieden wird an der AUFGELOESTEN Form, nicht an einem Zeichenketten-Praefix: ein
        // Praefixvergleich behandelt Gross-/Kleinschreibung und '/' zwar richtig, faellt aber bei
        // einem "."-Segment durch -- "BepInEx\.\config\Dot.cfg" wurde nachweislich GELOESCHT statt
        // gesichert. Weder Apply noch PackageMapper erzeugen diese Schreibweise, eine
        // handbearbeitete state.json aber sehr wohl, und der Schutz gilt laut Regel unabhaengig
        // davon, wie der Eintrag entstanden ist (Fix Round 3, Minor 1). Ein Pfad, den GetFullPath
        // gar nicht verarbeiten kann, ist hier "nicht geschuetzt" -- er wird aber auch nie
        // geloescht, weil die Aufloesung im Loeschzweig an derselben Stelle scheitert und der
        // Eintrag dort uebersprungen wird.
        var full = TryResolve(game, relPath);
        return full is not null && IsRootOrUnder(full, game.Config) && !IsSamePath(full, game.Config);
    }

    /// <summary>Der Teil eines gespeicherten Pfades unterhalb von "BepInEx\plugins\", oder null,
    /// wenn die Datei gar nicht dort liegt. Nur Dateien unterhalb von plugins duerfen beim
    /// Umschalten wandern: ein Mod, der (auch) winhttp.dll oder doorstop_config.ini im
    /// Spielordner mitbringt, haette sonst beim Deaktivieren den ganzen Doorstop-Loader nach
    /// plugins-disabled verschoben und damit still SAEMTLICHE Mods abgeschaltet (Fix Round 2,
    /// Minor 5).</summary>
    private static string? PluginsRelative(GameInstall game, string relPath)
    {
        // Aufgeloest verglichen, aus demselben Grund wie bei IsProtectedConfig -- und ohne
        // Mehraufwand, weil Path.GetRelativePath den Rest unterhalb von plugins gleich mitliefert.
        var full = TryResolve(game, relPath);
        if (full is null || !IsRootOrUnder(full, game.Plugins) || IsSamePath(full, game.Plugins))
            return null;
        return Path.GetRelativePath(Path.GetFullPath(game.Plugins), full);
    }

    /// <summary>Der gespiegelte Ort unterhalb von plugins-disabled, oder null, wenn die Datei beim
    /// Umschalten gar nicht wandert. Eine Datei kann physisch an genau zwei Stellen liegen -- hier
    /// oder an ihrem kanonischen Ort --, und Remove() muss beide kennen (Fix Round 3, wichtig).</summary>
    private static string? DisabledMirror(GameInstall game, string relPath)
    {
        if (!relPath.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)) return null;
        var rel = PluginsRelative(game, relPath);
        return rel is null ? null : ResolveInside(game.Root, Path.Combine("BepInEx", "plugins-disabled", rel));
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

            // Ein Ziel, unter dessen Namen bereits ein VERZEICHNIS liegt, kann nie eine Datei
            // werden: File.Exists() liefert false (es ist ja kein File), der Eintrag landete
            // faelschlich in "written", der Kopiervorgang scheiterte, und das Rollback versuchte
            // per File.Delete() ein Verzeichnis zu loeschen -- das schlaegt ebenfalls fehl und
            // meldete Schaden an einem Ordner, den nie jemand angefasst hat (Fix Round 2, Minor 1).
            if (Directory.Exists(full))
                throw new ArgumentException($"target is an existing directory, not a file: '{target}'", nameof(ops));

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

        // Der Zeitstempel allein ist NICHT eindeutig: zwoelf unmittelbar aufeinander folgende
        // Apply()-Aufrufe erzeugten gemessen nur sechs verschiedene Ordnernamen (Millisekunden-
        // Aufloesung), und der zweite Aufruf mit demselben Namen haette die unberuehrte Sicherung
        // des ersten ueberschrieben -- ein Rollback des ersten Aufrufs haette dann die falschen
        // Daten zurueckgespielt (Fix Round 2, Minor 2).
        var opId = $"{DateTime.Now:yyyyMMdd-HHmmss-fff}-{Guid.NewGuid().ToString("N")[..8]}";
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

        if (enabled) return canonical;

        // Nur .dll-Dateien UNTERHALB VON BepInEx\plugins wandern; alles andere (Configs, TOMLs,
        // und vor allem Dateien direkt im Spielordner wie winhttp.dll) bleibt an seinem
        // kanonischen Ort, auch wenn der Mod gerade deaktiviert ist.
        //
        // Die Unterordnerstruktur wird GESPIEGELT, nicht abgeschnitten: mit
        // Path.GetFileName(file.Path) fielen "BepInEx\plugins\A\Core.dll" und
        // "BepInEx\plugins\B\Core.dll" beide auf dasselbe "plugins-disabled\Core.dll" zusammen --
        // ein Deaktivieren beider Mods ueberschrieb die eine Datei mit der anderen, und nach dem
        // Wieder-Aktivieren enthielt A\Core.dll den Inhalt von B, waehrend B\Core.dll ganz weg war.
        // PackageMapper laesst beide Ziele aus EINEM Archiv zu, der Fall ist also erreichbar
        // (Fix Round 2, I5). Als reiner Praefixtausch plugins <-> plugins-disabled bleibt das
        // Umschalten dagegen umkehrbar.
        return DisabledMirror(game, file.Path) ?? canonical;
    }

    /// <summary>Alle Orte, an denen eine Datei physisch liegen kann. Remove() darf sich NICHT auf
    /// mod.Enabled verlassen, um daraus einen einzigen Ort abzuleiten: seit SetEnabled eine
    /// geteilte Bibliothek bewusst stehen laesst, wenn ein anderer aktiver Mod sie noch braucht
    /// (Fix Round 2, I1), koennen Zustand und Wirklichkeit legitim auseinanderfallen -- die Datei
    /// liegt dann in plugins, waehrend mod.Enabled false ist. Genau diese Kombination liess nach
    /// "modA deaktivieren, modB entfernen, modA entfernen" eine verwaiste, von niemandem mehr
    /// verzeichnete DLL zurueck, die BepInEx munter weiterlud (Fix Round 3, wichtig). Geloescht
    /// wird deshalb, was tatsaechlich da ist -- beide Orte gehoeren derselben Datei, und die
    /// Referenzzaehlung hat bereits bestaetigt, dass sie niemand mehr braucht.</summary>
    private static string[] PhysicalCandidates(GameInstall game, ModEntry mod, InstalledFile file)
    {
        var enabled = PhysicalPathFor(game, mod, file, true);
        var disabled = PhysicalPathFor(game, mod, file, false);
        return enabled.Equals(disabled, StringComparison.OrdinalIgnoreCase) ? [enabled] : [enabled, disabled];
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

        // Zufallsanteil wie bei Apply()s opId: der Millisekunden-Zeitstempel allein ist nicht
        // eindeutig, und File.Copy(overwrite: true) unten wuerde eine gleichnamige aeltere
        // Sicherung sonst still ueberschreiben (Fix Round 2, Minor 2).
        var dir = Path.Combine(AppPaths.BackupDir,
            $"toggle-{DateTime.Now:yyyyMMdd-HHmmss-fff}-{Guid.NewGuid().ToString("N")[..8]}");
        Directory.CreateDirectory(dir);
        var backup = Path.Combine(dir, Path.GetFileName(to));
        File.Copy(to, backup, overwrite: true);
        AppLog.Warn($"toggling {modId}: '{to}' already existed and would have been overwritten; backed it up to '{backup}' first");
    }

    /// <summary>True, wenn die Datei laut Referenzzaehlung noch von einem ANDEREN, gerade
    /// aktivierten Mod gebraucht wird. SetEnabled hat bis Fix Round 2 jede .dll aus mod.Files
    /// verschoben, ohne state.SharedFiles ueberhaupt sehen zu koennen (die Signatur kannte den
    /// AppState nicht): Json.dll, angeboten von modA und modB, landete beim Deaktivieren von modA
    /// in plugins-disabled, waehrend modB aktiviert blieb -- modB konnte seine Abhaengigkeit
    /// stillschweigend nicht mehr laden, obwohl die UI ihn als aktiv fuehrte (Fix Round 2, I1).</summary>
    private static bool IsSharedWithAnotherEnabledMod(AppState state, ModEntry mod, string relPath)
    {
        var record = state.SharedFiles.FirstOrDefault(
            f => f.Path.Equals(relPath, StringComparison.OrdinalIgnoreCase));
        if (record is null) return false;

        return record.Providers.Any(p =>
            !p.Equals(mod.Id, StringComparison.OrdinalIgnoreCase)
            && state.Mods.Any(m => m.Enabled && m.Id.Equals(p, StringComparison.OrdinalIgnoreCase)));
    }

    /// <summary>Raeumt Unterebenen auf, die durch das Umschalten leer zurueckgeblieben sind --
    /// dasselbe rein kosmetische Aufraeumen, das der Rollback in Apply() fuer selbst angelegte
    /// Verzeichnisse macht. plugins und plugins-disabled SELBST bleiben immer stehen; entfernt
    /// werden nur die gespiegelten Unterordner darunter, und auch die nur, wenn sie wirklich leer
    /// sind. Tiefste zuerst, sonst haelt ein noch nicht entferntes Kind die Elternebene belegt.</summary>
    private static void PruneEmptyToggleDirs(GameInstall game, IEnumerable<string> dirs)
    {
        foreach (var dir in dirs.Distinct(StringComparer.OrdinalIgnoreCase)
                     .OrderByDescending(d => d.Count(c => c == Path.DirectorySeparatorChar)))
        {
            if (IsSamePath(dir, game.Plugins) || IsSamePath(dir, game.PluginsDisabled)) continue;
            if (!IsRootOrUnder(dir, game.Plugins) && !IsRootOrUnder(dir, game.PluginsDisabled)) continue;

            try
            {
                if (Directory.Exists(dir) && !Directory.EnumerateFileSystemEntries(dir).Any())
                    Directory.Delete(dir);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
    }

    /// <summary>An/Aus per Verschieben — die Konvention, die der Bestand schon benutzt. Kann
    /// (Pre-Flight-Review Fix Round 1, M2/M3) genau wie Apply() eine InstallRollbackException
    /// werfen, wenn ein Mehrdatei-Mod nach einem Teilfehlschlag nicht vollstaendig zurueckgeschoben
    /// werden kann. Braucht den AppState, weil eine geteilte Bibliothek nicht bewegt werden darf,
    /// solange ein anderer, noch aktiver Mod sie benoetigt (Fix Round 2, I1).</summary>
    public static void SetEnabled(AppState state, GameInstall game, ModEntry mod, bool enabled)
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

        // Vorflug wie in Apply(): erst ALLE Pfade aufloesen und alle Ausschluesse entscheiden,
        // dann erst die erste Datei bewegen. Die Aufloesung wirft bei einem Pfad, der den
        // Spielordner verliesse, eine InvalidOperationException -- also gerade NICHT eine der
        // beiden Ausnahmen, auf die der Rollback-Catch unten gefiltert war. Mit
        // Files = [BepInEx\plugins\Good.dll, ..\..\Escape.dll] wurde Good.dll deshalb verschoben,
        // der zweite Eintrag warf, KEIN Rollback lief, und mod.Enabled blieb auf dem alten Wert
        // stehen -- PhysicalPath zeigte danach auf eine Datei, die dort nicht mehr liegt: genau
        // die Drift zwischen Buchfuehrung und Platte, gegen die C1 existiert (Fix Round 2, I4).
        var plan = new List<(string From, string To)>();
        foreach (var f in mod.Files)
        {
            if (!f.Path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)) continue;

            // Ein Eintrag, der gar nicht im Spielordner liegt, ist Datenmuell oder ein
            // Manipulationsversuch -- deutlich protokollieren (Warn, nicht Info), aber die
            // Umschaltung nicht daran scheitern lassen: sonst liesse sich ein Mod wegen eines
            // einzigen kaputten Eintrags nie wieder an- oder abschalten. Angefasst wird er nicht.
            var resolved = TryResolve(game, f.Path);
            if (resolved is null || !IsRootOrUnder(resolved, game.Root))
            {
                AppLog.Warn($"toggling {mod.Id}: ignoring file entry '{f.Path}', it does not resolve inside the game folder");
                continue;
            }

            if (PluginsRelative(game, f.Path) is null)
            {
                AppLog.Info($"toggling {mod.Id}: leaving '{f.Path}' where it is, only files under BepInEx\\plugins are moved");
                continue;
            }

            if (IsSharedWithAnotherEnabledMod(state, mod, f.Path))
            {
                AppLog.Warn($"toggling {mod.Id}: leaving shared file '{f.Path}' in place, another enabled mod still provides it");
                continue;
            }

            // BEIDE Seiten aus demselben kanonischen Schluessel ableiten. Ein Ziel, das statt
            // dessen stumpf aus plugins\<Dateiname> gebaut wird, verliert die Verzeichnisstruktur:
            // eine Datei aus "BepInEx\plugins\MyMod\X.dll" landete beim Wieder-Aktivieren in
            // "BepInEx\plugins\X.dll", PhysicalPath zeigte danach dauerhaft ins Leere, ein
            // spaeteres Deaktivieren fasste sie nie wieder an (sie bliebe fuer BepInEx aktiv,
            // obwohl die UI "deaktiviert" zeigt) und Remove() liesse sie fuer immer liegen.
            var from = PhysicalPathFor(game, mod, f, mod.Enabled); // ALTER Zustand
            var to   = PhysicalPathFor(game, mod, f, enabled);     // NEUER Zustand
            if (from.Equals(to, StringComparison.OrdinalIgnoreCase)) continue; // schon im Zielzustand

            plan.Add((from, to));
        }

        // Mehrdatei-Mods sollen nicht in einem Mischzustand enden, wenn eine mittlere Datei
        // scheitert (z. B. vom laufenden Spiel gesperrt): jede erfolgreiche Verschiebung wird
        // gemerkt, damit ein spaeterer Fehlschlag alle bereits bewegten Dateien wieder
        // zurueckschieben kann, bevor mod.Enabled ueberhaupt angefasst wird. f.Path wird dabei nie
        // veraendert (s. PhysicalPath) -- es bleibt der stabile Schluessel, unter dem state.SharedFiles
        // dieselbe Datei womoeglich noch fuehrt. Nur die reinen Pfade: seit f.Path nie mehr
        // umgeschrieben wird (C1), gibt es beim Zurueckrollen nichts an der Buchfuehrung
        // wiederherzustellen -- die Datei muss nur physisch zurueck.
        var moved = new List<(string From, string To)>();
        try
        {
            foreach (var (from, to) in plan)
            {
                if (!File.Exists(from)) continue; // fehlt -- nichts zu tun

                Directory.CreateDirectory(Path.GetDirectoryName(to)!);
                BackupBeforeOverwrite(to, mod.Id);
                File.Move(from, to, overwrite: true);
                moved.Add((from, to));
            }
        }
        // Bewusst unfiltert, aus demselben Grund wie der Rollback-Catch in Apply(): die Schleife
        // kann durch mehr als IOException/UnauthorizedAccessException unterbrochen werden (etwa
        // eine ArgumentException aus File.Move bei einem entarteten Pfad), und in JEDEM dieser
        // Faelle muessen die bereits verschobenen Dateien zurueck, sonst bleibt der Mod halb
        // umgeschaltet und mod.Enabled beschreibt die Platte nicht mehr (Fix Round 2, I4).
        catch (Exception e)
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

        PruneEmptyToggleDirs(game, moved.Select(m => Path.GetDirectoryName(m.From)!));
        mod.Enabled = enabled;
        AppLog.Info($"{mod.Id} {(enabled ? "enabled" : "disabled")}");
    }

    /// <summary>Deinstalliert. Configs werden nie geloescht, nur gesichert (Spec §6.6).</summary>
    public static void Remove(AppState state, GameInstall game, ModEntry mod)
    {
        // Verzeichnisse, aus denen wirklich etwas verschwunden ist -- am Ende werden die davon
        // aufgeraeumt, die leer zurueckbleiben (Fix Round 3, kosmetisch).
        var touchedDirs = new List<string>();

        foreach (var f in mod.Files)
        {
            // BepInEx\config\*.cfg wird NIE geloescht, nur verschoben (Spec §6.6) -- und zwar
            // unabhaengig davon, WIE der Eintrag in mod.Files geraten ist. Ein Archiv, das seine
            // eigene Default-Config mitliefert ("MyMod/BepInEx/config/MyMod.cfg"), bildet auf ein
            // voellig regulaeres Ziel ab und landete damit in mod.Files -- diese Schleife hat die
            // vom Nutzer eingestellte Config dann geloescht, und die Sicherung weiter unten fand
            // nichts mehr vor (Fix Round 2, kritisch). Solche Eintraege werden unten gesichert.
            if (IsProtectedConfig(game, f.Path)) continue;

            // Erst aufloesen, dann erst die Buchfuehrung anfassen: die Aufloesung wirft bei einem
            // Pfad, der den Spielordner verliesse (Pre-Flight-Review Fix Round 1, I4) -- geschaehe
            // das nach ReleaseShared, waere der Anbieter-Eintrag bereits ausgetragen, obwohl der
            // Abbruch verhindert, dass ueberhaupt je etwas geloescht wird.
            string[] candidates;
            try { candidates = PhysicalCandidates(game, mod, f); }
            catch (InvalidOperationException e)
            {
                // Protokollieren und ueberspringen statt werfen: ein solcher Eintrag scheitert bei
                // JEDEM Versuch an derselben Stelle. Wuerde Remove() daran abbrechen, blieben die
                // bereits geloeschten frueheren Dateien geloescht, waehrend der Mod fuer immer in
                // state.Mods stehen bliebe und sich nie deinstallieren liesse -- ein einziger
                // leerer "Path" in einer handbearbeiteten state.json genuegt dafuer
                // (Fix Round 2, Minor 4). Geloescht wird trotzdem nichts ausserhalb.
                AppLog.Error($"skipping file entry '{f.Path}' of {mod.Id}: it does not resolve inside the game folder", e);
                continue;
            }

            if (!ReleaseShared(state, f.Path, mod.Id)) continue;
            foreach (var candidate in candidates)
            {
                DeleteIfExists(candidate, mod.Id);
                touchedDirs.Add(Path.GetDirectoryName(candidate)!);
            }
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

            // Auch hier gilt der Config-Vorrang: eine geteilte Config wird verschoben, nie
            // geloescht. ReleaseShared hat oben bereits 'true' geliefert, dieser Mod war also der
            // letzte Anbieter -- niemand sonst verliert dadurch seine Einstellungen.
            if (IsProtectedConfig(game, path)) { BackupConfigFile(game, path, mod.Id); continue; }

            string[] candidates;
            try
            {
                // Auch eine geteilte Datei kann an zwei Stellen liegen: SetEnabled verschiebt sie
                // zwar nur, wenn kein anderer aktiver Mod sie mehr braucht -- aber dann eben doch.
                // Beide Orte pruefen, sonst bleibt die Datei nach dem Entfernen des letzten
                // Anbieters unverzeichnet liegen (Fix Round 3, wichtig). Die Enthaltenseins-
                // Pruefung bleibt Pflicht: 'path' stammt aus state.json (Fix Round 1, I4).
                var canonical = ResolveInside(game.Root, path);
                var mirror = DisabledMirror(game, path);
                candidates = mirror is null || mirror.Equals(canonical, StringComparison.OrdinalIgnoreCase)
                    ? [canonical]
                    : [canonical, mirror];
            }
            catch (InvalidOperationException e)
            {
                // Wie in der Schleife oben: protokollieren und weitermachen, statt die
                // Deinstallation an einem Eintrag scheitern zu lassen, der bei jedem Versuch
                // erneut scheitern wuerde (Fix Round 2, Minor 4). Der Anbieter bleibt ausgetragen
                // -- ein Datensatz, dessen Pfad nirgendwo hinzeigt, hilft niemandem, und geloescht
                // wird ausserhalb des Spielordners nach wie vor nichts.
                AppLog.Error($"skipping shared file '{path}' of {mod.Id}: it does not resolve inside the game folder", e);
                continue;
            }

            try
            {
                foreach (var candidate in candidates)
                {
                    DeleteIfExists(candidate, mod.Id);
                    touchedDirs.Add(Path.GetDirectoryName(candidate)!);
                }
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

        // Configs, die der Mod selbst mitgebracht hat: verschieben statt loeschen.
        foreach (var f in mod.Files.Where(f => IsProtectedConfig(game, f.Path)))
            BackupConfigIfUnshared(state, game, f.Path, mod.Id);

        // Und zusaetzlich die nach der Mod-Id benannte Config: die legt BepInEx erst zur Laufzeit
        // an, sie steht in keinem Archiv und taucht deshalb in keinem mod.Files-Eintrag auf.
        // Wurde sie oben schon verschoben, findet dieser Aufruf nichts mehr vor und tut nichts.
        BackupConfig(state, game, mod.Id);

        PruneEmptyToggleDirs(game, touchedDirs);
        state.Mods.Remove(mod);
        AppLog.Info($"removed {mod.Id}");
    }

    /// <summary>Sichert eine Config nur dann, wenn sie NICHT mehr in der Referenzzaehlung steht.
    /// Steht ihr Pfad noch in state.SharedFiles, war dieser Mod nicht der letzte Anbieter: sie
    /// wegzuverschieben naehme einem anderen, weiterhin installierten Mod seine Einstellungen --
    /// verloren waere nichts (verschoben ist nicht geloescht), aber der andere Mod stuende ohne
    /// seine Konfiguration da. Die Entscheidung gehoert damit an dieselbe Referenzzaehlung wie
    /// jede andere geteilte Datei (Fix Round 3, Minor 3).</summary>
    private static void BackupConfigIfUnshared(AppState state, GameInstall game, string relPath, string modId)
    {
        if (state.SharedFiles.Any(s => SameRelativePath(game, s.Path, relPath)))
        {
            AppLog.Info($"leaving config '{relPath}' in place while removing {modId}: another mod still provides it");
            return;
        }
        BackupConfigFile(game, relPath, modId);
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

    private static void BackupConfig(AppState state, GameInstall game, string modId)
    {
        // modId kommt ungefiltert aus [BepInPlugin] einer Fremd-DLL (ModInspector) und wird dort
        // nirgends validiert -- effektiv angreifer-kontrollierter Text. Ohne Saeuberung wuerde
        // z. B. modId == "..\\..\\evil" aus dem Config-Dateinamen einen Pfad machen, der aus dem
        // vorgesehenen Ordner hinauszeigt (Pre-Flight-Review Fix Round 1, I4). ModInspector selbst
        // bleibt unveraendert -- die GUID bleibt die Identitaet des Mods, sie darf nur nie
        // ungefiltert Teil eines Pfads werden.
        // Auch dieser Weg muss die Referenzzaehlung achten: die nach der Mod-Id benannte Config
        // kann genauso von mehreren Mods gefuehrt werden wie jede andere Datei.
        BackupConfigIfUnshared(state, game,
            Path.Combine("BepInEx", "config", SanitizeForFileName(modId) + ".cfg"), modId);
    }

    /// <summary>Verschiebt EINE Konfigurationsdatei in den Sicherungsordner. Nie loeschen, immer
    /// nur verschieben (Spec §6.6) -- deshalb ist ein fehlgeschlagenes Sichern auch kein
    /// Datenverlust, die Datei bleibt dann einfach liegen.</summary>
    private static void BackupConfigFile(GameInstall game, string relPath, string modId)
    {
        string cfg, dest;
        try
        {
            cfg = ResolveInside(game.Root, relPath);

            var safeId = SanitizeForFileName(modId);
            var stem = SanitizeForFileName(Path.GetFileNameWithoutExtension(relPath));
            var label = stem.Equals(safeId, StringComparison.OrdinalIgnoreCase) ? safeId : $"{safeId}-{stem}";

            // Sekundengenauer Zeitstempel PLUS Zufallsanteil: zwei Deinstallationen desselben Mods
            // innerhalb derselben Sekunde erzeugten sonst denselben Zielnamen, File.Move ohne
            // overwrite warf, und die zweite Sicherung wurde still uebersprungen -- die Config
            // blieb liegen, aber der Nutzer bekam sie nie in den Sicherungsordner
            // (Fix Round 2, Minor 3). Ueberschreiben ist hier bewusst KEINE Option: die aeltere
            // Sicherung ist genauso wertvoll wie die neue.
            dest = ResolveUnder(AppPaths.ConfigBackupDir,
                $"{label}-{DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid().ToString("N")[..8]}.cfg",
                "config backup path escapes the backup folder");
        }
        catch (InvalidOperationException e)
        {
            AppLog.Error($"config path '{relPath}' for mod '{modId}' would escape its folder, skipping backup", e);
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
