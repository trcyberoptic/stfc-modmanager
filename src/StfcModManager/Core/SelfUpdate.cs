using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;

namespace StfcModManager.Core;

/// <summary>
/// Ersetzt die eigene EXE, waehrend sie laeuft. Kein Hilfsprozess noetig.
///
/// Fix Round 1: die urspruengliche Loesung war ein Umbenennen+Verschieben in zwei Schritten
/// (EXE -> .old, dann Nebendatei -> EXE) -- funktionierte, hatte aber ein winziges, unvermeidbares
/// Zeitfenster dazwischen, in dem unter dem EXE-Namen ueberhaupt keine Datei liegt: ein Prozesstod
/// genau dort haette die Installation unstartbar zurueckgelassen, ohne dass irgendein Code danach
/// das haette reparieren koennen. Ersetzt durch EINEN Aufruf von Win32 ReplaceFile: das ist genau
/// die dafuer vorgesehene API (in-use-Datei atomar ersetzen, mit Sicherung des alten Inhalts) --
/// kein beobachtbarer Zwischenzustand, kein Hilfsprozess. Handverifiziert an einer echten laufenden
/// Single-File-EXE (s. Taskbericht Fix Round 1): der Prozess blieb reaktionsfaehig, ExePath trug
/// hinterher den neuen Inhalt, OldPath den alten.
///
/// Die vom Host beim ersten Start extrahierten nativen Bibliotheken sind hier ohnehin kein Thema:
/// fuer diesen Build (net10.0-windows, PublishSingleFile + IncludeNativeLibrariesForSelfExtract=true)
/// mit DOTNET_BUNDLE_EXTRACT_BASE_DIR auf ein leeres Verzeichnis gemessen, dass zur Laufzeit KEINE
/// Datei dorthin extrahiert wird -- der moderne Windows-Host laedt seine eigenen nativen
/// Hosting-Komponenten (coreclr.dll usw.) direkt speicher-gemappt aus der einen gebuendelten EXE.
/// Die laufende EXE ist damit die EINZIGE beteiligte gesperrte Datei.
/// </summary>
public static partial class SelfUpdate
{
    public const string RepoOwner = "trcyberoptic";
    public const string RepoName = "stfc-modmanager";

    // ReplaceFileW: ersetzt lpReplacedFileName durch lpReplacementFileName in einem Kernel-Aufruf
    // und sichert dessen bisherigen Inhalt (falls angegeben) unter lpBackupFileName -- selbst wenn
    // lpReplacedFileName gerade als laufendes Prozess-Image geoeffnet ist (handverifiziert, s.
    // Klassenkommentar). lpExclude/lpReserved sind reserviert und muessen 0 sein.
    [LibraryImport("kernel32.dll", EntryPoint = "ReplaceFileW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ReplaceFile(
        string lpReplacedFileName,
        string lpReplacementFileName,
        string lpBackupFileName,
        uint dwReplaceFlags,
        nint lpExclude,
        nint lpReserved);

    private static string ExePath => Environment.ProcessPath
        ?? throw new InvalidOperationException("cannot determine own executable path");

    private static string OldPath => ExePath + ".old";

    public static Version CurrentVersion =>
        Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0, 0);

    /// <summary>Beim Start aufrufen: raeumt die umbenannte Vorgaengerversion weg.</summary>
    public static void CleanupOldExecutable()
    {
        try { if (File.Exists(OldPath)) File.Delete(OldPath); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Noch gesperrt (z. B. ein Virenscanner, der die frisch umbenannte Datei kurz haelt)
            // oder schreibgeschuetzt -- der naechste Start versucht es erneut. Kein Nutzerhinweis
            // noetig: eine liegen gebliebene .old-Datei ist reine Kosmetik, kein Datenverlust.
        }
    }

    /// <summary>Reine Entscheidung "ist dieses Release-Tag ein anwendbares Update", getrennt von
    /// CheckAsync, damit sie ohne Netzwerk testbar ist (dasselbe Muster wie
    /// GitHubClient.IsAllowedDownloadHost / ResolveAllowedRedirect). Liefert null sowohl fuer ein
    /// Tag ohne gueltige Versionsform als auch fuer eine gueltige, aber nicht neuere Version --
    /// CheckAsync braucht diesen Unterschied nicht, beides heisst "kein Update anbieten".</summary>
    internal static Version? ApplicableUpdateVersion(string tag, Version current)
    {
        var trimmed = tag.TrimStart('v', 'V');
        if (!Version.TryParse(trimmed, out var latest)) return null;
        return latest > current ? latest : null;
    }

    public static async Task<ReleaseInfo?> CheckAsync(string? token, CancellationToken ct)
    {
        var release = await GitHubClient.GetLatestReleaseAsync(RepoOwner, RepoName, null, token, ct)
            .ConfigureAwait(false);
        if (release is null) return null;

        return ApplicableUpdateVersion(release.Tag, CurrentVersion) is not null ? release : null;
    }

    public static async Task ApplyAsync(ReleaseAsset asset, CancellationToken ct)
    {
        // GitHubClient.DownloadAssetAsync wirft bei jedem Fehlschlag bereits eine
        // InvalidOperationException mit fertiger, englischer Nutzermeldung -- hier nichts extra
        // einfangen, nur durchreichen. Die laufende EXE ist an dieser Stelle noch unberuehrt.
        var downloaded = await GitHubClient.DownloadAssetAsync(asset, AppPaths.DownloadDir, ct)
            .ConfigureAwait(false);

        var staged = ExePath + ".new";

        // Schritt 1, gefahrlos: in eine Nebendatei NEBEN der laufenden EXE kopieren. Schlaegt das
        // fehl -- schreibgeschuetzter Installationsordner (Program Files ohne Elevation), eine
        // Nur-Lese-Netzfreigabe, voller Datentraeger --, ist die laufende EXE noch komplett
        // unberuehrt: kein Rollback noetig, nur eine klare Meldung statt eines rohen IOException-Texts.
        try
        {
            File.Copy(downloaded, staged, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            AppLog.Error($"self-update: could not stage {asset.Name} next to {ExePath}", e);
            throw new InvalidOperationException(
                $"Could not write the update next to {Path.GetFileName(ExePath)}. If the application " +
                "is installed in a protected folder (e.g. Program Files) or on a read-only location, " +
                "move it to a writable folder and try again.", e);
        }

        // Vorherigen Rest aufraeumen (best effort): ReplaceFile ueberschreibt eine bereits
        // vorhandene Sicherungsdatei zwar selbst, ein sauberer Ausgangszustand schadet trotzdem
        // nicht.
        CleanupOldExecutable();

        // Der Punkt ohne Rueckweg -- aber ohne das Zwischenfenster, das ein Umbenennen+Verschieben
        // in zwei Schritten zwangslaeufig haette (s. Klassenkommentar): EIN Kernel-Aufruf ersetzt
        // die laufende EXE durch die Nebendatei und sichert deren alten Inhalt unter OldPath. Es
        // gibt keinen Moment, in dem unter dem EXE-Namen keine Datei liegt -- entweder der Aufruf
        // gelingt vollstaendig, oder ExePath ist unveraendert die alte Datei. Ein Doppel-Rollback
        // wie bei der frueheren Zwei-Schritt-Loesung ist deshalb unnoetig und entfaellt: ein
        // Fehlschlag hier laesst die bestehende Installation per Definition unberuehrt.
        if (!ReplaceFile(ExePath, staged, OldPath, dwReplaceFlags: 0, lpExclude: 0, lpReserved: 0))
        {
            var error = Marshal.GetLastWin32Error();
            AppLog.Error($"self-update: ReplaceFile failed for {ExePath} (Win32 error {error})");
            try { File.Delete(staged); } catch (Exception ce) when (ce is IOException or UnauthorizedAccessException) { }
            throw new InvalidOperationException(
                $"Could not update {Path.GetFileName(ExePath)}: the running file could not be replaced. " +
                "Check that the application is not installed in a protected, read-only location, and that " +
                "no other program (e.g. an antivirus scanner) has it locked. Nothing was changed.");
        }

        AppLog.Info($"self-update applied, restarting into {asset.Name}");

        // --restarted-by-update signalisiert der neuen Instanz, kurz auf den Instanz-Mutex zu
        // warten statt sofort "laeuft bereits" zu melden: DIESE (alte) Instanz haelt ihn noch, bis
        // Application.Exit() unten ihren Message-Loop beendet hat und Main() zurueckkehrt. Ohne
        // dieses Signal koennte die frisch gestartete Instanz den Mutex noch belegt vorfinden und
        // sich faelschlich sofort wieder beenden (s. Program.AcquireSingleInstanceMutex).
        Process.Start(new ProcessStartInfo(ExePath) { UseShellExecute = true, Arguments = "--restarted-by-update" });
        Application.Exit();
    }
}
