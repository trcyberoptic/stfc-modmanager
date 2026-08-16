using System.Diagnostics;
using System.IO.Compression;

namespace StfcModManager.Core;

/// <summary>
/// Erkennt und installiert die BepInEx-IL2CPP-Laufzeit. Der Build ist gepinnt —
/// beim Anheben muss die URL hier mitwandern (Spec §16).
///
/// SafeExtract entpackt ein von aussen bezogenes Archiv direkt in den Spielordner -- das ist eine
/// Sicherheitsgrenze, keine Bequemlichkeit (dieselbe Einstufung wie GitHubClient fuer Netzwerkzugriff
/// und PackageMapper fuer Modarchive). Die Pruefungen hier folgen bewusst denselben Grundsaetzen wie
/// PackageMapper (s. dortige, ausfuehrlich begruendete Kommentare), auch wo sie nicht woertlich
/// aufgerufen werden koennen: PackageMapper bildet Modarchive auf BepInEx\plugins\... um, SafeExtract
/// muss dagegen die Ordnerstruktur des Archivs UNVERAENDERT unter destRoot spiegeln.
/// </summary>
public static class BepInExRuntime
{
    public const string PinnedVersion = "6.0.0-be.755";

    public const string PinnedUrl =
        "https://builds.bepinex.dev/projects/bepinex_be/755/BepInEx-Unity.IL2CPP-win-x64-6.0.0-be.755%2B3fab71a.zip";

    private const long MinimumArchiveBytes = 20_000_000;   // echte Datei ~33 MB; faengt Fehlerseiten ab

    // Zip-Bomb-Schutz: Obergrenze fuer die UNKOMPRIMIERTE Gesamtgroesse. Grosszuegig genug fuer das
    // echte, gepinnte Archiv (~33 MB komprimiert, mit den generierten Interop-Assemblies NICHT
    // eingerechnet -- die entstehen erst beim ersten Spielstart, nicht beim Entpacken), eng genug, um
    // ein winziges Archiv mit einem riesigen deklarierten Inhalt zu stoppen, bevor ueberhaupt ein Byte
    // geschrieben wird. Wird zusaetzlich WAEHREND des Schreibens gegen die tatsaechlich kopierten Bytes
    // durchgesetzt (s. SafeExtract) -- ein Archiv, dessen Zip-Metadaten eine kleinere als die
    // tatsaechliche Groesse behaupten, waere sonst an der reinen Metadatenpruefung vorbeigekommen.
    internal const long MaxTotalUncompressedBytes = 600_000_000;

    // Dieselbe Ablehnungsliste wie PackageMapper.Blocked (dort privat, hier als zweite, unabhaengige
    // Verteidigungslinie dupliziert -- vgl. den Installer.ResolveUnder-Kommentar "Zweite
    // Verteidigungslinie hinter PackageMapper" fuer denselben, in diesem Codebestand bereits
    // etablierten Grundsatz). Das gepinnte BepInEx-Release enthaelt nichts davon; im Erfolgsfall
    // kostet die Pruefung nichts, faengt aber eine kompromittierte Downloadquelle ab, die sonst eine
    // .exe direkt in den Spielordner schreiben koennte.
    private static readonly string[] BlockedExtensions =
        { ".exe", ".bat", ".cmd", ".ps1", ".psm1", ".msi", ".scr", ".vbs", ".js", ".jar", ".com", ".lnk" };

    // Wie PackageMapper.IllegalWindowsChars: ohne diese Pruefung wuerde ein Eintrag mit einem dieser
    // Zeichen erst spaeter bei Directory.CreateDirectory/File.Create mit einer ungefangenen
    // ArgumentException auffallen, statt sauber als Ablehnung des ganzen Archivs zu enden.
    private static readonly char[] IllegalWindowsChars = { '<', '>', '"', '|', '?', '*' };

    private const int IdleTimeoutSeconds = 20;

    /// <summary>Versionszeichenkette der installierten Laufzeit, sonst null.</summary>
    public static string? Detect(GameInstall game)
    {
        if (!File.Exists(game.WinHttp) || !File.Exists(game.CoreDll)) return null;
        try
        {
            var v = FileVersionInfo.GetVersionInfo(game.CoreDll);
            var version = string.IsNullOrWhiteSpace(v.ProductVersion) ? v.FileVersion : v.ProductVersion;

            // Haertung: FileVersionInfo.GetVersionInfo wirft fuer eine VORHANDENE, aber beschaedigte
            // oder abgeschnittene Datei nicht (empirisch geprueft: eine leere Datei, zufaellige Bytes
            // und ein blosser MZ-Header liefern alle ein FileVersionInfo mit leeren Feldern statt einer
            // Exception -- die Datei muss dafuer nicht einmal eine gueltige PE sein). Ohne diese Zeile
            // haette Detect() in genau diesem Fall "" statt null zurueckgegeben: ein Aufrufer, der auf
            // "!= null" prueft (die einzig sinnvolle Lesart der Signatur "sonst null"), haette einen
            // durch eine abgebrochene Installation beschaedigten Kern faelschlich als "installiert"
            // gemeldet und nie das Reparatur-Angebot (erneute Installation, SafeExtract ueberschreibt)
            // gezeigt.
            return string.IsNullOrWhiteSpace(version) ? null : version;
        }
        catch (FileNotFoundException) { return null; }
    }

    public static async Task InstallAsync(GameInstall game, IProgress<string> progress, CancellationToken ct)
    {
        progress.Report("Downloading BepInEx runtime (about 33 MB)…");

        string tmp;
        try
        {
            tmp = await DownloadArchiveAsync(PinnedUrl, ct).ConfigureAwait(false);
        }
        catch (Exception e) when (e is HttpRequestException or IOException or UnauthorizedAccessException
                                        or OperationCanceledException or TimeoutException)
        {
            // Derselbe Filter wie GitHubClient.DownloadAssetAsync, aus demselben Grund: ein echter,
            // vom Aufrufer gewollter Abbruch (ct) muss unveraendert durchgereicht werden, alles andere
            // wird in eine englische, OS-freie Nutzermeldung uebersetzt -- die Rohdetails stehen im
            // Log.
            AppLog.Error("BepInEx runtime download failed", e);
            if (e is OperationCanceledException && ct.IsCancellationRequested) throw;
            throw new InvalidOperationException(
                "Could not download the BepInEx runtime. Check your internet connection and try again.", e);
        }

        try
        {
            progress.Report("Extracting runtime…");
            SafeExtract(tmp, game.Root);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Das Spiel kann waehrend des Entpackens laufen (eine gesperrte core-DLL) oder der
            // Datentraeger kann voll sein -- beides darf keinen rohen, lokalisierten OS-Text bis zur
            // UI durchreichen. Was bis zum Fehler bereits geschrieben wurde, bleibt liegen (keine
            // volle Transaktion -- s. Taskbericht); Detect() erkennt einen so unterbrochenen Zustand
            // zuverlaessig als "nicht installiert", solange winhttp.dll oder eine lesbare Kernversion
            // fehlen. InvalidOperationException aus SafeExtract selbst (abgelehntes Archiv) wird
            // bewusst NICHT hier gefangen: deren Text ist bereits eine saubere, englische, von uns
            // selbst verfasste Meldung ohne OS-Anteil und darf unveraendert durchgereicht werden.
            AppLog.Error($"BepInEx extraction into {game.Root} failed partway", e);
            throw new InvalidOperationException(
                "Extracting the BepInEx runtime failed partway through. Close the game if it is running, " +
                "then try installing again.", e);
        }
        finally
        {
            // Die heruntergeladene Zip-Datei ist ab hier in jedem Fall verbraucht (entpackt oder
            // gescheitert) und gehoert nicht dauerhaft in den Download-Ordner.
            try { File.Delete(tmp); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }

        AppLog.Info($"BepInEx {PinnedVersion} installed into {game.Root}");
        progress.Report("Runtime installed. The first game start will take several minutes "
                      + "while BepInEx generates its interop assemblies.");
    }

    /// <summary>Laedt <paramref name="url"/> in eine eindeutig benannte Nebendatei im Downloadordner
    /// und liefert deren Pfad erst nach bestandener Groessenpruefung zurueck -- dieselbe
    /// Erst-in-Nebendatei-dann-verifizieren-Form wie GitHubClient.DownloadAssetAsync (s. Kommentar
    /// dort). Der Rueckgabewert ist bewusst KEIN fester Name im Downloadordner: zwei gleichzeitige
    /// InstallAsync-Aufrufe (die Signatur nimmt ein CancellationToken und wird von der UI aus
    /// aufgerufen, ein Doppelklick auf "Installieren" ist also nicht ausgeschlossen) wuerden sich sonst
    /// gegenseitig die Nebendatei ueberschreiben. Oeffentlich testbar ueber die URL statt fest auf
    /// PinnedUrl verdrahtet -- InstallAsync ruft sie mit PinnedUrl auf, ein Test kann stattdessen einen
    /// lokalen Server angeben, ohne das gepinnte Release wirklich herunterzuladen.</summary>
    internal static async Task<string> DownloadArchiveAsync(string url, CancellationToken ct)
    {
        Directory.CreateDirectory(AppPaths.DownloadDir);
        SweepOrphanedTempFiles();

        var tmp = Path.Combine(AppPaths.DownloadDir, $"bepinex.{Environment.ProcessId}.{Guid.NewGuid():N}.tmp");

        // EIN Try/Catch um den gesamten Download inklusive Kopieren und Groessenpruefung, nicht nur um
        // die Pruefung am Ende: eine fruehere Fassung liess das Aufraeumen der Nebendatei aus, wenn
        // CopyWithIdleTimeoutAsync selbst warf (Leerlauf-Zeitlimit oder eine abgebrochene Verbindung) --
        // gemessen mit einem lokalen HttpListener, der nach ein paar Bytes verstummt: die Nebendatei
        // blieb bis zum naechsten Aufruf (der sie erst durch SweepOrphanedTempFiles oben mitraeumt) im
        // Downloadordner liegen. Jetzt deckt ein einziger Filter (wie GitHubClient.DownloadAssetAsync)
        // den kompletten Ablauf ab.
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
            long? expectedFromContentLength;
            using (var res = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false))
            {
                res.EnsureSuccessStatusCode();
                expectedFromContentLength = res.Content.Headers.ContentLength;

                var source = await res.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                var target = File.Create(tmp);
                await using (target.ConfigureAwait(false))
                {
                    // s. GitHubClient.DownloadAssetAsync: HttpClient.Timeout deckt einen mit
                    // ResponseHeadersRead gestreamten Body NICHT ab, nur den Empfang der Header. Ohne
                    // ein gleitendes Leerlauf-Zeitlimit wuerde ein Server, der nach ein paar Bytes
                    // verstummt, CopyToAsync -- und damit den synchron aufrufenden UI-Thread -- fuer
                    // immer haengen lassen. Inhaltlich identisch zu
                    // GitHubClient.CopyWithIdleTimeoutAsync, hier eigenstaendig nachgebildet statt
                    // aufgerufen: diese Aufgabe darf nur BepInExRuntime.cs und SelfTest.cs anfassen.
                    await CopyWithIdleTimeoutAsync(source, target, TimeSpan.FromSeconds(IdleTimeoutSeconds), ct)
                        .ConfigureAwait(false);
                }
            }

            var actualLength = new FileInfo(tmp).Length;

            if (expectedFromContentLength is { } expected && expected > 0 && actualLength != expected)
            {
                AppLog.Error($"BepInEx download from {url} is incomplete: {actualLength} of {expected} bytes");
                throw new InvalidOperationException(
                    $"The runtime download is incomplete ({actualLength} of {expected} bytes). " +
                    "Check your internet connection and try again.");
            }

            // Faengt zusaetzlich den Fall ab, dass der Server statt der Zip-Datei z. B. eine kleine
            // HTML-Fehlerseite mit HTTP 200 liefert: ohne Content-Length-Header haette die Pruefung
            // oben nichts zum Vergleichen.
            if (actualLength < MinimumArchiveBytes)
            {
                AppLog.Error($"BepInEx download from {url} is smaller than expected: {actualLength} bytes");
                throw new InvalidOperationException(
                    $"The runtime download is incomplete ({actualLength} bytes). Check your internet connection and try again.");
            }
        }
        catch
        {
            try { File.Delete(tmp); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            throw;
        }

        return tmp;
    }

    /// <summary>Kopiert mit einem gleitenden Leerlauf-Zeitlimit statt eines starren Gesamt-Caps (s.
    /// Aufrufer-Kommentar). Wirft TimeoutException, wenn innerhalb von idleTimeout kein einziges Byte
    /// ankommt; ein echter Abbruch ueber "ct" wird unveraendert durchgereicht.</summary>
    private static async Task CopyWithIdleTimeoutAsync(Stream source, Stream destination, TimeSpan idleTimeout, CancellationToken ct)
    {
        var buffer = new byte[81920];
        using var idleCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        while (true)
        {
            idleCts.CancelAfter(idleTimeout);
            int read;
            try
            {
                read = await source.ReadAsync(buffer, idleCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new TimeoutException($"No data received for {idleTimeout.TotalSeconds:F0} seconds.");
            }
            if (read == 0) break;
            await destination.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
        }
    }

    /// <summary>Raeumt Ueberbleibsel eines fruehreren, abgebrochenen Downloads auf (Absturz oder
    /// Stromausfall zwischen Schreiben und Verbrauch der Nebendatei) -- dieselbe Ueberlegung wie
    /// GitHubClient.SweepOrphanedTempFiles, hier ohne dessen Namens-Praezisionspruefung: das feste
    /// "bepinex."-Praefix ist bereits eng genug, es gibt in diesem Ordner keine gleich benannten
    /// Downloads eines anderen Features, die versehentlich mitgeloescht werden koennten.</summary>
    private static void SweepOrphanedTempFiles()
    {
        try
        {
            foreach (var f in Directory.EnumerateFiles(AppPaths.DownloadDir, "bepinex.*.tmp"))
                try { File.Delete(f); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>Entpackt und weigert sich, ausserhalb des Zielordners zu schreiben. Zwei Runden: erst
    /// werden ALLE Eintraege gegen Name, Groesse und Zielpfad geprueft (kein Byte geschrieben), dann
    /// erst wird geschrieben. Damit hinterlaesst ein abgelehntes Archiv NICHTS auf der Platte, auch
    /// wenn der ungueltige Eintrag nicht der erste ist -- anders als eine Pruefen-und-Schreiben-Schleife
    /// in einem Durchgang, die bereits verarbeitete, harmlos aussehende Eintraege stehen liesse, bevor
    /// sie auf den bösartigen stoesst.</summary>
    public static void SafeExtract(string zipPath, string destRoot) => SafeExtract(zipPath, destRoot, MaxTotalUncompressedBytes);

    /// <summary>Wie SafeExtract, aber mit einstellbarer Zip-Bomb-Obergrenze -- der Selbsttest braucht
    /// eine kleine Grenze, um die Ablehnung ohne eine tatsaechlich riesige Testdatei zu ueberpruefen.</summary>
    internal static void SafeExtract(string zipPath, string destRoot, long maxTotalUncompressedBytes)
    {
        Directory.CreateDirectory(destRoot);
        var root = Path.GetFullPath(destRoot);
        var rootPrefix = NormalizeRootPrefix(root);
        var rootNoSep = Path.TrimEndingDirectorySeparator(rootPrefix);

        // Derselbe Sonderfall wie Installer.RejectReparsedEscape: ist destRoot SELBST ein Reparse-Point
        // (ein per mklink auf eine andere Platte ausgelagerter Spielordner ist eine normale
        // Nutzerkonfiguration), loest jeder Pfad darin textuell auf eine Stelle ausserhalb auf. Ohne
        // die zusaetzliche Wurzel wuerde die Pruefung unten JEDE Installation in einen solchen
        // Spielordner ablehnen -- fail closed heisst, echte Ausbrueche zu stoppen, nicht legitime
        // Einrichtungen.
        var resolvedRoot = ResolveLinkOrNull(root) ?? root;
        var resolvedPrefix = NormalizeRootPrefix(resolvedRoot);
        var resolvedNoSep = Path.TrimEndingDirectorySeparator(resolvedPrefix);

        using var zip = ZipFile.OpenRead(zipPath);

        var plan = new List<(ZipArchiveEntry Entry, string Full)>();
        long declaredTotal = 0;
        foreach (var entry in zip.Entries)
        {
            if (entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\')) continue; // Verzeichniseintrag

            var normalized = NormalizeEntryName(entry.FullName); // wirft bei Ablehnung

            var ext = Path.GetExtension(normalized).ToLowerInvariant();
            if (BlockedExtensions.Contains(ext))
                throw new InvalidOperationException(
                    $"archive contains an executable, refusing the whole runtime archive: {entry.FullName}");

            // Zip-Bomb-Schutz, Stufe 1: aus den Metadaten, bevor ueberhaupt ein Byte geschrieben wird.
            declaredTotal += entry.Length;
            if (declaredTotal > maxTotalUncompressedBytes)
                throw new InvalidOperationException(
                    "archive's declared uncompressed size is implausibly large (possible zip bomb)");

            var full = Path.GetFullPath(Path.Combine(root, normalized));
            if (!full.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"archive entry escapes the destination: {entry.FullName}");

            // RejectReparsedEscape (unten, pro Ziel in Runde 2) prueft nur die ELTERN-Verzeichniskette --
            // ein Reparse-Point kann aber auch direkt AM Zielpfad selbst liegen: ein NTFS-Datei-Symlink
            // dort wuerde von File.Create/entry-Kopieren transparent verfolgt und schriebe durch den
            // Link hindurch, ohne dass die Verzeichnispruefung das je sieht (die geht nur den
            // Verzeichnispfad hoch, nicht die Datei selbst an). Ein legitimes Release trifft nie auf
            // einen bereits vorhandenen Symlink an genau seinem eigenen Zielpfad -- deshalb hier fail
            // closed statt den Symlink aufzuloesen und zu pruefen, wohin er zeigt.
            if (File.Exists(full) && File.GetAttributes(full).HasFlag(FileAttributes.ReparsePoint))
                throw new InvalidOperationException(
                    $"archive entry target is itself a pre-existing reparse point: {entry.FullName}");

            plan.Add((entry, full));
        }

        long writtenTotal = 0;
        foreach (var (entry, full) in plan)
        {
            var dir = Path.GetDirectoryName(full)!;
            Directory.CreateDirectory(dir);

            // Reparse-Point-Pruefung PRO ZIEL, nicht einmalig vorab: ein frueherer Eintrag in
            // DIESEM Lauf kann ein Verzeichnis neu angelegt haben, unter dem ein spaeterer Eintrag
            // liegt -- die Pruefung muss den jeweils aktuellen Zustand des Dateisystems sehen.
            RejectReparsedEscape(dir, rootPrefix, rootNoSep, resolvedPrefix, resolvedNoSep);

            using var entryStream = entry.Open();
            using var fileStream = File.Create(full);

            // Zip-Bomb-Schutz, Stufe 2: die TATSAECHLICH kopierten Bytes, nicht nur die deklarierte
            // Groesse aus Stufe 1 -- ein Archiv, dessen Zip-Metadaten eine kleinere als die wirklich
            // entpackte Menge behaupten, waere an Stufe 1 sonst vorbeigekommen.
            var buffer = new byte[81920];
            int read;
            while ((read = entryStream.Read(buffer, 0, buffer.Length)) > 0)
            {
                writtenTotal += read;
                if (writtenTotal > maxTotalUncompressedBytes)
                    throw new InvalidOperationException(
                        "archive expands far beyond its declared size while extracting (possible zip bomb)");
                fileStream.Write(buffer, 0, read);
            }
        }
    }

    /// <summary>Normalisiert und prueft einen Zip-Eintragsnamen nach denselben Grundsaetzen wie
    /// PackageMapper.MapEntries (s. dortige Kommentare fuer die einzelnen Begruendungen) -- hier neu
    /// geschrieben statt aufgerufen, weil PackageMapper einen Modarchiv-spezifischen Zielpfad bildet
    /// (Umschreiben auf BepInEx\plugins\...), SafeExtract dagegen die Ordnerstruktur des Archivs
    /// unveraendert unter destRoot spiegeln muss. Wirft InvalidOperationException bei Ablehnung.</summary>
    private static string NormalizeEntryName(string rawEntryName)
    {
        // Path.Combine(root, entryName) IGNORIERT root komplett, wenn entryName selbst gerootet ist
        // (empirisch geprueft: Combine("C:\Games\Root", "C:\evil.dll") liefert "C:\evil.dll", die
        // Praefixpruefung weiter unten in SafeExtract waere dagegen wirkungslos) -- ein Laufwerksbuch-
        // stabe, ein fuehrender Slash/Backslash oder ein UNC-Pfad muessen deshalb VOR jeder
        // Pfadverknuepfung abgefangen werden, nicht erst danach. ':' erlaubt zusaetzlich sowohl
        // Laufwerksangaben als auch NTFS-Alternate-Data-Streams ("readme.txt:hidden.dll" bleibt
        // textuell unter destRoot, legt aber einen versteckten Stream an) -- Path.IsPathRooted erkennt
        // das nicht in jeder Schreibweise, ein Doppelpunkt an beliebiger Position schliesst die Luecke.
        if (Path.IsPathRooted(rawEntryName) || rawEntryName.Contains(':'))
            throw new InvalidOperationException($"archive entry has an absolute, drive-qualified, or UNC-style path: {rawEntryName}");

        // "." steht fuer "aktuelles Verzeichnis" (Windows' eigenes tar.exe erzeugt es routinemaessig).
        // Segmente werden vollstaendig entfernt statt nur von der Traversal-Pruefung unten ausgenommen:
        // sonst umgeht ein eingebettetes "." die Zielpfad-Berechnung und ein Eintrag, der nur aus "."
        // besteht, erzeugt einen leeren Rest.
        var e = string.Join('/', rawEntryName.Replace('\\', '/').Trim().Split('/').Where(s => s != "."));
        if (e.Length == 0)
            throw new InvalidOperationException($"archive entry resolves to an empty name: {rawEntryName}");
        if (e.StartsWith('/'))
            throw new InvalidOperationException($"archive entry has an absolute or UNC-style path: {rawEntryName}");

        // ".." bleibt verboten. Reine Punkte-/Leerzeichen-Segmente wie "...", "...." oder ".. " sind
        // zusaetzlich verboten -- NICHT weil Windows sie beim tatsaechlichen Anlegen zu einer
        // Eltern-Referenz zusammenzieht (das tut es nachweislich nicht, s. PackageMapper-Kommentar),
        // sondern als reine Vorsichtsmassnahme gegen Formen, die kein legitimes Release traegt.
        if (e.Split('/').Any(s => s.Length > 0 && s != "." && s.TrimEnd('.', ' ').Length == 0))
            throw new InvalidOperationException($"archive entry contains a path traversal: {rawEntryName}");

        // Dieselbe Trailing-Trim-Normalisierung wie PackageMapper, jetzt fuer die Endungspruefung in
        // SafeExtract: "setup.exe." bzw. "setup.exe " wuerden ohne sie an der Endungsliste vorbeigehen,
        // obwohl Windows beim tatsaechlichen Anlegen wieder "setup.exe" daraus macht.
        var normalized = e.TrimEnd('.', ' ');
        if (normalized.Length == 0)
            throw new InvalidOperationException($"archive entry resolves to an empty name: {rawEntryName}");

        if (normalized.Any(c => c < 0x20 || IllegalWindowsChars.Contains(c)))
            throw new InvalidOperationException($"archive entry contains a character illegal on Windows: {rawEntryName}");

        return normalized;
    }

    private static string NormalizeRootPrefix(string root)
    {
        // Wie Installer.NormalizeRootPrefix: ein Trailing-Separator in der Wurzel (ein
        // Ordnerauswahl-Dialog liefert den bei einem Laufwerk als Spielordner, "C:\") wuerde sonst aus
        // dem Vergleichspraefix "C:\\" machen und JEDEN Zielpfad ablehnen.
        var full = Path.GetFullPath(root);
        var trimmed = Path.TrimEndingDirectorySeparator(full);
        return trimmed.EndsWith(Path.DirectorySeparatorChar) ? trimmed : trimmed + Path.DirectorySeparatorChar;
    }

    private static string? ResolveLinkOrNull(string dir)
    {
        try { return Directory.ResolveLinkTarget(dir, returnFinalTarget: true)?.FullName; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }

    private static bool IsRootOrUnder(string candidate, string prefix, string noSep)
        => candidate.Equals(noSep, StringComparison.OrdinalIgnoreCase)
           || candidate.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);

    /// <summary>Tiefenverteidigung gegen Reparse-Points (Junctions/Symlinks) innerhalb von destRoot --
    /// dieselbe Ueberlegung wie Installer.RejectReparsedEscape (s. dort fuer die ausfuehrliche
    /// Begruendung), hier eigenstaendig fuer SafeExtracts generische Zielwurzel nachgebildet (kein
    /// GameInstall-Kontext verfuegbar). Path.GetFullPath ist rein textuell: "BepInEx\plugins" koennte
    /// per "mklink /J" (keine Elevation noetig) auf ein Verzeichnis AUSSERHALB von destRoot zeigen,
    /// obwohl der textuelle Zielpfad harmlos aussieht. Geprueft wird jede Ebene vom Zielverzeichnis
    /// aufwaerts bis zur Wurzel, nicht nur das unmittelbare Elternverzeichnis.</summary>
    private static void RejectReparsedEscape(
        string dir, string rootPrefix, string rootNoSep, string resolvedPrefix, string resolvedNoSep)
    {
        var probe = Path.GetFullPath(dir);
        var remainder = "";

        while (true)
        {
            var link = ResolveLinkOrNull(probe);
            if (link is not null)
            {
                var landing = Path.GetFullPath(remainder.Length == 0 ? link : Path.Combine(link, remainder));
                if (IsRootOrUnder(landing, rootPrefix, rootNoSep) || IsRootOrUnder(landing, resolvedPrefix, resolvedNoSep))
                    return;
                throw new InvalidOperationException(
                    $"archive entry escapes the destination via a reparse point pointing to '{landing}'");
            }

            if (probe.Equals(rootNoSep, StringComparison.OrdinalIgnoreCase)) return;

            var parent = Path.GetDirectoryName(probe);
            if (parent is null) return; // Laufwerkswurzel ueberschritten (kann bei gueltigen Zielen nicht passieren)

            remainder = remainder.Length == 0 ? Path.GetFileName(probe) : Path.Combine(Path.GetFileName(probe), remainder);
            probe = parent;
        }
    }
}
