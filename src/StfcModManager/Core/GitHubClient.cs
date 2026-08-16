using System.Net;
using System.Net.Http;
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
    private static readonly HttpClient Http = CreateClient();

    /// <summary>Letzte gelesene Rueckstzeit des Rate-Limits (lokale Zeit, "HH:mm"), fuer die
    /// Fehlermeldung bei HTTP 403. Null, solange kein Limit beobachtet wurde.</summary>
    public static string? RateLimitHint { get; private set; }

    private static HttpClient CreateClient()
    {
        // AllowAutoRedirect ist bewusst aus: HttpClients eingebauter Redirect-Mechanismus prueft
        // ein Umleitungsziel NICHT erneut gegen unsere Host-Allowlist -- eine Weiterleitung auf
        // einen fremden Host wuerde die Pruefung in DownloadAssetAsync sonst lautlos umgehen, denn
        // die laeuft nur gegen die urspruengliche URL. DownloadAssetAsync verfolgt Weiterleitungen
        // deshalb von Hand und prueft jede Zwischenstation einzeln (s. dort). GetLatestReleaseAsync
        // braucht sie fuer den festen Host api.github.com im Normalfall ohnehin nicht.
        var handler = new HttpClientHandler { AllowAutoRedirect = false };
        var c = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
        c.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("StfcModManager", "0.1.0"));
        c.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        return c;
    }

    /// <summary>Erkennt nur "https://github.com/{owner}/{repo}"-artige URLs (Slash am Ende,
    /// tieferer Pfad wie /releases/latest, optionales ".git"-Suffix erlaubt). http:// und jeder
    /// fremde Host werden abgelehnt; der Host wird per exaktem Vergleich geprueft, nicht per
    /// Praefix oder Suffix, also keine Verwechslung mit z. B. "evil-github.com" moeglich.</summary>
    public static (string Owner, string Repo)? ParseRepoUrl(string url)
    {
        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri)) return null;
        if (uri.Scheme != Uri.UriSchemeHttps) return null;
        if (!uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase)) return null;

        var parts = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2) return null;
        return (parts[0], parts[1].EndsWith(".git", StringComparison.OrdinalIgnoreCase)
                              ? parts[1][..^4] : parts[1]);
    }

    /// <summary>Erste zutreffende Regel gewinnt (Spec §6.2). Null heisst: der Aufrufer muss fragen
    /// -- aber die Funktion sagt nicht, WARUM. Ob "es gibt hier gar nichts Installierbares" oder
    /// "es gibt mehrere gleich plausible Kandidaten" vorliegt, muss der Aufrufer selbst aus der
    /// UEBERGEBENEN `names`-Liste ablesen (dieselbe .zip/.dll-Filterregel wie hier): ein Dialog
    /// "waehle eine Datei" waere sonst faelschlich leer, wenn tatsaechlich keine einzige Kandidatin
    /// existiert, statt "dieses Release enthaelt keine installierbare Datei" zu melden.</summary>
    public static string? PickAsset(IReadOnlyList<string> names, string? remembered)
    {
        if (remembered is not null &&
            names.FirstOrDefault(n => n.Equals(remembered, StringComparison.OrdinalIgnoreCase)) is { } exact)
            return exact;

        var zips = names.Where(n => n.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)).ToList();
        if (zips.Count == 1) return zips[0];
        if (zips.Count > 1) return null;

        var dlls = names.Where(n => n.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)).ToList();
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
            res = await Http.SendAsync(req, ct).ConfigureAwait(false);
        }
        catch (HttpRequestException e)
        {
            AppLog.Error($"could not reach GitHub to check {owner}/{repo}", e);
            throw new InvalidOperationException(
                $"Could not reach GitHub to check {owner}/{repo} for updates. Check your internet connection and try again.", e);
        }
        catch (TaskCanceledException e) when (!ct.IsCancellationRequested)
        {
            // HttpClient wirft bei Ueberschreiten von Timeout eine TaskCanceledException, obwohl
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
            if ((int)res.StatusCode == 403)
            {
                // RateLimitHint ist nur gefuellt, wenn X-RateLimit-Remaining bereits einmal "0"
                // war (s. NoteRateLimit) -- fehlt es (z. B. gleich der allererste Aufruf im
                // Prozess scheitert schon), bleibt die Meldung trotzdem konkret genug zum Handeln.
                var msg = RateLimitHint is not null
                    ? $"GitHub rate limit reached. Resets at {RateLimitHint}. Add a personal access token in Settings to raise it."
                    : "GitHub rejected the request (403 Forbidden). This is usually the hourly rate limit for requests without a token -- add a personal access token in Settings to raise it.";
                AppLog.Warn($"GitHub 403 for {owner}/{repo}: {msg}");
                throw new InvalidOperationException(msg);
            }
            res.EnsureSuccessStatusCode();

            using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
            var root = doc.RootElement;

            if (root.TryGetProperty("prerelease", out var pre) && pre.GetBoolean())
            {
                AppLog.Warn($"latest release of {owner}/{repo} is a pre-release, ignored");
                throw new InvalidOperationException(
                    $"The latest release of {owner}/{repo} is marked as a pre-release and is not offered as an update.");
            }

            var tag = root.GetProperty("tag_name").GetString() ?? "";
            var assets = new List<ReleaseAsset>();
            if (root.TryGetProperty("assets", out var arr))
            {
                foreach (var a in arr.EnumerateArray())
                {
                    var name = a.GetProperty("name").GetString();
                    var url = a.GetProperty("browser_download_url").GetString();
                    var size = a.TryGetProperty("size", out var s) ? s.GetInt64() : 0;
                    if (name is not null && url is not null) assets.Add(new ReleaseAsset(name, url, size));
                }
            }

            return new ReleaseInfo(tag, assets, res.Headers.ETag?.Tag);
        }
    }

    private static void NoteRateLimit(HttpResponseMessage res)
    {
        if (!res.Headers.TryGetValues("X-RateLimit-Remaining", out var rem)) return;
        if (rem.FirstOrDefault() != "0") { RateLimitHint = null; return; }
        if (res.Headers.TryGetValues("X-RateLimit-Reset", out var reset)
            && long.TryParse(reset.FirstOrDefault(), out var epoch))
            RateLimitHint = DateTimeOffset.FromUnixTimeSeconds(epoch).LocalDateTime.ToString("HH:mm");
    }

    private static readonly string[] AllowedDownloadHosts =
        { "github.com", "objects.githubusercontent.com", "release-assets.githubusercontent.com" };

    private static bool IsAllowedDownloadHost(Uri uri) =>
        uri.Scheme == Uri.UriSchemeHttps &&
        AllowedDownloadHosts.Any(h => uri.Host.Equals(h, StringComparison.OrdinalIgnoreCase)
                                    || uri.Host.EndsWith("." + h, StringComparison.OrdinalIgnoreCase));

    private static bool IsRedirectStatus(HttpStatusCode code) =>
        code is HttpStatusCode.MovedPermanently or HttpStatusCode.Found or HttpStatusCode.SeeOther
             or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect;

    public static async Task<string> DownloadAssetAsync(ReleaseAsset asset, string destDir, CancellationToken ct)
    {
        var uri = new Uri(asset.DownloadUrl);
        if (!IsAllowedDownloadHost(uri))
        {
            AppLog.Error($"refused to download {asset.Name}: untrusted host {uri.Host}");
            throw new InvalidOperationException($"Refusing to download from an untrusted host: {uri.Host}");
        }

        Directory.CreateDirectory(destDir);
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
                res = await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);

                if (!IsRedirectStatus(res.StatusCode)) break;

                var location = res.Headers.Location;
                res.Dispose();
                if (location is null)
                    throw new InvalidOperationException("GitHub redirected the download without a target location.");

                // Echte GitHub-Downloads durchlaufen genau das hier: browser_download_url zeigt
                // zuerst auf github.com und leitet von dort auf den eigentlichen CDN-Host um. Ohne
                // AllowAutoRedirect (s. CreateClient) muss diese Weiterleitung von Hand verfolgt
                // werden, und JEDE Zwischenstation wird gegen dieselbe Allowlist geprueft wie die
                // urspruengliche URL -- sonst koennte eine manipulierte Umleitung eine Datei von
                // einem beliebigen Host laden, die anschliessend ins Spiel geladen wird.
                current = location.IsAbsoluteUri ? location : new Uri(current, location);
                if (++hops > 5)
                    throw new InvalidOperationException("Too many redirects while downloading the file.");
                if (!IsAllowedDownloadHost(current))
                {
                    AppLog.Error($"refused redirect for {asset.Name}: untrusted host {current.Host}");
                    throw new InvalidOperationException($"Refusing to follow a redirect to an untrusted host: {current.Host}");
                }
            }

            using (res)
            {
                res.EnsureSuccessStatusCode();
                await using (var target = File.Create(tmp))
                    await res.Content.CopyToAsync(target, ct).ConfigureAwait(false);
            }

            // Zusaetzliche Absicherung neben dem, was ein abgebrochener Transfer bereits als
            // Exception meldet: stimmt die tatsaechlich geschriebene Groesse nicht mit der von
            // GitHub gemeldeten ueberein (z. B. weil eine Zwischenstation den Inhalt stillschweigend
            // gekuerzt hat, ohne die Verbindung sichtbar abzubrechen), gilt der Download als
            // fehlgeschlagen statt als vollstaendig.
            var actualLength = new FileInfo(tmp).Length;
            if (asset.Size > 0 && actualLength != asset.Size)
            {
                File.Delete(tmp);
                throw new InvalidOperationException(
                    $"Download of {asset.Name} is incomplete ({actualLength} of {asset.Size} bytes). Please try again.");
            }

            File.Move(tmp, dest, overwrite: true);
        }
        catch (Exception e) when (e is HttpRequestException or IOException or UnauthorizedAccessException or TaskCanceledException)
        {
            // Jede Netzwerk- oder Datei-Ausnahme waehrend des Downloads muss die Nebendatei
            // mitnehmen, sonst bliebe im Fehlerfall eine unvollstaendige Datei unter dem Zielnamen
            // liegen (s. Kommentar bei "tmp" oben).
            try { File.Delete(tmp); } catch (Exception ce) when (ce is IOException or UnauthorizedAccessException) { }

            AppLog.Error($"download of {asset.Name} failed", e);

            if (e is TaskCanceledException && ct.IsCancellationRequested) throw;
            if (e is TaskCanceledException)
                throw new InvalidOperationException(
                    $"Download of {asset.Name} timed out. Check your internet connection and try again.", e);
            if (e is HttpRequestException)
                throw new InvalidOperationException(
                    $"Could not download {asset.Name} from GitHub. {e.Message}", e);
            throw;
        }

        AppLog.Info($"downloaded {asset.Name} ({new FileInfo(dest).Length} bytes)");
        return dest;
    }
}
