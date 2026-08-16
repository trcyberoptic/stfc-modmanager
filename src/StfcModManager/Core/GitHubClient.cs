using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace StfcModManager.Core;

public sealed record ReleaseAsset(string Name, string DownloadUrl, long Size);
public sealed record ReleaseInfo(string Tag, IReadOnlyList<ReleaseAsset> Assets, string? ETag);

/// <summary>Einzige Quelle ausgehenden Netzwerkverkehrs der Anwendung. Was hier akzeptiert und
/// heruntergeladen wird, landet als Plugin im Spielprozess -- jede Pruefung in dieser Datei ist
/// eine Sicherheitsgrenze, keine Bequemlichkeit.</summary>
public static class GitHubClient
{
    // Zwei getrennte Clients mit bewusst unterschiedlichem Redirect-Verhalten:
    //
    // ApiHttp (api.github.com) laesst Redirects automatisch zu. Das ist sicher, weil ein Redirect
    // von api.github.com den Host nie verlaesst -- z. B. bei einem umbenannten Repository
    // (GET .../releases/latest -> 301 -> https://api.github.com/repositories/{id}/releases/latest,
    // real beobachtet fuer Homebrew/homebrew). Repository-Umbenennungen sind bei Mod-Projekten
    // Alltag, eine gespeicherte Repo-URL zeigt dann auf den alten Namen -- ohne automatisches
    // Folgen wuerde jede Statusabfrage danach mit einem rohen HTTP 301 scheitern.
    //
    // DownloadHttp (github.com / *.githubusercontent.com) schaltet automatische Redirects AUS.
    // HttpClients eingebauter Redirect-Mechanismus prueft ein Umleitungsziel NICHT erneut gegen
    // unsere Host-Allowlist -- eine Weiterleitung auf einen fremden Host wuerde die Pruefung in
    // DownloadAssetAsync sonst lautlos umgehen. Echte Asset-Downloads durchlaufen genau das:
    // browser_download_url zeigt zuerst auf github.com und leitet von dort auf den eigentlichen
    // CDN-Host um (real beobachtet: release-assets.githubusercontent.com) -- das ist also der
    // Normalfall, kein hypothetischer Randfall. DownloadAssetAsync verfolgt diese Weiterleitung
    // deshalb von Hand und prueft jede Zwischenstation einzeln (s. ResolveAllowedRedirect).
    private static readonly HttpClient ApiHttp = CreateClient(allowAutoRedirect: true);
    private static readonly HttpClient DownloadHttp = CreateClient(allowAutoRedirect: false);

    private const int IdleTimeoutSeconds = 20;

    /// <summary>Letzte gelesene Rueckstzeit des Rate-Limits (lokale Zeit, "HH:mm"), fuer die
    /// Fehlermeldung bei HTTP 403. Null, solange kein aktuell gueltiges Limit beobachtet wurde.</summary>
    public static string? RateLimitHint { get; private set; }

    private static HttpClient CreateClient(bool allowAutoRedirect)
    {
        var handler = new HttpClientHandler { AllowAutoRedirect = allowAutoRedirect };
        var c = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
        c.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("StfcModManager", "0.1.0"));
        c.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        return c;
    }

    /// <summary>Erkennt nur "https://github.com/{owner}/{repo}"-artige URLs (Slash am Ende,
    /// tieferer Pfad wie /releases/latest, optionales ".git"-Suffix erlaubt). http:// und jeder
    /// fremde Host werden abgelehnt; verglichen wird IdnHost statt Host -- das ist die Form, die
    /// die eigentliche Verbindung verwendet (DNS/TLS arbeiten mit dem ASCII/Punycode-Namen), und
    /// exakt, nicht per Praefix oder Suffix, also keine Verwechslung mit z. B. "evil-github.com".</summary>
    public static (string Owner, string Repo)? ParseRepoUrl(string url)
    {
        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri)) return null;
        if (uri.Scheme != Uri.UriSchemeHttps) return null;
        if (!uri.IdnHost.Equals("github.com", StringComparison.OrdinalIgnoreCase)) return null;

        var parts = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2) return null;

        var owner = parts[0];
        var repo = parts[1].EndsWith(".git", StringComparison.OrdinalIgnoreCase)
            ? parts[1][..^4] : parts[1];
        // Ein Repo-Name, der NUR aus ".git" besteht (z. B. ".../a/.git"), wuerde nach dem Abschneiden
        // des Suffix zu einer leeren Zeichenkette und damit zu einem kaputten API-Pfad
        // (".../repos/a//releases/latest") fuehren -- als ungueltige URL behandeln statt das
        // durchzureichen.
        return repo.Length == 0 ? null : (owner, repo);
    }

    private static bool IsZip(string n) => n.EndsWith(".zip", StringComparison.OrdinalIgnoreCase);
    private static bool IsDll(string n) => n.EndsWith(".dll", StringComparison.OrdinalIgnoreCase);

    /// <summary>Dieselbe .zip/.dll-Regel wie PickAsset, als eigener oeffentlicher Baustein: ein
    /// Aufrufer, der von PickAsset ein null bekommt, kann so selbst unterscheiden, ob es
    /// UEBERHAUPT Kandidaten gab (dann: Dialog "waehle eine Datei") oder keinen einzigen (dann:
    /// "dieses Release enthaelt nichts Installierbares") -- OHNE die Filterregel nachzubauen und
    /// so unbemerkt von PickAsset abzuweichen, falls sich die Regel je aendert.</summary>
    public static IReadOnlyList<string> InstallableCandidates(IReadOnlyList<string> names) =>
        names.Where(n => IsZip(n) || IsDll(n)).ToList();

    /// <summary>Erste zutreffende Regel gewinnt (Spec §6.2). Null heisst: der Aufrufer muss fragen
    /// -- aber die Funktion sagt nicht, WARUM (s. InstallableCandidates fuer die Unterscheidung).</summary>
    public static string? PickAsset(IReadOnlyList<string> names, string? remembered)
    {
        if (remembered is not null &&
            names.FirstOrDefault(n => n.Equals(remembered, StringComparison.OrdinalIgnoreCase)) is { } exact)
            return exact;

        var zips = names.Where(IsZip).ToList();
        if (zips.Count == 1) return zips[0];
        if (zips.Count > 1) return null;

        var dlls = names.Where(IsDll).ToList();
        return dlls.Count == 1 ? dlls[0] : null;
    }

    /// <summary>Null bedeutet HTTP 304 -- das gemerkte ETag ist noch gueltig, nichts Neues.</summary>
    public static async Task<ReleaseInfo?> GetLatestReleaseAsync(
        string owner, string repo, string? etag, string? token, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get,
            $"https://api.github.com/repos/{owner}/{repo}/releases/latest");

        if (!string.IsNullOrWhiteSpace(etag))
            req.Headers.TryAddWithoutValidation("If-None-Match", etag);
        if (!string.IsNullOrWhiteSpace(token))
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        HttpResponseMessage res;
        try
        {
            res = await ApiHttp.SendAsync(req, ct).ConfigureAwait(false);
        }
        catch (HttpRequestException e)
        {
            AppLog.Error($"could not reach GitHub to check {owner}/{repo}", e);
            throw new InvalidOperationException(
                $"Could not reach GitHub to check {owner}/{repo} for updates. Check your internet connection and try again.", e);
        }
        catch (OperationCanceledException e) when (!ct.IsCancellationRequested)
        {
            // HttpClient wirft bei Ueberschreiten von Timeout eine (Task)CanceledException, obwohl
            // niemand das uebergebene CancellationToken abgebrochen hat -- der Filter oben
            // unterscheidet das von einem echten, vom Aufrufer gewollten Abbruch, der unveraendert
            // durchgereicht werden muss statt hier in eine falsche Meldung verpackt zu werden.
            AppLog.Error($"request to GitHub for {owner}/{repo} timed out", e);
            throw new InvalidOperationException(
                $"The request to GitHub for {owner}/{repo} timed out. Check your internet connection and try again.", e);
        }

        using (res)
        {
            NoteRateLimit(res);

            if (res.StatusCode == HttpStatusCode.NotModified) return null;

            if (res.StatusCode == HttpStatusCode.NotFound)
            {
                AppLog.Warn($"{owner}/{repo} has no published release");
                throw new InvalidOperationException($"{owner}/{repo} has no published release yet.");
            }

            if (res.StatusCode == HttpStatusCode.Unauthorized)
            {
                // Ein abgelaufenes oder falsch eingetragenes Personal Access Token ist ein
                // First-Class-Feature dieser Anwendung, kein Randfall -- ohne diesen Zweig laeuft
                // 401 unten in den generischen Fallback und zeigt dem Nutzer rohen HTTP-Text.
                var msg = !string.IsNullOrWhiteSpace(token)
                    ? "GitHub rejected the personal access token configured in Settings (401 Unauthorized). It may be invalid, expired, or missing required scopes -- check it in Settings."
                    : "GitHub rejected the request (401 Unauthorized).";
                AppLog.Warn($"GitHub 401 for {owner}/{repo}{(string.IsNullOrWhiteSpace(token) ? "" : ", configured token rejected")}");
                throw new InvalidOperationException(msg);
            }

            if ((int)res.StatusCode == 403)
            {
                // Nicht jedes 403 ist das stuendliche Rate-Limit -- NoteRateLimit hat RateLimitHint
                // oben bereits genau dann gesetzt, wenn DIESE Antwort X-RateLimit-Remaining: 0
                // zeigt; ist es null, ist es ein anderer Grund (z. B. eine gesperrte oder
                // zugriffsbeschraenkte Repository) und die Meldung darf das Limit nicht behaupten.
                // Ausserdem: ist bereits ein Token konfiguriert, ist "trag eines ein" keine
                // brauchbare Handlungsanweisung mehr.
                string msg;
                if (RateLimitHint is not null)
                {
                    msg = string.IsNullOrWhiteSpace(token)
                        ? $"GitHub's hourly rate limit for requests without a token has been reached. It resets at {RateLimitHint}. Add a personal access token in Settings to raise it."
                        : $"GitHub's rate limit for the configured token has been reached. It resets at {RateLimitHint}. Wait until then and try again.";
                }
                else
                {
                    msg = "GitHub refused the request (403 Forbidden). This does not look like the usual rate limit -- the repository may be blocked or access-restricted.";
                }
                AppLog.Warn($"GitHub 403 for {owner}/{repo}: {msg}");
                throw new InvalidOperationException(msg);
            }

            if ((int)res.StatusCode == 429)
            {
                // GitHubs tatsaechlicher Code fuer das SEKUNDAERE Rate-Limit (Abuse-Erkennung bei
                // zu vielen Anfragen in kurzer Zeit) -- ein eigener Fall, kein 403.
                var retryAfter = res.Headers.RetryAfter?.Delta;
                var msg = retryAfter is not null
                    ? $"GitHub is temporarily rate-limiting requests. Try again in about {(int)Math.Ceiling(retryAfter.Value.TotalSeconds)} seconds."
                    : "GitHub is temporarily rate-limiting requests. Wait a moment and try again.";
                AppLog.Warn($"GitHub 429 for {owner}/{repo}: {msg}");
                throw new InvalidOperationException(msg);
            }

            if (!res.IsSuccessStatusCode)
            {
                // Auffangnetz fuer alles Uebrige (5xx u. ae.): kein res.EnsureSuccessStatusCode(),
                // dessen Meldung ("Response status code does not indicate success: 500 (Internal
                // Server Error)") dem Nutzer roh vorgesetzt wuerde.
                AppLog.Error($"GitHub returned {(int)res.StatusCode} for {owner}/{repo}");
                throw new InvalidOperationException(
                    $"GitHub returned an unexpected error ({(int)res.StatusCode}) while checking {owner}/{repo}. Try again later.");
            }

            string body;
            try
            {
                body = await res.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            }
            catch (Exception e) when (e is IOException or HttpRequestException)
            {
                AppLog.Error($"could not read GitHub response body for {owner}/{repo}", e);
                throw new InvalidOperationException(
                    $"Could not read the response from GitHub for {owner}/{repo}. Check your internet connection and try again.", e);
            }

            JsonDocument doc;
            try
            {
                doc = JsonDocument.Parse(body);
            }
            catch (JsonException e)
            {
                // Ein 200 mit einer HTML-Fehlerseite statt JSON ist ein klassisches Symptom eines
                // TLS-inspizierenden Firmenproxys -- die rohe JsonException-Meldung ("'<' is an
                // invalid start of a value.") waere fuer einen Endnutzer bedeutungslos.
                AppLog.Error($"GitHub response for {owner}/{repo} was not valid JSON", e);
                throw new InvalidOperationException(
                    $"GitHub did not return the expected data for {owner}/{repo}. This can happen if a proxy or firewall is interfering with the connection.", e);
            }

            using (doc)
            {
                var root = doc.RootElement;

                if (root.TryGetProperty("prerelease", out var pre) && pre.GetBoolean())
                {
                    AppLog.Warn($"latest release of {owner}/{repo} is a pre-release, ignored");
                    throw new InvalidOperationException(
                        $"The latest release of {owner}/{repo} is marked as a pre-release and is not offered as an update.");
                }

                if (!root.TryGetProperty("tag_name", out var tagEl) || tagEl.GetString() is not { } tag)
                {
                    AppLog.Error($"GitHub release for {owner}/{repo} is missing 'tag_name'");
                    throw new InvalidOperationException(
                        $"The latest release of {owner}/{repo} is missing information the manager needs. Try again later.");
                }

                var assets = new List<ReleaseAsset>();
                if (root.TryGetProperty("assets", out var arr))
                {
                    if (arr.ValueKind != JsonValueKind.Array)
                    {
                        AppLog.Error($"GitHub release for {owner}/{repo} has a non-array 'assets' field");
                        throw new InvalidOperationException(
                            $"The latest release of {owner}/{repo} has an unexpected format. Try again later.");
                    }

                    foreach (var a in arr.EnumerateArray())
                    {
                        if (a.ValueKind != JsonValueKind.Object) continue;
                        // TryGetProperty statt GetProperty: eine einzelne Asset-Zeile, der ein Feld
                        // fehlt, wird uebersprungen statt das komplette Release-Abrufen mit einer
                        // KeyNotFoundException zu Fall zu bringen -- der Null-Check unten war schon
                        // vorher da, GetProperty haette ihn nie erreicht.
                        var name = a.TryGetProperty("name", out var nameEl) ? nameEl.GetString() : null;
                        var url = a.TryGetProperty("browser_download_url", out var urlEl) ? urlEl.GetString() : null;
                        var size = a.TryGetProperty("size", out var sizeEl) && sizeEl.ValueKind == JsonValueKind.Number
                            ? sizeEl.GetInt64() : 0;
                        if (name is not null && url is not null) assets.Add(new ReleaseAsset(name, url, size));
                    }
                }

                return new ReleaseInfo(tag, assets, res.Headers.ETag?.Tag);
            }
        }
    }

    private static void NoteRateLimit(HttpResponseMessage res)
    {
        // Immer zuerst zuruecksetzen: fehlt der Remaining-Header oder zeigt er noch Kontingent,
        // gibt es fuer DIESE Antwort keinen aktuellen Hinweis -- ein alter Wert aus einem
        // frueheren Aufruf darf nie stehen bleiben, sonst meldet ein spaeteres 403 eine
        // Rueckstzeit, die schon in der Vergangenheit liegt.
        RateLimitHint = null;
        if (!res.Headers.TryGetValues("X-RateLimit-Remaining", out var rem) || rem.FirstOrDefault() != "0")
            return;
        if (res.Headers.TryGetValues("X-RateLimit-Reset", out var reset)
            && long.TryParse(reset.FirstOrDefault(), out var epoch))
            RateLimitHint = DateTimeOffset.FromUnixTimeSeconds(epoch).LocalDateTime.ToString("HH:mm");
    }

    // Deckt exakt den in der Aufgabenstellung festgelegten Rahmen ab: "https:// und nur von
    // github.com oder *.githubusercontent.com". github.com bekommt bewusst KEINEN Wildcard-Suffix
    // (sonst waere z. B. "irgendwas.github.com" faelschlich erlaubt) -- nur die
    // githubusercontent.com-Familie (der tatsaechliche CDN-Host fuer Asset-Downloads) darf
    // Subdomains haben.
    internal static bool IsAllowedDownloadHost(Uri uri)
    {
        if (uri.Scheme != Uri.UriSchemeHttps || !uri.IsDefaultPort) return false;
        var host = uri.IdnHost; // s. ParseRepoUrl: die Form, die die Verbindung tatsaechlich nutzt
        return host.Equals("github.com", StringComparison.OrdinalIgnoreCase)
            || host.Equals("githubusercontent.com", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith(".githubusercontent.com", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsRedirectStatus(HttpStatusCode code) =>
        code is HttpStatusCode.MovedPermanently or HttpStatusCode.Found or HttpStatusCode.SeeOther
             or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect;

    /// <summary>Loest einen "Location"-Header (absolut, relativ, oder protokoll-relativ wie
    /// "//host/pfad") gegen die aktuelle URL auf und liefert das Ziel nur zurueck, wenn es die
    /// Host-Allowlist besteht -- sonst null. Rein und ohne Netzwerkzugriff testbar; reproduziert
    /// exakt, was DownloadAssetAsync fuer jeden Redirect-Hop tut, damit Test und Produktivcode nie
    /// auseinanderlaufen koennen.</summary>
    internal static Uri? ResolveAllowedRedirect(Uri current, Uri? location)
    {
        if (location is null) return null;
        var next = location.IsAbsoluteUri ? location : new Uri(current, location);
        return IsAllowedDownloadHost(next) ? next : null;
    }

    public static async Task<string> DownloadAssetAsync(ReleaseAsset asset, string destDir, CancellationToken ct)
    {
        Uri uri;
        try
        {
            uri = new Uri(asset.DownloadUrl);
        }
        catch (UriFormatException e)
        {
            AppLog.Error($"asset {asset.Name} has an unparseable download URL: {asset.DownloadUrl}", e);
            throw new InvalidOperationException($"The download link for {asset.Name} is invalid.", e);
        }

        if (!IsAllowedDownloadHost(uri))
        {
            AppLog.Error($"refused to download {asset.Name}: untrusted host {uri.IdnHost}");
            throw new InvalidOperationException($"Refusing to download from an untrusted host: {uri.IdnHost}");
        }

        try
        {
            Directory.CreateDirectory(destDir);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            AppLog.Error($"could not create download folder {destDir}", e);
            throw new InvalidOperationException(
                $"Could not create the download folder for {asset.Name}. Check your permissions and available disk space.", e);
        }

        // Nebendateien eines fruehren, abgebrochenen Downloads (Absturz, Stromausfall zwischen
        // Schreiben und Umbenennen) raeumen, genau wie AppState.Load es fuer seine eigenen
        // Temp-Dateien tut. Best-effort: eine noch offene Datei eines PARALLEL laufenden Downloads
        // laesst sich unter Windows ohnehin nicht loeschen (IOException, hier stillschweigend
        // uebersprungen), das Fegen trifft also nur wirklich verwaiste Dateien.
        SweepOrphanedTempFiles(destDir);

        var dest = Path.Combine(destDir, asset.Name);

        // In eine Nebendatei schreiben und erst bei vollstaendigem Erfolg an den Zielnamen
        // verschieben: ein Abbruch mittendrin (Netzwerk, Timeout, voller Datentraeger) darf nie
        // eine Halbdatei unter dem Zielnamen hinterlassen -- ein spaeterer Schritt (PackageMapper)
        // wuerde sie sonst faelschlich fuer ein vollstaendiges, installierbares Archiv halten.
        var tmp = $"{dest}.{Environment.ProcessId}.{Guid.NewGuid():N}.tmp";

        try
        {
            var current = uri;
            HttpResponseMessage res;
            var hops = 0;
            while (true)
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, current);
                res = await DownloadHttp.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);

                if (!IsRedirectStatus(res.StatusCode)) break;

                var location = res.Headers.Location;
                res.Dispose();

                if (++hops > 5)
                    throw new InvalidOperationException("Too many redirects while downloading the file.");

                var next = ResolveAllowedRedirect(current, location);
                if (next is null)
                {
                    AppLog.Error($"refused redirect for {asset.Name} from {current}: target is missing or not on the allowlist");
                    throw new InvalidOperationException($"Refusing to follow an untrusted redirect while downloading {asset.Name}.");
                }
                current = next;
            }

            long? expectedFromContentLength;
            using (res)
            {
                res.EnsureSuccessStatusCode();
                expectedFromContentLength = res.Content.Headers.ContentLength;

                var source = await res.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                var target = File.Create(tmp);
                await using (target.ConfigureAwait(false))
                {
                    // HttpClient.Timeout deckt einen mit ResponseHeadersRead gestreamten Body NICHT
                    // ab -- nur den Empfang der Header. Ein Server, der nach den Headern ein paar
                    // Bytes schickt und dann fuer immer schweigt, wuerde CopyToAsync sonst ewig
                    // haengen lassen; eine spaetere Aufgabe ruft diese Methode synchron vom
                    // UI-Thread aus auf, das waere ein permanentes Einfrieren der Anwendung. Deshalb
                    // hier ein GLEITENDES Leerlauf-Zeitlimit statt eines starren Gesamt-Caps: jeder
                    // erfolgreiche Lese-Chunk setzt es zurueck, ein grosses Archiv auf einer
                    // langsamen, aber stetig liefernden Leitung wird trotzdem fertig.
                    await CopyWithIdleTimeoutAsync(source, target, TimeSpan.FromSeconds(IdleTimeoutSeconds), ct)
                        .ConfigureAwait(false);
                }
            }

            // Zusaetzliche Absicherung neben dem, was ein abgebrochener Transfer bereits als
            // Exception meldet: stimmt die tatsaechlich geschriebene Groesse nicht mit der
            // erwarteten ueberein, gilt der Download als fehlgeschlagen statt als vollstaendig.
            // GitHub liefert "size" nicht fuer jedes Asset zuverlaessig (0 heisst hier "unbekannt",
            // nicht "leere Datei") -- in dem Fall auf den vom Server gemeldeten Content-Length
            // zurueckfallen, sonst wuerde eine abgeschnittene Uebertragung bei fehlendem
            // Groessenfeld unbemerkt als vollstaendig durchgehen.
            var actualLength = new FileInfo(tmp).Length;
            var expectedLength = asset.Size > 0 ? asset.Size : expectedFromContentLength ?? 0;
            if (expectedLength > 0 && actualLength != expectedLength)
            {
                AppLog.Error($"download of {asset.Name} is incomplete: {actualLength} of {expectedLength} bytes");
                File.Delete(tmp);
                throw new InvalidOperationException(
                    $"Download of {asset.Name} is incomplete ({actualLength} of {expectedLength} bytes). Please try again.");
            }

            File.Move(tmp, dest, overwrite: true);
        }
        catch (Exception e) when (e is HttpRequestException or IOException or UnauthorizedAccessException
                                        or OperationCanceledException or TimeoutException)
        {
            // Jede Netzwerk-, Zeitlimit- oder Datei-Ausnahme waehrend des Downloads muss die
            // Nebendatei mitnehmen, sonst bliebe im Fehlerfall eine unvollstaendige Datei unter
            // dem Zielnamen liegen (s. Kommentar bei "tmp" oben).
            try { File.Delete(tmp); } catch (Exception ce) when (ce is IOException or UnauthorizedAccessException) { }

            AppLog.Error($"download of {asset.Name} failed", e);

            if (e is OperationCanceledException && ct.IsCancellationRequested) throw;
            if (e is OperationCanceledException or TimeoutException)
                throw new InvalidOperationException(
                    $"Download of {asset.Name} timed out. Check your internet connection and try again.", e);
            if (e is HttpRequestException)
                throw new InvalidOperationException(
                    $"Could not download {asset.Name} from GitHub. Check your internet connection and try again.", e);
            throw;
        }

        AppLog.Info($"downloaded {asset.Name} ({new FileInfo(dest).Length} bytes)");
        return dest;
    }

    /// <summary>Kopiert mit einem gleitenden Leerlauf-Zeitlimit statt eines starren Gesamt-Caps
    /// (s. Aufrufer-Kommentar). Wirft TimeoutException, wenn innerhalb von idleTimeout kein
    /// einziges Byte ankommt; ein echter Abbruch ueber "ct" wird unveraendert durchgereicht.</summary>
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

    private static void SweepOrphanedTempFiles(string destDir)
    {
        try
        {
            foreach (var f in Directory.EnumerateFiles(destDir, "*.tmp"))
            {
                try { File.Delete(f); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }
}
