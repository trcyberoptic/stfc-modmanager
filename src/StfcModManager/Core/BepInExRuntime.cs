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
///
/// Fix Round 1 (Pre-Flight-Review): drei Important-Befunde (Hardlink an einem Blattziel, toter
/// Zip-Bomb-Zweig, Reparse-Pruefung zu spaet in der Schreibrunde) und eine von der Aufgabe nicht
/// genannte Sicherheitsluecke (Download vertraute Weiterleitungen ungeprueft) behoben -- s. die
/// jeweiligen Kommentare unten und den Taskbericht fuer die Messungen dahinter.
/// </summary>
public static class BepInExRuntime
{
    public const string PinnedVersion = "6.0.0-be.755";

    public const string PinnedUrl =
        "https://builds.bepinex.dev/projects/bepinex_be/755/BepInEx-Unity.IL2CPP-win-x64-6.0.0-be.755%2B3fab71a.zip";

    private const string RuntimeDownloadHost = "builds.bepinex.dev";

    private const long MinimumArchiveBytes = 20_000_000;   // echte Datei ~33 MB; faengt Fehlerseiten ab

    // Zip-Bomb-Schutz, drei unabhaengige Obergrenzen, alle aus reinen Zip-Metadaten geprueft, bevor
    // ueberhaupt ein Byte geschrieben wird. Werte gegen das ECHTE, gepinnte Archiv gemessen (per
    // HTTP-Range nur das Ende der Datei gelesen, NICHT die vollen 33 MB -- s. Taskbericht): 233
    // Eintraege (228 Dateien + 5 reine Verzeichniseintraege), groesster einzelner deklarierter Inhalt
    // 10.631.320 Bytes (dotnet/System.Private.CoreLib.dll), deklarierte Gesamtgroesse 75.631.273
    // Bytes -- alle drei Grenzen unten liegen mit rund einer Groessenordnung Abstand darueber.
    //
    // Fix Round 1, I2: eine fruehere Fassung hatte zusaetzlich eine "Stufe 2", die die tatsaechlich
    // beim Kopieren gelesenen Bytes gegen dieselbe Grenze zaehlte. Das war toter Code: gemessen mit
    // einem Eintrag, der 10 Bytes deklariert und 4.000.000 tatsaechlich enthaelt, liefert
    // ZipArchiveEntry.Open() beim Lesen exakt 10 Bytes -- der Stream wird von .NET selbst auf die
    // DEKLARIERTE Laenge abgeschnitten. Die "tatsaechlich gelesenen Bytes" koennen die deklarierte
    // Groesse also nie uebersteigen, der Wurf in Stufe 2 konnte nie feuern, und kein Assert deckte
    // das je auf (der alte Zip-Bomb-Test pruefte nur Stufe 1). Ersetzt durch zwei Metadaten-Grenzen,
    // die tatsaechlich feuern koennen: eine pro Eintrag (faengt eine einzelne riesige Datei) und eine
    // Gesamtsumme (faengt viele mittelgrosse). Dazu eine dritte, unabhaengige Grenze fuer die reine
    // ANZAHL der Eintraege: 20.000 winzige Eintraege wurden ohne jede Grenze anstandslos akzeptiert
    // und brauchten 8,8 Sekunden -- auf dem synchron vom UI-Thread aufgerufenen Installationspfad
    // waere das ein spuerbares Einfrieren der Anwendung, unabhaengig von der Gesamtgroesse.
    internal const long MaxTotalUncompressedBytes = 600_000_000;
    internal const long MaxSingleEntryBytes = 100_000_000;
    internal const int MaxEntryCount = 2000;

    // NTFS' harte Grenze pro Pfadsegment (255 UTF-16-Zeichen) -- NICHT dieselbe wie die alte
    // MAX_PATH-Gesamtlaengengrenze (die dieses Projekt bereits ueberschreitet, klaglos: ein
    // Gesamtpfad von ueber 5000 Zeichen wurde gemessen anstandslos akzeptiert). Die Segmentgrenze
    // dagegen ist auch mit Long-Path-Unterstuetzung hart und wirft erst beim tatsaechlichen
    // Schreiben eine LOKALISIERTE IOException ("Die Syntax fuer den Dateinamen... ist falsch",
    // empirisch auf einem deutschen Windows beobachtet) -- NICHT schon bei Path.GetFullPath. Explizit
    // vorab geprueft, damit (a) die Meldung englisch bleibt statt vom Betriebssystem lokalisiert zu
    // werden, und (b) die Ablehnung in der Validierungsrunde faellt, nicht erst mitten im Schreiben
    // (das Schreiben-nichts-Versprechen wuerde sonst genau hier brechen, Fix Round 1, kleiner Befund).
    private const int MaxPathSegmentLength = 255;

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

    // Fix Round 1, kleiner Befund: DOS-reservierte Geraetenamen (CON, NUL, COM1, ...). Windows bildet
    // einen Pfad, dessen Segment (ohne Endung) genau so heisst, auf ein GERAET ab ("CON.dll" ->
    // "\\.\CON", empirisch geprueft), nicht auf eine Datei im Zielordner -- die
    // Enthaltenseins-Pruefung in SafeExtract faengt das zwar schon "zufaellig" ab (das Geraet liegt
    // nie unter root), meldet dann aber "escapes the destination" fuer etwas, das gar kein
    // Traversal ist. Explizit geprueft, damit die Ablehnung den richtigen Grund traegt.
    private static readonly HashSet<string> ReservedDeviceNames = BuildReservedDeviceNames();

    private static HashSet<string> BuildReservedDeviceNames()
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "CON", "PRN", "AUX", "NUL" };
        for (var i = 1; i <= 9; i++) { names.Add($"COM{i}"); names.Add($"LPT{i}"); }
        return names;
    }

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

            // Fix Round 1, kleiner Befund: das Archiv liefert "BepInEx\plugins" und "BepInEx\patchers"
            // nur als LEERE Verzeichnis-Eintraege (fuer Nutzerinhalte gedacht, BepInEx selbst legt
            // nichts hinein) -- SafeExtract ueberspringt reine Verzeichniseintraege grundsaetzlich
            // (s. dort), sie wuerden also sonst nie entstehen. Guenstige Absicherung direkt nach
            // erfolgreicher Extraktion.
            EnsureRuntimeSkeleton(game);
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

    /// <summary>Legt die beiden vom Archiv nur als leere Verzeichnis-Eintraege mitgelieferten Ordner
    /// explizit an (s. Aufrufer-Kommentar). Eigenstaendig aufrufbar/testbar, rein und ohne Netzwerk --
    /// InstallAsync ruft sie nach einer erfolgreichen SafeExtract auf.</summary>
    internal static void EnsureRuntimeSkeleton(GameInstall game)
    {
        Directory.CreateDirectory(game.Plugins);
        Directory.CreateDirectory(Path.Combine(game.Root, "BepInEx", "patchers"));
    }

    /// <summary>Laedt <paramref name="url"/> in eine eindeutig benannte Nebendatei im Downloadordner
    /// und liefert deren Pfad erst nach bestandener Groessenpruefung zurueck -- dieselbe
    /// Erst-in-Nebendatei-dann-verifizieren-Form wie GitHubClient.DownloadAssetAsync (s. Kommentar
    /// dort). Der Rueckgabewert ist bewusst KEIN fester Name im Downloadordner: zwei gleichzeitige
    /// InstallAsync-Aufrufe (die Signatur nimmt ein CancellationToken und wird von der UI aus
    /// aufgerufen, ein Doppelklick auf "Installieren" ist also nicht ausgeschlossen) wuerden sich sonst
    /// gegenseitig die Nebendatei ueberschreiben. Oeffentlich testbar ueber die URL statt fest auf
    /// PinnedUrl verdrahtet -- InstallAsync ruft sie mit PinnedUrl auf, ein Test kann stattdessen einen
    /// lokalen Server angeben, ohne das gepinnte Release wirklich herunterzuladen.
    ///
    /// Fix Round 1, Sicherheitsluecke (von der Aufgabe nicht genannt): frueher ein Standard-HttpClient
    /// mit automatischen Weiterleitungen UND ohne Host-Pruefung -- GitHubClient.DownloadAssetAsync
    /// begruendet ausfuehrlich, warum das gefaehrlich ist (der eingebaute Redirect-Mechanismus prueft
    /// ein Umleitungsziel nie erneut gegen eine Allowlist), und dieser Download landet direkt im
    /// LEBENDEN Spielordner -- das hoechstwertige Ziel der Anwendung. Anders als GitHubs
    /// Asset-Downloads (die planmaessig auf einen CDN-Host umleiten und deshalb manuelles,
    /// pro-Hop-geprueftes Mitverfolgen brauchen) leitet der echte, gepinnte builds.bepinex.dev NICHT
    /// um (gemessen). Automatische Weiterleitungen sind deshalb einfach aus, und jede
    /// Weiterleitungsantwort wird als Ablehnung behandelt statt verfolgt -- das schliesst die Luecke
    /// (ein kompromittierter oder falsch konfigurierter Server koennte sonst per Redirect ein
    /// beliebiges Archiv unterschieben) ohne GitHubClients zusaetzliche Pro-Hop-Komplexitaet fuer
    /// einen Host nachzubauen, der planmaessig nie umleitet.</summary>
    internal static async Task<string> DownloadArchiveAsync(string url, CancellationToken ct)
    {
        Uri uri;
        try
        {
            uri = new Uri(url);
        }
        catch (UriFormatException e)
        {
            AppLog.Error($"BepInEx runtime download URL is unparseable: {url}", e);
            throw new InvalidOperationException("The runtime download URL is invalid.", e);
        }

        if (!IsAllowedRuntimeHost(uri))
        {
            AppLog.Error($"refused to download the BepInEx runtime: untrusted host {uri.Host}");
            throw new InvalidOperationException("Refusing to download the runtime from an untrusted host.");
        }

        Directory.CreateDirectory(AppPaths.DownloadDir);
        SweepOrphanedTempFiles();

        var tmp = Path.Combine(AppPaths.DownloadDir, $"bepinex.{Environment.ProcessId}.{Guid.NewGuid():N}.tmp");

        // EIN Try/Catch um den gesamten Download inklusive Kopieren und Groessenpruefung, nicht nur um
        // die Pruefung am Ende: eine fruehere Fassung liess das Aufraeumen der Nebendatei aus, wenn
        // CopyWithIdleTimeoutAsync selbst warf (Leerlauf-Zeitlimit oder eine abgebrochene Verbindung) --
        // gemessen mit einem lokalen HttpListener, der nach ein paar Bytes verstummt: die Nebendatei
        // blieb bis zum naechsten Aufruf (der sie erst durch SweepOrphanedTempFiles oben mitraeumt) im
        // Downloadordner liegen. Bewusst UNGEFILTERT (nicht "when (e is ...)"): dieser Block muss die
        // Nebendatei fuer JEDE Ausnahme aufraeumen, die unten auftreten kann -- Netzwerkfehler
        // (HttpRequestException/IOException), das Leerlauf-Zeitlimit (TimeoutException), ein echter
        // Abbruch (OperationCanceledException) UND die eigenen InvalidOperationException-Wuerfe weiter
        // unten (Weiterleitung abgelehnt, Groessenpruefung). Ein gefilterter Catch wuerde genau die
        // eigenen Wuerfe -- in der Praxis die haeufigsten Ablehnungsgruende -- NICHT abdecken und die
        // Nebendatei liegen lassen (dieselbe Ueberlegung wie beim bewusst ungefilterten Catch in
        // Installer.Apply).
        try
        {
            using var handler = new HttpClientHandler { AllowAutoRedirect = false };
            using var http = new HttpClient(handler) { Timeout = TimeSpan.FromMinutes(5) };
            long? expectedFromContentLength;
            using (var res = await http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false))
            {
                if ((int)res.StatusCode is >= 300 and < 400)
                {
                    AppLog.Error($"BepInEx runtime download from {url} was redirected to '{res.Headers.Location}', refusing to follow");
                    throw new InvalidOperationException(
                        "The runtime download tried to redirect to another location, which was refused. " +
                        "This may indicate a compromised or misconfigured download source.");
                }
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

    /// <summary>Erlaubt nur https und exakt den gepinnten Host -- kein Wildcard-Suffix wie bei
    /// GitHubClient.IsAllowedDownloadHost (das dort *.githubusercontent.com fuer den bekannten
    /// CDN-Host von GitHub-Assets braucht): fuer bepinex.dev ist keine CDN-Subdomain-Familie bekannt,
    /// auf die eine Weiterleitung legitim zeigen koennte, und der echte Endpunkt leitet ohnehin nicht
    /// um (s. Aufrufer-Kommentar). Rein, ohne Netzwerkzugriff testbar.</summary>
    internal static bool IsAllowedRuntimeHost(Uri uri) =>
        uri.Scheme == Uri.UriSchemeHttps && uri.IsDefaultPort
        && uri.IdnHost.Equals(RuntimeDownloadHost, StringComparison.OrdinalIgnoreCase);

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

    /// <summary>Protokolliert eine Ablehnung und liefert die dazu passende Ausnahme -- der Aufrufer
    /// schreibt "throw Reject(...)". Fix Round 1, kleiner Befund: SafeExtract-Ablehnungen wurden
    /// bisher NICHT geloggt (InstallAsync faengt InvalidOperationException aus SafeExtract bewusst
    /// nicht ab, s. dort -- die Meldung erreicht die UI unveraendert, landete aber nirgends im Log).
    /// Ein Rueckgabewert statt "void throw" (dasselbe Muster wie GitHubClient.UnexpectedShape) hat
    /// zwei Gruende: der Compiler erkennt "throw Reject(...)" als garantiertes Verlassen des
    /// Kontrollflusses, waehrend ein reiner "void"-Aufruf das nicht koennte (Definite-Assignment-
    /// Warnungen an mehreren Aufrufstellen); und jede Ablehnung wird an GENAU einer Stelle geloggt,
    /// nicht an zwoelf einzelnen throw-Zeilen dupliziert.</summary>
    private static InvalidOperationException Reject(string reason)
    {
        AppLog.Error($"BepInEx runtime archive rejected: {reason}");
        return new InvalidOperationException(reason);
    }

    /// <summary>Entpackt und weigert sich, ausserhalb des Zielordners zu schreiben. Zwei Runden: erst
    /// werden ALLE Eintraege gegen Name, Groesse, Zielpfad, Reparse-Points und Zielkollisionen geprueft
    /// (kein Byte geschrieben), dann erst wird geschrieben. Damit hinterlaesst ein abgelehntes Archiv
    /// NICHTS auf der Platte, auch wenn der ungueltige Eintrag nicht der erste ist -- anders als eine
    /// Pruefen-und-Schreiben-Schleife in einem Durchgang, die bereits verarbeitete, harmlos aussehende
    /// Eintraege stehen liesse, bevor sie auf den boesartigen stoesst.</summary>
    public static void SafeExtract(string zipPath, string destRoot) =>
        SafeExtract(zipPath, destRoot, MaxTotalUncompressedBytes, MaxSingleEntryBytes, MaxEntryCount);

    /// <summary>Wie SafeExtract, aber mit einstellbaren Zip-Bomb-Obergrenzen -- der Selbsttest braucht
    /// kleine Grenzen, um jede Ablehnung ohne eine tatsaechlich riesige oder eine 20.000-Eintraege-
    /// Testdatei zu ueberpruefen.</summary>
    internal static void SafeExtract(
        string zipPath, string destRoot, long maxTotalUncompressedBytes, long maxSingleEntryBytes, int maxEntryCount)
    {
        // Fix Round 1, kleiner Befund: ein mistgetippter Spielpfad liess SafeExtract den Ordner
        // klanglos anlegen und dort "installieren" -- der Nutzer saehe scheinbaren Erfolg in einem
        // Ordner, der gar nicht das Spiel ist. Die Wurzel muss vorher existieren; wer sie anlegen
        // will, tut das explizit (z. B. beim Anlegen des GameInstall aus einem echten Spielfund).
        if (!Directory.Exists(destRoot))
            throw Reject($"the destination folder does not exist: {destRoot}");

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
        var targets = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var neededDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long declaredTotal = 0;
        var entryCount = 0;

        foreach (var entry in zip.Entries)
        {
            if (entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\')) continue; // Verzeichniseintrag

            // Fix Round 1, I2 (Anzahl-Grenze): 20.000 winzige Eintraege wurden ohne diese Pruefung
            // anstandslos akzeptiert und brauchten 8,8 s auf dem synchron aufgerufenen UI-Pfad.
            entryCount++;
            if (entryCount > maxEntryCount)
                throw Reject($"archive contains too many entries (over {maxEntryCount}), refusing (possible zip bomb)");

            var normalized = NormalizeEntryName(entry.FullName); // wirft bei Ablehnung

            var ext = Path.GetExtension(normalized).ToLowerInvariant();
            if (BlockedExtensions.Contains(ext))
                throw Reject($"archive contains an executable, refusing the whole runtime archive: {entry.FullName}");

            // Zip-Bomb-Schutz: pro-Eintrag- und Gesamt-Obergrenze, beide rein aus der deklarierten
            // Groesse in den Zip-Metadaten (s. Klassenkommentar fuer die Messung, die diese Werte
            // belegt, und fuer die Begruendung, warum eine dritte, "waehrend des Kopierens gepruefte"
            // Stufe hier NICHT mehr steht -- sie war toter Code).
            if (entry.Length > maxSingleEntryBytes)
                throw Reject($"archive entry's declared size is implausibly large (possible zip bomb): {entry.FullName}");
            declaredTotal += entry.Length;
            if (declaredTotal > maxTotalUncompressedBytes)
                throw Reject("archive's declared uncompressed size is implausibly large (possible zip bomb)");

            string full;
            try
            {
                full = Path.GetFullPath(Path.Combine(root, normalized));
            }
            catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException)
            {
                // Ruecksicherung hinter der expliziten Segmentlaengenpruefung in NormalizeEntryName --
                // dieselbe Ueberlegung wie Installer.ResolveUnder fuer jeden anderen Pfad, den
                // Path.GetFullPath nicht verarbeiten kann.
                throw Reject($"archive entry has a path Windows cannot resolve: {entry.FullName}");
            }

            if (!full.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))
                throw Reject($"archive entry escapes the destination: {entry.FullName}");

            // Fix Round 1, I3: lief bisher erst in der Schreibrunde, PRO bereits angelegtem
            // Verzeichnis -- mit einer Junction auf "dest\BepInEx" und einem Eintrag
            // "BepInEx/plugins/evil.dll" legte Directory.CreateDirectory "outside\plugins" an, BEVOR
            // die Ablehnung ueberhaupt griff, und ein vorangehender harmloser Eintrag war zu diesem
            // Zeitpunkt schon geschrieben. Jetzt hier in der Validierungsrunde, rein textuell gegen
            // den bereits VORHANDENEN Teil der Verzeichniskette (RejectReparsedEscape toleriert
            // nicht-existierende Zwischenebenen bereits, s. dort) -- es wird noch nichts angelegt und
            // noch nichts geschrieben.
            RejectReparsedEscape(Path.GetDirectoryName(full)!, rootPrefix, rootNoSep, resolvedPrefix, resolvedNoSep);

            // Fix Round 1, kleiner Befund: Ziel-Kollisionen, die PackageMapper fuer Modarchive schon
            // kennt, fehlten hier. Zwei Eintraege auf denselben Zielpfad (auch nur in Gross-/
            // Kleinschreibung unterschiedlich) wuerden sich beim Schreiben gegenseitig ueberschreiben;
            // ein Eintrag, dessen Zielpfad ein ANDERER Eintrag als Verzeichnis braucht (z. B. "BepInEx"
            // als Datei UND "BepInEx/core/x.dll" als Datei darunter), kann nicht beides gleichzeitig
            // sein. Erst nach dem vollstaendigen Durchlauf (unten) entscheidbar, weil eine Kollision in
            // beide Richtungen auftreten kann, unabhaengig von der Reihenfolge im Archiv -- deshalb
            // hier nur sammeln, nicht sofort werfen.
            if (!targets.TryAdd(full, entry.FullName))
                throw Reject($"archive maps multiple entries to the same target: '{entry.FullName}' and '{targets[full]}'");

            var dirWalk = Path.GetDirectoryName(full);
            while (dirWalk is not null && !dirWalk.Equals(rootNoSep, StringComparison.OrdinalIgnoreCase) && neededDirs.Add(dirWalk))
                dirWalk = Path.GetDirectoryName(dirWalk);

            plan.Add((entry, full));
        }

        foreach (var full in targets.Keys)
            if (neededDirs.Contains(full))
                throw Reject($"archive entry conflicts with another entry that requires the same path to be a folder: '{full}'");

        foreach (var (entry, full) in plan)
        {
            var dir = Path.GetDirectoryName(full)!;
            Directory.CreateDirectory(dir);

            // Fix Round 1, I1: eine vorhandene Datei an full wurde bisher per File.Create (bzw.
            // ExtractToFile(overwrite: true)) UEBERSCHRIEBEN, nicht ersetzt -- fuer einen normalen
            // Reinstall harmlos, aber fuer einen Hardlink (mklink /H, KEINE Elevation noetig, anders
            // als ein Datei-Symlink) genau der Weg hinaus: File.GetAttributes zeigt fuer einen
            // Hardlink ganz normal "Archive" ohne Reparse-Flag, die fruehere Reparse-Point-Pruefung an
            // dieser Stelle war dagegen blind, und ein Schreiben "durch" den Link hinein aenderte
            // nachweislich eine Datei AUSSERHALB von destRoot. File.Delete entfernt bei einem Hardlink
            // oder Datei-Symlink dagegen nur den Verzeichniseintrag selbst, nie das gemeinsame Ziel
            // dahinter (auch fuer einen HAENGENDEN Symlink, dessen Ziel gar nicht mehr existiert --
            // File.Exists war dafuer blind, File.Delete ist es nicht: ein No-Op, wenn full nicht
            // existiert, sonst ein sauberes Entfernen genau des Links). overwrite:false danach ist
            // Absicht, nicht Nachlaessigkeit: existiert an full trotzdem noch etwas (ein enges
            // Zeitfenster fuer eine gleichzeitige externe Aenderung), soll ExtractToFile LAUT
            // scheitern statt still durchzuschreiben.
            File.Delete(full);
            entry.ExtractToFile(full, overwrite: false);
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
            throw Reject($"archive entry has an absolute, drive-qualified, or UNC-style path: {rawEntryName}");

        // "." steht fuer "aktuelles Verzeichnis" (Windows' eigenes tar.exe erzeugt es routinemaessig).
        // Segmente werden vollstaendig entfernt statt nur von der Traversal-Pruefung unten ausgenommen:
        // sonst umgeht ein eingebettetes "." die Zielpfad-Berechnung und ein Eintrag, der nur aus "."
        // besteht, erzeugt einen leeren Rest.
        var e = string.Join('/', rawEntryName.Replace('\\', '/').Trim().Split('/').Where(s => s != "."));
        if (e.Length == 0)
            throw Reject($"archive entry resolves to an empty name: {rawEntryName}");
        if (e.StartsWith('/'))
            throw Reject($"archive entry has an absolute or UNC-style path: {rawEntryName}");

        var segments = e.Split('/');

        // ".." bleibt verboten. Reine Punkte-/Leerzeichen-Segmente wie "...", "...." oder ".. " sind
        // zusaetzlich verboten -- NICHT weil Windows sie beim tatsaechlichen Anlegen zu einer
        // Eltern-Referenz zusammenzieht (das tut es nachweislich nicht, s. PackageMapper-Kommentar),
        // sondern als reine Vorsichtsmassnahme gegen Formen, die kein legitimes Release traegt.
        if (segments.Any(s => s.Length > 0 && s != "." && s.TrimEnd('.', ' ').Length == 0))
            throw Reject($"archive entry contains a path traversal: {rawEntryName}");

        // s. Klassenkommentar bei MaxPathSegmentLength: eine harte NTFS-Grenze, die erst beim
        // tatsaechlichen Schreiben als lokalisierte IOException auffiele, hier vorab und englisch.
        if (segments.Any(s => s.Length > MaxPathSegmentLength))
            throw Reject($"archive entry has a path segment longer than {MaxPathSegmentLength} characters: {rawEntryName}");

        // DOS-reservierte Geraetenamen (s. Klassenkommentar bei ReservedDeviceNames). Der massgebliche
        // Teil ist das Segment VOR dem ersten Punkt, egal wie viele Punkte danach folgen ("CON.dll",
        // "CON.tar.gz" sind beide reserviert) -- exakt die Regel, die Windows selbst anwendet.
        if (segments.Any(s => ReservedDeviceNames.Contains(s.Split('.')[0])))
            throw Reject($"archive entry uses a reserved DOS device name: {rawEntryName}");

        // Dieselbe Trailing-Trim-Normalisierung wie PackageMapper, jetzt fuer die Endungspruefung in
        // SafeExtract: "setup.exe." bzw. "setup.exe " wuerden ohne sie an der Endungsliste vorbeigehen,
        // obwohl Windows beim tatsaechlichen Anlegen wieder "setup.exe" daraus macht.
        var normalized = e.TrimEnd('.', ' ');
        if (normalized.Length == 0)
            throw Reject($"archive entry resolves to an empty name: {rawEntryName}");

        if (normalized.Any(c => c < 0x20 || IllegalWindowsChars.Contains(c)))
            throw Reject($"archive entry contains a character illegal on Windows: {rawEntryName}");

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
    /// aufwaerts bis zur Wurzel, nicht nur das unmittelbare Elternverzeichnis. Toleriert Ebenen, die
    /// noch gar nicht existieren (ResolveLinkOrNull liefert dafuer null, die Schleife laeuft einfach
    /// weiter aufwaerts) -- das ist Absicht: seit Fix Round 1 (I3) laeuft diese Pruefung in der
    /// Validierungsrunde, BEVOR irgendein Verzeichnis angelegt wurde.</summary>
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
                throw Reject($"archive entry escapes the destination via a reparse point pointing to '{landing}'");
            }

            if (probe.Equals(rootNoSep, StringComparison.OrdinalIgnoreCase)) return;

            var parent = Path.GetDirectoryName(probe);
            if (parent is null) return; // Laufwerkswurzel ueberschritten (kann bei gueltigen Zielen nicht passieren)

            remainder = remainder.Length == 0 ? Path.GetFileName(probe) : Path.Combine(Path.GetFileName(probe), remainder);
            probe = parent;
        }
    }
}
