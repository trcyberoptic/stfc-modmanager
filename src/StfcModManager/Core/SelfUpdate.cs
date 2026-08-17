using System.Diagnostics;
using System.Reflection;

namespace StfcModManager.Core;

/// <summary>
/// Ersetzt die eigene EXE. Eine laufende EXE laesst sich nicht ueberschreiben,
/// aber umbenennen — daraus besteht der ganze Trick. Kein Hilfsprozess noetig.
///
/// Der Trick haelt auch fuer den self-contained Single-File-Host dieses Projekts: der
/// Windows-Loader oeffnet eine gestartete EXE mit FILE_SHARE_DELETE (aber ohne FILE_SHARE_WRITE) --
/// direktes Ueberschreiben scheitert deshalb, Umbenennen (das nur den Verzeichniseintrag aendert,
/// nicht den offenen Datei-Handle) dagegen nicht. Vor dem Schreiben dieser Klasse angenommen, aber
/// NICHT einfach geglaubt: fuer diesen Build (net10.0-windows, PublishSingleFile +
/// IncludeNativeLibrariesForSelfExtract=true) mit DOTNET_BUNDLE_EXTRACT_BASE_DIR auf ein leeres
/// Verzeichnis gemessen, dass zur Laufzeit KEINE Datei dorthin extrahiert wird -- der moderne
/// Windows-Host laedt seine eigenen nativen Hosting-Komponenten (coreclr.dll usw.) direkt
/// speicher-gemappt aus der einen gebuendelten EXE, ohne Extraktion auf die Platte. Die laufende EXE
/// ist damit auf diesem Build tatsaechlich die EINZIGE beteiligte gesperrte Datei, keine zweite
/// Altlast, die das Umbenennen unterlaufen koennte. Von Hand nachgemessen (publish, EXE starten,
/// aus einer zweiten Shell umbenennen+ersetzen, DOTNET_BUNDLE_EXTRACT_BASE_DIR-Gegenprobe), s.
/// Taskbericht.
/// </summary>
public static class SelfUpdate
{
    public const string RepoOwner = "trcyberoptic";
    public const string RepoName = "stfc-modmanager";

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

        // Vorherigen Rest aufraeumen, damit der folgende Move nicht an einer bereits vorhandenen
        // OldPath scheitert (File.Move ohne overwrite wirft, wenn das Ziel schon existiert).
        CleanupOldExecutable();

        // Schritt 2, der Punkt ohne Rueckweg: die laufende EXE umbenennen. Erlaubt, auch waehrend
        // der Prozess laeuft (s. Klassenkommentar). Schlaegt DAS schon fehl, ist wiederum nichts
        // Zerstoerendes passiert -- die EXE heisst noch, wie sie immer hiess.
        try
        {
            File.Move(ExePath, OldPath);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            AppLog.Error($"self-update: could not rename running executable {ExePath}", e);
            try { File.Delete(staged); } catch (Exception ce) when (ce is IOException or UnauthorizedAccessException) { }
            throw new InvalidOperationException(
                $"Could not update {Path.GetFileName(ExePath)}: the running file could not be renamed. " +
                "Check that the application is not installed in a protected, read-only location.", e);
        }

        // Schritt 3: die Nebendatei an den freigewordenen Namen verschieben. Schlaegt GENAU DAS
        // fehl, stuende die Anwendung ohne EXE da -- weder die alte noch die neue Version waere
        // unter dem erwarteten Namen vorhanden, eine Verknuepfung faende nichts mehr zum Starten.
        // Deshalb hier sofort zurueckrollen, statt den Nutzer damit alleinzulassen.
        try
        {
            File.Move(staged, ExePath);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            AppLog.Error("self-update: could not move the staged file into place, rolling back", e);
            try
            {
                File.Move(OldPath, ExePath);
                AppLog.Warn("self-update: rolled back to the previous executable after a failed move");
            }
            catch (Exception re) when (re is IOException or UnauthorizedAccessException)
            {
                // Der seltene Doppel-Fehlschlag: nicht einmal der Rueckbau gelingt. Dann liegt
                // wirklich keine EXE mehr unter dem erwarteten Namen -- das darf nicht
                // stillschweigend untergehen, der Nutzer braucht die Handlungsanweisung, die alte
                // Version von Hand zurueckzubenennen.
                AppLog.Error($"self-update: rollback ALSO failed -- {ExePath} is missing, previous version is at {OldPath}", re);
                throw new InvalidOperationException(
                    $"The update failed and the automatic rollback could not restore the previous version. " +
                    $"The previous executable is still at '{OldPath}' -- rename it back to " +
                    $"'{Path.GetFileName(ExePath)}' manually.", re);
            }
            throw new InvalidOperationException(
                $"Could not finish updating {Path.GetFileName(ExePath)}. The previous version was restored, " +
                "nothing was lost. Try the update again later.", e);
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
